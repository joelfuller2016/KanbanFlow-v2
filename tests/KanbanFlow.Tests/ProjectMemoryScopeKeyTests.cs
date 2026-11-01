using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using KanbanFlow.Data;
using KanbanFlow.Mcp;
using KanbanFlow.Models;
using KanbanFlow.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Tests;

public sealed class ProjectMemoryScopeKeyTests
{
    [Theory]
    [InlineData("1")]
    [InlineData("01")]
    [InlineData("0001")]
    [InlineData("+1")]
    [InlineData(" +0001 ")]
    [InlineData("\t01\n")]
    [InlineData("\u00a001\u00a0")]
    public async Task CreateNode_AcceptedProjectKeys_AreCanonicalAndVisibleInContext(string scopeKey)
    {
        using var factory = new SqliteTestFactory();
        await using (var db = factory.CreateDbContext())
        {
            db.Projects.Add(new Project { Id = 1, Name = "Project key" });
            await db.SaveChangesAsync();
        }
        var service = new MemoryService(factory);
        var node = await service.CreateNodeAsync(new MemoryNodeCreate(
            MemoryType.Decision, MemoryScope.Project, scopeKey, "# Decision\n\nKeep  two spaces.\n\n尾",
            Source: "reviewed-source"), "scope-regression");

        var context = await KanbanMcpTools.GetProjectContext(factory, 1);

        var memory = Assert.Single(context.Memory);
        Assert.Equal(node.Id, memory.Id);
        Assert.Equal(node.Content, memory.Content);
        Assert.Equal(node.Source, memory.Source);
        Assert.Equal("1", node.ScopeKey);
        await using var persisted = factory.CreateDbContext();
        Assert.Equal("1", (await persisted.MemoryNodes.SingleAsync()).ScopeKey);
        var audit = await persisted.ActivityEvents.SingleAsync();
        Assert.Equal(1, audit.ProjectId);
        using var after = JsonDocument.Parse(audit.AfterJson!);
        Assert.Equal("1", after.RootElement.GetProperty("scopeKey").GetString());
    }

    [Theory]
    [InlineData(MemoryScope.Feature)]
    [InlineData(MemoryScope.Agent)]
    [InlineData(MemoryScope.Session)]
    public async Task CreateNode_NonProjectKey_KeepsItsExistingTrimPolicy(MemoryScope scope)
    {
        using var factory = new SqliteTestFactory();
        var node = await new MemoryService(factory).CreateNodeAsync(
            new MemoryNodeCreate(MemoryType.Semantic, scope, " +0001 ", "Unchanged namespace"), null);

        Assert.Equal("+0001", node.ScopeKey);
    }

    [Theory]
    [InlineData(MemoryScope.Project, "1")]
    [InlineData(MemoryScope.Project, "01")]
    [InlineData(MemoryScope.Project, "+0001")]
    [InlineData(MemoryScope.Project, "\u00a001\u00a0")]
    [InlineData(MemoryScope.Feature, "01")]
    [InlineData(MemoryScope.Agent, "01")]
    [InlineData(MemoryScope.Session, "01")]
    public async Task TypedReads_UseProjectIdentity_AndKeepOtherNamespacesExact(MemoryScope scope, string key)
    {
        using var factory = new SqliteTestFactory();
        var service = new MemoryService(factory);
        var node = await service.CreateNodeAsync(new MemoryNodeCreate(MemoryType.Semantic, scope, key, "lookup target"), null);
        await service.SetEmbeddingAsync(node.Id, [1f, 0f], null);
        var other = await service.CreateNodeAsync(new MemoryNodeCreate(MemoryType.Semantic, scope,
            scope == MemoryScope.Project ? "2" : "1", "lookup other namespace"), null);
        await service.SetEmbeddingAsync(other.Id, [1f, 0f], null);
        var vector = new VectorSearch(factory);
        var hybrid = new MemorySearch(factory, new FixedEmbeddings(), vector);

        Assert.Equal(node.Id, Assert.Single(await service.ListAsync(scope, key)).Id);
        Assert.Equal(node.Id, Assert.Single((await service.ReadGraphAsync(scope, key)).Nodes).Id);
        Assert.Equal(node.Id, Assert.Single(await vector.SearchAsync(scope, key, [1f, 0f], 10)).Id);
        var hit = Assert.Single(await hybrid.SearchAsync(scope, key, "lookup", 10, null));
        Assert.Equal(node.Id, hit.Id);
        Assert.Equal(1, hit.KeywordRank);
        Assert.Equal(1, hit.VectorRank);
        if (scope != MemoryScope.Project)
            Assert.Equal(node.Id, Assert.Single(await service.ListAsync(scopeKey: key)).Id);
    }

    [Fact]
    public async Task Startup_NormalizesLegacyProjectKeys_WithAuditedPreservationAndIdempotence()
    {
        using var factory = new SqliteTestFactory();
        await using var db = factory.CreateDbContext();
        db.Projects.AddRange(new Project { Id = 1, Name = "One" }, new Project { Id = 2, Name = "Two" });
        var accepted = new[] { "1", "01", "0001", "+1", " +0001 ", "\t01\n", "\u00a001\u00a0", "02", "02147483647" };
        var malformed = new[] { "1junk", "1.0", "1e0", "0", "-01", "2147483648", "", "   ", "+ 1", "١" };
        var epoch = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var nodes = accepted.Concat(malformed).Select((key, i) => new MemoryNode
        {
            ScopeKey = key,
            Content = $"  # Authored {i}\n\nKeep  whitespace — 日本語.  \n",
            Source = "  original source  ",
            Tags = " keep,tags ",
            Confidence = 0.75,
            Type = MemoryType.Decision,
            CreatedAt = epoch.AddTicks(i),
            ValidFrom = epoch.AddTicks(i),
            AccessCount = 7,
            LastAccessedAt = epoch.AddDays(1),
            EmbeddingBlob = [0, 1, 2, 255],
            EmbeddingDim = 1
        }).ToList();
        nodes.AddRange(new[] { MemoryScope.Feature, MemoryScope.Agent, MemoryScope.Session }
            .Select(scope => new MemoryNode { Scope = scope, ScopeKey = " +01 ", Content = "Namespace remains exact" }));
        var retired = new MemoryNode { ScopeKey = "0001", Content = "Retired text", ValidTo = epoch.AddDays(2) };
        var superseded = new MemoryNode { ScopeKey = "+01", Content = "Prior version" };
        nodes.AddRange([retired, superseded]);
        db.MemoryNodes.AddRange(nodes);
        await db.SaveChangesAsync();
        superseded.SupersededById = nodes[0].Id;
        db.Observations.Add(new Observation { MemoryNodeId = nodes[1].Id, Content = "  Original\n\nobservation  ", Source = " observation source " });
        db.MemoryRelations.Add(new MemoryRelation { FromType = "MemoryNode", FromId = nodes[1].Id, RelationType = "supports", ToType = "MemoryNode", ToId = nodes[0].Id });
        db.ActivityEvents.Add(new ActivityEvent { ProjectId = 1, EntityType = "MemoryNode", EntityId = nodes[1].Id, EventType = "MemoryCreated", Actor = "original-author", AfterJson = "{\"scopeKey\":\"01\"}" });
        await db.SaveChangesAsync();
        await MemoryFts.UpsertAsync(db.Database.GetDbConnection(), null, nodes[1].Id, nodes[1].Content, default);
        var originalKeys = nodes.ToDictionary(n => n.Id, n => n.ScopeKey);
        var expectedKeys = new Dictionary<int, string>(originalKeys);
        foreach (var node in nodes.Take(7))
            expectedKeys[node.Id] = "1";
        expectedKeys[nodes[7].Id] = "2";
        expectedKeys[nodes[8].Id] = "2147483647";
        expectedKeys[retired.Id] = "1";
        expectedKeys[superseded.Id] = "1";
        var connection = db.Database.GetDbConnection();
        var preserved = DumpPreservedRows(connection);

        SchemaPatcher.EnsureMemoryTables(connection);

        Assert.Equal(preserved, DumpPreservedRows(connection));
        await using var actual = factory.CreateDbContext();
        var patched = await actual.MemoryNodes.AsNoTracking().OrderBy(n => n.Id).ToListAsync();
        Assert.All(patched, node => Assert.Equal(expectedKeys[node.Id], node.ScopeKey));
        var events = await actual.ActivityEvents.AsNoTracking().Where(e => e.EventType == "MemoryScopeKeyNormalized").OrderBy(e => e.Id).ToListAsync();
        var changedIds = nodes.Where(n => originalKeys[n.Id] != expectedKeys[n.Id]).Select(n => n.Id).Order().ToArray();
        Assert.Equal(changedIds, events.Select(e => e.EntityId).Order());
        foreach (var audit in events)
        {
            Assert.Equal("schema-patcher", audit.Actor);
            Assert.Equal("MemoryNode", audit.EntityType);
            Assert.Equal(int.Parse(expectedKeys[audit.EntityId], CultureInfo.InvariantCulture), audit.ProjectId);
            using var before = JsonDocument.Parse(audit.BeforeJson!);
            using var after = JsonDocument.Parse(audit.AfterJson!);
            Assert.Equal(originalKeys[audit.EntityId], before.RootElement.GetProperty("scopeKey").GetString());
            Assert.Equal(expectedKeys[audit.EntityId], after.RootElement.GetProperty("scopeKey").GetString());
            Assert.Equal(before.RootElement.EnumerateObject().Where(p => p.Name != "scopeKey").Select(p => p.Value.GetRawText()),
                after.RootElement.EnumerateObject().Where(p => p.Name != "scopeKey").Select(p => p.Value.GetRawText()));
        }
        var context = await KanbanMcpTools.GetProjectContext(factory, 1);
        Assert.Equal(nodes.Take(7).Select(n => n.Id).Order(), context.Memory.Select(n => n.Id).Order());
        var allRows = DumpRows(connection, "SELECT * FROM MemoryNodes ORDER BY Id", "SELECT * FROM ActivityEvents ORDER BY Id");

        SchemaPatcher.EnsureMemoryTables(connection);

        Assert.Equal(allRows, DumpRows(connection, "SELECT * FROM MemoryNodes ORDER BY Id", "SELECT * FROM ActivityEvents ORDER BY Id"));
        Assert.Equal(preserved, DumpPreservedRows(connection));
    }

    [Fact]
    public async Task Startup_AuditFailure_RollsBackKeyChangesAndCanRetry()
    {
        using var factory = new SqliteTestFactory();
        await using var db = factory.CreateDbContext();
        db.MemoryNodes.Add(new MemoryNode { ScopeKey = "01", Content = "Keep this exact node" });
        await db.SaveChangesAsync();
        var connection = db.Database.GetDbConnection();
        var before = DumpRows(connection, "SELECT * FROM MemoryNodes ORDER BY Id", "SELECT * FROM ActivityEvents ORDER BY Id");
        var attemptedKeys = new List<string>();
        ((SqliteConnection)connection).CreateFunction<string, int>("observe_scope_key", key =>
        {
            attemptedKeys.Add(key);
            return 1;
        });
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER fail_scope_key_audit BEFORE INSERT ON ActivityEvents
            WHEN NEW.EventType = 'MemoryScopeKeyNormalized'
            BEGIN
                SELECT observe_scope_key((SELECT ScopeKey FROM MemoryNodes WHERE Id = NEW.EntityId));
                SELECT RAISE(ABORT, 'scope-key audit rejected');
            END;
            """);

        var error = Assert.Throws<DbUpdateException>(() => SchemaPatcher.EnsureMemoryTables(connection));

        Assert.Contains("scope-key audit rejected", error.InnerException!.Message);
        Assert.Equal(["1"], attemptedKeys); // the key update landed inside the transaction before the audit failed
        Assert.Equal(before, DumpRows(connection, "SELECT * FROM MemoryNodes ORDER BY Id", "SELECT * FROM ActivityEvents ORDER BY Id"));
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_scope_key_audit");
        SchemaPatcher.EnsureMemoryTables(connection);
        await using var actual = factory.CreateDbContext();
        Assert.Equal("1", (await actual.MemoryNodes.SingleAsync()).ScopeKey);
        Assert.Equal("MemoryScopeKeyNormalized", (await actual.ActivityEvents.SingleAsync()).EventType);
    }

    [Fact]
    public async Task Normalization_RequiresTheCallersTransaction_BeforeChangingData()
    {
        using var factory = new SqliteTestFactory();
        await using var db = factory.CreateDbContext();
        db.MemoryNodes.Add(new MemoryNode { ScopeKey = "01", Content = "Transaction guard" });
        await db.SaveChangesAsync();

        var error = Assert.Throws<InvalidOperationException>(() => MemoryService.NormalizeProjectScopeKeys(db));

        Assert.Contains("requires an existing transaction", error.Message);
        Assert.Equal("01", (await db.MemoryNodes.AsNoTracking().SingleAsync()).ScopeKey);
        Assert.Empty(await db.ActivityEvents.ToListAsync());
    }

    private static string[] DumpPreservedRows(DbConnection connection) => DumpRows(connection,
        "SELECT Id, Type, Scope, Content, Tags, Source, Confidence, CreatedAt, ValidFrom, ValidTo, SupersededById, AccessCount, LastAccessedAt, EmbeddingBlob, EmbeddingDim FROM MemoryNodes ORDER BY Id",
        "SELECT * FROM Observations ORDER BY Id", "SELECT * FROM MemoryRelations ORDER BY Id",
        "SELECT rowid, content FROM fts_memory ORDER BY rowid",
        "SELECT * FROM ActivityEvents WHERE EventType <> 'MemoryScopeKeyNormalized' ORDER BY Id");

    private sealed class FixedEmbeddings : IEmbeddingService
    {
        public Task<EmbedResult> EmbedAsync(string text, CancellationToken ct = default) =>
            Task.FromResult(EmbedResult.Success([1f, 0f], "scope-test"));
        public Task<EmbedResult> ProbeAsync(string text, CancellationToken ct = default) => EmbedAsync(text, ct);
        public Task<IReadOnlyList<EmbedResult>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EmbedResult>>(texts.Select(_ => EmbedResult.Success([1f, 0f], "scope-test")).ToArray());
    }

    private static string[] DumpRows(DbConnection connection, params string[] queries)
    {
        var rows = new List<string>();
        foreach (var sql in queries)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var values = new object[reader.FieldCount];
                reader.GetValues(values);
                rows.Add(JsonSerializer.Serialize(values));
            }
        }
        return [.. rows];
    }
}
