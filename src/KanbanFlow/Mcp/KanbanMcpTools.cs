using System.ComponentModel;
using KanbanFlow.Data;
using KanbanFlow.Models;
using KanbanFlow.Services;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace KanbanFlow.Mcp;

/// <summary>
/// Prototype native (in-process) MCP tool surface for KanbanFlow v2.
///
/// These tools run inside the same ASP.NET Core process as the REST API and the
/// Blazor UI. Unlike the Node wrapper (<c>scripts/mcp-server.mjs</c>), which forwards
/// JSON-RPC to the REST API over HTTP, these methods call the EF Core data layer and
/// the audit-aware <see cref="ITaskService"/> DIRECTLY via DI — no extra network hop.
///
/// This is a curated vertical slice (5 tools) proving parity with the Node wrapper,
/// NOT the full 36-tool surface. See <c>docs/native-mcp-notes.md</c> for the rationale,
/// the path to full coverage, and the Kebechet.Api.ToMcp evaluation.
///
/// Tool-design conventions follow Anthropic's mcp-builder skill: snake_case names,
/// concise descriptions that say what the tool does and what to do next, and
/// actionable error messages.
/// </summary>
[McpServerToolType]
public static class KanbanMcpTools
{
    private const string DefaultActor = "mcp-agent";

    [McpServerTool(Name = "list_projects")]
    [Description("List all projects with id, name, description, goals, and counts of features and tasks. Start here to discover which project to work in.")]
    public static async Task<IReadOnlyList<ProjectSummary>> ListProjects(
        IDbContextFactory<AppDbContext> dbFactory,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var projects = await db.Projects.AsNoTracking()
            .OrderBy(p => p.Id)
            .ToListAsync(cancellationToken);
        var projectIds = projects.Select(p => p.Id).ToList();

        // Scope the count aggregations to the projects we just loaded rather than
        // aggregating across the whole DB (cheap now, avoids a scan as projects grow).
        var featureCounts = (await db.Features.AsNoTracking()
                .Where(f => projectIds.Contains(f.ProjectId))
                .GroupBy(f => f.ProjectId)
                .Select(g => new { ProjectId = g.Key, Count = g.Count() })
                .ToListAsync(cancellationToken))
            .ToDictionary(x => x.ProjectId, x => x.Count);

        var taskCounts = (await db.Tasks.AsNoTracking()
                .Where(t => projectIds.Contains(t.Feature.ProjectId))
                .GroupBy(t => t.Feature.ProjectId)
                .Select(g => new { ProjectId = g.Key, Count = g.Count() })
                .ToListAsync(cancellationToken))
            .ToDictionary(x => x.ProjectId, x => x.Count);

        return projects
            .Select(p => new ProjectSummary(
                p.Id, p.Name, p.Description, p.Goals,
                featureCounts.GetValueOrDefault(p.Id),
                taskCounts.GetValueOrDefault(p.Id)))
            .ToList();
    }

    [McpServerTool(Name = "get_project_context")]
    [Description("Return the full project graph in one payload: project docs (description, goals, readme), all features (with parentFeatureId for the tree), and every task (status, priority, assignee, claim). Load this before planning work on a project.")]
    public static async Task<ProjectContext> GetProjectContext(
        IDbContextFactory<AppDbContext> dbFactory,
        [Description("The project id (from list_projects).")] int projectId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var project = await db.Projects.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken)
            ?? throw new McpException($"Project {projectId} not found. Call list_projects to see valid ids.");

        // Materialise, then map: EF Core cannot reliably translate enum.ToString() inside
        // a projection, so we pull the entities first and shape them in memory.
        var features = (await db.Features.AsNoTracking()
                .Where(f => f.ProjectId == projectId)
                .OrderBy(f => f.SortOrder).ThenBy(f => f.Id)
                .ToListAsync(cancellationToken))
            .Select(f => new FeatureNode(f.Id, f.ParentFeatureId, f.Name, f.Description))
            .ToList();

        var tasks = (await db.Tasks.AsNoTracking()
                .Where(t => t.Feature.ProjectId == projectId)
                .OrderBy(t => t.FeatureId).ThenBy(t => t.SortOrder).ThenBy(t => t.Id)
                .ToListAsync(cancellationToken))
            .Select(t => new TaskNode(
                t.Id, t.FeatureId, t.Name, t.Status.ToString(), t.Priority.ToString(),
                t.Assignee, t.ClaimedBy))
            .ToList();

        return new ProjectContext(
            project.Id, project.Name, project.Description, project.Goals, project.Readme,
            features, tasks);
    }

    [McpServerTool(Name = "get_task")]
    [Description("Return one task in detail: description, status, priority, claim, acceptance criteria, subtasks, and the ids of tasks it depends on. Call before working a task.")]
    public static async Task<TaskDetail> GetTask(
        IDbContextFactory<AppDbContext> dbFactory,
        [Description("The task id.")] int taskId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // AsSplitQuery: three collection Includes would otherwise produce a cartesian
        // JOIN that explodes row counts for tasks with many children.
        var task = await db.Tasks.AsNoTracking()
            .Include(t => t.AcceptanceCriteria)
            .Include(t => t.Subtasks)
            .Include(t => t.Dependencies)
            .AsSplitQuery()
            .FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken)
            ?? throw new McpException($"Task {taskId} not found.");

        // Stable, deterministic ordering for agent payloads: SortOrder, then Id as a tie-break.
        return new TaskDetail(
            task.Id, task.FeatureId, task.Name, task.Description,
            task.Status.ToString(), task.Priority.ToString(), task.Assignee, task.ClaimedBy,
            task.AcceptanceCriteria.OrderBy(c => c.SortOrder).ThenBy(c => c.Id)
                .Select(c => new ChecklistItem(c.Id, c.Text, c.Completed)).ToList(),
            task.Subtasks.OrderBy(s => s.SortOrder).ThenBy(s => s.Id)
                .Select(s => new ChecklistItem(s.Id, s.Text, s.Completed)).ToList(),
            task.Dependencies.Select(d => d.DependsOnTaskItemId).OrderBy(id => id).ToList());
    }

    [McpServerTool(Name = "get_next_tasks")]
    [Description("Recommend what to work on next in a project: not-done tasks, excluding any blocked by incomplete dependencies, priority-sorted (Critical first), de-prioritising tasks claimed by other actors. Each result includes a human-readable reason. This is the project's 'direction' primitive.")]
    public static async Task<IReadOnlyList<NextTask>> GetNextTasks(
        IDbContextFactory<AppDbContext> dbFactory,
        [Description("The project id.")] int projectId,
        [Description("Optional actor label; tasks claimed by a different actor are de-prioritised.")] string? actor = null,
        [Description("Maximum results (default 5, clamped to 1-50).")] int limit = 5,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 50);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        if (!await db.Projects.AsNoTracking().AnyAsync(p => p.Id == projectId, cancellationToken))
            throw new McpException($"Project {projectId} not found.");

        // Status of every task in the project, to resolve dependency completion.
        var statusById = (await db.Tasks.AsNoTracking()
                .Where(t => t.Feature.ProjectId == projectId)
                .Select(t => new { t.Id, t.Status })
                .ToListAsync(cancellationToken))
            .ToDictionary(x => x.Id, x => x.Status);

        var openTasks = await db.Tasks.AsNoTracking()
            .Where(t => t.Feature.ProjectId == projectId && t.Status != WorkStatus.Done)
            .Include(t => t.Dependencies)
            .ToListAsync(cancellationToken);

        return openTasks
            .Select(t =>
            {
                var hasOpenBlocker = t.Dependencies.Any(d =>
                    !statusById.TryGetValue(d.DependsOnTaskItemId, out var s) || s != WorkStatus.Done);
                var claimedByOther = !string.IsNullOrWhiteSpace(t.ClaimedBy)
                    && !string.Equals(t.ClaimedBy, actor, StringComparison.OrdinalIgnoreCase);
                return (Task: t, HasOpenBlocker: hasOpenBlocker, ClaimedByOther: claimedByOther);
            })
            .Where(x => !x.HasOpenBlocker)
            .OrderBy(x => x.ClaimedByOther)                                   // unclaimed / own first
            .ThenByDescending(x => x.Task.Priority)                           // Critical → Low
            .ThenBy(x => x.Task.Status == WorkStatus.InProgress ? 0 : 1)      // in-progress first
            .ThenBy(x => x.Task.Id)                                           // stable, oldest first
            .Take(limit)
            .Select(x => new NextTask(
                x.Task.Id, x.Task.Name, x.Task.Status.ToString(), x.Task.Priority.ToString(),
                x.Task.ClaimedBy, BuildReason(x.Task, x.ClaimedByOther)))
            .ToList();
    }

    [McpServerTool(Name = "update_task_status")]
    [Description("Change a task's status through the audit-aware service layer (writes a TaskStatusChanged activity event + StatusChange comment; no HTTP hop). Valid statuses: Backlog, Todo, InProgress, Review, Done.")]
    public static async Task<TaskStatusResult> UpdateTaskStatus(
        ITaskService taskService,
        [Description("The task id.")] int taskId,
        [Description("New status: Backlog | Todo | InProgress | Review | Done (case-insensitive).")] string status,
        [Description("Optional actor label recorded in the audit trail (default 'mcp-agent').")] string? actor = null,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<WorkStatus>(status, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            throw new McpException(
                $"Invalid status '{status}'. Valid values: {string.Join(", ", Enum.GetNames<WorkStatus>())}.");

        try
        {
            var updated = await taskService.ChangeStatusAsync(
                taskId, parsed, string.IsNullOrWhiteSpace(actor) ? DefaultActor : actor.Trim(), cancellationToken);
            return new TaskStatusResult(
                updated.Id, updated.Name, updated.Status.ToString(),
                "Status updated; audit event recorded.");
        }
        catch (TaskNotFoundException)
        {
            throw new McpException($"Task {taskId} not found.");
        }
    }

    private static string BuildReason(TaskItem task, bool claimedByOther)
    {
        var parts = new List<string> { $"{task.Priority} priority" };
        if (task.Status == WorkStatus.InProgress) parts.Add("already in progress");
        parts.Add("no open blockers");
        if (claimedByOther) parts.Add($"claimed by {task.ClaimedBy} (deprioritised)");
        else if (string.IsNullOrWhiteSpace(task.ClaimedBy)) parts.Add("unclaimed");
        return "Top because: " + string.Join(", ", parts) + ".";
    }
}

// ── Response DTOs (records → serialised to JSON by the MCP SDK) ────────────────────
public sealed record ProjectSummary(int Id, string Name, string? Description, string? Goals, int FeatureCount, int TaskCount);
public sealed record FeatureNode(int Id, int? ParentFeatureId, string Name, string? Description);
public sealed record TaskNode(int Id, int FeatureId, string Name, string Status, string Priority, string? Assignee, string? ClaimedBy);
public sealed record ProjectContext(int Id, string Name, string? Description, string? Goals, string? Readme, IReadOnlyList<FeatureNode> Features, IReadOnlyList<TaskNode> Tasks);
public sealed record ChecklistItem(int Id, string Text, bool Completed);
public sealed record TaskDetail(int Id, int FeatureId, string Name, string? Description, string Status, string Priority, string? Assignee, string? ClaimedBy, IReadOnlyList<ChecklistItem> AcceptanceCriteria, IReadOnlyList<ChecklistItem> Subtasks, IReadOnlyList<int> DependsOnTaskIds);
public sealed record NextTask(int Id, string Name, string Status, string Priority, string? ClaimedBy, string Reason);
public sealed record TaskStatusResult(int Id, string Name, string Status, string Message);
