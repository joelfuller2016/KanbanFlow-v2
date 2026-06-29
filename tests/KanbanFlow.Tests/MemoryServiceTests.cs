using System.Data.Common;
using KanbanFlow.Data;
using KanbanFlow.Models;
using KanbanFlow.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Tests;

/// <summary>
/// Real-code tests for <see cref="MemoryService"/> and the memory schema (#221) against a live
/// SQLite database (<see cref="SqliteTestFactory"/>). No mocks of the data layer — every test seeds
/// real rows, runs the real service, and re-queries a fresh context to assert what was actually
/// persisted, including the bi-temporal supersession chain and the <see cref="ActivityEvent"/> audit
/// trail (the #88 single-audit-boundary rule the memory store inherits).
/// </summary>
public sealed class MemoryServiceTests
{
    // ── helpers ─────────────────────────────────────────────────────────────────
    private static int SeedNode(
        SqliteTestFactory f, string content = "fact",
        MemoryType type = MemoryType.Semantic, MemoryScope scope = MemoryScope.Project,
        string scopeKey = "1", string? tags = null)
    {
        using var db = f.CreateDbContext();
        var now = DateTime.UtcNow;
        var node = new MemoryNode
        {
            Type = type,
            Scope = scope,
            ScopeKey = scopeKey,
            Content = content,
            Tags = tags,
            CreatedAt = now,
            ValidFrom = now
        };
        db.Add(node);
        db.SaveChanges();
        return node.Id;
    }

    private static List<ActivityEvent> NodeEvents(SqliteTestFactory f, int nodeId)
    {
        using var db = f.CreateDbContext();
        return db.ActivityEvents
            .Where(a => a.EntityType == "MemoryNode" && a.EntityId == nodeId)
            .OrderBy(a => a.Id)
            .ToList();
    }

    private static int SeedTask(SqliteTestFactory f)
    {
        using var db = f.CreateDbContext();
        var task = new TaskItem { Name = "T", Feature = new Feature { Name = "F", Project = new Project { Name = "P" } } };
        db.Add(task);
        db.SaveChanges();
        return task.Id;
    }

    private static HashSet<string> SqliteMasterNames(DbConnection connection, string type)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = $type AND name NOT LIKE 'sqlite_%'";
        var p = cmd.CreateParameter();
        p.ParameterName = "$type";
        p.Value = type;
        cmd.Parameters.Add(p);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }

    // ── CreateNodeAsync ───────────────────────────────────────────────────────────
    [Fact]
    public async Task CreateNode_PersistsNode_AndEmitsMemoryCreatedEvent()
    {
        using var f = new SqliteTestFactory();
        var svc = new MemoryService(f);

        var node = await svc.CreateNodeAsync(
            new MemoryNodeCreate(MemoryType.Decision, MemoryScope.Project, "42", "chose SQLite",
                Tags: "db,decision", Source: "architect", Confidence: 0.9), "tester");

        Assert.True(node.Id > 0);
        Assert.Null(node.ValidTo);
        Assert.Null(node.SupersededById);
        Assert.Equal(0, node.AccessCount);
        Assert.Null(node.EmbeddingBlob);

        using var db = f.CreateDbContext();
        var persisted = await db.MemoryNodes.FindAsync(node.Id);
        Assert.NotNull(persisted);
        Assert.Equal(MemoryType.Decision, persisted!.Type);
        Assert.Equal(MemoryScope.Project, persisted.Scope);
        Assert.Equal("42", persisted.ScopeKey);
        Assert.Equal("chose SQLite", persisted.Content);
        Assert.Equal("db,decision", persisted.Tags);
        Assert.Equal("architect", persisted.Source);
        Assert.Equal(0.9, persisted.Confidence);
        Assert.Equal(persisted.CreatedAt, persisted.ValidFrom);

        // Exactly one audit event: MemoryCreated, attributed to the project scope id (42).
        var created = Assert.Single(NodeEvents(f, node.Id));
        Assert.Equal("MemoryNode", created.EntityType);
        Assert.Equal("MemoryCreated", created.EventType);
        Assert.Equal("tester", created.Actor);
        Assert.Equal(42, created.ProjectId);
        Assert.Null(created.BeforeJson);
        Assert.NotNull(created.AfterJson);
    }

    [Fact]
    public async Task CreateNode_TrimsContent_AndNullsBlankOptionalFields()
    {
        using var f = new SqliteTestFactory();
        var svc = new MemoryService(f);

        var node = await svc.CreateNodeAsync(
            new MemoryNodeCreate(MemoryType.Episodic, MemoryScope.Session, "  sess-1  ",
                "  spaced content  ", Tags: "   ", Source: ""), null);

        using var db = f.CreateDbContext();
        var persisted = await db.MemoryNodes.FindAsync(node.Id);
        Assert.Equal("spaced content", persisted!.Content);
        Assert.Equal("sess-1", persisted.ScopeKey);
        Assert.Null(persisted.Tags);
        Assert.Null(persisted.Source);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateNode_BlankContent_Throws(string content)
    {
        using var f = new SqliteTestFactory();
        var svc = new MemoryService(f);
        await Assert.ThrowsAsync<ValidationException>(
            () => svc.CreateNodeAsync(new MemoryNodeCreate(MemoryType.Semantic, MemoryScope.Project, "1", content), null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateNode_BlankScopeKey_Throws(string? scopeKey)
    {
        // ScopeKey pins the node's placement and resolves its audit ProjectId; a blank key would store
        // an unscoped row and misattribute the audit to project 0 (scopeKey! deliberately probes the
        // null-contract violation a misbehaving caller could make).
        using var f = new SqliteTestFactory();
        var svc = new MemoryService(f);
        await Assert.ThrowsAsync<ValidationException>(
            () => svc.CreateNodeAsync(new MemoryNodeCreate(MemoryType.Semantic, MemoryScope.Project, scopeKey!, "content"), null));
    }

    [Fact]
    public async Task CreateNode_InvalidType_Throws()
    {
        using var f = new SqliteTestFactory();
        var svc = new MemoryService(f);
        await Assert.ThrowsAsync<ValidationException>(
            () => svc.CreateNodeAsync(new MemoryNodeCreate((MemoryType)999, MemoryScope.Project, "1", "x"), null));
    }

    [Fact]
    public async Task CreateNode_InvalidScope_Throws()
    {
        using var f = new SqliteTestFactory();
        var svc = new MemoryService(f);
        await Assert.ThrowsAsync<ValidationException>(
            () => svc.CreateNodeAsync(new MemoryNodeCreate(MemoryType.Semantic, (MemoryScope)42, "1", "x"), null));
    }

    // ── GetNodeAsync ──────────────────────────────────────────────────────────────
    [Fact]
    public async Task GetNode_ReturnsNull_WhenMissing()
    {
        using var f = new SqliteTestFactory();
        var svc = new MemoryService(f);
        Assert.Null(await svc.GetNodeAsync(9999));
    }

    [Fact]
    public async Task GetNode_BumpsAccessCountAndLastAccessedAt_WithoutAuditEvent()
    {
        using var f = new SqliteTestFactory();
        var id = SeedNode(f, "recall me");
        var svc = new MemoryService(f);

        var first = await svc.GetNodeAsync(id);
        Assert.Equal(1, first!.AccessCount);
        Assert.NotNull(first.LastAccessedAt);

        // The bump persists, and a second read increments again.
        var second = await svc.GetNodeAsync(id);
        Assert.Equal(2, second!.AccessCount);

        using var db = f.CreateDbContext();
        Assert.Equal(2, (await db.MemoryNodes.FindAsync(id))!.AccessCount);

        // A read is NOT a mutation — no audit events were written.
        Assert.Empty(NodeEvents(f, id));
    }

    // ── ListAsync ─────────────────────────────────────────────────────────────────
    [Fact]
    public async Task List_FiltersByScopeAndScopeKeyAndType()
    {
        using var f = new SqliteTestFactory();
        SeedNode(f, "p1-sem", MemoryType.Semantic, MemoryScope.Project, "1");
        SeedNode(f, "p1-proc", MemoryType.Procedural, MemoryScope.Project, "1");
        SeedNode(f, "p2-sem", MemoryType.Semantic, MemoryScope.Project, "2");
        SeedNode(f, "agent-sem", MemoryType.Semantic, MemoryScope.Agent, "1");
        var svc = new MemoryService(f);

        var hits = await svc.ListAsync(scope: MemoryScope.Project, scopeKey: "1", type: MemoryType.Semantic);
        Assert.Equal("p1-sem", Assert.Single(hits).Content);
    }

    [Fact]
    public async Task ListAsync_ExcludesSuperseded_WhenFlagFalse()
    {
        using var f = new SqliteTestFactory();
        var svc = new MemoryService(f);
        var created = await svc.CreateNodeAsync(new MemoryNodeCreate(MemoryType.Semantic, MemoryScope.Project, "1", "v1"), null);
        await svc.UpdateNodeAsync(created.Id, new MemoryNodeUpdate(Content: "v2"), null);

        // Default (excludeSuperseded) returns only the current version.
        var current = await svc.ListAsync(scope: MemoryScope.Project, scopeKey: "1");
        Assert.Equal("v2", Assert.Single(current).Content);
    }

    [Fact]
    public async Task ListAsync_IncludesSuperseded_WhenFlagTrue()
    {
        using var f = new SqliteTestFactory();
        var svc = new MemoryService(f);
        var created = await svc.CreateNodeAsync(new MemoryNodeCreate(MemoryType.Semantic, MemoryScope.Project, "1", "v1"), null);
        await svc.UpdateNodeAsync(created.Id, new MemoryNodeUpdate(Content: "v2"), null);

        var all = await svc.ListAsync(scope: MemoryScope.Project, scopeKey: "1", includeSuperseded: true);
        Assert.Equal(2, all.Count);
        Assert.Contains(all, m => m.Content == "v1");
        Assert.Contains(all, m => m.Content == "v2");
    }

    // ── UpdateNodeAsync (bi-temporal) ───────────────────────────────────────────────
    [Fact]
    public async Task UpdateNode_BiTemporal_SupersedesPriorRow_AndCreatesNewVersion()
    {
        using var f = new SqliteTestFactory();
        var svc = new MemoryService(f);
        var created = await svc.CreateNodeAsync(
            new MemoryNodeCreate(MemoryType.Semantic, MemoryScope.Project, "1", "original"), "author");

        var updated = await svc.UpdateNodeAsync(created.Id, new MemoryNodeUpdate(Content: "revised"), "editor");

        // A NEW row is created — the update does not overwrite in place.
        Assert.NotEqual(created.Id, updated.Id);
        Assert.Equal("revised", updated.Content);
        Assert.Null(updated.ValidTo);
        Assert.Null(updated.SupersededById);

        using var db = f.CreateDbContext();

        // The prior row remains intact and queryable, stamped with ValidTo + a pointer to the new row.
        var prior = await db.MemoryNodes.FindAsync(created.Id);
        Assert.NotNull(prior);
        Assert.Equal("original", prior!.Content);
        Assert.NotNull(prior.ValidTo);
        Assert.Equal(updated.Id, prior.SupersededById);

        var newRow = await db.MemoryNodes.FindAsync(updated.Id);
        Assert.Equal("revised", newRow!.Content);
        Assert.Null(newRow.ValidTo);

        // Exactly one MemoryUpdated event, on the new row.
        var ev = Assert.Single(NodeEvents(f, updated.Id));
        Assert.Equal("MemoryUpdated", ev.EventType);
        Assert.Equal("editor", ev.Actor);
        Assert.NotNull(ev.BeforeJson);
        Assert.NotNull(ev.AfterJson);
    }

    [Fact]
    public async Task UpdateNode_ClearsStaleEmbedding_OnNewVersion_ButPriorKeepsIt()
    {
        using var f = new SqliteTestFactory();
        var svc = new MemoryService(f);
        var created = await svc.CreateNodeAsync(new MemoryNodeCreate(MemoryType.Semantic, MemoryScope.Project, "1", "v1"), null);

        // Simulate #222 having embedded the node.
        using (var seed = f.CreateDbContext())
        {
            var n = await seed.MemoryNodes.FindAsync(created.Id);
            n!.EmbeddingBlob = EmbeddingCodec.Pack([1f, 2f, 3f]);
            n.EmbeddingDim = 3;
            await seed.SaveChangesAsync();
        }

        // An explicit Content change ("v1" -> "v2") is what makes the prior vector stale.
        var updated = await svc.UpdateNodeAsync(created.Id, new MemoryNodeUpdate(Content: "v2"), null);
        Assert.Equal("v2", updated.Content);

        // New version starts unembedded (content changed ⇒ prior vector is stale; #222/#223 re-embeds).
        Assert.Null(updated.EmbeddingBlob);
        Assert.Null(updated.EmbeddingDim);

        // Prior version keeps its embedding (historical fidelity).
        using var db = f.CreateDbContext();
        var prior = await db.MemoryNodes.FindAsync(created.Id);
        Assert.NotNull(prior!.EmbeddingBlob);
        Assert.Equal(3, prior.EmbeddingDim);
    }

    [Fact]
    public async Task UpdateNode_MetadataOnlyChange_CarriesEmbeddingForward()
    {
        using var f = new SqliteTestFactory();
        var svc = new MemoryService(f);
        var created = await svc.CreateNodeAsync(new MemoryNodeCreate(MemoryType.Semantic, MemoryScope.Project, "1", "stable content"), null);

        // Simulate #222 having embedded the node.
        var vector = EmbeddingCodec.Pack([1f, 2f, 3f]);
        using (var seed = f.CreateDbContext())
        {
            var n = await seed.MemoryNodes.FindAsync(created.Id);
            n!.EmbeddingBlob = vector;
            n.EmbeddingDim = 3;
            await seed.SaveChangesAsync();
        }

        // A metadata-only edit (Tags/Source/Confidence; Content carried forward) keeps the content — so
        // the embedding still matches and must NOT be cleared.
        var updated = await svc.UpdateNodeAsync(
            created.Id, new MemoryNodeUpdate(Tags: "alpha,beta", Source: "agent", Confidence: 0.42), null);

        // A new bi-temporal version is still created, but it carries the prior vector forward unchanged.
        Assert.NotEqual(created.Id, updated.Id);
        Assert.Equal("stable content", updated.Content);
        Assert.Equal(vector, updated.EmbeddingBlob);
        Assert.Equal(3, updated.EmbeddingDim);

        // ...and that is what actually persisted on the new row.
        using var db = f.CreateDbContext();
        var newRow = await db.MemoryNodes.FindAsync(updated.Id);
        Assert.Equal(vector, newRow!.EmbeddingBlob);
        Assert.Equal(3, newRow.EmbeddingDim);
    }

    [Fact]
    public async Task UpdateNode_NoActualChange_IsIdempotent_NoNewRowNoEvent()
    {
        using var f = new SqliteTestFactory();
        var svc = new MemoryService(f);
        var created = await svc.CreateNodeAsync(new MemoryNodeCreate(MemoryType.Semantic, MemoryScope.Project, "1", "same"), null);

        var result = await svc.UpdateNodeAsync(created.Id, new MemoryNodeUpdate(Content: "same"), "editor");

        // Same id back (no new version), no MemoryUpdated event, one row total.
        Assert.Equal(created.Id, result.Id);
        Assert.DoesNotContain(NodeEvents(f, created.Id), e => e.EventType == "MemoryUpdated");

        using var db = f.CreateDbContext();
        Assert.Equal(1, await db.MemoryNodes.CountAsync());
    }

    [Fact]
    public async Task UpdateNode_Missing_Throws()
    {
        using var f = new SqliteTestFactory();
        var svc = new MemoryService(f);
        await Assert.ThrowsAsync<MemoryNodeNotFoundException>(
            () => svc.UpdateNodeAsync(123, new MemoryNodeUpdate(Content: "x"), null));
    }

    [Fact]
    public async Task UpdateNode_BlankContent_Throws()
    {
        using var f = new SqliteTestFactory();
        var id = SeedNode(f);
        var svc = new MemoryService(f);
        await Assert.ThrowsAsync<ValidationException>(
            () => svc.UpdateNodeAsync(id, new MemoryNodeUpdate(Content: "   "), null));
    }

    [Fact]
    public async Task UpdateNode_AlreadySuperseded_Throws()
    {
        using var f = new SqliteTestFactory();
        var svc = new MemoryService(f);
        var created = await svc.CreateNodeAsync(new MemoryNodeCreate(MemoryType.Semantic, MemoryScope.Project, "1", "v1"), null);
        await svc.UpdateNodeAsync(created.Id, new MemoryNodeUpdate(Content: "v2"), null);

        // The original row is now historical; updating it (rather than its current version) is rejected.
        await Assert.ThrowsAsync<ValidationException>(
            () => svc.UpdateNodeAsync(created.Id, new MemoryNodeUpdate(Content: "v3"), null));
    }

    // ── SupersedeAsync / DeleteNodeAsync ────────────────────────────────────────────
    [Fact]
    public async Task Supersede_StampsValidTo_AndEmitsMemorySuperseded()
    {
        using var f = new SqliteTestFactory();
        var svc = new MemoryService(f);
        var created = await svc.CreateNodeAsync(new MemoryNodeCreate(MemoryType.Semantic, MemoryScope.Project, "7", "retire me"), "author");

        var superseded = await svc.SupersedeAsync(created.Id, "retirer");
        Assert.NotNull(superseded.ValidTo);
        Assert.Null(superseded.SupersededById); // no replacement row for a plain retire

        using var db = f.CreateDbContext();
        Assert.NotNull((await db.MemoryNodes.FindAsync(created.Id))!.ValidTo);

        var ev = Assert.Single(NodeEvents(f, created.Id), e => e.EventType == "MemorySuperseded");
        Assert.Equal("retirer", ev.Actor);
        Assert.Equal(7, ev.ProjectId);
    }

    [Fact]
    public async Task Supersede_Idempotent_WhenAlreadyRetired()
    {
        using var f = new SqliteTestFactory();
        var svc = new MemoryService(f);
        var created = await svc.CreateNodeAsync(new MemoryNodeCreate(MemoryType.Semantic, MemoryScope.Project, "1", "x"), null);

        await svc.SupersedeAsync(created.Id, null);
        await svc.SupersedeAsync(created.Id, null); // no-op

        // Still exactly one MemorySuperseded event.
        Assert.Single(NodeEvents(f, created.Id), e => e.EventType == "MemorySuperseded");
    }

    [Fact]
    public async Task Supersede_Missing_Throws()
    {
        using var f = new SqliteTestFactory();
        var svc = new MemoryService(f);
        await Assert.ThrowsAsync<MemoryNodeNotFoundException>(() => svc.SupersedeAsync(404, null));
    }

    [Fact]
    public async Task DeleteNode_SoftSupersedes_KeepsRowQueryable()
    {
        using var f = new SqliteTestFactory();
        var svc = new MemoryService(f);
        var created = await svc.CreateNodeAsync(new MemoryNodeCreate(MemoryType.Semantic, MemoryScope.Project, "1", "doomed"), null);

        await svc.DeleteNodeAsync(created.Id, "deleter");

        using var db = f.CreateDbContext();
        var row = await db.MemoryNodes.FindAsync(created.Id);
        Assert.NotNull(row);            // NOT hard-deleted
        Assert.NotNull(row!.ValidTo);   // soft-superseded
        Assert.Single(NodeEvents(f, created.Id), e => e.EventType == "MemorySuperseded");
    }

    // ── AddObservationAsync ─────────────────────────────────────────────────────────
    [Fact]
    public async Task AddObservation_Persists_AndEmitsObservationAdded()
    {
        using var f = new SqliteTestFactory();
        var svc = new MemoryService(f);
        var node = await svc.CreateNodeAsync(new MemoryNodeCreate(MemoryType.Episodic, MemoryScope.Project, "1", "the fact"), null);

        var obs = await svc.AddObservationAsync(node.Id, "  confirmed again  ", "agent", "tester");

        Assert.True(obs.Id > 0);
        using var db = f.CreateDbContext();
        var persisted = await db.Observations.FindAsync(obs.Id);
        Assert.NotNull(persisted);
        Assert.Equal("confirmed again", persisted!.Content); // trimmed
        Assert.Equal("agent", persisted.Source);
        Assert.Equal(node.Id, persisted.MemoryNodeId);

        var ev = Assert.Single(NodeEvents(f, node.Id), e => e.EventType == "ObservationAdded");
        Assert.Equal("tester", ev.Actor);
    }

    [Fact]
    public async Task AddObservation_Missing_Throws()
    {
        using var f = new SqliteTestFactory();
        var svc = new MemoryService(f);
        await Assert.ThrowsAsync<MemoryNodeNotFoundException>(() => svc.AddObservationAsync(404, "x", null, null));
    }

    [Fact]
    public async Task AddObservation_BlankContent_Throws()
    {
        using var f = new SqliteTestFactory();
        var id = SeedNode(f);
        var svc = new MemoryService(f);
        await Assert.ThrowsAsync<ValidationException>(() => svc.AddObservationAsync(id, "   ", null, null));
    }

    [Fact]
    public async Task AddObservation_CascadeDeletesWithNode()
    {
        using var f = new SqliteTestFactory();
        var svc = new MemoryService(f);
        var node = await svc.CreateNodeAsync(new MemoryNodeCreate(MemoryType.Episodic, MemoryScope.Project, "1", "fact"), null);
        var obs = await svc.AddObservationAsync(node.Id, "note", null, null);

        // Hard-delete the node directly (not a service op) to prove the FK cascade.
        using (var db = f.CreateDbContext())
        {
            db.MemoryNodes.Remove((await db.MemoryNodes.FindAsync(node.Id))!);
            await db.SaveChangesAsync();
        }

        using var check = f.CreateDbContext();
        Assert.Null(await check.Observations.FindAsync(obs.Id)); // cascaded away with its parent
    }

    // ── AddRelationAsync ────────────────────────────────────────────────────────────
    [Fact]
    public async Task AddRelation_Persists_AndEmitsRelationAdded()
    {
        using var f = new SqliteTestFactory();
        var svc = new MemoryService(f);
        var node = await svc.CreateNodeAsync(new MemoryNodeCreate(MemoryType.Decision, MemoryScope.Project, "1", "decision"), null);

        var relation = await svc.AddRelationAsync("MemoryNode", node.Id, "relates_to", "MemoryNode", node.Id, "agent");

        Assert.True(relation.Id > 0);
        using var db = f.CreateDbContext();
        var persisted = await db.MemoryRelations.FindAsync(relation.Id);
        Assert.NotNull(persisted);
        Assert.Equal("relates_to", persisted!.RelationType);

        var ev = Assert.Single(NodeEvents(f, node.Id), e => e.EventType == "RelationAdded");
        Assert.Equal("MemoryNode", ev.EntityType);
        Assert.Equal(node.Id, ev.EntityId);
    }

    [Fact]
    public async Task AddRelation_CanTargetTaskId_NoFkViolation()
    {
        using var f = new SqliteTestFactory();
        var svc = new MemoryService(f);
        var taskId = SeedTask(f);
        var node = await svc.CreateNodeAsync(new MemoryNodeCreate(MemoryType.Decision, MemoryScope.Project, "1", "decided to do the task"), null);

        // A no-FK edge can span the memory store and an existing card id without a cascade/FK error.
        var relation = await svc.AddRelationAsync("MemoryNode", node.Id, "decided", "Task", taskId, "agent");

        using var db = f.CreateDbContext();
        var persisted = await db.MemoryRelations.FindAsync(relation.Id);
        Assert.NotNull(persisted);
        Assert.Equal("MemoryNode", persisted!.FromType);
        Assert.Equal(node.Id, persisted.FromId);
        Assert.Equal("Task", persisted.ToType);
        Assert.Equal(taskId, persisted.ToId);

        // The audit attributes to the From-side MemoryNode (the only memory endpoint): EntityId is that
        // node's id and ProjectId is resolved from its scope (project "1"), never 0.
        var ev = Assert.Single(NodeEvents(f, node.Id), e => e.EventType == "RelationAdded");
        Assert.Equal("MemoryNode", ev.EntityType);
        Assert.Equal(node.Id, ev.EntityId);
        Assert.Equal(1, ev.ProjectId);
    }

    [Fact]
    public async Task AddRelation_ToMemoryNode_AttributesAuditToToNode()
    {
        using var f = new SqliteTestFactory();
        var svc = new MemoryService(f);
        var taskId = SeedTask(f);
        var node = await svc.CreateNodeAsync(new MemoryNodeCreate(MemoryType.Decision, MemoryScope.Project, "1", "explains the task"), null);

        // From is a non-memory card; the MemoryNode is the To endpoint, so the audit attributes to it.
        var relation = await svc.AddRelationAsync("Task", taskId, "explained_by", "MemoryNode", node.Id, "agent");

        using var db = f.CreateDbContext();
        var persisted = await db.MemoryRelations.FindAsync(relation.Id);
        Assert.NotNull(persisted);
        Assert.Equal("Task", persisted!.FromType);
        Assert.Equal(taskId, persisted.FromId);
        Assert.Equal("MemoryNode", persisted.ToType);
        Assert.Equal(node.Id, persisted.ToId);

        // Audit attributed to the To-side MemoryNode: EntityId == the To node id, project-scoped (not 0).
        var ev = Assert.Single(NodeEvents(f, node.Id), e => e.EventType == "RelationAdded");
        Assert.Equal("MemoryNode", ev.EntityType);
        Assert.Equal(node.Id, ev.EntityId);
        Assert.Equal(1, ev.ProjectId);
    }

    [Fact]
    public async Task AddRelation_NoMemoryNodeEndpoint_Throws()
    {
        using var f = new SqliteTestFactory();
        var svc = new MemoryService(f);

        // Neither endpoint is a MemoryNode — a memory relation must touch the memory graph.
        await Assert.ThrowsAsync<ValidationException>(
            () => svc.AddRelationAsync("Task", 1, "blocks", "Project", 2, null));
    }

    [Fact]
    public async Task AddRelation_MissingMemoryNode_Throws()
    {
        using var f = new SqliteTestFactory();
        var svc = new MemoryService(f);

        // The chosen MemoryNode endpoint must exist (mirrors GetNodeAsync's not-found contract).
        await Assert.ThrowsAsync<MemoryNodeNotFoundException>(
            () => svc.AddRelationAsync("MemoryNode", 999, "relates_to", "Task", 1, null));
    }

    [Fact]
    public async Task AddRelation_BlankType_Throws()
    {
        using var f = new SqliteTestFactory();
        var svc = new MemoryService(f);
        await Assert.ThrowsAsync<ValidationException>(() => svc.AddRelationAsync("", 1, "rel", "Task", 2, null));
        await Assert.ThrowsAsync<ValidationException>(() => svc.AddRelationAsync("MemoryNode", 1, "  ", "Task", 2, null));
        await Assert.ThrowsAsync<ValidationException>(() => svc.AddRelationAsync("MemoryNode", 1, "rel", "", 2, null));
    }

    // ── schema (SchemaPatcher / EnsureCreated) ──────────────────────────────────────
    [Fact]
    public void FreshDb_MemoryTablesAndIndexesExist()
    {
        // SqliteTestFactory builds the schema via AppDbContext.EnsureCreated().
        using var f = new SqliteTestFactory();
        using var db = f.CreateDbContext();
        var conn = db.Database.GetDbConnection();

        var tables = SqliteMasterNames(conn, "table");
        Assert.Contains("MemoryNodes", tables);
        Assert.Contains("Observations", tables);
        Assert.Contains("MemoryRelations", tables);

        var indexes = SqliteMasterNames(conn, "index");
        Assert.Contains("IX_MemoryNodes_Scope_ScopeKey_Type", indexes);
        Assert.Contains("IX_MemoryNodes_SupersededById", indexes);
        Assert.Contains("IX_Observations_MemoryNodeId", indexes);
        Assert.Contains("IX_MemoryRelations_FromType_FromId", indexes);
        Assert.Contains("IX_MemoryRelations_ToType_ToId", indexes);
    }

    [Fact]
    public void PreExistingDb_EnsureMemoryTables_IsIdempotent()
    {
        // A database created before the memory feature has no memory tables. Drive the patcher
        // directly against a raw connection (the same code path Program.cs runs at startup).
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        Assert.DoesNotContain("MemoryNodes", SqliteMasterNames(connection, "table"));

        SchemaPatcher.EnsureMemoryTables(connection);
        SchemaPatcher.EnsureMemoryTables(connection); // must not throw (every statement is IF NOT EXISTS)

        var tables = SqliteMasterNames(connection, "table");
        Assert.Contains("MemoryNodes", tables);
        Assert.Contains("Observations", tables);
        Assert.Contains("MemoryRelations", tables);

        var indexes = SqliteMasterNames(connection, "index");
        Assert.Contains("IX_MemoryNodes_Scope_ScopeKey_Type", indexes);
        Assert.Contains("IX_MemoryNodes_SupersededById", indexes);
        Assert.Contains("IX_Observations_MemoryNodeId", indexes);
        Assert.Contains("IX_MemoryRelations_FromType_FromId", indexes);
        Assert.Contains("IX_MemoryRelations_ToType_ToId", indexes);
    }
}
