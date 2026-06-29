using System.Text.Json;
using KanbanFlow.Data;
using KanbanFlow.Models;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Services;

/// <summary>
/// Default <see cref="IMemoryService"/> implementation. Uses
/// <see cref="IDbContextFactory{AppDbContext}"/> so each call gets its own short-lived
/// <see cref="AppDbContext"/> — safe for concurrent Blazor Server circuits and Minimal API
/// requests alike. Mirrors the audit conventions of <see cref="IdeaService"/> (#180/#88): every
/// mutation writes exactly one <see cref="ActivityEvent"/>, and create/supersede happen inside a
/// transaction so a row never lands without its audit event.
/// </summary>
public sealed class MemoryService(IDbContextFactory<AppDbContext> dbFactory) : IMemoryService
{
    private static readonly JsonSerializerOptions ActivityJsonOptions = new(JsonSerializerDefaults.Web);

    private static object MemoryActivitySnapshot(MemoryNode m) => new
    {
        m.Id,
        Type = m.Type.ToString(),
        Scope = m.Scope.ToString(),
        m.ScopeKey,
        m.Content,
        m.Tags,
        m.Source,
        m.Confidence,
        m.CreatedAt,
        m.ValidFrom,
        m.ValidTo,
        m.SupersededById,
        m.AccessCount,
        m.LastAccessedAt,
        // Never serialize raw vector bytes into the audit JSON — record presence + shape only.
        HasEmbedding = m.EmbeddingBlob is not null,
        m.EmbeddingDim
    };

    private static void AddActivityEvent(
        AppDbContext db, int projectId, string entityType, int entityId,
        string? actor, string eventType, object? before, object? after, string? message)
    {
        db.ActivityEvents.Add(new ActivityEvent
        {
            ProjectId = projectId,
            EntityType = entityType,
            EntityId = entityId,
            Actor = string.IsNullOrWhiteSpace(actor) ? null : actor.Trim(),
            EventType = eventType,
            BeforeJson = before is null ? null : JsonSerializer.Serialize(before, ActivityJsonOptions),
            AfterJson = after is null ? null : JsonSerializer.Serialize(after, ActivityJsonOptions),
            Message = message
        });
    }

    public async Task<MemoryNode> CreateNodeAsync(MemoryNodeCreate dto, string? actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dto);

        // Service is the trust boundary — REST, MCP, and Blazor callers all flow through here (#88).
        if (string.IsNullOrWhiteSpace(dto.Content))
            throw new ValidationException("Content cannot be empty or whitespace.");
        // ScopeKey identifies the node's placement (and resolves its project id for the audit event).
        // A blank key would store an unscoped row and misattribute the audit to ProjectId 0 — reject it.
        if (string.IsNullOrWhiteSpace(dto.ScopeKey))
            throw new ValidationException("ScopeKey cannot be empty or whitespace.");
        if (!Enum.IsDefined(dto.Type))
            throw new ValidationException($"Invalid memory type '{dto.Type}'.");
        if (!Enum.IsDefined(dto.Scope))
            throw new ValidationException($"Invalid memory scope '{dto.Scope}'.");
        if (dto.Scope == MemoryScope.Project
            && (!int.TryParse(dto.ScopeKey.Trim(), out var projectId) || projectId <= 0))
            throw new ValidationException("Project-scoped memories require a positive integer project id as ScopeKey.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var now = DateTime.UtcNow;
        var node = new MemoryNode
        {
            Type = dto.Type,
            Scope = dto.Scope,
            ScopeKey = dto.ScopeKey.Trim(),
            Content = dto.Content.Trim(),
            Tags = Normalize(dto.Tags),
            Source = Normalize(dto.Source),
            Confidence = dto.Confidence,
            CreatedAt = now,
            ValidFrom = now
        };

        // Atomic create + audit: first save assigns node.Id (needed by the activity event),
        // second persists the audit row. One transaction so a failure can't leave a node
        // without its MemoryCreated event.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        db.MemoryNodes.Add(node);
        await db.SaveChangesAsync(ct);

        AddActivityEvent(
            db, MemoryProjectId(node), "MemoryNode", node.Id, actor,
            "MemoryCreated", null, MemoryActivitySnapshot(node),
            $"Memory node {node.Id} created");

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return node;
    }

    public async Task<MemoryNode?> GetNodeAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // Tracked (not AsNoTracking) so the access-stat bump below persists.
        var node = await db.MemoryNodes.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (node is null)
            return null;

        // A read bumps access stats but is NOT a mutation, so no audit event is written.
        node.AccessCount++;
        node.LastAccessedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return node;
    }

    public async Task<IReadOnlyList<MemoryNode>> ListAsync(
        MemoryScope? scope = null, string? scopeKey = null, MemoryType? type = null,
        bool includeSuperseded = false, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var query = db.MemoryNodes.AsNoTracking().AsQueryable();

        if (scope is not null)
            query = query.Where(m => m.Scope == scope);
        if (!string.IsNullOrWhiteSpace(scopeKey))
        {
            var key = scopeKey.Trim();
            query = query.Where(m => m.ScopeKey == key);
        }
        if (type is not null)
            query = query.Where(m => m.Type == type);
        if (!includeSuperseded)
            // A node is "current" only while it has not been retired (ValidTo) and has not been
            // replaced by a newer version (SupersededById). Either stamp excludes it.
            query = query.Where(m => m.ValidTo == null && m.SupersededById == null);

        return await query
            .OrderByDescending(m => m.CreatedAt)
            .ThenByDescending(m => m.Id)
            .ToListAsync(ct);
    }

    public async Task<MemoryNode> UpdateNodeAsync(int id, MemoryNodeUpdate dto, string? actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dto);

        if (dto.Content is not null && string.IsNullOrWhiteSpace(dto.Content))
            throw new ValidationException("Content cannot be empty or whitespace.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var current = await db.MemoryNodes.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (current is null)
            throw new MemoryNodeNotFoundException(id);
        if (current.ValidTo is not null || current.SupersededById is not null)
            throw new ValidationException($"Memory node {id} is already superseded; update its current version instead.");

        // Resolve new values (null dto fields carry the current value forward).
        var newContent = dto.Content is null ? current.Content : dto.Content.Trim();
        var newTags = dto.Tags is null ? current.Tags : Normalize(dto.Tags);
        var newSource = dto.Source is null ? current.Source : Normalize(dto.Source);
        var newConfidence = dto.Confidence ?? current.Confidence;

        // Idempotent: nothing actually changed ⇒ no supersession, no audit noise.
        if (newContent == current.Content && newTags == current.Tags
            && newSource == current.Source && newConfidence == current.Confidence)
            return current;

        var now = DateTime.UtcNow;
        var before = MemoryActivitySnapshot(current);

        // The embedding is derived from Content, so only a real content change invalidates it. A null
        // dto.Content carries the value forward (never a change); a metadata-only edit (Tags/Source/
        // Confidence) keeps Content — and therefore the prior vector — intact.
        var contentChanged = dto.Content is not null && newContent != current.Content;

        var replacement = new MemoryNode
        {
            Type = current.Type,
            Scope = current.Scope,
            ScopeKey = current.ScopeKey,
            Content = newContent,
            Tags = newTags,
            Source = newSource,
            Confidence = newConfidence,
            CreatedAt = now,
            ValidFrom = now,
            // Content changed ⇒ the prior embedding is stale: leave the new row unembedded so the
            // pipeline (#222/#223) re-embeds it. Metadata-only change ⇒ carry the prior vector forward
            // unchanged (it still matches the unchanged content).
            EmbeddingBlob = contentChanged ? null : current.EmbeddingBlob,
            EmbeddingDim = contentChanged ? null : current.EmbeddingDim
        };

        // Atomic supersede: insert the new version (assigns its id), then stamp the prior row so it
        // stays intact and queryable. One transaction so we never leave two live versions or a
        // dangling SupersededById pointer.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        db.MemoryNodes.Add(replacement);
        await db.SaveChangesAsync(ct);

        current.ValidTo = now;
        current.SupersededById = replacement.Id;

        AddActivityEvent(
            db, MemoryProjectId(replacement), "MemoryNode", replacement.Id, actor,
            "MemoryUpdated", before, MemoryActivitySnapshot(replacement),
            $"Memory {current.Id} superseded by {replacement.Id}");

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return replacement;
    }

    public async Task<MemoryNode> SupersedeAsync(int id, string? actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var node = await db.MemoryNodes.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (node is null)
            throw new MemoryNodeNotFoundException(id);

        // Idempotent: a node already retired is a no-op (no audit noise).
        if (node.ValidTo is not null)
            return node;

        var before = MemoryActivitySnapshot(node);
        node.ValidTo = DateTime.UtcNow;
        // No replacement row, so SupersededById stays null — this distinguishes a plain retire
        // from an update-driven supersession (which points SupersededById at the new version).

        AddActivityEvent(
            db, MemoryProjectId(node), "MemoryNode", node.Id, actor,
            "MemorySuperseded", before, MemoryActivitySnapshot(node),
            $"Memory {node.Id} superseded");

        await db.SaveChangesAsync(ct);
        return node;
    }

    /// <summary>
    /// Delete is a soft-supersede by charter — prior memory stays queryable (never a hard delete),
    /// so this is exactly <see cref="SupersedeAsync"/>.
    /// </summary>
    public Task<MemoryNode> DeleteNodeAsync(int id, string? actor, CancellationToken ct = default)
        => SupersedeAsync(id, actor, ct);

    public async Task<Observation> AddObservationAsync(int nodeId, string content, string? source, string? actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new ValidationException("Observation content cannot be empty or whitespace.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var node = await db.MemoryNodes.FirstOrDefaultAsync(m => m.Id == nodeId, ct);
        if (node is null)
            throw new MemoryNodeNotFoundException(nodeId);
        if (node.ValidTo is not null || node.SupersededById is not null)
            throw new ValidationException($"Memory node {nodeId} is superseded; add observations to the current version.");

        var observation = new Observation
        {
            MemoryNodeId = nodeId,
            Content = content.Trim(),
            Source = Normalize(source),
            CreatedAt = DateTime.UtcNow
        };

        // Atomic add + audit (first save assigns observation.Id for the event payload).
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        db.Observations.Add(observation);
        await db.SaveChangesAsync(ct);

        AddActivityEvent(
            db, MemoryProjectId(node), "MemoryNode", node.Id, actor,
            "ObservationAdded", null,
            new { observation.Id, observation.MemoryNodeId, observation.Content, observation.Source, observation.CreatedAt },
            $"Observation {observation.Id} added to memory {node.Id}");

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return observation;
    }

    public async Task<MemoryRelation> AddRelationAsync(
        string fromType, int fromId, string relationType, string toType, int toId,
        string? actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fromType))
            throw new ValidationException("Relation fromType cannot be empty or whitespace.");
        if (string.IsNullOrWhiteSpace(relationType))
            throw new ValidationException("Relation type cannot be empty or whitespace.");
        if (string.IsNullOrWhiteSpace(toType))
            throw new ValidationException("Relation toType cannot be empty or whitespace.");
        if (fromId <= 0 || toId <= 0)
            throw new ValidationException("Relation endpoint ids must be positive.");

        var fromTypeTrimmed = fromType.Trim();
        var toTypeTrimmed = toType.Trim();

        // A memory relation must touch a memory node: the audit event is attributed to that node
        // (EntityType "MemoryNode" + the node's project scope), so an edge with no memory endpoint has
        // nothing valid to attribute to. Require at least one side to be a MemoryNode.
        var fromIsNode = string.Equals(fromTypeTrimmed, "MemoryNode", StringComparison.Ordinal);
        var toIsNode = string.Equals(toTypeTrimmed, "MemoryNode", StringComparison.Ordinal);
        if (!fromIsNode && !toIsNode)
            throw new ValidationException("A memory relation must have a MemoryNode endpoint (FromType or ToType must be \"MemoryNode\").");

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // The audited node: prefer From when it is the MemoryNode, else To. Load it so we can resolve
        // its project scope and reject a dangling memory id (consistent with GetNodeAsync's lookup).
        var memoryNodeId = fromIsNode ? fromId : toId;
        var memoryNode = await db.MemoryNodes.FirstOrDefaultAsync(m => m.Id == memoryNodeId, ct);
        if (memoryNode is null)
            throw new MemoryNodeNotFoundException(memoryNodeId);
        if (memoryNode.ValidTo is not null || memoryNode.SupersededById is not null)
            throw new ValidationException($"Memory node {memoryNode.Id} is superseded; add relations to the current version.");

        var relation = new MemoryRelation
        {
            FromType = fromTypeTrimmed,
            FromId = fromId,
            RelationType = relationType.Trim(),
            ToType = toTypeTrimmed,
            ToId = toId,
            CreatedAt = DateTime.UtcNow
        };

        // Atomic add + audit (first save assigns relation.Id for the event payload).
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        db.MemoryRelations.Add(relation);
        await db.SaveChangesAsync(ct);

        // Attributed to the MemoryNode endpoint (#221): EntityType "MemoryNode", EntityId the node's id,
        // and ProjectId resolved from THAT node's scope (the same helper CreateNodeAsync uses) — so a
        // project-scoped node attributes to its project rather than 0.
        AddActivityEvent(
            db, MemoryProjectId(memoryNode), "MemoryNode", memoryNode.Id, actor,
            "RelationAdded", null,
            new { relation.Id, relation.FromType, relation.FromId, relation.RelationType, relation.ToType, relation.ToId, relation.CreatedAt },
            $"Relation '{relation.RelationType}' {relation.FromType}:{relation.FromId} -> {relation.ToType}:{relation.ToId}");

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return relation;
    }

    public async Task<MemoryNode> SetEmbeddingAsync(int id, float[] vector, string? actor, CancellationToken ct = default)
    {
        // A null vector is a caller bug (typed float[] contract); an empty vector is invalid input.
        // Same split CreateNodeAsync uses: ArgumentNullException for null, ValidationException for empty.
        ArgumentNullException.ThrowIfNull(vector);
        if (vector.Length == 0)
            throw new ValidationException("Embedding vector cannot be empty.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var node = await db.MemoryNodes.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (node is null)
            throw new MemoryNodeNotFoundException(id);
        // Embeddings attach only to the live row — a retired/superseded version is historical (mirrors
        // AddObservation's guard). Re-embed the current version instead.
        if (node.ValidTo is not null || node.SupersededById is not null)
            throw new ValidationException($"Memory node {id} is superseded; set embeddings on the current version.");

        var before = MemoryActivitySnapshot(node);

        // Derived metadata — written in place (NOT a bi-temporal supersession). Pack via the shared
        // codec so the layout matches what VectorSearch.Unpack reads back (#222).
        node.EmbeddingBlob = EmbeddingCodec.Pack(vector);
        node.EmbeddingDim = vector.Length;

        // One MemoryEmbedded event in the same SaveChanges (atomic, like SupersedeAsync) — the #88
        // single-audit-boundary rule the memory store inherits.
        AddActivityEvent(
            db, MemoryProjectId(node), "MemoryNode", node.Id, actor,
            "MemoryEmbedded", before, MemoryActivitySnapshot(node),
            $"Memory {node.Id} embedded ({vector.Length}-dim)");

        await db.SaveChangesAsync(ct);
        return node;
    }

    /// <summary>Trim to null: blank strings become <c>null</c> for clean storage.</summary>
    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Audit attribution for a memory event. When the node is project-scoped and its
    /// <see cref="MemoryNode.ScopeKey"/> parses as an int, use that project id; otherwise 0
    /// ("no project"). Legal because <see cref="ActivityEvent"/> has no FK to <see cref="Project"/>;
    /// the event is identified by EntityType + EntityId.
    /// </summary>
    private static int MemoryProjectId(MemoryNode node) =>
        node.Scope == MemoryScope.Project && int.TryParse(node.ScopeKey, out var projectId) ? projectId : 0;
}
