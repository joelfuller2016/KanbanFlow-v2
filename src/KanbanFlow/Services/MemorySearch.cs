using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;
using KanbanFlow.Data;
using KanbanFlow.Models;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Services;

/// <summary>
/// Default <see cref="IMemorySearch"/>: hybrid recall fusing FTS5 BM25 (keyword), brute-force cosine
/// (vector, via <see cref="IVectorSearch"/>), recency, and usefulness with Reciprocal Rank Fusion
/// (#223, the #249 spike retarget — NO sqlite-vec/vec0/SK connector). Uses
/// <see cref="IDbContextFactory{AppDbContext}"/> for the same short-lived per-call context model as
/// <see cref="MemoryService"/> / <see cref="VectorSearch"/>.
///
/// <para>Flow: materialise the candidate set (current, in-scope nodes — or all, when
/// <see cref="RetrievalOptions.IncludeSuperseded"/>), gather the keyword + vector rank lists,
/// rank recency and usefulness, fuse, sort, and finally bump access stats for the returned hits. The
/// keyword query is tokenised into quoted FTS5 OR-terms so arbitrary user input can never produce a
/// MATCH syntax error, and the vector signal degrades to empty when the query can't be embedded.</para>
/// </summary>
public sealed partial class MemorySearch(
    IDbContextFactory<AppDbContext> dbFactory,
    IEmbeddingService embeddings,
    IVectorSearch vectorSearch) : IMemorySearch
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<MemoryHit>> SearchAsync(
        MemoryScope scope, string scopeKey, string query, int k, RetrievalOptions? opts, CancellationToken ct = default)
    {
        // Reject a non-positive k up front (no meaningful top-k), consistent with IVectorSearch.
        if (k <= 0)
            throw new ValidationException("k (result count) must be greater than zero.");

        opts ??= new RetrievalOptions();
        var key = (scopeKey ?? string.Empty).Trim();
        var now = DateTime.UtcNow;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // The keyword signal + the access-stat bump run against this context's connection. Open it if
        // it is closed (the production per-call connection) and close it again afterwards; if it was
        // already open (the shared in-memory test connection) leave it untouched — closing that would
        // destroy the in-memory database.
        var connection = db.Database.GetDbConnection();
        var openedByUs = connection.State != ConnectionState.Open;
        if (openedByUs)
            await connection.OpenAsync(ct);
        try
        {
            // (a) Candidate set: current (or, when requested, all) in-scope nodes.
            var candidateQuery = db.MemoryNodes.AsNoTracking()
                .Where(m => m.Scope == scope && m.ScopeKey == key);
            if (!opts.IncludeSuperseded)
                candidateQuery = candidateQuery.Where(m => m.ValidTo == null && m.SupersededById == null);

            var rows = await candidateQuery
                .Select(m => new { m.Id, m.Content, m.CreatedAt, m.LastAccessedAt, m.AccessCount })
                .ToListAsync(ct);

            if (rows.Count == 0)
                return Array.Empty<MemoryHit>();

            var candidates = rows
                .Select(r => new Candidate(r.Id, r.Content, r.CreatedAt, r.LastAccessedAt, r.AccessCount))
                .ToList();
            var candidateIds = candidates.Select(c => c.Id).ToHashSet();

            // (b) Keyword signal (FTS5 BM25; lower = better). Sanitised + fail-soft: never throws.
            var keywordRank = await KeywordRanksAsync(connection, query, candidateIds, ct);

            // (c) Vector signal (embed the query → brute-force cosine). Degrades to empty when the
            //     query is blank, ZeroRouter is down, or nothing in scope is embedded.
            var vectorRank = await VectorRanksAsync(
                scope, key, query, Math.Max(k, candidates.Count), candidateIds, ct);

            // (d) recency + (e) usefulness scores, computed for every candidate.
            var recencyScore = new Dictionary<int, double>(candidates.Count);
            var usefulnessScore = new Dictionary<int, double>(candidates.Count);
            foreach (var c in candidates)
            {
                var ageHours = Math.Max(0, (now - (c.LastAccessedAt ?? c.CreatedAt)).TotalHours);
                recencyScore[c.Id] = Math.Exp(-ageHours / opts.RecencyHalfLifeHours);
                usefulnessScore[c.Id] = Math.Log(1 + Math.Max(0, c.AccessCount));
            }

            var recencyRank = RankByScoreDescending(candidates, recencyScore);
            var usefulnessRank = RankByScoreDescending(candidates, usefulnessScore);

            // (f) Reciprocal Rank Fusion. A signal the candidate is absent from contributes 0.
            var fused = new List<MemoryHit>(candidates.Count);
            foreach (var c in candidates)
            {
                keywordRank.TryGetValue(c.Id, out var kr);
                vectorRank.TryGetValue(c.Id, out var vr);
                var rr = recencyRank[c.Id];
                var ur = usefulnessRank[c.Id];

                var score =
                    opts.VectorWeight * Rrf(opts.RrfK, vr) +
                    opts.KeywordWeight * Rrf(opts.RrfK, kr) +
                    opts.RecencyWeight * Rrf(opts.RrfK, rr) +
                    opts.UsefulnessWeight * Rrf(opts.RrfK, ur);

                fused.Add(new MemoryHit(
                    c.Id, c.Content, score,
                    VectorRank: vr == 0 ? null : vr,
                    KeywordRank: kr == 0 ? null : kr,
                    RecencyScore: recencyScore[c.Id],
                    UsefulnessScore: usefulnessScore[c.Id]));
            }

            var hits = fused
                .OrderByDescending(h => h.FusedScore)
                .ThenBy(h => h.Id) // deterministic tie-break
                .Take(k)
                .ToList();

            // Usefulness bump: read-tracking for the returned hits only. NOT audited (consistent with
            // GetNodeAsync's un-audited access bump). It runs AFTER scoring, so the returned MemoryHit
            // scores reflect the PRE-bump state; non-hit candidates are never bumped.
            if (hits.Count > 0)
            {
                var hitIds = hits.Select(h => h.Id).ToList();
                await db.MemoryNodes
                    .Where(m => hitIds.Contains(m.Id))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(m => m.AccessCount, m => m.AccessCount + 1)
                        .SetProperty(m => m.LastAccessedAt, m => (DateTime?)now), ct);
            }

            return hits;
        }
        finally
        {
            if (openedByUs)
                await connection.CloseAsync();
        }
    }

    /// <summary>Reciprocal-rank term: <c>1 / (k + rank)</c>, or <c>0</c> when the item is absent (rank ≤ 0).</summary>
    private static double Rrf(int rrfK, int rank) => rank <= 0 ? 0.0 : 1.0 / (rrfK + rank);

    /// <summary>
    /// Assign 1-based ranks to candidates by descending score, breaking ties by ascending Id so the
    /// ranking (and therefore the fused result) is deterministic.
    /// </summary>
    private static Dictionary<int, int> RankByScoreDescending(
        IReadOnlyList<Candidate> candidates, IReadOnlyDictionary<int, double> scores)
    {
        var ordered = candidates
            .OrderByDescending(c => scores[c.Id])
            .ThenBy(c => c.Id)
            .ToList();

        var ranks = new Dictionary<int, int>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
            ranks[ordered[i].Id] = i + 1;
        return ranks;
    }

    /// <summary>
    /// Keyword signal: run the sanitised FTS5 MATCH, keep hits that are in the candidate set, and rank
    /// them by ascending BM25 (lower = better). Returns an empty map when the query yields no terms or
    /// — defensively — if the MATCH ever errors, so a keyword failure never sinks the search.
    /// </summary>
    private static async Task<Dictionary<int, int>> KeywordRanksAsync(
        DbConnection connection, string query, IReadOnlySet<int> candidateIds, CancellationToken ct)
    {
        var match = BuildFtsMatchQuery(query);
        if (match is null)
            return new Dictionary<int, int>();

        var scored = new List<(int Id, double Bm25)>();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT rowid, rank FROM {MemoryFts.Table} WHERE {MemoryFts.Table} MATCH $match";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$match";
            parameter.Value = match;
            command.Parameters.Add(parameter);

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = (int)reader.GetInt64(0);
                if (candidateIds.Contains(id))
                    scored.Add((id, reader.GetDouble(1)));
            }
        }
        catch (DbException)
        {
            // Defense in depth: the query is already tokenised into quoted OR-terms, but a malformed
            // FTS5 MATCH must NEVER throw — degrade to an empty keyword signal.
            return new Dictionary<int, int>();
        }

        var ordered = scored
            .OrderBy(s => s.Bm25) // lower BM25 = better match
            .ThenBy(s => s.Id)
            .ToList();

        var ranks = new Dictionary<int, int>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
            ranks[ordered[i].Id] = i + 1;
        return ranks;
    }

    /// <summary>
    /// Vector signal: embed the query and rank the candidates by the brute-force cosine search. Empty
    /// (graceful degradation) when the query is blank, the backend is unavailable, the embed fails
    /// (ZeroRouter down ⇒ fail-soft), or nothing in scope is embedded.
    /// </summary>
    private async Task<Dictionary<int, int>> VectorRanksAsync(
        MemoryScope scope, string scopeKey, string query, int fetch, IReadOnlySet<int> candidateIds, CancellationToken ct)
    {
        var ranks = new Dictionary<int, int>();
        if (string.IsNullOrWhiteSpace(query) || !vectorSearch.IsAvailable)
            return ranks;

        var embedded = await embeddings.EmbedAsync(query, ct);
        if (!embedded.Ok || embedded.Vector is not { Length: > 0 })
            return ranks; // ZeroRouter down / fail-soft ⇒ degrade to keyword + recency + usefulness

        var vectorHits = await vectorSearch.SearchAsync(scope, scopeKey, embedded.Vector, fetch, ct);
        var rank = 0;
        foreach (var hit in vectorHits) // already ordered by descending cosine, then Id ascending
        {
            if (candidateIds.Contains(hit.Id))
                ranks[hit.Id] = ++rank;
        }
        return ranks;
    }

    /// <summary>
    /// Turn arbitrary user text into a safe FTS5 MATCH expression: extract word tokens and join them
    /// as double-quoted phrases with <c>OR</c> (e.g. <c>"foo" OR "bar"</c>). Quoting every token means
    /// FTS5 operator characters (<c>* ( ) : ^ - " </c>, bare <c>AND/OR/NOT/NEAR</c>) in the input are
    /// treated as literals, never as syntax, so a malformed query can't error. Returns <c>null</c> when
    /// the input has no word characters (⇒ no keyword signal).
    /// </summary>
    private static string? BuildFtsMatchQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return null;

        var terms = new List<string>();
        foreach (Match token in WordTokenRegex().Matches(query))
            terms.Add("\"" + token.Value.Replace("\"", "\"\"") + "\"");

        return terms.Count == 0 ? null : string.Join(" OR ", terms);
    }

    // Unicode word run: letters, digits, underscore. Strips all FTS5 operator/punctuation characters.
    [GeneratedRegex(@"[\p{L}\p{N}_]+", RegexOptions.CultureInvariant)]
    private static partial Regex WordTokenRegex();

    /// <summary>A scoped candidate node, projected with just the columns the fusion needs.</summary>
    private sealed record Candidate(int Id, string Content, DateTime CreatedAt, DateTime? LastAccessedAt, int AccessCount);
}
