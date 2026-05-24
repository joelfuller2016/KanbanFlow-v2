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
/// This is a curated slice (15 tools: 7 read + 8 audit-aware write) proving parity with the
/// Node wrapper, not yet the full 41-tool surface. See <c>docs/native-mcp-notes.md</c> for the rationale,
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

    [McpServerTool(Name = "create_task")]
    [Description("Create a task inside a feature through the audit-aware service layer (writes a Created comment + TaskCreated activity event; no HTTP hop). Defaults: status=Backlog, priority=Low. Returns the new task — pass its id to update_task_status, claim_task, or add_task_comment next.")]
    public static async Task<TaskDetailResult> CreateTask(
        ITaskService taskService,
        [Description("The feature id this task belongs to (from get_project_context).")] int featureId,
        [Description("Task name (required, <= 500 chars).")] string name,
        [Description("Optional task description (markdown).")] string? description = null,
        [Description("Optional initial status: Backlog | Todo | InProgress | Review | Done (case-insensitive). Defaults to Backlog.")] string? status = null,
        [Description("Optional initial priority: Low | Medium | High | Critical (case-insensitive). Defaults to Low.")] string? priority = null,
        [Description("Optional free-text assignee label.")] string? assignee = null,
        [Description("Optional actor label recorded in the audit trail (default 'mcp-agent').")] string? actor = null,
        CancellationToken cancellationToken = default)
    {
        WorkStatus? parsedStatus = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<WorkStatus>(status, ignoreCase: true, out var s) || !Enum.IsDefined(s))
                throw new McpException(
                    $"Invalid status '{status}'. Valid values: {string.Join(", ", Enum.GetNames<WorkStatus>())}.");
            parsedStatus = s;
        }

        TaskPriority? parsedPriority = null;
        if (!string.IsNullOrWhiteSpace(priority))
        {
            if (!Enum.TryParse<TaskPriority>(priority, ignoreCase: true, out var p) || !Enum.IsDefined(p))
                throw new McpException(
                    $"Invalid priority '{priority}'. Valid values: {string.Join(", ", Enum.GetNames<TaskPriority>())}.");
            parsedPriority = p;
        }

        try
        {
            var created = await taskService.CreateAsync(
                new TaskCreate(featureId, name, description, parsedStatus, parsedPriority, assignee),
                string.IsNullOrWhiteSpace(actor) ? DefaultActor : actor.Trim(),
                cancellationToken);
            return new TaskDetailResult(
                created.Id, created.FeatureId, created.Name, created.Description,
                created.Status.ToString(), created.Priority.ToString(), created.Assignee,
                "Task created; audit event recorded.");
        }
        catch (FeatureNotFoundException)
        {
            throw new McpException($"Feature {featureId} not found. Call get_project_context to see valid feature ids.");
        }
        catch (ValidationException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "delete_task")]
    [Description("Delete a task through the audit-aware service layer (writes a TaskDeleted activity event before removal; no HTTP hop). Cascades to its subtasks, acceptance criteria, comments, and dependencies; the audit row remains. This is irreversible — confirm the id with get_task first.")]
    public static async Task<TaskDeleteResult> DeleteTask(
        ITaskService taskService,
        [Description("The task id to delete.")] int taskId,
        [Description("Optional actor label recorded in the audit trail (default 'mcp-agent').")] string? actor = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await taskService.DeleteAsync(
                taskId, string.IsNullOrWhiteSpace(actor) ? DefaultActor : actor.Trim(), cancellationToken);
            return new TaskDeleteResult(taskId, true, $"Task {taskId} deleted; audit event recorded.");
        }
        catch (TaskNotFoundException)
        {
            throw new McpException($"Task {taskId} not found.");
        }
    }

    [McpServerTool(Name = "add_task_comment")]
    [Description("Add a typed comment to a task's activity stream through the audit-aware service layer (writes a TaskCommentAdded activity event; no HTTP hop). Use this to record decisions, questions, blockers, or handoffs as you work. Valid types: Note, Decision, Question, Blocked, Handoff, Verification, Comment (defaults to Note).")]
    public static async Task<TaskCommentResult> AddTaskComment(
        ITaskService taskService,
        [Description("The task id to comment on.")] int taskId,
        [Description("Comment text (required, non-empty).")] string text,
        [Description("Comment type: Note | Decision | Question | Blocked | Handoff | Verification | Comment (case-insensitive). Defaults to Note.")] string? type = null,
        [Description("Optional comment author label.")] string? author = null,
        [Description("Optional actor label recorded in the audit trail (default 'mcp-agent').")] string? actor = null,
        CancellationToken cancellationToken = default)
    {
        CommentType commentType = CommentType.Note;
        if (!string.IsNullOrWhiteSpace(type))
        {
            if (!Enum.TryParse<CommentType>(type, ignoreCase: true, out var parsed)
                || !taskService.IsUserCommentType(parsed))
                throw new McpException(
                    $"Invalid comment type '{type}'. Valid values: {string.Join(", ", KanbanFlow.Services.TaskService.AllowedUserCommentTypes)}.");
            commentType = parsed;
        }

        try
        {
            var comment = await taskService.AddCommentAsync(
                taskId, text, author, commentType,
                string.IsNullOrWhiteSpace(actor) ? DefaultActor : actor.Trim(), cancellationToken);
            return new TaskCommentResult(
                comment.Id, comment.TaskItemId, comment.Text, comment.Author,
                comment.Type.ToString(), comment.CreatedAt);
        }
        catch (TaskNotFoundException)
        {
            throw new McpException($"Task {taskId} not found.");
        }
        catch (ValidationException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "claim_task")]
    [Description("Claim a task for an actor through the audit-aware service layer (writes a TaskClaimed activity event; no HTTP hop). Claim before starting work so other agents skip it. Re-claiming by the same actor is idempotent.")]
    public static async Task<TaskClaimResult> ClaimTask(
        ITaskService taskService,
        [Description("The task id to claim.")] int taskId,
        [Description("Actor claiming the task (required, non-empty).")] string actor,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var claimed = await taskService.ClaimAsync(taskId, actor, cancellationToken);
            return new TaskClaimResult(
                claimed.Id, claimed.Name, claimed.ClaimedBy, claimed.ClaimedAt,
                $"Task claimed by {claimed.ClaimedBy}; audit event recorded.");
        }
        catch (TaskNotFoundException)
        {
            throw new McpException($"Task {taskId} not found.");
        }
        catch (TaskClaimConflictException ex)
        {
            throw new McpException(
                $"Task {taskId} is already claimed by {ex.ClaimedBy}. Ask {ex.ClaimedBy} to release it, or work a different task.");
        }
        catch (ValidationException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "release_task")]
    [Description("Release a task you previously claimed through the audit-aware service layer (writes a TaskReleased activity event; no HTTP hop). Clears the claim so other actors can pick it up.")]
    public static async Task<TaskClaimResult> ReleaseTask(
        ITaskService taskService,
        [Description("The task id to release.")] int taskId,
        [Description("Optional actor releasing the task (default 'mcp-agent'). Must match the current claimant.")] string? actor = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var released = await taskService.ReleaseAsync(
                taskId, string.IsNullOrWhiteSpace(actor) ? DefaultActor : actor.Trim(), cancellationToken);
            return new TaskClaimResult(
                released.Id, released.Name, released.ClaimedBy, released.ClaimedAt,
                "Task released; audit event recorded.");
        }
        catch (TaskNotFoundException)
        {
            throw new McpException($"Task {taskId} not found.");
        }
        catch (TaskClaimConflictException ex)
        {
            throw new McpException(
                $"Task {taskId} is claimed by {ex.ClaimedBy}, not by the releasing actor. Only {ex.ClaimedBy} can release it.");
        }
        catch (ValidationException ex)
        {
            throw new McpException(ex.Message);
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

    // ── Ideas inbox tools (#180 Phase 3) — native parity with the /api/ideas REST surface ──
    // All five route through IIdeaService (the single audit boundary) instead of touching
    // EF directly, so every mutation writes its ActivityEvent exactly like the REST/Blazor paths.

    [McpServerTool(Name = "create_idea")]
    [Description("Capture a new idea in the inbox through the audit-aware service layer (writes an IdeaCreated activity event; no HTTP hop). Always starts status=New. Returns the new idea — triage it with update_idea or promote it with promote_idea.")]
    public static async Task<IdeaResult> CreateIdea(
        IIdeaService ideaService,
        [Description("Idea title (required, <= 500 chars).")] string title,
        [Description("Optional markdown brain-dump body.")] string? body = null,
        [Description("Optional markdown plan (problem · approach · scope · value).")] string? plan = null,
        [Description("Optional comma-separated tags.")] string? tags = null,
        [Description("Optional priority: Low | Medium | High | Critical (case-insensitive).")] string? priority = null,
        [Description("Optional actor label recorded in the audit trail (default 'mcp-agent').")] string? actor = null,
        CancellationToken cancellationToken = default)
    {
        var parsedPriority = ParsePriority(priority);
        try
        {
            var idea = await ideaService.CreateAsync(
                new IdeaCreate(title, body, plan, tags, parsedPriority),
                NormalizeActor(actor), cancellationToken);
            return MapIdea(idea);
        }
        catch (ValidationException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "list_ideas")]
    [Description("List ideas newest-first with optional status, tag, and free-text filters. Use to triage the inbox before promoting. Returns up to 'limit' ideas (default 100).")]
    public static async Task<IReadOnlyList<IdeaResult>> ListIdeas(
        IIdeaService ideaService,
        [Description("Optional status filter: New | Triaging | Promoted | Archived | Rejected (case-insensitive).")] string? status = null,
        [Description("Optional case-insensitive tag substring filter.")] string? tag = null,
        [Description("Optional case-insensitive search across title and body.")] string? q = null,
        [Description("Max results (default 100, clamped 1-500 by the service).")] int? limit = null,
        [Description("Offset for paging (default 0).")] int? offset = null,
        CancellationToken cancellationToken = default)
    {
        var statusFilter = ParseIdeaStatus(status);
        // Service materialises the entities; enums are projected to strings in memory below.
        var ideas = await ideaService.ListAsync(statusFilter, tag, q, limit ?? 100, offset ?? 0, cancellationToken);
        return ideas.Select(MapIdea).ToList();
    }

    [McpServerTool(Name = "get_idea")]
    [Description("Return one idea in detail (title, body, plan, status, tags, priority, promoted project id). Errors if the idea does not exist.")]
    public static async Task<IdeaResult> GetIdea(
        IIdeaService ideaService,
        [Description("The idea id (from list_ideas).")] int ideaId,
        CancellationToken cancellationToken = default)
    {
        var idea = await ideaService.GetByIdAsync(ideaId, cancellationToken)
            ?? throw new McpException($"Idea {ideaId} not found.");
        return MapIdea(idea);
    }

    [McpServerTool(Name = "update_idea")]
    [Description("Update mutable idea fields (title, body, plan, status, tags, priority) through the audit-aware service layer (writes an IdeaUpdated activity event; no HTTP hop). Omitted/null fields are left unchanged. Idempotent when nothing changes.")]
    public static async Task<IdeaResult> UpdateIdea(
        IIdeaService ideaService,
        [Description("The idea id to update.")] int ideaId,
        [Description("New title (<= 500 chars), or omit to leave unchanged.")] string? title = null,
        [Description("New markdown body, or omit.")] string? body = null,
        [Description("New markdown plan, or omit.")] string? plan = null,
        [Description("New status: New | Triaging | Promoted | Archived | Rejected (case-insensitive), or omit.")] string? status = null,
        [Description("New comma-separated tags, or omit.")] string? tags = null,
        [Description("New priority: Low | Medium | High | Critical (case-insensitive), or omit.")] string? priority = null,
        [Description("Optional actor label recorded in the audit trail (default 'mcp-agent').")] string? actor = null,
        CancellationToken cancellationToken = default)
    {
        var parsedStatus = ParseIdeaStatus(status);
        var parsedPriority = ParsePriority(priority);
        try
        {
            var idea = await ideaService.UpdateAsync(
                ideaId, new IdeaUpdate(title, body, plan, parsedStatus, tags, parsedPriority),
                NormalizeActor(actor), cancellationToken);
            return MapIdea(idea);
        }
        catch (IdeaNotFoundException)
        {
            throw new McpException($"Idea {ideaId} not found.");
        }
        catch (ValidationException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "promote_idea")]
    [Description("Promote an idea into a new project (Title→Name, Body→Description, Plan→Goals) through the audit-aware service layer (writes IdeaPromoted + ProjectCreatedFromIdea activity events; no HTTP hop). Fails if the idea is already promoted. Returns the idea id and the new project id.")]
    public static async Task<IdeaPromotionResult> PromoteIdea(
        IIdeaService ideaService,
        [Description("The idea id to promote.")] int ideaId,
        [Description("Optional actor label recorded in the audit trail (default 'mcp-agent').")] string? actor = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await ideaService.PromoteAsync(ideaId, NormalizeActor(actor), cancellationToken);
            return new IdeaPromotionResult(
                result.IdeaId, result.ProjectId,
                $"Idea {result.IdeaId} promoted to project {result.ProjectId}");
        }
        catch (IdeaNotFoundException)
        {
            throw new McpException($"Idea {ideaId} not found.");
        }
        catch (ValidationException ex)
        {
            // e.g. the idea is already promoted — surface the service's reason verbatim.
            throw new McpException(ex.Message);
        }
    }

    // Normalise the optional audit actor exactly like the task tools do (trim, fall back to default).
    private static string NormalizeActor(string? actor) =>
        string.IsNullOrWhiteSpace(actor) ? DefaultActor : actor.Trim();

    // One DRY enum-parse for the idea tools' string? enum params: null/omitted → null;
    // present-but-blank or unrecognised → McpException listing the valid values (Phase-2 finding).
    private static TaskPriority? ParsePriority(string? priority)
    {
        if (priority is null) return null;
        if (!Enum.TryParse<TaskPriority>(priority, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            throw new McpException(
                $"Invalid priority '{priority}'. Valid values: {string.Join(", ", Enum.GetNames<TaskPriority>())}.");
        return parsed;
    }

    private static IdeaStatus? ParseIdeaStatus(string? status)
    {
        if (status is null) return null;
        if (!Enum.TryParse<IdeaStatus>(status, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            throw new McpException(
                $"Invalid status '{status}'. Valid values: {string.Join(", ", Enum.GetNames<IdeaStatus>())}.");
        return parsed;
    }

    // Mirrors the REST MapIdea projection (ApiEndpoints) so MCP and HTTP return the same idea shape.
    // Enums → strings via .ToString()/?.ToString(); the repo has no JsonStringEnumConverter.
    private static IdeaResult MapIdea(Idea idea) => new(
        idea.Id, idea.Title, idea.Body, idea.Plan, idea.Status.ToString(), idea.Tags,
        idea.Priority?.ToString(), idea.PromotedProjectId, idea.CreatedAt, idea.UpdatedAt);
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
public sealed record TaskDetailResult(int Id, int FeatureId, string Name, string? Description, string Status, string Priority, string? Assignee, string Message);
public sealed record TaskDeleteResult(int Id, bool Deleted, string Message);
public sealed record TaskCommentResult(int Id, int TaskItemId, string Text, string? Author, string Type, DateTime CreatedAt);
public sealed record TaskClaimResult(int Id, string Name, string? ClaimedBy, DateTime? ClaimedAt, string Message);

// ── Idea inbox DTOs (#180 Phase 3) — IdeaResult mirrors the REST MapIdea field set ──
public sealed record IdeaResult(int Id, string Title, string? Body, string? Plan, string Status, string? Tags, string? Priority, int? PromotedProjectId, DateTime CreatedAt, DateTime UpdatedAt);
public sealed record IdeaPromotionResult(int IdeaId, int ProjectId, string Message);
