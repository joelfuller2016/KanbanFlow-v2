using KanbanFlow.Models;

namespace KanbanFlow.Services;

/// <summary>
/// Hybrid recall over the memory store (#223): a single ranked retrieval that fuses four signals with
/// <b>Reciprocal Rank Fusion</b> (RRF) — keyword relevance (FTS5 BM25), semantic similarity
/// (brute-force cosine via <see cref="IVectorSearch"/>, #222), recency, and usefulness (access
/// count). RRF is rank-based, so the wildly different score scales of the signals (a negative BM25, a
/// <c>[-1,1]</c> cosine, an exponential recency decay, a log access count) never need cross-signal
/// normalization: each signal contributes <c>weight / (k + rank)</c> and the sums are comparable.
///
/// <para>There is deliberately NO <c>sqlite-vec</c>/<c>vec0</c> and NO Semantic Kernel connector — the
/// keyword index is SQLite's built-in FTS5 and the vector signal is the existing in-C# cosine scan
/// (#249 spike retarget). The pipeline degrades gracefully: if ZeroRouter is down (the query cannot be
/// embedded) or nothing in scope is embedded, the vector signal is simply empty and results fall back
/// to keyword + recency + usefulness.</para>
///
/// <para>Mutations still flow through <see cref="IMemoryService"/>; this is a read surface. As a read
/// side effect it bumps <see cref="MemoryNode.AccessCount"/> / <see cref="MemoryNode.LastAccessedAt"/>
/// for the returned hits (un-audited read-tracking, exactly like <see cref="IMemoryService.GetNodeAsync"/>).</para>
/// </summary>
public interface IMemorySearch
{
    /// <summary>
    /// Return the top-<paramref name="k"/> current memory nodes in <paramref name="scope"/>/
    /// <paramref name="scopeKey"/> for <paramref name="query"/>, best first, fused via RRF.
    ///
    /// <para>Honors <see cref="RetrievalOptions.IncludeSuperseded"/>; a <c>null</c>
    /// <paramref name="opts"/> uses the baked-in defaults. An empty/blank <paramref name="query"/>
    /// returns candidates ranked by recency + usefulness only (no keyword/vector signal, never a
    /// throw). When no node exists in scope the result is empty. A malformed query never throws — it
    /// is sanitized for FTS5 and the keyword signal degrades to empty.</para>
    ///
    /// <para>The returned <see cref="MemoryHit"/> scores reflect the state <em>before</em> the
    /// access-stat bump that this call applies to the hits.</para>
    /// </summary>
    /// <param name="scope">Memory scope to search within.</param>
    /// <param name="scopeKey">The scope's key (matched exactly, trimmed).</param>
    /// <param name="query">Free-text query; drives both the keyword and the (embedded) vector signal.</param>
    /// <param name="k">Maximum number of hits to return.</param>
    /// <param name="opts">Signal weights + tuning; <c>null</c> ⇒ defaults.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ValidationException"><paramref name="k"/> is not positive.</exception>
    Task<IReadOnlyList<MemoryHit>> SearchAsync(
        MemoryScope scope, string scopeKey, string query, int k, RetrievalOptions? opts, CancellationToken ct = default);
}

/// <summary>
/// One fused retrieval result. <see cref="FusedScore"/> is the RRF sum (higher = better); the
/// per-signal fields make a result explainable. <see cref="VectorRank"/> and <see cref="KeywordRank"/>
/// are 1-based ranks within those signals or <c>null</c> when the hit did not appear in that signal
/// (e.g. unembedded ⇒ no vector rank; no keyword match ⇒ no keyword rank). <see cref="RecencyScore"/>
/// and <see cref="UsefulnessScore"/> are the raw per-signal scores (always present).
/// </summary>
/// <param name="Id">The matched memory node id.</param>
/// <param name="Content">The node's content (so callers need not re-fetch).</param>
/// <param name="FusedScore">The RRF-fused score (higher = better).</param>
/// <param name="VectorRank">1-based rank in the vector signal, or <c>null</c> if absent.</param>
/// <param name="KeywordRank">1-based rank in the keyword (BM25) signal, or <c>null</c> if absent.</param>
/// <param name="RecencyScore">Raw recency score (exponential decay, higher = more recent).</param>
/// <param name="UsefulnessScore">Raw usefulness score (<c>log(1 + AccessCount)</c>, higher = more used).</param>
public sealed record MemoryHit(
    int Id,
    string Content,
    double FusedScore,
    int? VectorRank,
    int? KeywordRank,
    double RecencyScore,
    double UsefulnessScore);

/// <summary>
/// Tuning for <see cref="IMemorySearch.SearchAsync"/>. All weights default to <c>1</c> (equal
/// blend); raise one to bias toward that signal. Defaults are baked in so a <c>null</c>
/// <see cref="RetrievalOptions"/> is the standard hybrid blend — config binding is optional.
/// </summary>
public sealed class RetrievalOptions
{
    /// <summary>Weight of the semantic (cosine) signal in the RRF sum.</summary>
    public double VectorWeight { get; init; } = 1;

    /// <summary>Weight of the keyword (FTS5 BM25) signal in the RRF sum.</summary>
    public double KeywordWeight { get; init; } = 1;

    /// <summary>Weight of the recency signal in the RRF sum.</summary>
    public double RecencyWeight { get; init; } = 1;

    /// <summary>Weight of the usefulness (access-count) signal in the RRF sum.</summary>
    public double UsefulnessWeight { get; init; } = 1;

    /// <summary>
    /// The RRF constant <c>k</c> in <c>1 / (k + rank)</c>. The canonical value is 60; a larger value
    /// flattens the contribution of top ranks (less aggressive), a smaller one sharpens it.
    /// </summary>
    public int RrfK { get; init; } = 60;

    /// <summary>
    /// Recency decay time-constant (hours) in <c>exp(-ageHours / RecencyHalfLifeHours)</c>, measured
    /// from <see cref="MemoryNode.LastAccessedAt"/> (falling back to <see cref="MemoryNode.CreatedAt"/>).
    /// Default 168h (one week).
    /// </summary>
    public double RecencyHalfLifeHours { get; init; } = 168;

    /// <summary>
    /// When <c>true</c>, retired/superseded versions are eligible candidates (their historical content
    /// stays FTS-matchable). Default <c>false</c> — only current nodes are recalled.
    /// </summary>
    /// <remarks>
    /// Known limitation: this currently affects the keyword, recency, and usefulness signals only. The
    /// vector signal delegates to <see cref="IVectorSearch"/>, which ranks CURRENT versions only, so a
    /// superseded candidate never receives a vector rank (it can still surface via the other signals).
    /// Full superseded-aware vector ranking is a tracked follow-up (#258).
    /// </remarks>
    public bool IncludeSuperseded { get; init; }
}
