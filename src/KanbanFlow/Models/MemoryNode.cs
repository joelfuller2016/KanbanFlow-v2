namespace KanbanFlow.Models;

/// <summary>
/// A unit of durable agent memory (#221). A node is the smallest recallable fact,
/// decision, or procedure the system remembers about a project, feature, agent, or
/// session. Memory is <em>bi-temporal</em>: a node is never overwritten in place —
/// an update <see cref="MemoryScope"/>d edit retires the old row (stamping
/// <see cref="ValidTo"/> + <see cref="SupersededById"/>) and inserts a fresh one, so
/// the full history stays queryable. The <see cref="EmbeddingBlob"/> column is a
/// nullable packed-float vector filled in later by the embedding pipeline (#222/#223);
/// it stays <c>null</c> until then. Mutations flow through <c>MemoryService</c>, which
/// writes an <see cref="ActivityEvent"/> for every change (the #88 single-audit-boundary rule).
/// </summary>
public class MemoryNode
{
    public int Id { get; set; }

    /// <summary>Kind of memory (stored as <c>int</c> — same enum-as-int convention as <see cref="IdeaStatus"/>).</summary>
    public MemoryType Type { get; set; } = MemoryType.Episodic;

    /// <summary>What the memory is scoped to (project / feature / agent / session).</summary>
    public MemoryScope Scope { get; set; } = MemoryScope.Project;

    /// <summary>
    /// The scope's key — e.g. a project id (as text) when <see cref="Scope"/> is
    /// <see cref="MemoryScope.Project"/>, a feature id, an agent name, or a session GUID.
    /// Free-form text so a single column serves every scope.
    /// </summary>
    public required string ScopeKey { get; set; }

    /// <summary>The remembered content (the fact / decision / procedure). Never empty.</summary>
    public required string Content { get; set; }

    /// <summary>Comma-separated freeform tags for grouping/recall (LIKE-searchable, like <see cref="Idea.Tags"/>).</summary>
    public string? Tags { get; set; }

    /// <summary>Optional provenance (who/what produced this memory, e.g. an agent name or tool).</summary>
    public string? Source { get; set; }

    /// <summary>Optional confidence in [0,1] (or any caller scale); <c>null</c> when unknown.</summary>
    public double? Confidence { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // ── bi-temporal validity ─────────────────────────────────────────────────────
    /// <summary>Start of this version's validity window (defaults to creation time).</summary>
    public DateTime ValidFrom { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// End of this version's validity window. <c>null</c> means "currently valid". Set when
    /// the node is superseded (by an update) or soft-deleted, so prior versions remain queryable.
    /// </summary>
    public DateTime? ValidTo { get; set; }

    /// <summary>
    /// When an update supersedes this row, points at the id of the new version. <c>null</c>
    /// for a currently-valid node or a node retired without a replacement (a plain supersede).
    /// Kept as a plain scalar (no hard self-FK) to avoid cascade surprises.
    /// </summary>
    public int? SupersededById { get; set; }

    // ── access tracking ──────────────────────────────────────────────────────────
    /// <summary>Number of times this node has been read via <c>MemoryService.GetNodeAsync</c>.</summary>
    public int AccessCount { get; set; }

    /// <summary>Last time this node was read; <c>null</c> until first access.</summary>
    public DateTime? LastAccessedAt { get; set; }

    // ── embedding (filled by #222/#223) ──────────────────────────────────────────
    /// <summary>
    /// The semantic embedding for <see cref="Content"/>, packed as float32 little-endian via
    /// <see cref="EmbeddingCodec"/>. <c>null</c> until the embedding pipeline (#222/#223) fills it.
    /// Cleared only on a <em>content-changing</em> bi-temporal update (a metadata-only edit carries the
    /// prior vector forward), so a stale vector is never reused for changed content.
    /// </summary>
    public byte[]? EmbeddingBlob { get; set; }

    /// <summary>Dimensionality of <see cref="EmbeddingBlob"/> (e.g. 1536); <c>null</c> when unembedded.</summary>
    public int? EmbeddingDim { get; set; }
}

/// <summary>Kind of memory. Stored as <c>int</c> (no <c>JsonStringEnumConverter</c> in this repo).</summary>
public enum MemoryType
{
    /// <summary>A specific event or experience ("on 6/28 the build failed because…").</summary>
    Episodic,

    /// <summary>A general fact or concept ("the API base path is /api").</summary>
    Semantic,

    /// <summary>A how-to / procedure ("to release, run scripts/verify.ps1 then…").</summary>
    Procedural,

    /// <summary>A decision and its rationale ("chose SQLite over Postgres because…").</summary>
    Decision
}

/// <summary>What a memory node is scoped to. Stored as <c>int</c>.</summary>
public enum MemoryScope
{
    Project,
    Feature,
    Agent,
    Session
}
