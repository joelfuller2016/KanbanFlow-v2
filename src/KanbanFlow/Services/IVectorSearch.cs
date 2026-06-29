using KanbanFlow.Models;

namespace KanbanFlow.Services;

/// <summary>
/// Semantic nearest-neighbour search over the memory store's embedding vectors (#222).
///
/// <para><b>v1 is brute-force cosine similarity computed in C#</b> (the #249 spike RETARGET of this
/// issue): the implementation iterates the <em>current</em> <see cref="MemoryNode"/> rows for a scope,
/// unpacks each <see cref="MemoryNode.EmbeddingBlob"/> via <see cref="EmbeddingCodec"/>, and ranks by
/// cosine similarity. There is deliberately NO <c>sqlite-vec</c>/<c>vec0</c> virtual table and NO
/// native SQLite extension — EF Core + <see cref="MemoryNode.EmbeddingBlob"/> is the single source of
/// truth, and the "index" is just the in-memory pass over the embedded corpus. For this single-user
/// local tool the corpus is tiny, so a full scan is sub-millisecond; a <c>vec0</c>-backed
/// implementation is a future, corpus-size-gated upgrade behind this same interface.</para>
/// </summary>
public interface IVectorSearch
{
    /// <summary>
    /// <c>true</c> when the search backend is usable. The brute-force v1 has no native dependency, so
    /// it is always available; this flag exists so #223's hybrid fusion (FTS5 + cosine + RRF) can
    /// degrade gracefully if a future native-backed implementation is unavailable.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Return the <paramref name="k"/> current memory nodes in the given scope whose embeddings are
    /// most similar to <paramref name="queryEmbedding"/>, highest cosine similarity first.
    ///
    /// <para>Only <em>current</em> nodes are considered (<see cref="MemoryNode.ValidTo"/> <c>== null</c>
    /// AND <see cref="MemoryNode.SupersededById"/> <c>== null</c>) that are scoped to
    /// <paramref name="scope"/>/<paramref name="scopeKey"/> and carry an embedding. A candidate whose
    /// stored vector has a different dimensionality than <paramref name="queryEmbedding"/> is skipped
    /// (never an error — the corpus may legitimately mix embed models/dims). An empty corpus, or one
    /// with no embeddings, yields an empty list.</para>
    /// </summary>
    /// <param name="scope">Memory scope to search within.</param>
    /// <param name="scopeKey">The scope's key (e.g. a project id as text); matched exactly (trimmed).</param>
    /// <param name="queryEmbedding">The query vector; ranked against each candidate by cosine similarity.</param>
    /// <param name="k">Maximum number of hits to return (the top-k).</param>
    /// <returns>Up to <paramref name="k"/> <see cref="VectorHit"/>s ordered by descending score.</returns>
    /// <exception cref="ValidationException"><paramref name="queryEmbedding"/> is null/empty, or <paramref name="k"/> is not positive.</exception>
    Task<IReadOnlyList<VectorHit>> SearchAsync(
        MemoryScope scope, string scopeKey, float[] queryEmbedding, int k, CancellationToken ct = default);
}

/// <summary>
/// A single search result: the matched <see cref="MemoryNode.Id"/> and its
/// <see cref="Score"/> — cosine similarity in <c>[-1, 1]</c>, where <c>1</c> is identical direction
/// and <c>0</c> is orthogonal (also used when either vector has zero magnitude).
/// </summary>
/// <param name="Id">The matched memory node id.</param>
/// <param name="Score">Cosine similarity against the query (higher = more similar).</param>
public sealed record VectorHit(int Id, double Score);
