using KanbanFlow.Data;
using KanbanFlow.Mcp;
using KanbanFlow.Models;
using KanbanFlow.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace KanbanFlow.Tests;

/// <summary>
/// Real-code tests for the knowledge-graph MCP tools (#224) — the drop-in implementation of the
/// official MCP memory server contract (create_entities … open_nodes) — against a live SQLite
/// database (<see cref="SqliteTestFactory"/>). Every test runs the REAL <see cref="MemoryService"/>
/// (audit events, FTS dual-write) and, for search_nodes, the REAL <see cref="MemorySearch"/> hybrid
/// retrieval; the ONLY substituted collaborator is the embedding network transport (a downed-provider
/// stub, so retrieval exercises its documented keyword+recency degradation — no ZeroRouter needed).
/// </summary>
public sealed class McpKnowledgeGraphToolsTests
{
    private const string Kg = "knowledge-graph";

    private static MemoryService NewService(SqliteTestFactory f) => new(f);

    /// <summary>Real MemorySearch whose embedding transport is down (fail-soft ⇒ keyword+recency).</summary>
    private static MemorySearch NewSearch(SqliteTestFactory f) => new(
        f,
        new EmbeddingService(
            new StubEmbeddingGenerator(),
            new EmbeddingOptions { Model = "stub-embed", TimeoutMs = 5_000 },
            NullLogger<EmbeddingService>.Instance),
        new VectorSearch(f));

    private static List<ActivityEvent> NodeEvents(SqliteTestFactory f, int nodeId)
    {
        using var db = f.CreateDbContext();
        return db.ActivityEvents
            .Where(a => a.EntityType == "MemoryNode" && a.EntityId == nodeId)
            .OrderBy(a => a.Id)
            .ToList();
    }

    private static async Task<MemoryNode> NodeByName(SqliteTestFactory f, string name, string scopeKey = Kg)
    {
        await using var db = await f.CreateDbContextAsync();
        return await db.MemoryNodes.SingleAsync(m =>
            m.Scope == MemoryScope.Agent && m.ScopeKey == scopeKey && m.Content == name
            && m.ValidTo == null && m.SupersededById == null);
    }

    // ── create_entities ───────────────────────────────────────────────────────────────
    [Fact]
    public async Task CreateEntities_CreatesNodesInDefaultNamespace_WithObservations()
    {
        using var f = new SqliteTestFactory();
        var svc = NewService(f);

        var created = await KanbanMcpTools.CreateEntities(svc,
        [
            new MemoryEntityInput("Ada Lovelace", "person", ["wrote the first program", "born 1815"]),
            new MemoryEntityInput("Analytical Engine", "machine")
        ]);

        Assert.Equal(2, created.Count);
        Assert.Equal("Ada Lovelace", created[0].Name);
        Assert.Equal("person", created[0].EntityType);
        Assert.Equal(["wrote the first program", "born 1815"], created[0].Observations);
        Assert.Empty(created[1].Observations);

        // The contract mapping actually persisted: name→Content, entityType→Tags, Semantic type,
        // Agent scope, "knowledge-graph" key — the zero-arg namespace default.
        var node = await NodeByName(f, "Ada Lovelace");
        Assert.Equal(MemoryType.Semantic, node.Type);
        Assert.Equal(MemoryScope.Agent, node.Scope);
        Assert.Equal(Kg, node.ScopeKey);
        Assert.Equal("person", node.Tags);

        using var db = f.CreateDbContext();
        Assert.Equal(2, await db.Observations.CountAsync(o => o.MemoryNodeId == node.Id));

        // Audit boundary held: one MemoryCreated + one ObservationAdded per observation,
        // attributed to the default MCP actor.
        var events = NodeEvents(f, node.Id);
        Assert.Single(events, e => e.EventType == "MemoryCreated");
        Assert.Equal(2, events.Count(e => e.EventType == "ObservationAdded"));
        Assert.All(events, e => Assert.Equal("mcp-agent", e.Actor));
    }

    [Fact]
    public async Task CreateEntities_IgnoresExistingNames_ReturnsOnlyNewlyCreated()
    {
        using var f = new SqliteTestFactory();
        var svc = NewService(f);
        await KanbanMcpTools.CreateEntities(svc, [new MemoryEntityInput("Existing", "thing")]);

        var created = await KanbanMcpTools.CreateEntities(svc,
        [
            new MemoryEntityInput("Existing", "thing", ["should not be added"]),
            new MemoryEntityInput("Brand New", "thing")
        ]);

        // Official behavior: the existing name is silently ignored (its observations too).
        Assert.Equal("Brand New", Assert.Single(created).Name);
        using var db = f.CreateDbContext();
        Assert.Equal(2, await db.MemoryNodes.CountAsync(m => m.ScopeKey == Kg));
        Assert.Equal(0, await db.Observations.CountAsync());
    }

    [Fact]
    public async Task CreateEntities_IgnoresInBatchDuplicateNames()
    {
        using var f = new SqliteTestFactory();
        var svc = NewService(f);

        var created = await KanbanMcpTools.CreateEntities(svc,
        [
            new MemoryEntityInput("Twin", "first"),
            new MemoryEntityInput("Twin", "second")
        ]);

        // First wins; a same-name repeat in one batch can't create a second node (which would
        // break name→node resolution for every later call).
        var only = Assert.Single(created);
        Assert.Equal("first", only.EntityType);
        using var db = f.CreateDbContext();
        Assert.Equal(1, await db.MemoryNodes.CountAsync());
    }

    [Fact]
    public async Task CreateEntities_TrimsNames_ForIdentityResolution()
    {
        using var f = new SqliteTestFactory();
        var svc = NewService(f);
        await KanbanMcpTools.CreateEntities(svc, [new MemoryEntityInput("  Padded  ", "thing")]);

        // The stored identity is trimmed, and a re-create with different padding is still a dupe.
        var again = await KanbanMcpTools.CreateEntities(svc, [new MemoryEntityInput("Padded", "thing")]);
        Assert.Empty(again);
        Assert.Equal("Padded", (await NodeByName(f, "Padded")).Content);
    }

    [Fact]
    public async Task CreateEntities_CustomScopeKey_IsolatesNamespaces()
    {
        using var f = new SqliteTestFactory();
        var svc = NewService(f);
        await KanbanMcpTools.CreateEntities(svc, [new MemoryEntityInput("Same Name", "a")], scopeKey: "graph-a");

        // The same name in another namespace is NOT a duplicate.
        var created = await KanbanMcpTools.CreateEntities(svc, [new MemoryEntityInput("Same Name", "b")], scopeKey: "graph-b");
        Assert.Single(created);
        Assert.Equal("a", (await NodeByName(f, "Same Name", "graph-a")).Tags);
        Assert.Equal("b", (await NodeByName(f, "Same Name", "graph-b")).Tags);
    }

    [Fact]
    public async Task CreateEntities_InvalidInput_ThrowsMcpException_WithoutPartialWrites()
    {
        using var f = new SqliteTestFactory();
        var svc = NewService(f);

        await Assert.ThrowsAsync<McpException>(() => KanbanMcpTools.CreateEntities(svc, null!));
        await Assert.ThrowsAsync<McpException>(() => KanbanMcpTools.CreateEntities(svc,
            [new MemoryEntityInput("  ", "thing")]));
        // A blank observation ANYWHERE in the batch fails validation BEFORE the first mutation.
        await Assert.ThrowsAsync<McpException>(() => KanbanMcpTools.CreateEntities(svc,
        [
            new MemoryEntityInput("Fine", "thing"),
            new MemoryEntityInput("Broken", "thing", ["   "])
        ]));

        using var db = f.CreateDbContext();
        Assert.Equal(0, await db.MemoryNodes.CountAsync()); // nothing half-created
    }

    // ── create_relations ──────────────────────────────────────────────────────────────
    [Fact]
    public async Task CreateRelations_AddsEdgeBetweenResolvedNodes()
    {
        using var f = new SqliteTestFactory();
        var svc = NewService(f);
        await KanbanMcpTools.CreateEntities(svc,
            [new MemoryEntityInput("Ada", "person"), new MemoryEntityInput("Engine", "machine")]);

        var created = await KanbanMcpTools.CreateRelations(svc,
            [new MemoryRelationInput("Ada", "Engine", "programs")]);

        var dto = Assert.Single(created);
        Assert.Equal(new MemoryRelationDto("Ada", "Engine", "programs"), dto);

        var ada = await NodeByName(f, "Ada");
        var engine = await NodeByName(f, "Engine");
        using var db = f.CreateDbContext();
        var relation = Assert.Single(await db.MemoryRelations.ToListAsync());
        Assert.Equal(("MemoryNode", ada.Id, "programs", "MemoryNode", engine.Id),
            (relation.FromType, relation.FromId, relation.RelationType, relation.ToType, relation.ToId));

        Assert.Single(NodeEvents(f, ada.Id), e => e.EventType == "RelationAdded");
    }

    [Fact]
    public async Task CreateRelations_SkipsExactDuplicates_ExistingAndInBatch()
    {
        using var f = new SqliteTestFactory();
        var svc = NewService(f);
        await KanbanMcpTools.CreateEntities(svc,
            [new MemoryEntityInput("A", "t"), new MemoryEntityInput("B", "t")]);
        await KanbanMcpTools.CreateRelations(svc, [new MemoryRelationInput("A", "B", "knows")]);

        var created = await KanbanMcpTools.CreateRelations(svc,
        [
            new MemoryRelationInput("A", "B", "knows"),   // duplicate of an existing edge
            new MemoryRelationInput("A", "B", "likes"),   // new verb ⇒ a different edge
            new MemoryRelationInput("A", "B", "likes"),   // in-batch repeat
            new MemoryRelationInput("B", "A", "knows")    // reversed direction is NOT a duplicate
        ]);

        Assert.Equal(2, created.Count);
        Assert.Contains(new MemoryRelationDto("A", "B", "likes"), created);
        Assert.Contains(new MemoryRelationDto("B", "A", "knows"), created);
        using var db = f.CreateDbContext();
        Assert.Equal(3, await db.MemoryRelations.CountAsync());
    }

    [Fact]
    public async Task CreateRelations_MissingEntity_ThrowsActionableMcpException()
    {
        using var f = new SqliteTestFactory();
        var svc = NewService(f);
        await KanbanMcpTools.CreateEntities(svc, [new MemoryEntityInput("A", "t")]);

        var ex = await Assert.ThrowsAsync<McpException>(() => KanbanMcpTools.CreateRelations(svc,
            [new MemoryRelationInput("A", "Ghost", "haunts")]));
        Assert.Contains("Ghost", ex.Message);
        Assert.Contains("create_entities", ex.Message); // tells the agent what to do next
    }

    [Fact]
    public async Task CreateRelations_BlankField_ThrowsMcpException()
    {
        using var f = new SqliteTestFactory();
        var svc = NewService(f);
        await Assert.ThrowsAsync<McpException>(() => KanbanMcpTools.CreateRelations(svc, null!));
        await Assert.ThrowsAsync<McpException>(() => KanbanMcpTools.CreateRelations(svc,
            [new MemoryRelationInput("A", "B", " ")]));
    }

    // ── add_observations ──────────────────────────────────────────────────────────────
    [Fact]
    public async Task AddObservations_AppendsAndReturnsAddedPerEntity()
    {
        using var f = new SqliteTestFactory();
        var svc = NewService(f);
        await KanbanMcpTools.CreateEntities(svc,
            [new MemoryEntityInput("A", "t"), new MemoryEntityInput("B", "t")]);

        var results = await KanbanMcpTools.AddObservations(svc,
        [
            new MemoryObservationInput("A", ["fact one", "fact two"]),
            new MemoryObservationInput("B", ["fact three"])
        ]);

        Assert.Equal(2, results.Count);
        Assert.Equal("A", results[0].EntityName);
        Assert.Equal(["fact one", "fact two"], results[0].AddedObservations);
        Assert.Equal(["fact three"], results[1].AddedObservations);

        var a = await NodeByName(f, "A");
        using var db = f.CreateDbContext();
        Assert.Equal(2, await db.Observations.CountAsync(o => o.MemoryNodeId == a.Id));
        Assert.Equal(2, NodeEvents(f, a.Id).Count(e => e.EventType == "ObservationAdded"));
    }

    [Fact]
    public async Task AddObservations_SkipsAlreadyPresentContents()
    {
        using var f = new SqliteTestFactory();
        var svc = NewService(f);
        await KanbanMcpTools.CreateEntities(svc, [new MemoryEntityInput("A", "t", ["known fact"])]);

        var results = await KanbanMcpTools.AddObservations(svc,
            [new MemoryObservationInput("A", ["known fact", "new fact", "new fact"])]);

        // Only the genuinely new content is added (existing + in-batch repeats are skipped).
        Assert.Equal(["new fact"], Assert.Single(results).AddedObservations);
        var a = await NodeByName(f, "A");
        using var db = f.CreateDbContext();
        Assert.Equal(2, await db.Observations.CountAsync(o => o.MemoryNodeId == a.Id));
    }

    [Fact]
    public async Task AddObservations_MissingEntity_ThrowsMcpException()
    {
        using var f = new SqliteTestFactory();
        var svc = NewService(f);
        // The official contract ERRORS here (unlike the delete_* tools, which are silent no-ops).
        var ex = await Assert.ThrowsAsync<McpException>(() => KanbanMcpTools.AddObservations(svc,
            [new MemoryObservationInput("Ghost", ["boo"])]));
        Assert.Contains("Ghost", ex.Message);
    }

    // ── delete_entities ───────────────────────────────────────────────────────────────
    [Fact]
    public async Task DeleteEntities_RemovesFromGraph_CascadesRelations_SilentOnMissing()
    {
        using var f = new SqliteTestFactory();
        var svc = NewService(f);
        await KanbanMcpTools.CreateEntities(svc,
            [new MemoryEntityInput("A", "t"), new MemoryEntityInput("B", "t")]);
        await KanbanMcpTools.CreateRelations(svc,
            [new MemoryRelationInput("A", "B", "knows"), new MemoryRelationInput("B", "A", "knows")]);
        var aId = (await NodeByName(f, "A")).Id;
        var bId = (await NodeByName(f, "B")).Id;

        var result = await KanbanMcpTools.DeleteEntities(svc, ["A", "Never Existed"]);

        Assert.Equal(["A"], result.Deleted); // missing names silently skipped

        // A left the current graph and BOTH edges touching it cascaded away.
        var graph = await KanbanMcpTools.ReadGraph(svc);
        Assert.Equal("B", Assert.Single(graph.Entities).Name);
        Assert.Empty(graph.Relations);

        // Soft delete by charter: the node row remains, retired, with its audit trail.
        using var db = f.CreateDbContext();
        var row = await db.MemoryNodes.FindAsync(aId);
        Assert.NotNull(row);
        Assert.NotNull(row!.ValidTo);
        Assert.Equal(0, await db.MemoryRelations.CountAsync()); // edge rows hard-deleted
        // One RelationDeleted per cascaded edge, attributed (like RelationAdded) to each edge's
        // From endpoint: A→B audits on A, B→A audits on B. The supersede lands on A.
        var aEvents = NodeEvents(f, aId);
        Assert.Single(aEvents, e => e.EventType == "MemorySuperseded");
        Assert.Single(aEvents, e => e.EventType == "RelationDeleted");
        Assert.Single(NodeEvents(f, bId), e => e.EventType == "RelationDeleted");
    }

    [Fact]
    public async Task DeleteEntities_AllMissing_ReturnsEmpty_NoEvents()
    {
        using var f = new SqliteTestFactory();
        var svc = NewService(f);
        var result = await KanbanMcpTools.DeleteEntities(svc, ["Ghost", "  ", "Phantom"]);
        Assert.Empty(result.Deleted);
        using var db = f.CreateDbContext();
        Assert.Equal(0, await db.ActivityEvents.CountAsync());
    }

    // ── delete_observations ───────────────────────────────────────────────────────────
    [Fact]
    public async Task DeleteObservations_RemovesMatching_SilentOnMissingEntityOrContent()
    {
        using var f = new SqliteTestFactory();
        var svc = NewService(f);
        await KanbanMcpTools.CreateEntities(svc, [new MemoryEntityInput("A", "t", ["keep", "drop"])]);

        var result = await KanbanMcpTools.DeleteObservations(svc,
        [
            new MemoryObservationDeletionInput("A", ["drop", "never existed"]),
            new MemoryObservationDeletionInput("Ghost", ["anything"])
        ]);

        Assert.Equal(1, result.DeletedCount);
        var graph = await KanbanMcpTools.ReadGraph(svc);
        Assert.Equal(["keep"], Assert.Single(graph.Entities).Observations);
    }

    // ── delete_relations ──────────────────────────────────────────────────────────────
    [Fact]
    public async Task DeleteRelations_RemovesEdge_SilentOnMissing()
    {
        using var f = new SqliteTestFactory();
        var svc = NewService(f);
        await KanbanMcpTools.CreateEntities(svc,
            [new MemoryEntityInput("A", "t"), new MemoryEntityInput("B", "t")]);
        await KanbanMcpTools.CreateRelations(svc, [new MemoryRelationInput("A", "B", "knows")]);

        var result = await KanbanMcpTools.DeleteRelations(svc,
        [
            new MemoryRelationInput("A", "B", "knows"),   // exists ⇒ removed
            new MemoryRelationInput("B", "A", "knows"),   // wrong direction ⇒ silent no-op
            new MemoryRelationInput("A", "Ghost", "x")    // missing entity ⇒ silent no-op
        ]);

        Assert.Equal(1, result.DeletedCount);
        Assert.Empty((await KanbanMcpTools.ReadGraph(svc)).Relations);
    }

    // ── read_graph ────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task ReadGraph_ReturnsWholeGraphInOfficialShape()
    {
        using var f = new SqliteTestFactory();
        var svc = NewService(f);
        await KanbanMcpTools.CreateEntities(svc,
        [
            new MemoryEntityInput("Ada", "person", ["mathematician"]),
            new MemoryEntityInput("Engine", "machine")
        ]);
        await KanbanMcpTools.CreateRelations(svc, [new MemoryRelationInput("Ada", "Engine", "programs")]);
        // A node in ANOTHER namespace must not leak into this graph.
        await KanbanMcpTools.CreateEntities(svc, [new MemoryEntityInput("Elsewhere", "t")], scopeKey: "other");

        var graph = await KanbanMcpTools.ReadGraph(svc);

        Assert.Equal(2, graph.Entities.Count);
        var ada = Assert.Single(graph.Entities, e => e.Name == "Ada");
        Assert.Equal("person", ada.EntityType);
        Assert.Equal(["mathematician"], ada.Observations);
        Assert.Equal(new MemoryRelationDto("Ada", "Engine", "programs"), Assert.Single(graph.Relations));
    }

    // ── search_nodes ──────────────────────────────────────────────────────────────────
    [Fact]
    public async Task SearchNodes_ReturnsMatchingEntities_AndOnlyRelationsBetweenMatches()
    {
        using var f = new SqliteTestFactory();
        var svc = NewService(f);
        await KanbanMcpTools.CreateEntities(svc,
        [
            new MemoryEntityInput("Alpha Service", "service", ["hosts the zebrafish pipeline"]),
            new MemoryEntityInput("Beta Worker", "service", ["consumes zebrafish events"]),
            new MemoryEntityInput("Gamma Cache", "infra", ["stores unrelated blobs"])
        ]);
        await KanbanMcpTools.CreateRelations(svc,
        [
            new MemoryRelationInput("Alpha Service", "Beta Worker", "feeds"),
            new MemoryRelationInput("Alpha Service", "Gamma Cache", "writes_to")
        ]);

        var result = await KanbanMcpTools.SearchNodes(NewSearch(f), svc, "zebrafish");

        // Only the entities that actually matched the query (keyword fallback — embeddings down),
        // NOT the whole recency-ranked candidate set.
        Assert.Equal(2, result.Entities.Count);
        Assert.Contains(result.Entities, e => e.Name == "Alpha Service");
        Assert.Contains(result.Entities, e => e.Name == "Beta Worker");
        // Relations BETWEEN matched entities only: the edge to unmatched Gamma Cache is excluded.
        Assert.Equal(new MemoryRelationDto("Alpha Service", "Beta Worker", "feeds"), Assert.Single(result.Relations));
    }

    [Fact]
    public async Task SearchNodes_RanksBestMatchFirst()
    {
        using var f = new SqliteTestFactory();
        var svc = NewService(f);
        await KanbanMcpTools.CreateEntities(svc,
        [
            new MemoryEntityInput("Weak Match", "t", ["quokka mentioned once among many other words entirely"]),
            new MemoryEntityInput("Strong Match", "t", ["quokka quokka quokka"])
        ]);

        var result = await KanbanMcpTools.SearchNodes(NewSearch(f), svc, "quokka", k: 2);

        // Ranked order preserved from the hybrid retrieval: the BM25-stronger entity leads.
        Assert.Equal(2, result.Entities.Count);
        Assert.Equal("Strong Match", result.Entities[0].Name);
    }

    [Fact]
    public async Task SearchNodes_NoMatches_ReturnsEmptyGraph()
    {
        using var f = new SqliteTestFactory();
        var svc = NewService(f);
        await KanbanMcpTools.CreateEntities(svc, [new MemoryEntityInput("A", "t", ["something"])]);

        var result = await KanbanMcpTools.SearchNodes(NewSearch(f), svc, "xylophone");

        Assert.Empty(result.Entities);
        Assert.Empty(result.Relations);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SearchNodes_BlankQuery_ThrowsMcpException(string query)
    {
        using var f = new SqliteTestFactory();
        var svc = NewService(f);
        // The blank-input guard lives in the tool: the service-layer exception never escapes raw.
        var ex = await Assert.ThrowsAsync<McpException>(
            () => KanbanMcpTools.SearchNodes(NewSearch(f), svc, query));
        Assert.Contains("query", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task SearchNodes_NonPositiveK_ThrowsMcpException(int k)
    {
        using var f = new SqliteTestFactory();
        var svc = NewService(f);
        // #338: k was Math.Clamp(k, 1, 100), which rewrote 0 to 1 and left this catch dead.
        // Ceiling-only clamping lets the service guard run, so MCP and REST report one message.
        var ex = await Assert.ThrowsAsync<McpException>(
            () => KanbanMcpTools.SearchNodes(NewSearch(f), svc, "anything", k));
        Assert.Contains("greater than zero", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchNodes_KAboveCeiling_IsCapped_NotRejected()
    {
        using var f = new SqliteTestFactory();
        var svc = NewService(f);
        // The UPPER clamp is load-bearing (it bounds ranking work and payload size on an open
        // endpoint) and must survive the #338 fix -- dropping BOTH bounds is the regression risk.
        var result = await KanbanMcpTools.SearchNodes(NewSearch(f), svc, "anything", 500);
        Assert.NotNull(result);
    }

    // ── open_nodes ────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task OpenNodes_ReturnsRequested_SkipsMissing_OnlyRelationsBetweenOpened()
    {
        using var f = new SqliteTestFactory();
        var svc = NewService(f);
        await KanbanMcpTools.CreateEntities(svc,
        [
            new MemoryEntityInput("A", "t", ["a-fact"]),
            new MemoryEntityInput("B", "t"),
            new MemoryEntityInput("C", "t")
        ]);
        await KanbanMcpTools.CreateRelations(svc,
        [
            new MemoryRelationInput("A", "B", "knows"),
            new MemoryRelationInput("A", "C", "knows")
        ]);

        var result = await KanbanMcpTools.OpenNodes(svc, ["A", "B", "Ghost"]);

        Assert.Equal(2, result.Entities.Count); // Ghost silently skipped
        Assert.Equal(["a-fact"], Assert.Single(result.Entities, e => e.Name == "A").Observations);
        // A→C is excluded because C was not opened.
        Assert.Equal(new MemoryRelationDto("A", "B", "knows"), Assert.Single(result.Relations));
    }

    [Fact]
    public async Task OpenNodes_NullNames_ThrowsMcpException()
    {
        using var f = new SqliteTestFactory();
        var svc = NewService(f);
        await Assert.ThrowsAsync<McpException>(() => KanbanMcpTools.OpenNodes(svc, null!));
    }

    // ── MCP SDK binding (the array-params decision, #224) ───────────────────────────────
    [Fact]
    public void SdkBindsAllNineTools_SnakeCaseNames_ArrayParamsInSchema_ServicesExcluded()
    {
        // Proves the SDK route chosen for complex params actually works: ModelContextProtocol 1.4.0
        // (AIFunctionFactory-based binding) must build every knowledge-graph tool from its MethodInfo,
        // exposing typed array-of-object parameters in the JSON schema while keeping the DI-resolved
        // service parameters OUT of it. If arrays of objects were unsupported, Create would throw and
        // the implementation would have to fall back to JSON-string params.
        using var f = new SqliteTestFactory();
        var services = new ServiceCollection()
            .AddSingleton<IDbContextFactory<AppDbContext>>(f)
            .AddSingleton<IMemoryService>(NewService(f))
            .AddSingleton<IMemorySearch>(NewSearch(f))
            .BuildServiceProvider();

        (string Method, string ToolName, string? ArrayParam)[] tools =
        [
            (nameof(KanbanMcpTools.CreateEntities), "create_entities", "entities"),
            (nameof(KanbanMcpTools.CreateRelations), "create_relations", "relations"),
            (nameof(KanbanMcpTools.AddObservations), "add_observations", "observations"),
            (nameof(KanbanMcpTools.DeleteEntities), "delete_entities", "entityNames"),
            (nameof(KanbanMcpTools.DeleteObservations), "delete_observations", "deletions"),
            (nameof(KanbanMcpTools.DeleteRelations), "delete_relations", "relations"),
            (nameof(KanbanMcpTools.ReadGraph), "read_graph", null),
            (nameof(KanbanMcpTools.SearchNodes), "search_nodes", null),
            (nameof(KanbanMcpTools.OpenNodes), "open_nodes", "names")
        ];

        foreach (var (method, toolName, arrayParam) in tools)
        {
            var tool = McpServerTool.Create(
                typeof(KanbanMcpTools).GetMethod(method)!,
                options: new McpServerToolCreateOptions { Services = services });

            Assert.Equal(toolName, tool.ProtocolTool.Name);

            var schema = tool.ProtocolTool.InputSchema;
            Assert.True(schema.TryGetProperty("properties", out var properties),
                $"{toolName}: input schema has no properties");

            if (arrayParam is not null)
            {
                Assert.True(properties.TryGetProperty(arrayParam, out var arrayProp),
                    $"{toolName}: '{arrayParam}' missing from input schema");
                Assert.Equal("array", arrayProp.GetProperty("type").GetString());
            }

            // DI-resolved services and the CancellationToken are invocation plumbing, not tool input.
            Assert.False(properties.TryGetProperty("memoryService", out _), $"{toolName}: leaked memoryService");
            Assert.False(properties.TryGetProperty("memorySearch", out _), $"{toolName}: leaked memorySearch");
            Assert.False(properties.TryGetProperty("cancellationToken", out _), $"{toolName}: leaked cancellationToken");
        }
    }

    // ── beyond the graph-read cap: mutation dedupe/cascade must stay correct ─────────────
    [Fact]
    public async Task KgMutations_DedupeAndCascade_StayCorrectBeyondGraphReadCap()
    {
        using var f = new SqliteTestFactory();
        var svc = NewService(f);

        // Fill the namespace past the ReadGraphAsync cap with direct inserts (bulk, one SaveChanges —
        // the fillers only need to occupy the capped oldest-first window; FTS/audit are irrelevant
        // to them). Targets created AFTERWARDS land beyond that window.
        await using (var db = await f.CreateDbContextAsync())
        {
            db.MemoryNodes.AddRange(Enumerable.Range(1, MemoryService.ReadGraphNodeCap + 5)
                .Select(i => new MemoryNode
                {
                    Type = MemoryType.Semantic,
                    Scope = MemoryScope.Agent,
                    ScopeKey = Kg,
                    Content = $"filler {i:D4}"
                }));
            await db.SaveChangesAsync();
        }

        await KanbanMcpTools.CreateEntities(svc,
        [
            new MemoryEntityInput("Beyond A", "person", ["already observed"]),
            new MemoryEntityInput("Beyond B", "person")
        ]);
        var firstEdge = await KanbanMcpTools.CreateRelations(svc,
            [new MemoryRelationInput("Beyond A", "Beyond B", "knows")]);
        Assert.Single(firstEdge);
        var beyondA = await NodeByName(f, "Beyond A");

        // Sanity: the capped graph read genuinely cannot see the targets (they're beyond the window)…
        var graph = await svc.ReadGraphAsync(MemoryScope.Agent, Kg);
        Assert.Equal(MemoryService.ReadGraphNodeCap, graph.Nodes.Count);
        Assert.DoesNotContain(graph.Nodes, n => n.Content == "Beyond A");

        // …yet the official dedupe still holds: an already-present observation and an exact-duplicate
        // relation are both skipped (id-targeted reads, not the capped graph — Gemini, PR #259).
        var additions = await KanbanMcpTools.AddObservations(svc,
            [new MemoryObservationInput("Beyond A", ["already observed"])]);
        Assert.Empty(additions.Single().AddedObservations);
        var duplicateEdge = await KanbanMcpTools.CreateRelations(svc,
            [new MemoryRelationInput("Beyond A", "Beyond B", "knows")]);
        Assert.Empty(duplicateEdge);

        // …and delete_entities still cascades the edge (no dangling relation rows survive).
        var deletion = await KanbanMcpTools.DeleteEntities(svc, ["Beyond A"]);
        Assert.Equal(["Beyond A"], deletion.Deleted);
        await using (var db = await f.CreateDbContextAsync())
        {
            Assert.False(await db.MemoryRelations.AnyAsync(r => r.FromId == beyondA.Id || r.ToId == beyondA.Id));
        }

        // The read tools are id-targeted too: an entity beyond the read_graph cap still opens by
        // name and still surfaces as a search match (only read_graph itself keeps the payload cap).
        var opened = await KanbanMcpTools.OpenNodes(svc, ["Beyond B", "not a real entity"]);
        Assert.Equal("Beyond B", Assert.Single(opened.Entities).Name);
        var found = await KanbanMcpTools.SearchNodes(NewSearch(f), svc, "beyond", k: 5);
        Assert.Contains(found.Entities, e => e.Name == "Beyond B");
    }

    /// <summary>Downed embedding transport (mirrors MemorySearchTests) — always throws, so the real
    /// EmbeddingService fail-softs and the retrieval degrades to keyword + recency.</summary>
    private sealed class StubEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
            => throw new HttpRequestException("stub: embedding provider unreachable");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
