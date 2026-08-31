using KanbanFlow.Models;

namespace KanbanFlow.Services;

/// <summary>
/// Builds the bounded, deterministic read model used by the Decision &amp; Memory Map (#496).
/// This is deliberately a projection over <see cref="MemoryGraph"/>, not a second graph store.
/// </summary>
internal static class DecisionGraphProjection
{
    internal const int DefaultNodeLimit = 80;
    internal const int DefaultEdgeLimit = DefaultNodeLimit * 4;
    internal const int MaxVisibleObservationsPerNode = 50;
    internal const int NodeWidth = 232;
    internal const int NodeHeight = 112;

    private const int CanvasPadding = 24;
    private const int LaneHeaderHeight = 48;
    private const int LaneGap = 64;
    private const int RowGap = 28;

    private static readonly DecisionGraphLane[] LaneOrder =
    [
        DecisionGraphLane.Context,
        DecisionGraphLane.Question,
        DecisionGraphLane.Option,
        DecisionGraphLane.Decision,
        DecisionGraphLane.Evidence,
        DecisionGraphLane.Outcome,
        DecisionGraphLane.Action,
        DecisionGraphLane.Other
    ];

    private static readonly HashSet<string> QuestionTags =
        new(StringComparer.OrdinalIgnoreCase) { "question", "open-question", "problem", "unknown" };
    private static readonly HashSet<string> OptionTags =
        new(StringComparer.OrdinalIgnoreCase) { "option", "alternative", "choice", "candidate" };
    private static readonly HashSet<string> DecisionTags =
        new(StringComparer.OrdinalIgnoreCase) { "decision", "decided", "selected", "chosen" };
    private static readonly HashSet<string> EvidenceTags =
        new(StringComparer.OrdinalIgnoreCase) { "evidence", "fact", "observation", "constraint", "reason", "rationale" };
    private static readonly HashSet<string> OutcomeTags =
        new(StringComparer.OrdinalIgnoreCase) { "outcome", "result", "consequence", "learning", "verification" };
    private static readonly HashSet<string> ActionTags =
        new(StringComparer.OrdinalIgnoreCase) { "action", "next-action", "procedure", "plan", "step", "follow-up" };
    private static readonly HashSet<string> ContextTags =
        new(StringComparer.OrdinalIgnoreCase) { "context", "project", "system", "concept", "person", "background" };

    internal static DecisionGraphView Build(
        MemoryGraph graph,
        DecisionGraphFilter? filter = null,
        int nodeLimit = DefaultNodeLimit,
        int edgeLimit = DefaultEdgeLimit)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (nodeLimit <= 0)
            throw new ArgumentOutOfRangeException(nameof(nodeLimit), "Node limit must be greater than zero.");
        if (edgeLimit <= 0)
            throw new ArgumentOutOfRangeException(nameof(edgeLimit), "Edge limit must be greater than zero.");

        filter ??= DecisionGraphFilter.Empty;

        var observationsByNode = graph.Observations
            .GroupBy(o => o.MemoryNodeId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Observation>)g.OrderBy(o => o.Id).ToList());

        var candidates = graph.Nodes
            .Select(node => new Candidate(
                node,
                Classify(node),
                observationsByNode.GetValueOrDefault(node.Id) ?? []))
            .Where(candidate => Matches(candidate, filter))
            .OrderBy(candidate => LaneRank(candidate.Lane))
            .ThenByDescending(candidate => candidate.Node.CreatedAt)
            .ThenBy(candidate => candidate.Node.Id)
            .ToList();

        var candidateIds = candidates.Select(candidate => candidate.Node.Id).ToHashSet();
        var graphRelations = graph.Relations
            .Where(relation =>
                relation.FromType == "MemoryNode"
                && relation.ToType == "MemoryNode"
                && candidateIds.Contains(relation.FromId)
                && candidateIds.Contains(relation.ToId))
            .OrderBy(relation => relation.Id)
            .ToList();

        var adjacent = BuildAdjacency(graphRelations);
        var selected = SelectNodes(candidates, adjacent, nodeLimit);
        var selectedIds = selected.Select(candidate => candidate.Node.Id).ToHashSet();
        var decisionIds = selected
            .Where(candidate => candidate.Lane == DecisionGraphLane.Decision)
            .Select(candidate => candidate.Node.Id)
            .ToHashSet();
        var matchingRelations = graphRelations
            .Where(relation => selectedIds.Contains(relation.FromId) && selectedIds.Contains(relation.ToId))
            .GroupBy(relation => new EdgeIdentity(relation.FromId, relation.RelationType, relation.ToId))
            .Select(group => group.OrderBy(relation => relation.Id).First())
            .OrderByDescending(relation => decisionIds.Contains(relation.FromId) || decisionIds.Contains(relation.ToId))
            .ThenByDescending(relation => relation.CreatedAt)
            .ThenBy(relation => relation.Id)
            .ToList();
        var selectedRelations = matchingRelations.Take(edgeLimit).ToList();

        var populatedLanes = LaneOrder
            .Where(lane => selected.Any(candidate => candidate.Lane == lane))
            .ToList();

        var laneViews = new List<DecisionGraphLaneView>(populatedLanes.Count);
        var nodeViews = new List<DecisionGraphNodeView>(selected.Count);
        var maxRows = 0;

        for (var laneIndex = 0; laneIndex < populatedLanes.Count; laneIndex++)
        {
            var lane = populatedLanes[laneIndex];
            var laneNodes = selected
                .Where(candidate => candidate.Lane == lane)
                .OrderByDescending(candidate => candidate.Node.CreatedAt)
                .ThenBy(candidate => candidate.Node.Id)
                .ToList();
            var x = CanvasPadding + laneIndex * (NodeWidth + LaneGap);

            laneViews.Add(new DecisionGraphLaneView(lane, x, laneNodes.Count));
            maxRows = Math.Max(maxRows, laneNodes.Count);

            for (var row = 0; row < laneNodes.Count; row++)
            {
                var candidate = laneNodes[row];
                var y = CanvasPadding + LaneHeaderHeight + row * (NodeHeight + RowGap);
                var visibleObservations = SelectVisibleObservations(candidate.Observations, filter.Query);
                nodeViews.Add(new DecisionGraphNodeView(
                    candidate.Node,
                    candidate.Lane,
                    x,
                    y,
                    visibleObservations,
                    candidate.Observations.Count,
                    candidate.Observations.Count > visibleObservations.Count));
            }
        }

        var edgeViews = selectedRelations
            .Select(relation => new DecisionGraphEdgeView(
                relation,
                HumanizeRelation(relation.RelationType)))
            .ToList();

        var width = populatedLanes.Count == 0
            ? 720
            : CanvasPadding * 2 + populatedLanes.Count * NodeWidth + (populatedLanes.Count - 1) * LaneGap;
        var height = maxRows == 0
            ? 300
            : CanvasPadding * 2 + LaneHeaderHeight + maxRows * NodeHeight + (maxRows - 1) * RowGap;

        return new DecisionGraphView(
            nodeViews,
            edgeViews,
            laneViews,
            width,
            height,
            graph.Nodes.Count,
            candidates.Count,
            matchingRelations.Count,
            candidates.Count > selected.Count,
            matchingRelations.Count > selectedRelations.Count,
            graph.Nodes.Count >= MemoryService.ReadGraphNodeCap);
    }

    internal static DecisionGraphLane Classify(MemoryNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.Type == MemoryType.Decision)
            return DecisionGraphLane.Decision;

        var tags = TokenizeTags(node.Tags);
        if (tags.Overlaps(QuestionTags)) return DecisionGraphLane.Question;
        if (tags.Overlaps(OptionTags)) return DecisionGraphLane.Option;
        if (tags.Overlaps(DecisionTags)) return DecisionGraphLane.Decision;
        if (tags.Overlaps(EvidenceTags)) return DecisionGraphLane.Evidence;
        if (tags.Overlaps(OutcomeTags)) return DecisionGraphLane.Outcome;
        if (tags.Overlaps(ActionTags)) return DecisionGraphLane.Action;
        if (tags.Overlaps(ContextTags)) return DecisionGraphLane.Context;

        return node.Type switch
        {
            MemoryType.Episodic => DecisionGraphLane.Evidence,
            MemoryType.Procedural => DecisionGraphLane.Action,
            MemoryType.Semantic => DecisionGraphLane.Context,
            _ => DecisionGraphLane.Other
        };
    }

    private static HashSet<string> TokenizeTags(string? tags)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(tags))
            return result;

        foreach (var segment in tags.Split([',', ';', '|', '/', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalized = NormalizeTag(segment);
            if (normalized.Length == 0)
                continue;

            result.Add(normalized);
            foreach (var token in normalized.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                result.Add(token);
        }

        return result;
    }

    private static string NormalizeTag(string value) =>
        string.Join('-', value.Trim().ToLowerInvariant()
            .Replace('_', '-')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static bool Matches(Candidate candidate, DecisionGraphFilter filter)
    {
        if (filter.Lane is not null && candidate.Lane != filter.Lane)
            return false;
        if (string.IsNullOrWhiteSpace(filter.Query))
            return true;

        var query = filter.Query.Trim();
        return Contains(candidate.Node.Content, query)
            || Contains(candidate.Node.Tags, query)
            || Contains(candidate.Node.Source, query)
            || candidate.Observations.Any(observation =>
                Contains(observation.Content, query) || Contains(observation.Source, query));
    }

    private static bool Contains(string? value, string query) =>
        value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;

    private static IReadOnlyList<Observation> SelectVisibleObservations(
        IReadOnlyList<Observation> observations,
        string? query)
    {
        var newestFirst = observations
            .OrderByDescending(observation => observation.CreatedAt)
            .ThenByDescending(observation => observation.Id)
            .ToList();
        var selected = new List<Observation>(Math.Min(MaxVisibleObservationsPerNode, observations.Count));
        var selectedIds = new HashSet<int>();

        void Add(Observation observation)
        {
            if (selected.Count < MaxVisibleObservationsPerNode && selectedIds.Add(observation.Id))
                selected.Add(observation);
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            var needle = query.Trim();
            foreach (var observation in newestFirst.Where(observation =>
                         Contains(observation.Content, needle) || Contains(observation.Source, needle)))
                Add(observation);
        }

        foreach (var observation in newestFirst)
            Add(observation);

        return selected
            .OrderBy(observation => observation.CreatedAt)
            .ThenBy(observation => observation.Id)
            .ToList();
    }

    private static Dictionary<int, IReadOnlyList<int>> BuildAdjacency(IReadOnlyList<MemoryRelation> relations)
    {
        var mutable = new Dictionary<int, HashSet<int>>();
        foreach (var relation in relations)
        {
            AddNeighbor(mutable, relation.FromId, relation.ToId);
            AddNeighbor(mutable, relation.ToId, relation.FromId);
        }

        return mutable.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<int>)pair.Value.Order().ToList());
    }

    private static void AddNeighbor(Dictionary<int, HashSet<int>> adjacent, int nodeId, int neighborId)
    {
        if (!adjacent.TryGetValue(nodeId, out var neighbors))
        {
            neighbors = [];
            adjacent[nodeId] = neighbors;
        }
        neighbors.Add(neighborId);
    }

    private static List<Candidate> SelectNodes(
        IReadOnlyList<Candidate> candidates,
        IReadOnlyDictionary<int, IReadOnlyList<int>> adjacent,
        int nodeLimit)
    {
        var byId = candidates.ToDictionary(candidate => candidate.Node.Id);
        var selected = new List<Candidate>(Math.Min(nodeLimit, candidates.Count));
        var selectedIds = new HashSet<int>();

        void Add(Candidate candidate)
        {
            if (selected.Count < nodeLimit && selectedIds.Add(candidate.Node.Id))
                selected.Add(candidate);
        }

        void AddLane(DecisionGraphLane lane)
        {
            foreach (var candidate in candidates.Where(candidate => candidate.Lane == lane))
                Add(candidate);
        }

        // Decisions are never displaced by lower-signal context. Their immediate neighbors then
        // preserve the rationale/options/evidence that make those decisions understandable.
        AddLane(DecisionGraphLane.Decision);
        var decisionIds = selected.Select(candidate => candidate.Node.Id).ToList();
        foreach (var decisionId in decisionIds)
        {
            if (!adjacent.TryGetValue(decisionId, out var neighbors))
                continue;
            foreach (var neighborId in neighbors)
            {
                if (byId.TryGetValue(neighborId, out var neighbor))
                    Add(neighbor);
            }
        }

        AddLane(DecisionGraphLane.Question);
        AddLane(DecisionGraphLane.Option);
        AddLane(DecisionGraphLane.Outcome);

        foreach (var candidate in candidates)
            Add(candidate);

        return selected;
    }

    private static int LaneRank(DecisionGraphLane lane) => Array.IndexOf(LaneOrder, lane);

    private static string HumanizeRelation(string relationType) =>
        relationType.Trim().Replace('_', ' ').Replace('-', ' ');

    private sealed record Candidate(
        MemoryNode Node,
        DecisionGraphLane Lane,
        IReadOnlyList<Observation> Observations);

    private sealed record EdgeIdentity(int FromId, string RelationType, int ToId);
}

public enum DecisionGraphLane
{
    Context,
    Question,
    Option,
    Decision,
    Evidence,
    Outcome,
    Action,
    Other
}

internal sealed record DecisionGraphFilter(string? Query = null, DecisionGraphLane? Lane = null)
{
    internal static readonly DecisionGraphFilter Empty = new();
}

public sealed record DecisionGraphView(
    IReadOnlyList<DecisionGraphNodeView> Nodes,
    IReadOnlyList<DecisionGraphEdgeView> Edges,
    IReadOnlyList<DecisionGraphLaneView> Lanes,
    int Width,
    int Height,
    int SourceNodeCount,
    int MatchingNodeCount,
    int MatchingEdgeCount,
    bool IsRenderTruncated,
    bool IsEdgeRenderTruncated,
    bool SourceMayBeTruncated);

public sealed record DecisionGraphNodeView(
    MemoryNode Node,
    DecisionGraphLane Lane,
    int X,
    int Y,
    IReadOnlyList<Observation> Observations,
    int ObservationCount,
    bool ObservationsTruncated);

public sealed record DecisionGraphEdgeView(MemoryRelation Relation, string Label);

public sealed record DecisionGraphLaneView(DecisionGraphLane Lane, int X, int NodeCount);
