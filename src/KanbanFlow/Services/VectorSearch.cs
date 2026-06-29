using KanbanFlow.Data;
using KanbanFlow.Models;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Services;

/// <summary>
/// Default <see cref="IVectorSearch"/>: a brute-force cosine-similarity scan over the
/// <em>current</em> <see cref="MemoryNode"/> rows for a scope (#222, retargeted by the #249 spike).
///
/// <para>There is intentionally NO <c>sqlite-vec</c>/<c>vec0</c> virtual table, native extension, or
/// <c>LoadExtension</c> here: EF Core + <see cref="MemoryNode.EmbeddingBlob"/> is the source of truth.
/// Each search materialises the candidate <c>(Id, EmbeddingBlob)</c> pairs (the cosine math cannot be
/// translated to SQL), unpacks every blob via <see cref="EmbeddingCodec"/>, and ranks by cosine. For
/// this single-user local tool the corpus is tiny, so the full scan is sub-millisecond. A
/// <c>vec0</c>-backed implementation behind this same interface is a future, corpus-size-gated
/// upgrade. Uses <see cref="IDbContextFactory{AppDbContext}"/> for the same per-call short-lived
/// context model as <see cref="MemoryService"/>.</para>
/// </summary>
public sealed class VectorSearch(IDbContextFactory<AppDbContext> dbFactory) : IVectorSearch
{
    /// <inheritdoc />
    /// <remarks>Brute-force cosine has no native dependency, so it is always available.</remarks>
    public bool IsAvailable => true;

    /// <inheritdoc />
    public async Task<IReadOnlyList<VectorHit>> SearchAsync(
        MemoryScope scope, string scopeKey, float[] queryEmbedding, int k, CancellationToken ct = default)
    {
        // A null/empty query has no direction to compare against, and a non-positive k has no
        // meaningful top-k — reject both up front (clean ValidationException, like the rest of the
        // service layer) rather than silently returning nothing.
        if (queryEmbedding is null || queryEmbedding.Length == 0)
            throw new ValidationException("Query embedding cannot be null or empty.");
        if (k <= 0)
            throw new ValidationException("k (result count) must be greater than zero.");

        // Match the trimmed form stored by MemoryService (ScopeKey is persisted trimmed); a null key
        // matches nothing (ScopeKey is NOT NULL), yielding an empty result rather than throwing.
        var key = scopeKey?.Trim();

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Current, in-scope, embedded candidates only. Project to (Id, EmbeddingBlob): cosine cannot
        // run in SQL, so we materialise the minimal columns and rank in C#.
        var candidates = await db.MemoryNodes
            .AsNoTracking()
            .Where(m => m.ValidTo == null && m.SupersededById == null
                && m.Scope == scope && m.ScopeKey == key
                && m.EmbeddingBlob != null)
            .Select(m => new { m.Id, m.EmbeddingBlob })
            .ToListAsync(ct);

        var hits = new List<VectorHit>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var blob = candidate.EmbeddingBlob;
            if (blob is null)
                continue; // defensive: the WHERE already excludes nulls (also narrows the nullable).

            var vector = EmbeddingCodec.Unpack(blob);

            // A candidate embedded under a different model/dimensionality cannot be compared — skip it
            // (the corpus may legitimately be mixed), never throw.
            if (vector.Length != queryEmbedding.Length)
                continue;

            hits.Add(new VectorHit(candidate.Id, CosineSimilarity(queryEmbedding, vector)));
        }

        return hits
            .OrderByDescending(h => h.Score)
            .Take(k)
            .ToList();
    }

    /// <summary>
    /// Cosine similarity of two equal-length vectors: <c>dot / (‖a‖ · ‖b‖)</c>. Returns <c>0</c> when
    /// either vector has zero magnitude (a zero/empty embedding) so there is never a divide-by-zero.
    /// Accumulates in <see cref="double"/> for stability. Callers guarantee <c>a.Length == b.Length</c>.
    /// </summary>
    private static double CosineSimilarity(float[] a, float[] b)
    {
        double dot = 0, sumA = 0, sumB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            double x = a[i];
            double y = b[i];
            dot += x * y;
            sumA += x * x;
            sumB += y * y;
        }

        var normA = Math.Sqrt(sumA);
        var normB = Math.Sqrt(sumB);
        if (normA == 0 || normB == 0)
            return 0;

        // Clamp to the documented VectorHit.Score range: FP rounding can nudge the quotient a
        // hair outside [-1, 1] (e.g. 1.0000000000000002) (Gemini review, PR #256).
        return Math.Clamp(dot / (normA * normB), -1.0, 1.0);
    }
}
