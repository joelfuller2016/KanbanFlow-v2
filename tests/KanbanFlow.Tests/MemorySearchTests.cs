using KanbanFlow.Data;
using KanbanFlow.Models;
using KanbanFlow.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace KanbanFlow.Tests;

/// <summary>
/// Real-code tests for <see cref="MemorySearch"/> — the hybrid FTS5 BM25 + brute-force cosine +
/// recency + usefulness retrieval fused with RRF (#223) — against a live SQLite database
/// (<see cref="SqliteTestFactory"/>). No data-layer fakes: nodes are seeded through the real
/// <see cref="MemoryService"/> (which dual-writes the FTS index), embeddings via the real
/// <see cref="MemoryService.SetEmbeddingAsync"/>, and the search runs the real EF queries + real
/// FTS5 MATCH + real cosine ranking. The ONLY substituted collaborator is the embedding network
/// transport: a hand-written <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> (the same pattern as
/// <c>EmbeddingServiceTests</c>) feeds the real <see cref="EmbeddingService"/> a deterministic query
/// vector — or fails like a downed ZeroRouter — so fusion is exercised without a network.
/// </summary>
public sealed class MemorySearchTests
{
    private static readonly EmbeddingOptions EmbedOptions = new() { Model = "stub-embed", TimeoutMs = 5_000 };

    // ── collaborator + seeding helpers ───────────────────────────────────────────────

    /// <summary>A real EmbeddingService whose transport returns <paramref name="queryVector"/> for any input.</summary>
    private static EmbeddingService EmbeddingReturning(float[] queryVector) =>
        new(new StubEmbeddingGenerator(_ => queryVector), EmbedOptions, NullLogger<EmbeddingService>.Instance);

    /// <summary>A real EmbeddingService whose transport always throws — simulates ZeroRouter being down (fail-soft).</summary>
    private static EmbeddingService EmbeddingDown() =>
        new(new StubEmbeddingGenerator(_ => null), EmbedOptions, NullLogger<EmbeddingService>.Instance);

    private static MemorySearch NewSearch(SqliteTestFactory f, IEmbeddingService embeddings) =>
        new(f, embeddings, new VectorSearch(f));

    private static async Task<int> CreateNode(MemoryService svc, string scopeKey, string content)
    {
        var node = await svc.CreateNodeAsync(
            new MemoryNodeCreate(MemoryType.Semantic, MemoryScope.Project, scopeKey, content), null);
        return node.Id;
    }

    /// <summary>Force a node's bi-temporal/access timestamps so recency + usefulness are deterministic.</summary>
    private static async Task SetStats(
        SqliteTestFactory f, int id, DateTime createdAt, DateTime? lastAccessedAt, int? accessCount = null)
    {
        await using var db = await f.CreateDbContextAsync();
        var node = await db.MemoryNodes.FindAsync(id);
        node!.CreatedAt = createdAt;
        node.LastAccessedAt = lastAccessedAt;
        if (accessCount is { } count)
            node.AccessCount = count;
        await db.SaveChangesAsync();
    }

    // ── signal isolation: each weight surfaces a different winner ──────────────────────
    private enum Signal { Vector, Keyword, Recency }

    [Fact]
    public async Task Search_SignalIsolation_WeightsChangeWinner()
    {
        // Vector-heavy ⇒ the vector-nearest node wins; keyword-heavy ⇒ the keyword-exact node wins;
        // recency-heavy ⇒ the most-recently-accessed node wins — three DIFFERENT rows.
        await RunSignalScenario(new RetrievalOptions { VectorWeight = 1000 }, expectedWinner: Signal.Vector);
        await RunSignalScenario(new RetrievalOptions { KeywordWeight = 1000 }, expectedWinner: Signal.Keyword);
        await RunSignalScenario(new RetrievalOptions { RecencyWeight = 1000 }, expectedWinner: Signal.Recency);
    }

    private static async Task RunSignalScenario(RetrievalOptions weights, Signal expectedWinner)
    {
        // Fresh database per variation so each scenario's pre-bump state is pristine.
        using var f = new SqliteTestFactory();
        var svc = new MemoryService(f);

        var queryVector = new[] { 1f, 0f, 0f }; // the query "apple" embeds to this

        // V: vector-nearest (embedding == query), content has NO "apple", ancient.
        var vector = await CreateNode(svc, "1", "oranges lemons and grapefruit");
        await svc.SetEmbeddingAsync(vector, queryVector, null);
        // K: keyword-exact ("apple"), embedding orthogonal to the query, ancient.
        var keyword = await CreateNode(svc, "1", "apple pie and pastry");
        await svc.SetEmbeddingAsync(keyword, new[] { 0f, 1f, 0f }, null);
        // R: most-recently-accessed, embedding orthogonal, content has NO "apple".
        var recency = await CreateNode(svc, "1", "banana split dessert");
        await svc.SetEmbeddingAsync(recency, new[] { 0f, 0f, 1f }, null);

        var ancient = DateTime.UtcNow.AddHours(-1000);
        var fresh = DateTime.UtcNow;
        await SetStats(f, vector, ancient, ancient);
        await SetStats(f, keyword, ancient, ancient);
        await SetStats(f, recency, fresh, fresh);

        var search = NewSearch(f, EmbeddingReturning(queryVector));
        var hits = await search.SearchAsync(MemoryScope.Project, "1", "apple", 3, weights);

        var expectedId = expectedWinner switch
        {
            Signal.Vector => vector,
            Signal.Keyword => keyword,
            _ => recency
        };
        Assert.Equal(expectedId, hits[0].Id);
    }

    // ── deterministic fusion ───────────────────────────────────────────────────────────
    [Fact]
    public async Task Search_RrfFusion_DeterministicTieBreakById()
    {
        using var f = new SqliteTestFactory();
        var svc = new MemoryService(f);

        // Two nodes identical in every signal: same content (same BM25), same timestamps (same
        // recency), same access count (same usefulness), neither embedded. The result must order them
        // by ascending Id and be byte-for-byte identical across repeated calls.
        var first = await CreateNode(svc, "1", "shared token alpha");
        var second = await CreateNode(svc, "1", "shared token alpha");
        var when = DateTime.UtcNow.AddHours(-10);
        await SetStats(f, first, when, when);
        await SetStats(f, second, when, when);

        var search = NewSearch(f, EmbeddingDown());
        var opts = new RetrievalOptions();

        var run1 = await search.SearchAsync(MemoryScope.Project, "1", "shared token", 10, opts);
        var run2 = await search.SearchAsync(MemoryScope.Project, "1", "shared token", 10, opts);

        Assert.Equal(new[] { first, second }, run1.Select(h => h.Id).ToArray()); // tie-break by Id asc
        Assert.Equal(run1.Select(h => h.Id), run2.Select(h => h.Id));            // stable across calls
    }

    // ── usefulness bump (read-tracking) ──────────────────────────────────────────────────
    [Fact]
    public async Task Search_BumpsUsefulnessForHits()
    {
        using var f = new SqliteTestFactory();
        var svc = new MemoryService(f);

        // Two keyword matches; k=1 ⇒ only the winner is a hit (and is bumped). Pre-seed the winner
        // with a known access count so the bump is observable against a baseline and so it wins.
        var winner = await CreateNode(svc, "1", "alpha token frequently useful");
        var loser = await CreateNode(svc, "1", "alpha token rarely seen");
        var when = DateTime.UtcNow.AddHours(-5);
        await SetStats(f, winner, when, when, accessCount: 5);
        await SetStats(f, loser, when, when, accessCount: 0);

        var search = NewSearch(f, EmbeddingDown());
        var hit = Assert.Single(await search.SearchAsync(MemoryScope.Project, "1", "alpha", 1, new RetrievalOptions()));

        Assert.Equal(winner, hit.Id);
        // The returned hit reflects PRE-bump usefulness: log(1 + 5).
        Assert.Equal(Math.Log(1 + 5), hit.UsefulnessScore, 6);

        await using var db = await f.CreateDbContextAsync();
        var winnerRow = await db.MemoryNodes.FindAsync(winner);
        var loserRow = await db.MemoryNodes.FindAsync(loser);
        Assert.Equal(6, winnerRow!.AccessCount);   // 5 -> 6 (hit was bumped)
        Assert.NotNull(winnerRow.LastAccessedAt);   // and stamped now
        Assert.Equal(0, loserRow!.AccessCount);     // a NON-hit candidate is never bumped
    }

    // ── superseded visibility ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Search_ExcludesSuperseded_ByDefault()
    {
        using var f = new SqliteTestFactory();
        var svc = new MemoryService(f);
        var id = await CreateNode(svc, "1", "findme secrettoken");
        await svc.SupersedeAsync(id, null); // retire (ValidTo stamped)

        var search = NewSearch(f, EmbeddingDown());
        var hits = await search.SearchAsync(MemoryScope.Project, "1", "secrettoken", 10, new RetrievalOptions());

        Assert.Empty(hits); // a superseded node is not a candidate by default
    }

    [Fact]
    public async Task Search_IncludesSuperseded_WhenOptionSet()
    {
        using var f = new SqliteTestFactory();
        var svc = new MemoryService(f);
        var id = await CreateNode(svc, "1", "findme secrettoken");
        await svc.SupersedeAsync(id, null);

        var search = NewSearch(f, EmbeddingDown());
        var hits = await search.SearchAsync(
            MemoryScope.Project, "1", "secrettoken", 10, new RetrievalOptions { IncludeSuperseded = true });

        // With the option set the retired node is an eligible candidate and its FTS entry still matches.
        Assert.Equal(id, Assert.Single(hits).Id);
    }

    // ── graceful degradation (no vector signal) ──────────────────────────────────────────
    [Fact]
    public async Task Search_FallsBackToKeywordRecency_WhenNoEmbeddings()
    {
        using var f = new SqliteTestFactory();
        var svc = new MemoryService(f);

        // No node is embedded AND the query embed fails (ZeroRouter down) ⇒ the vector signal is empty
        // and results fall back to keyword + recency.
        var older = await CreateNode(svc, "1", "alpha topic one");
        var newer = await CreateNode(svc, "1", "alpha topic two");
        await SetStats(f, older, DateTime.UtcNow.AddHours(-100), DateTime.UtcNow.AddHours(-100));
        await SetStats(f, newer, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(-1));

        var search = NewSearch(f, EmbeddingDown());
        var hits = await search.SearchAsync(
            MemoryScope.Project, "1", "alpha", 10, new RetrievalOptions { RecencyWeight = 100 });

        Assert.Equal(2, hits.Count);                       // still returns results
        Assert.All(hits, h => Assert.Null(h.VectorRank));  // vector signal degraded to empty
        Assert.All(hits, h => Assert.NotNull(h.KeywordRank)); // keyword signal still ranked them
        Assert.Equal(newer, hits[0].Id);                   // recency-weighted ⇒ the newer node wins
    }

    [Fact]
    public async Task Search_KeywordOnlyMatch_Ranks()
    {
        using var f = new SqliteTestFactory();
        var svc = new MemoryService(f);

        // No embeddings anywhere; the query only FTS-matches two of three nodes. The matches must
        // outrank the non-match (which has only recency + usefulness).
        var match1 = await CreateNode(svc, "1", "kiwi mango fruit");
        var match2 = await CreateNode(svc, "1", "kiwi smoothie");
        var noMatch = await CreateNode(svc, "1", "carburetor engine");
        var when = DateTime.UtcNow.AddHours(-3);
        await SetStats(f, match1, when, when);
        await SetStats(f, match2, when, when);
        await SetStats(f, noMatch, when, when);

        var search = NewSearch(f, EmbeddingDown());
        var hits = await search.SearchAsync(
            MemoryScope.Project, "1", "kiwi", 10, new RetrievalOptions { KeywordWeight = 100 });

        Assert.Equal(3, hits.Count);
        var topTwo = new[] { hits[0].Id, hits[1].Id };
        Assert.Contains(match1, topTwo);                                       // both keyword matches...
        Assert.Contains(match2, topTwo);                                       // ...rank above
        Assert.Equal(noMatch, hits[2].Id);                                     // the non-match
        Assert.NotNull(hits[0].KeywordRank);
        Assert.Null(hits.Single(h => h.Id == noMatch).KeywordRank);            // non-match has no keyword rank
    }

    // ── FTS sync across create / update / observation ─────────────────────────────────────
    [Fact]
    public async Task Search_FtsSync_CreateUpdateObservation()
    {
        using var f = new SqliteTestFactory();
        var svc = new MemoryService(f);
        var search = NewSearch(f, EmbeddingDown());
        var opts = new RetrievalOptions();

        // CreateNode ⇒ content is findable by keyword (a real keyword hit ⇒ non-null KeywordRank).
        var node = await svc.CreateNodeAsync(
            new MemoryNodeCreate(MemoryType.Semantic, MemoryScope.Project, "1", "alpha originaltoken"), null);
        var byCreate = Assert.Single(await search.SearchAsync(MemoryScope.Project, "1", "originaltoken", 10, opts));
        Assert.Equal(node.Id, byCreate.Id);
        Assert.NotNull(byCreate.KeywordRank);

        // UpdateNode ⇒ the NEW content is keyword-findable...
        var updated = await svc.UpdateNodeAsync(node.Id, new MemoryNodeUpdate(Content: "beta revisedtoken"), null);
        var byNew = Assert.Single(await search.SearchAsync(MemoryScope.Project, "1", "revisedtoken", 10, opts));
        Assert.Equal(updated.Id, byNew.Id);
        Assert.NotNull(byNew.KeywordRank);

        // ...and the superseded OLD content is NOT surfaced: the old row (node.Id) never appears, and
        // the stale token keyword-matches no current node (RRF still ranks the current row by recency,
        // but with a null KeywordRank — the old FTS entry is filtered out by the current-node join).
        var byOld = await search.SearchAsync(MemoryScope.Project, "1", "originaltoken", 10, opts);
        Assert.DoesNotContain(byOld, h => h.Id == node.Id);
        Assert.All(byOld, h => Assert.Null(h.KeywordRank));

        // AddObservation ⇒ the observation text becomes keyword-matchable on the (still-current) parent...
        await svc.AddObservationAsync(updated.Id, "obstoken extra detail", null, null);
        var byObservation = Assert.Single(await search.SearchAsync(MemoryScope.Project, "1", "obstoken", 10, opts));
        Assert.Equal(updated.Id, byObservation.Id);
        Assert.NotNull(byObservation.KeywordRank);
        // ...and the node's own content is still keyword-matchable after the observation recompute.
        var stillByContent = Assert.Single(await search.SearchAsync(MemoryScope.Project, "1", "revisedtoken", 10, opts));
        Assert.Equal(updated.Id, stillByContent.Id);
        Assert.NotNull(stillByContent.KeywordRank);
    }

    // ── empty / edge inputs ────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Search_EmptyQuery_ReturnsCleanly()
    {
        using var f = new SqliteTestFactory();
        var svc = new MemoryService(f);
        await CreateNode(svc, "1", "alpha one");
        await CreateNode(svc, "1", "beta two");

        // A blank query must NOT throw (a blank embed would) — no keyword/vector signal, just recency
        // + usefulness over the candidates.
        var search = NewSearch(f, EmbeddingDown());
        var hits = await search.SearchAsync(MemoryScope.Project, "1", "   ", 10, new RetrievalOptions());

        Assert.Equal(2, hits.Count);
        Assert.All(hits, h => Assert.Null(h.KeywordRank)); // blank ⇒ no keyword signal
        Assert.All(hits, h => Assert.Null(h.VectorRank));  // blank ⇒ no embed / vector signal
    }

    [Fact]
    public async Task Search_NoCandidates_ReturnsEmpty()
    {
        using var f = new SqliteTestFactory();
        var svc = new MemoryService(f);
        await CreateNode(svc, "1", "alpha in scope one");

        // Scope "2" has no nodes ⇒ empty candidate set ⇒ empty result (no embed / FTS work needed).
        var search = NewSearch(f, EmbeddingReturning(new[] { 1f, 0f, 0f }));
        var hits = await search.SearchAsync(MemoryScope.Project, "2", "alpha", 10, new RetrievalOptions());

        Assert.Empty(hits);
    }

    [Fact]
    public async Task Search_MalformedFtsQuery_DoesNotThrow()
    {
        using var f = new SqliteTestFactory();
        var svc = new MemoryService(f);
        var id = await CreateNode(svc, "1", "alpha bar baz content");

        // FTS5 operator characters + an unbalanced quote/paren that would be a MATCH syntax error if
        // passed raw. Sanitisation turns it into quoted OR-terms, so it returns cleanly and "bar"
        // still matches — no SqliteException.
        var search = NewSearch(f, EmbeddingDown());
        const string malformed = "\"foo* AND (bar OR NEAR/ : ^";
        var hits = await search.SearchAsync(MemoryScope.Project, "1", malformed, 10, new RetrievalOptions());

        Assert.Equal(id, Assert.Single(hits).Id);
    }

    [Fact]
    public async Task Search_NonPositiveRrfKOrHalfLife_Throws()
    {
        using var f = new SqliteTestFactory();
        var search = NewSearch(f, EmbeddingDown());

        // Degenerate fusion options are rejected up front (before any DB/embed work) so the RRF and
        // recency math can't divide by zero or produce NaN (Gemini review, PR #257).
        await Assert.ThrowsAsync<ValidationException>(
            () => search.SearchAsync(MemoryScope.Project, "1", "q", 5, new RetrievalOptions { RrfK = 0 }));
        await Assert.ThrowsAsync<ValidationException>(
            () => search.SearchAsync(MemoryScope.Project, "1", "q", 5, new RetrievalOptions { RecencyHalfLifeHours = 0 }));
    }

    /// <summary>
    /// Minimal hand-written embedding transport (no Moq) so the REAL <see cref="EmbeddingService"/>
    /// runs: <paramref name="map"/> returns the vector for an input, or <c>null</c> to throw (a downed
    /// provider, which the service maps to a fail-soft result). Mirrors the fake in EmbeddingServiceTests.
    /// </summary>
    private sealed class StubEmbeddingGenerator(Func<string, float[]?> map)
        : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            var result = new List<Embedding<float>>();
            foreach (var value in values)
            {
                var vector = map(value);
                if (vector is null)
                    throw new HttpRequestException("stub: embedding provider unreachable");
                result.Add(new Embedding<float>(vector) { ModelId = "stub-embed" });
            }

            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(result));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
