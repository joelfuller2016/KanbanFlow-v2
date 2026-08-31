using KanbanFlow.Models;
using KanbanFlow.Services;

namespace KanbanFlow.Tests;

public class DecisionGraphProjectionTests
{
    [Theory]
    [InlineData(MemoryType.Semantic, "OPEN_QUESTION", DecisionGraphLane.Question)]
    [InlineData(MemoryType.Semantic, "candidate-option", DecisionGraphLane.Option)]
    [InlineData(MemoryType.Semantic, "Decision", DecisionGraphLane.Decision)]
    [InlineData(MemoryType.Semantic, "constraint, Evidence", DecisionGraphLane.Evidence)]
    [InlineData(MemoryType.Semantic, "verification/result", DecisionGraphLane.Outcome)]
    [InlineData(MemoryType.Semantic, "next action", DecisionGraphLane.Action)]
    [InlineData(MemoryType.Semantic, "project", DecisionGraphLane.Context)]
    [InlineData(MemoryType.Episodic, null, DecisionGraphLane.Evidence)]
    [InlineData(MemoryType.Procedural, null, DecisionGraphLane.Action)]
    public void Classify_UsesMemoryTypeAndNormalizedEntityTags(
        MemoryType type,
        string? tags,
        DecisionGraphLane expected)
    {
        var node = Node(1, type, "record", tags);

        Assert.Equal(expected, DecisionGraphProjection.Classify(node));
    }

    [Fact]
    public void Classify_ExplicitDecisionTypeWinsOverConflictingTags()
    {
        var node = Node(1, MemoryType.Decision, "chosen architecture", "question");

        Assert.Equal(DecisionGraphLane.Decision, DecisionGraphProjection.Classify(node));
    }

    [Fact]
    public void Build_SearchesObservationsAndOmitsEdgesWhoseOtherEndpointIsFilteredOut()
    {
        var context = Node(1, MemoryType.Semantic, "runtime context", "context");
        var decision = Node(2, MemoryType.Decision, "use the existing graph", "decision");
        var graph = new MemoryGraph(
            [context, decision],
            [new Observation { Id = 10, MemoryNodeId = decision.Id, Content = "Verified by the needle test" }],
            [new MemoryRelation
            {
                Id = 20,
                FromType = "MemoryNode",
                FromId = context.Id,
                RelationType = "supports",
                ToType = "MemoryNode",
                ToId = decision.Id
            }]);

        var result = DecisionGraphProjection.Build(graph, new DecisionGraphFilter("needle"));

        var visible = Assert.Single(result.Nodes);
        Assert.Equal(decision.Id, visible.Node.Id);
        Assert.Single(visible.Observations);
        Assert.Empty(result.Edges);
        Assert.Equal(1, result.MatchingNodeCount);
    }

    [Fact]
    public void Build_IsDeterministicAndToleratesCyclesSelfLinksAndDanglingEdges()
    {
        var first = Node(1, MemoryType.Semantic, "first", "option");
        var second = Node(2, MemoryType.Semantic, "second", "option");
        var third = Node(3, MemoryType.Decision, "third", "decision");
        var graph = new MemoryGraph(
            [third, first, second],
            [],
            [
                Relation(1, first.Id, second.Id, "leads_to"),
                Relation(2, second.Id, first.Id, "returns_to"),
                Relation(3, third.Id, third.Id, "reaffirms"),
                Relation(4, third.Id, 999, "missing_endpoint")
            ]);

        var firstBuild = DecisionGraphProjection.Build(graph);
        var secondBuild = DecisionGraphProjection.Build(graph);

        Assert.Equal(
            firstBuild.Nodes.Select(node => (node.Node.Id, node.Lane, node.X, node.Y)),
            secondBuild.Nodes.Select(node => (node.Node.Id, node.Lane, node.X, node.Y)));
        Assert.Equal(new[] { 1, 2, 3 }, firstBuild.Edges.Select(edge => edge.Relation.Id).Order().ToArray());
        Assert.DoesNotContain(firstBuild.Edges, edge => edge.Relation.Id == 4);
        Assert.Equal(3, firstBuild.Nodes.Select(node => (node.X, node.Y)).Distinct().Count());
    }

    [Fact]
    public void Build_RenderCapKeepsDecisionAndItsNeighborAndReportsOmission()
    {
        var contextNodes = Enumerable.Range(1, 5)
            .Select(id => Node(id, MemoryType.Semantic, $"context {id}", "context"))
            .ToList();
        var decision = Node(100, MemoryType.Decision, "ship the map", "decision");
        var graph = new MemoryGraph(
            [.. contextNodes, decision],
            [],
            [Relation(1, decision.Id, contextNodes[4].Id, "based_on")]);

        var result = DecisionGraphProjection.Build(graph, nodeLimit: 2);

        Assert.Equal(6, result.MatchingNodeCount);
        Assert.True(result.IsRenderTruncated);
        Assert.Contains(result.Nodes, node => node.Node.Id == decision.Id);
        Assert.Contains(result.Nodes, node => node.Node.Id == contextNodes[4].Id);
        Assert.Single(result.Edges);
    }

    [Fact]
    public void Build_AssignsDistinctSemanticLanesAndHonestStoreCapWarning()
    {
        var nodes = Enumerable.Range(1, MemoryService.ReadGraphNodeCap)
            .Select(id => Node(
                id,
                id == 2 ? MemoryType.Decision : MemoryType.Semantic,
                $"record {id}",
                id == 1 ? "question" : id == 3 ? "outcome" : null))
            .ToList();

        var result = DecisionGraphProjection.Build(new MemoryGraph(nodes, [], []), nodeLimit: 10);

        Assert.True(result.SourceMayBeTruncated);
        Assert.True(result.IsRenderTruncated);
        Assert.Contains(result.Lanes, lane => lane.Lane == DecisionGraphLane.Question);
        Assert.Contains(result.Lanes, lane => lane.Lane == DecisionGraphLane.Decision);
        Assert.Contains(result.Lanes, lane => lane.Lane == DecisionGraphLane.Outcome);
        Assert.Equal(result.Lanes.Count, result.Lanes.Select(lane => lane.X).Distinct().Count());
    }

    [Fact]
    public void Build_RejectsNonPositiveRenderCap()
    {
        var graph = new MemoryGraph([], [], []);

        Assert.Throws<ArgumentOutOfRangeException>(() => DecisionGraphProjection.Build(graph, nodeLimit: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => DecisionGraphProjection.Build(graph, edgeLimit: 0));
    }

    [Fact]
    public void Build_BoundsDenseRelationsDeterministicallyAndPrioritizesDecisionEdges()
    {
        var nodes = Enumerable.Range(1, DecisionGraphProjection.DefaultNodeLimit)
            .Select(id => Node(
                id,
                id == DecisionGraphProjection.DefaultNodeLimit ? MemoryType.Decision : MemoryType.Semantic,
                $"record {id}",
                id == DecisionGraphProjection.DefaultNodeLimit ? "decision" : "context"))
            .ToList();
        var decisionId = nodes[^1].Id;
        var nextRelationId = 1;
        var relations = nodes
            .Where(node => node.Id != decisionId)
            .Select(node => Relation(nextRelationId++, decisionId, node.Id, "considers"))
            .ToList();
        relations.AddRange(nodes
            .Where(node => node.Id != decisionId)
            .SelectMany(from => nodes
                .Where(to => to.Id != decisionId && to.Id != from.Id)
                .Select(to => Relation(nextRelationId++, from.Id, to.Id, "relates_to")))
            .Take(DecisionGraphProjection.DefaultEdgeLimit + 50));
        relations.Add(Relation(nextRelationId, decisionId, nodes[0].Id, "considers")); // exact duplicate
        var graph = new MemoryGraph(nodes, [], relations);

        var first = DecisionGraphProjection.Build(graph);
        var second = DecisionGraphProjection.Build(graph);

        Assert.Equal(DecisionGraphProjection.DefaultEdgeLimit, first.Edges.Count);
        Assert.Equal(relations.Count - 1, first.MatchingEdgeCount);
        Assert.True(first.IsEdgeRenderTruncated);
        Assert.All(first.Edges, edge =>
        {
            Assert.Contains(first.Nodes, node => node.Node.Id == edge.Relation.FromId);
            Assert.Contains(first.Nodes, node => node.Node.Id == edge.Relation.ToId);
        });
        Assert.All(
            nodes.Where(node => node.Id != decisionId),
            node => Assert.Contains(first.Edges, edge =>
                edge.Relation.FromId == decisionId && edge.Relation.ToId == node.Id));
        Assert.Equal(
            first.Edges.Select(edge => edge.Relation.Id),
            second.Edges.Select(edge => edge.Relation.Id));
    }

    [Fact]
    public void Build_BoundsVisibleObservationsButSearchesTheCompleteHistory()
    {
        var node = Node(1, MemoryType.Semantic, "bounded record", "context");
        var observations = Enumerable.Range(1, 1_000)
            .Select(id => new Observation
            {
                Id = id,
                MemoryNodeId = node.Id,
                Content = id == 1 ? "old needle" : $"observation {id}",
                CreatedAt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(id)
            })
            .ToList();
        var graph = new MemoryGraph([node], observations, []);

        var result = DecisionGraphProjection.Build(graph, new DecisionGraphFilter("needle"));

        var visible = Assert.Single(result.Nodes);
        Assert.Equal(1_000, visible.ObservationCount);
        Assert.True(visible.ObservationsTruncated);
        Assert.Equal(DecisionGraphProjection.MaxVisibleObservationsPerNode, visible.Observations.Count);
        Assert.Equal(1, visible.Observations[0].Id);
        Assert.Equal(952, visible.Observations[1].Id);
        Assert.Equal(1_000, visible.Observations[^1].Id);
        Assert.Contains(visible.Observations, observation => observation.Content.Contains("needle"));
    }

    [Fact]
    public void Build_OneFullLaneProducesAViewportScrollableCanvas()
    {
        var nodes = Enumerable.Range(1, DecisionGraphProjection.DefaultNodeLimit)
            .Select(id => Node(id, MemoryType.Semantic, $"context {id}", "context"))
            .ToList();

        var result = DecisionGraphProjection.Build(new MemoryGraph(nodes, [], []));

        Assert.Single(result.Lanes);
        Assert.Equal(DecisionGraphProjection.DefaultNodeLimit, result.Nodes.Count);
        Assert.True(result.Height > 10_000);
    }

    private static MemoryNode Node(int id, MemoryType type, string content, string? tags) => new()
    {
        Id = id,
        Type = type,
        Scope = MemoryScope.Agent,
        ScopeKey = "knowledge-graph",
        Content = content,
        Tags = tags,
        CreatedAt = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc).AddMinutes(id),
        ValidFrom = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc).AddMinutes(id)
    };

    private static MemoryRelation Relation(int id, int fromId, int toId, string relationType) => new()
    {
        Id = id,
        FromType = "MemoryNode",
        FromId = fromId,
        RelationType = relationType,
        ToType = "MemoryNode",
        ToId = toId,
        CreatedAt = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc).AddSeconds(id)
    };
}
