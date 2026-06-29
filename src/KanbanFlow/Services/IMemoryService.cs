using KanbanFlow.Models;

namespace KanbanFlow.Services;

/// <summary>
/// Audit-aware mutations for the bi-temporal memory store (#221). Every REST endpoint, MCP tool,
/// and Blazor component MUST go through this service so each change produces an
/// <see cref="ActivityEvent"/> row — the same single-audit-boundary rule that #88 established for
/// tasks. Memory activity events use <c>EntityType = "MemoryNode"</c>; their <c>ProjectId</c> is the
/// project scope id when <see cref="MemoryScope.Project"/> (else <c>0</c>, since
/// <see cref="ActivityEvent"/> has no FK to <see cref="Project"/>).
///
/// Memory is never overwritten: <see cref="UpdateNodeAsync"/> and <see cref="SupersedeAsync"/>
/// retire the prior row (stamping <see cref="MemoryNode.ValidTo"/> / <see cref="MemoryNode.SupersededById"/>)
/// so the full history stays queryable. Embeddings are attached after the fact via
/// <see cref="SetEmbeddingAsync"/> (#222) and searched by <see cref="IVectorSearch"/>; retrieval/MCP
/// surfacing is #223/#224. An embedding is derived metadata, so it is written in place rather than as
/// a bi-temporal version (a content change drops the stale vector — see <see cref="UpdateNodeAsync"/>).
/// </summary>
public interface IMemoryService
{
    /// <summary>
    /// Create a new, currently-valid memory node. Validates content (non-empty) and the
    /// type/scope enums. Emits a <c>MemoryCreated</c> event.
    /// </summary>
    /// <exception cref="ValidationException">Content empty/whitespace, or type/scope undefined.</exception>
    Task<MemoryNode> CreateNodeAsync(MemoryNodeCreate dto, string? actor, CancellationToken ct = default);

    /// <summary>
    /// Read a single node, or <c>null</c> when it does not exist. As a side effect of the read,
    /// bumps <see cref="MemoryNode.AccessCount"/> and <see cref="MemoryNode.LastAccessedAt"/> — a
    /// read is NOT a mutation, so no audit event is written.
    /// </summary>
    Task<MemoryNode?> GetNodeAsync(int id, CancellationToken ct = default);

    /// <summary>
    /// List nodes newest-first with optional filters. <paramref name="scopeKey"/> is an exact match.
    /// When <paramref name="includeSuperseded"/> is <c>false</c> (default), retired versions are
    /// excluded (any row with <see cref="MemoryNode.ValidTo"/> set OR
    /// <see cref="MemoryNode.SupersededById"/> set).
    /// </summary>
    Task<IReadOnlyList<MemoryNode>> ListAsync(
        MemoryScope? scope = null, string? scopeKey = null, MemoryType? type = null,
        bool includeSuperseded = false, CancellationToken ct = default);

    /// <summary>
    /// Bi-temporal update: does NOT overwrite. Inserts a new version with the updated content and
    /// stamps the prior row's <see cref="MemoryNode.ValidTo"/> + <see cref="MemoryNode.SupersededById"/>
    /// (pointing at the new row), so the prior version stays intact and queryable. When <c>Content</c>
    /// changes the new row starts unembedded (the stale vector is dropped so #222/#223 re-embeds); a
    /// metadata-only edit carries the prior vector forward. <c>null</c> fields on
    /// <paramref name="dto"/> mean "carry forward unchanged"; idempotent when nothing actually changes
    /// (no new row, no event). Emits a <c>MemoryUpdated</c> event.
    /// </summary>
    /// <exception cref="MemoryNodeNotFoundException">The node does not exist.</exception>
    /// <exception cref="ValidationException">Content provided but empty/whitespace, or the target row is already superseded.</exception>
    Task<MemoryNode> UpdateNodeAsync(int id, MemoryNodeUpdate dto, string? actor, CancellationToken ct = default);

    /// <summary>
    /// Soft-supersede (retire) a node by stamping <see cref="MemoryNode.ValidTo"/> with no
    /// replacement. The row remains intact and queryable (with <c>includeSuperseded: true</c>).
    /// Idempotent: a node already retired is a no-op (no event). Emits a <c>MemorySuperseded</c> event.
    /// </summary>
    /// <exception cref="MemoryNodeNotFoundException">The node does not exist.</exception>
    Task<MemoryNode> SupersedeAsync(int id, string? actor, CancellationToken ct = default);

    /// <summary>
    /// Delete a node. By charter this is a <em>soft</em>-supersede (memory stays queryable, never a
    /// hard delete) — equivalent to <see cref="SupersedeAsync"/>. Emits a <c>MemorySuperseded</c> event.
    /// </summary>
    /// <exception cref="MemoryNodeNotFoundException">The node does not exist.</exception>
    Task<MemoryNode> DeleteNodeAsync(int id, string? actor, CancellationToken ct = default);

    /// <summary>
    /// Append an observation to a node (additive detail; does not supersede the node).
    /// Emits an <c>ObservationAdded</c> event. Validates non-empty content.
    /// </summary>
    /// <exception cref="MemoryNodeNotFoundException">The parent node does not exist.</exception>
    /// <exception cref="ValidationException">Content empty/whitespace.</exception>
    Task<Observation> AddObservationAsync(int nodeId, string content, string? source, string? actor, CancellationToken ct = default);

    /// <summary>
    /// Add a directed graph edge <c>(fromType, fromId) --relationType--&gt; (toType, toId)</c>. The
    /// endpoints are discriminator pairs (no hard FK), so an edge may span a memory node and an
    /// existing card id (Task / Project / …) — but at least one endpoint MUST be a <c>"MemoryNode"</c>.
    /// Emits a <c>RelationAdded</c> event attributed to that MemoryNode endpoint (its id + the project
    /// scope resolved from it). Validates non-empty type discriminators and relation verb.
    /// </summary>
    /// <exception cref="ValidationException"><paramref name="fromType"/>, <paramref name="relationType"/>, or <paramref name="toType"/> empty/whitespace, or neither endpoint is a <c>"MemoryNode"</c>.</exception>
    /// <exception cref="MemoryNodeNotFoundException">The chosen MemoryNode endpoint does not exist.</exception>
    Task<MemoryRelation> AddRelationAsync(
        string fromType, int fromId, string relationType, string toType, int toId,
        string? actor, CancellationToken ct = default);

    /// <summary>
    /// Attach (or replace) the semantic embedding for a node's <see cref="MemoryNode.Content"/>,
    /// updating <see cref="MemoryNode.EmbeddingBlob"/> (packed via <see cref="EmbeddingCodec"/>) and
    /// <see cref="MemoryNode.EmbeddingDim"/> <em>in place</em>. Unlike a content edit this is NOT a
    /// bi-temporal supersession — an embedding is derived metadata, so the current row is updated
    /// directly and no new version is created. Used by the reindex path (#222) and the retrieval
    /// pipeline (#223). Emits exactly one <c>MemoryEmbedded</c> event. Only a <em>current</em> node can
    /// be embedded: a retired (<see cref="MemoryNode.ValidTo"/>) or superseded
    /// (<see cref="MemoryNode.SupersededById"/>) row is historical and rejected.
    /// </summary>
    /// <exception cref="MemoryNodeNotFoundException">The node does not exist.</exception>
    /// <exception cref="ValidationException">The node is retired/superseded, or <paramref name="vector"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="vector"/> is <c>null</c>.</exception>
    Task<MemoryNode> SetEmbeddingAsync(int id, float[] vector, string? actor, CancellationToken ct = default);
}

/// <summary>Create payload for <see cref="IMemoryService.CreateNodeAsync"/>.</summary>
public sealed record MemoryNodeCreate(
    MemoryType Type,
    MemoryScope Scope,
    string ScopeKey,
    string Content,
    string? Tags = null,
    string? Source = null,
    double? Confidence = null);

/// <summary>
/// Update payload for <see cref="IMemoryService.UpdateNodeAsync"/>. <c>null</c> fields carry forward
/// unchanged. (Type/Scope/ScopeKey are immutable — they define the node's placement; create a new
/// node to re-scope.)
/// </summary>
public sealed record MemoryNodeUpdate(
    string? Content = null,
    string? Tags = null,
    string? Source = null,
    double? Confidence = null);
