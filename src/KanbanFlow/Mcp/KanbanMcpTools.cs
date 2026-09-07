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
/// This is a curated slice (28 tools: 7 read + 8 audit-aware write + 4 acceptance-criteria (#438)
/// + the 9-tool knowledge-graph
/// memory surface, #224) proving parity with the Node wrapper, not yet the full 41-tool surface.
/// See <c>docs/native-mcp-notes.md</c> for the rationale, the path to full coverage, and the
/// Kebechet.Api.ToMcp evaluation.
///
/// Tool-design conventions follow Anthropic's mcp-builder skill: snake_case names,
/// concise descriptions that say what the tool does and what to do next, and
/// actionable error messages.
/// </summary>
[McpServerToolType]
public static class KanbanMcpTools
{
    private const string DefaultActor = "mcp-agent";

    /// <summary>Upper bound on acceptance-criterion text (#438).</summary>
    /// <remarks>
    /// An MCP-side guard, NOT a REST parity: REST validates only <c>IsNullOrWhiteSpace</c>, the
    /// model has no <c>[MaxLength]</c>, and the DbContext no <c>HasMaxLength</c>. So a criterion
    /// created through REST or the Blazor panel CAN exceed this, and
    /// <see cref="UpdateAcceptanceCriterion"/> would then refuse to save a corrected version of
    /// text the system already holds. <see cref="MarkAcceptanceCriterionDone"/> does no text
    /// validation, so such a criterion always remains completable and the Definition-of-Done gate
    /// stays satisfiable. Pushing the cap into REST and the model would make all three write
    /// surfaces agree; that is a separate change.
    /// </remarks>
    private const int MaxCriterionTextLength = 1000;

    [McpServerTool(Name = "update_task")]
    [Description("Edit an existing board task's fields through the same audited service as the human task editor. Omitted fields stay unchanged. Done still requires completed acceptance criteria. Read get_task_workflow afterward to see whether earlier evidence became historical.")]
    public static async Task<TaskDetailResult> UpdateTask(ITaskService service, int taskId, TaskUpdate update,
        string? actor = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var task = await service.UpdateAsync(taskId, update, NormalizeActor(actor), cancellationToken);
            return new(task.Id, task.FeatureId, task.Name, task.Description, task.Status.ToString(), task.Priority.ToString(), task.Assignee, task.SortOrder, "Task updated");
        }
        catch (TaskNotFoundException ex) { throw new McpException(ex.Message); }
        catch (DefinitionOfDoneNotMetException ex) { throw new McpException(ex.Message); }
        catch (ValidationException ex) { throw new McpException(ex.Message); }
    }

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
                .Include(t => t.Feature)
                .ToListAsync(cancellationToken))
            .OrderBy(t => t.Feature.SortOrder)
            .ThenBy(t => t.FeatureId)
            .ThenBy(TaskOrdering.EffectiveRank)
            .ThenBy(t => t.SortOrder)
            .ThenBy(t => t.Id)
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
            .Include(t => t.Comments)
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
            task.Dependencies.Select(d => d.DependsOnTaskItemId).OrderBy(id => id).ToList(),
            task.Comments.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
                .Select(c => new TaskCommentDetail(c.Id, c.Text, c.Author, c.Type.ToString(), c.CreatedAt)).ToList());
    }

    /// <summary>
    /// Recommends ranked open tasks for a project; stale claims are treated as available (#429)
    /// so a crashed agent cannot park work permanently.
    /// </summary>
    [McpServerTool(Name = "get_next_tasks")]
    [Description("Recommend what to work on next in a project: not-done tasks, excluding any blocked by incomplete dependencies, priority-sorted (Critical first), de-prioritising tasks claimed by other actors. Each result includes a human-readable reason. This is the project's 'direction' primitive.")]
    public static async Task<IReadOnlyList<NextTask>> GetNextTasks(
        IDbContextFactory<AppDbContext> dbFactory,
        ClaimTtlSetting claimTtlSetting,
        [Description("The project id.")] int projectId,
        [Description("Optional actor label; tasks claimed by a different actor are de-prioritised.")] string? actor = null,
        [Description("Maximum results (default 5, clamped to 1-50).")] int limit = 5,
        CancellationToken cancellationToken = default)
    {
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

        // Shared ranker (#151/#188): identical canonical order, actor de-prioritisation, reason
        // strings, and 1–50 limit clamp as the REST GET /api/projects/{id}/next-tasks endpoint.
        // Build the depends-on lookup from each open task's Dependencies navigation
        // (TaskItemId → DependsOnTaskItemId).
        var dependsOnByTask = openTasks
            .SelectMany(t => t.Dependencies)
            .ToLookup(d => d.TaskItemId, d => d.DependsOnTaskItemId);

        return TaskRanking.RankNextTasks(openTasks, statusById, dependsOnByTask, actor, limit, claimTtl: claimTtlSetting.Value)
            .Select(r => new NextTask(
                r.Task.Id, r.Task.Name, r.Task.Status.ToString(), r.Task.Priority.ToString(),
                r.Task.ClaimedBy, r.Reason))
            .ToList();
    }

    [McpServerTool(Name = "update_task_status")]
    [Description("Change a task's status through the audit-aware service layer (writes a TaskStatusChanged activity event + StatusChange comment; no HTTP hop). Valid statuses: Backlog, Todo, InProgress, Review, Done.")]
    public static async Task<TaskStatusResult> UpdateTaskStatus(
        ITaskService taskService,
        [Description("The task id.")] int taskId,
        [Description("New status: Backlog | Todo | InProgress | Review | Done (case-insensitive).")] string status,
        [Description("Optional actor label recorded in the audit trail (default 'mcp-agent').")] string? actor = null,
        [Description("Optional. Only needed when moving to Done with unmet acceptance criteria: a short reason why the task is done anyway. Recorded as an audited Verification comment. Leave unset to have the Definition-of-Done gate enforce the criteria.")] string? overrideReason = null,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<WorkStatus>(status, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            throw new McpException(
                $"Invalid status '{status}'. Valid values: {string.Join(", ", Enum.GetNames<WorkStatus>())}.");

        try
        {
            var updated = await taskService.ChangeStatusAsync(
                taskId, parsed, string.IsNullOrWhiteSpace(actor) ? DefaultActor : actor.Trim(), cancellationToken, overrideReason);
            return new TaskStatusResult(
                updated.Id, updated.Name, updated.Status.ToString(),
                "Status updated; audit event recorded.");
        }
        catch (TaskNotFoundException)
        {
            throw new McpException($"Task {taskId} not found.");
        }
        catch (DefinitionOfDoneNotMetException ex)
        {
            // mcp-builder convention: the error tells the agent exactly what to do next.
            var list = string.Join("; ", ex.Unmet);
            throw new McpException(
                $"Task {taskId} cannot be marked Done: {ex.UnmetCount} acceptance criterion/criteria are not met ({list}). "
                + "Call list_acceptance_criteria to get their ids, complete each with mark_acceptance_criterion_done, "
                + "then retry this call. If a criterion genuinely will not be met, call update_task_status again with "
                + "overrideReason set to a short explanation of why the task is done despite them.");
        }
        catch (ValidationException ex)
        {
            // An over-length overrideReason. Without this the agent gets a framework-internal
            // error instead of a message naming the limit it just exceeded.
            throw new McpException(ex.Message);
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
        [Description("Optional finite task rank within the column; may be fractional. Defaults to appending after the feature's last task.")] double? sortOrder = null,
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
                new TaskCreate(featureId, name, description, parsedStatus, parsedPriority, assignee, sortOrder),
                string.IsNullOrWhiteSpace(actor) ? DefaultActor : actor.Trim(),
                cancellationToken);
            return new TaskDetailResult(
                created.Id, created.FeatureId, created.Name, created.Description,
                created.Status.ToString(), created.Priority.ToString(), created.Assignee,
                created.SortOrder,
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
    [Description("Claim a task for an actor through the audit-aware service layer (no HTTP hop). Audit behavior: a new claim writes TaskClaimed; a fresh same-actor renewal writes no event; an expired same-actor renewal writes TaskClaimRenewed; takeover of a stale claim writes TaskClaimStolen. Claim before starting work so other agents skip it. Re-claiming by the same actor RENEWS the claim: call it periodically during long work to keep the lease alive, or the claim expires at the TTL and another agent may take the task over.")]
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
                $"Task claimed by {claimed.ClaimedBy}; claim lease is active.");
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

    // ── Acceptance-criteria tools (#438) — the tools the Definition-of-Done gate needs ──
    // #428 gated Done on unmet acceptance criteria across all four transition paths. Until this
    // block existed the native server had NO acceptance-criteria tools at all, so an agent on
    // POST /mcp that hit the gate could not list the criteria blocking it, let alone complete one:
    // its only exit was update_task_status's overrideReason. The gate's escape hatch became the
    // DEFAULT path for native-MCP agents because it was the only path, which inverts the intent of
    // #428 — the audit trail fills with overrides meaning "the tool was missing" rather than
    // "a human judged this done anyway".
    //
    // Criteria use the shared audited save boundary, as do REST and the existing Blazor editors.
    // Their events retain task context so both MCP readers and the card show the same history.

    [McpServerTool(Name = "list_acceptance_criteria")]
    [Description("List a task's acceptance criteria with their completion state, ordered as they appear on the card. Call this when update_task_status refuses a move to Done, to see exactly which criteria are unmet and get the ids to pass to mark_acceptance_criterion_done.")]
    public static async Task<IReadOnlyList<AcceptanceCriterionResult>> ListAcceptanceCriteria(
        IDbContextFactory<AppDbContext> dbFactory,
        [Description("The task id.")] int taskId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        if (!await db.Tasks.AnyAsync(t => t.Id == taskId, cancellationToken))
            throw new McpException($"Task {taskId} not found. Call get_project_context to see valid task ids.");

        // Same ordering as get_task and the REST endpoint: SortOrder, then Id as a tie-break, so
        // the list an agent reads matches the card a human sees.
        return await db.AcceptanceCriteria.AsNoTracking()
            .Where(c => c.TaskItemId == taskId)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Id)
            .Select(c => new AcceptanceCriterionResult(
                c.Id, c.TaskItemId, c.Text, c.Completed, c.SortOrder, "Listed."))
            .ToListAsync(cancellationToken);
    }

    [McpServerTool(Name = "list_project_activity")]
    [Description("Read retained project change history, including deleted items. Returns items, total and nextBeforeId; pass nextBeforeId as beforeId to read older events. Actor is reported attribution, not authenticated identity.")]
    public static async Task<ActivityPage> ListProjectActivity(IDbContextFactory<AppDbContext> dbFactory,
        int projectId, int? limit = null, int? beforeId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        try
        {
            var page = await ActivityLogReader.ReadAsync(db, projectId: projectId, limit: limit, beforeId: beforeId, ct: cancellationToken);
            if (page.Total == 0 && !await db.Projects.AnyAsync(p => p.Id == projectId, cancellationToken))
                throw new McpException($"Project {projectId} not found.");
            return page;
        }
        catch (ValidationException ex) { throw new McpException(ex.Message); }
    }

    [McpServerTool(Name = "list_task_activity")]
    [Description("Read retained task history including subtasks, acceptance checks, requirements and dependency changes. Returns items, total and nextBeforeId; pass nextBeforeId as beforeId for older events, including after deletion.")]
    public static async Task<ActivityPage> ListTaskActivity(IDbContextFactory<AppDbContext> dbFactory,
        int taskId, int? limit = null, int? beforeId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        try
        {
            var page = await ActivityLogReader.ReadAsync(db, taskId: taskId, limit: limit, beforeId: beforeId, ct: cancellationToken);
            if (page.Total == 0 && !await db.Tasks.AnyAsync(t => t.Id == taskId, cancellationToken))
                throw new McpException($"Task {taskId} not found.");
            return page;
        }
        catch (ValidationException ex) { throw new McpException(ex.Message); }
    }

    [McpServerTool(Name = "create_acceptance_criterion")]
    [Description("Add an acceptance criterion to a task. Criteria are the task's Definition of Done: once a task has any, update_task_status refuses to move it to Done until they are all complete (or an overrideReason is given). Returns the new criterion.")]
    public static async Task<AcceptanceCriterionResult> CreateAcceptanceCriterion(
        IDbContextFactory<AppDbContext> dbFactory,
        [Description("The task id this criterion belongs to.")] int taskId,
        [Description("The criterion text (required, <= 1000 chars) — a checkable statement.")] string text,
        [Description("Optional rank within the task's criteria, 0 or greater. Defaults to appending after the last one.")] int? sortOrder = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new McpException("Text is required and cannot be blank.");
        // The wrapper's schema declares `minimum: 0`, so a negative rank is rejected there and was
        // silently persisted here — the same cross-transport split as the acId naming. (CodeRabbit.)
        if (sortOrder is < 0)
            throw new McpException("sortOrder must be 0 or greater.");
        if (text.Trim().Length > MaxCriterionTextLength)
            throw new McpException($"Text must be {MaxCriterionTextLength} characters or fewer.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        if (!await db.Tasks.AnyAsync(t => t.Id == taskId, cancellationToken))
            throw new McpException($"Task {taskId} not found. Call get_project_context to see valid task ids.");

        // Append after the current last criterion when no rank is given. MaxAsync over an empty
        // set throws, so the projection is nullable — it yields null for a task with none yet.
        var resolvedSortOrder = sortOrder ?? ((await db.AcceptanceCriteria
            .Where(c => c.TaskItemId == taskId)
            .Select(c => (int?)c.SortOrder)
            .MaxAsync(cancellationToken)) ?? -1) + 1;

        var criterion = new AcceptanceCriterion
        {
            TaskItemId = taskId,
            Text = text.Trim(),
            Completed = false,
            SortOrder = resolvedSortOrder
        };
        db.AcceptanceCriteria.Add(criterion);
        await db.SaveChangesWithAuditAsync(DefaultActor, cancellationToken);

        // The Definition-of-Done gate is TRANSITION-only (GateDoneTransitionAsync returns
        // immediately when oldStatus == Done), so a criterion added to an already-Done task
        // changes nothing about it. Saying otherwise would be a lie in exactly that case.
        var taskStatus = await db.Tasks.Where(t => t.Id == taskId)
            .Select(t => t.Status).FirstAsync(cancellationToken);
        var message = taskStatus == WorkStatus.Done
            ? $"Criterion created, but task {taskId} is already Done — the Definition-of-Done gate "
              + "only runs on a transition INTO Done, so this does not reopen it or block anything."
            : "Criterion created. The task cannot move to Done until it is complete.";

        return new AcceptanceCriterionResult(
            criterion.Id, criterion.TaskItemId, criterion.Text, criterion.Completed, criterion.SortOrder,
            message);
    }

    [McpServerTool(Name = "mark_acceptance_criterion_done")]
    [Description("Mark one acceptance criterion complete (or, with completed=false, reopen it). This is the FIRST remedy when update_task_status refuses a move to Done: complete the criteria it names, then retry the move. Use overrideReason only when a criterion genuinely will not be met.")]
    public static async Task<AcceptanceCriterionResult> MarkAcceptanceCriterionDone(
        IDbContextFactory<AppDbContext> dbFactory,
        [Description("The acceptance criterion id (from list_acceptance_criteria or get_task).")] int acId,
        [Description("Optional. true (the default) completes the criterion; false reopens it.")] bool completed = true,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var criterion = await db.AcceptanceCriteria.FirstOrDefaultAsync(c => c.Id == acId, cancellationToken)
            ?? throw new McpException(
                $"Acceptance criterion {acId} not found. Call list_acceptance_criteria for a task to see valid ids.");

        criterion.Completed = completed;
        criterion.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesWithAuditAsync(DefaultActor, cancellationToken);

        // Tell the agent whether the task is now clear to move, so it does not have to call
        // list_acceptance_criteria again just to find out.
        var remaining = await db.AcceptanceCriteria
            .CountAsync(c => c.TaskItemId == criterion.TaskItemId && !c.Completed, cancellationToken);
        var verb = completed ? "completed" : "reopened";
        var message = remaining == 0
            ? $"Criterion {verb}. No unmet criteria remain on task {criterion.TaskItemId} — update_task_status can now move it to Done."
            : $"Criterion {verb}. {remaining} criterion/criteria still unmet on task {criterion.TaskItemId}.";

        return new AcceptanceCriterionResult(
            criterion.Id, criterion.TaskItemId, criterion.Text, criterion.Completed, criterion.SortOrder, message);
    }

    [McpServerTool(Name = "update_acceptance_criterion")]
    [Description("Update an acceptance criterion's text, completion, or rank. Omitted arguments are left unchanged. To only tick a criterion off, prefer mark_acceptance_criterion_done.")]
    public static async Task<AcceptanceCriterionResult> UpdateAcceptanceCriterion(
        IDbContextFactory<AppDbContext> dbFactory,
        [Description("The acceptance criterion id.")] int acId,
        [Description("Optional new text (<= 1000 chars). Omit to leave unchanged; blank is rejected.")] string? text = null,
        [Description("Optional new completion state. Omit to leave unchanged.")] bool? completed = null,
        [Description("Optional new rank within the task's criteria, 0 or greater. Omit to leave unchanged.")] int? sortOrder = null,
        CancellationToken cancellationToken = default)
    {
        if (sortOrder is < 0)
            throw new McpException("sortOrder must be 0 or greater.");
        // Omitted PRESERVES, blank REJECTS — the same partial-update rule the REST endpoint applies
        // (#203/#260): a rename that omits a field must never wipe it.
        if (text is not null)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new McpException("Text cannot be blank. Omit the argument to leave it unchanged.");
            if (text.Trim().Length > MaxCriterionTextLength)
                throw new McpException($"Text must be {MaxCriterionTextLength} characters or fewer.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var criterion = await db.AcceptanceCriteria.FirstOrDefaultAsync(c => c.Id == acId, cancellationToken)
            ?? throw new McpException(
                $"Acceptance criterion {acId} not found. Call list_acceptance_criteria for a task to see valid ids.");

        if (text is not null) criterion.Text = text.Trim();
        if (completed.HasValue) criterion.Completed = completed.Value;
        if (sortOrder.HasValue) criterion.SortOrder = sortOrder.Value;
        criterion.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesWithAuditAsync(DefaultActor, cancellationToken);

        return new AcceptanceCriterionResult(
            criterion.Id, criterion.TaskItemId, criterion.Text, criterion.Completed, criterion.SortOrder, "Criterion updated.");
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
        [Description("New status: New | Triaging | Archived | Rejected (case-insensitive), or omit. 'Promoted' is derived and terminal: it is rejected here (promote_idea is its only writer — it also creates the linked project), and an idea that is ALREADY promoted cannot be moved to any other status either. Its other fields stay editable — just omit status.")] string? status = null,
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
            var result = await ideaService.PromoteAsync(ideaId, NormalizeActor(actor), ct: cancellationToken);
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

    // ── Knowledge-graph memory tools (#224) — the official MCP memory server contract ──
    // Drop-in implementation of modelcontextprotocol/servers src/memory's 8-tool surface (9 here:
    // read_graph/search_nodes/open_nodes are split reads) on the bi-temporal memory store (#221):
    //   entity.name         -> MemoryNode.Content (identity text; resolved by exact match among
    //                          CURRENT nodes in the namespace)
    //   entity.entityType   -> MemoryNode.Tags (free label); MemoryType defaults to Semantic
    //   entity.observations -> Observation rows (AddObservationAsync)
    //   relation {from,to,relationType} -> MemoryRelation MemoryNode->MemoryNode edge
    // The namespace is MemoryScope.Agent + ScopeKey "knowledge-graph" by default; every tool takes an
    // optional scopeKey so agents can partition graphs, and the zero-arg default always works.
    // Mutations route ONLY through IMemoryService (the #88 single audit boundary); search_nodes reads
    // through IMemorySearch (#223 hybrid retrieval, degrades to keyword+recency without ZeroRouter).
    // ONLY read_graph rides the capped ReadGraphAsync (a whole-graph payload bound, documented
    // there). Everything else — name resolution, mutation dedupe + cascade, and the
    // search_nodes/open_nodes output shaping — is uncapped or id-targeted
    // (ListAsync / GetObservationsForNodesAsync / GetMemoryNodeRelationsAsync), so correctness
    // never degrades as the namespace grows (Gemini + Copilot reviews, PR #259).

    private const string DefaultKnowledgeGraphScopeKey = "knowledge-graph";

    [McpServerTool(Name = "create_entities")]
    [Description("Create multiple new entities in the knowledge-graph memory. Entities whose name already exists are ignored; returns only the newly created entities (official MCP memory contract). Each create writes MemoryCreated/ObservationAdded audit events; no HTTP hop.")]
    public static async Task<IReadOnlyList<MemoryEntity>> CreateEntities(
        IMemoryService memoryService,
        [Description("Entities to create: [{name, entityType, observations}]. 'name' is the unique identity text, 'entityType' a free-form label (e.g. 'person', 'project'), 'observations' an optional array of atomic facts about the entity.")]
        IReadOnlyList<MemoryEntityInput> entities,
        [Description("Optional memory namespace key (default 'knowledge-graph', the shared agent graph).")] string? scopeKey = null,
        [Description("Optional actor label recorded in the audit trail (default 'mcp-agent').")] string? actor = null,
        CancellationToken cancellationToken = default)
    {
        if (entities is null)
            throw new McpException("entities is required: pass an array of {name, entityType, observations}.");

        // Validate the WHOLE batch before mutating anything, so an invalid item can't leave a
        // half-created batch behind.
        foreach (var entity in entities)
        {
            if (entity is null || string.IsNullOrWhiteSpace(entity.Name))
                throw new McpException("Every entity requires a non-empty 'name' (the unique identity text).");
            foreach (var observation in entity.Observations ?? [])
            {
                if (string.IsNullOrWhiteSpace(observation))
                    throw new McpException($"Entity '{entity.Name.Trim()}' has a blank observation; observations must be non-empty text.");
            }
        }

        var key = NormalizeScopeKey(scopeKey);
        var who = NormalizeActor(actor);
        var byName = await CurrentNodesByNameAsync(memoryService, key, cancellationToken);

        var created = new List<MemoryEntity>();
        try
        {
            foreach (var entity in entities)
            {
                var name = entity.Name.Trim();
                if (byName.ContainsKey(name))
                    continue; // official behavior: silently ignore existing names (and in-batch duplicates)

                var node = await memoryService.CreateNodeAsync(
                    new MemoryNodeCreate(MemoryType.Semantic, MemoryScope.Agent, key, name, Tags: entity.EntityType),
                    who, cancellationToken);
                byName[node.Content] = node;

                var observations = new List<string>();
                foreach (var observation in entity.Observations ?? [])
                {
                    var added = await memoryService.AddObservationAsync(node.Id, observation, source: null, who, cancellationToken);
                    observations.Add(added.Content);
                }

                created.Add(new MemoryEntity(node.Content, node.Tags ?? string.Empty, observations));
            }
        }
        catch (ValidationException ex)
        {
            throw new McpException(ex.Message);
        }

        return created;
    }

    [McpServerTool(Name = "create_relations")]
    [Description("Create multiple new relations between existing knowledge-graph entities. Relations are directed, in active voice (e.g. 'works_at'). Exact duplicates are skipped; returns only the newly created relations. Errors if a referenced entity does not exist (create it with create_entities first). Writes RelationAdded audit events.")]
    public static async Task<IReadOnlyList<MemoryRelationDto>> CreateRelations(
        IMemoryService memoryService,
        [Description("Relations to create: [{from, to, relationType}]. 'from'/'to' are entity names; 'relationType' is an active-voice verb.")]
        IReadOnlyList<MemoryRelationInput> relations,
        [Description("Optional memory namespace key (default 'knowledge-graph').")] string? scopeKey = null,
        [Description("Optional actor label recorded in the audit trail (default 'mcp-agent').")] string? actor = null,
        CancellationToken cancellationToken = default)
    {
        if (relations is null)
            throw new McpException("relations is required: pass an array of {from, to, relationType}.");
        foreach (var relation in relations)
        {
            if (relation is null
                || string.IsNullOrWhiteSpace(relation.From)
                || string.IsNullOrWhiteSpace(relation.To)
                || string.IsNullOrWhiteSpace(relation.RelationType))
                throw new McpException("Every relation requires non-empty 'from', 'to', and 'relationType' values.");
        }

        var key = NormalizeScopeKey(scopeKey);
        var who = NormalizeActor(actor);
        var byName = await CurrentNodesByNameAsync(memoryService, key, cancellationToken);

        // Resolve every endpoint BEFORE mutating (a missing entity fails the whole batch cleanly,
        // matching create_entities' validate-first shape), and collect the endpoint ids so the
        // duplicate check below is id-targeted — correct at any namespace size, unlike the capped
        // graph read (Gemini review, PR #259).
        var resolved = relations
            .Select(r => (From: ResolveEntity(byName, r.From), To: ResolveEntity(byName, r.To), Verb: r.RelationType.Trim()))
            .ToList();
        var endpointIds = resolved.SelectMany(r => new[] { r.From.Id, r.To.Id }).ToHashSet();

        // Existing edges touching the request's endpoints, for the official skip-exact-duplicates behavior.
        var existing = (await memoryService.GetMemoryNodeRelationsAsync(endpointIds, cancellationToken))
            .Select(r => (r.FromId, r.RelationType, r.ToId))
            .ToHashSet();

        var created = new List<MemoryRelationDto>();
        try
        {
            foreach (var (fromNode, toNode, verb) in resolved)
            {
                if (!existing.Add((fromNode.Id, verb, toNode.Id)))
                    continue; // official behavior: skip exact duplicates (including in-batch repeats)

                await memoryService.AddRelationAsync(
                    "MemoryNode", fromNode.Id, verb, "MemoryNode", toNode.Id, who, cancellationToken);
                created.Add(new MemoryRelationDto(fromNode.Content, toNode.Content, verb));
            }
        }
        catch (ValidationException ex)
        {
            throw new McpException(ex.Message);
        }

        return created;
    }

    [McpServerTool(Name = "add_observations")]
    [Description("Add new observations (atomic facts) to existing knowledge-graph entities. Observations already present on an entity are skipped; returns the observations actually added per entity. Errors if an entity does not exist (official MCP memory contract). Writes ObservationAdded audit events.")]
    public static async Task<IReadOnlyList<ObservationAdditionResult>> AddObservations(
        IMemoryService memoryService,
        [Description("Additions: [{entityName, contents}]. 'contents' is an array of observation strings to add to the named entity.")]
        IReadOnlyList<MemoryObservationInput> observations,
        [Description("Optional memory namespace key (default 'knowledge-graph').")] string? scopeKey = null,
        [Description("Optional actor label recorded in the audit trail (default 'mcp-agent').")] string? actor = null,
        CancellationToken cancellationToken = default)
    {
        if (observations is null)
            throw new McpException("observations is required: pass an array of {entityName, contents}.");
        foreach (var addition in observations)
        {
            if (addition is null || string.IsNullOrWhiteSpace(addition.EntityName))
                throw new McpException("Every addition requires a non-empty 'entityName'.");
            foreach (var content in addition.Contents ?? [])
            {
                if (string.IsNullOrWhiteSpace(content))
                    throw new McpException($"Entity '{addition.EntityName.Trim()}' has a blank observation; observations must be non-empty text.");
            }
        }

        var key = NormalizeScopeKey(scopeKey);
        var who = NormalizeActor(actor);
        var byName = await CurrentNodesByNameAsync(memoryService, key, cancellationToken);

        // Resolve every target BEFORE mutating (validate-first, like create_entities), then fetch
        // existing observation contents id-targeted — correct at any namespace size, unlike the
        // capped graph read (Gemini review, PR #259).
        var targets = observations
            .Select(a => (Node: ResolveEntity(byName, a.EntityName), a.Contents))
            .ToList();
        var targetIds = targets.Select(t => t.Node.Id).ToHashSet();

        var existingByNode = (await memoryService.GetObservationsForNodesAsync(targetIds, cancellationToken))
            .GroupBy(o => o.MemoryNodeId)
            .ToDictionary(g => g.Key, g => g.Select(o => o.Content).ToHashSet(StringComparer.Ordinal));

        var results = new List<ObservationAdditionResult>();
        try
        {
            foreach (var (node, contents) in targets)
            {
                if (!existingByNode.TryGetValue(node.Id, out var present))
                    existingByNode[node.Id] = present = new HashSet<string>(StringComparer.Ordinal);

                var added = new List<string>();
                foreach (var content in contents ?? [])
                {
                    var trimmed = content.Trim();
                    if (!present.Add(trimmed))
                        continue; // already observed (or repeated within this batch) — skip, per contract

                    var observation = await memoryService.AddObservationAsync(node.Id, trimmed, source: null, who, cancellationToken);
                    added.Add(observation.Content);
                }

                results.Add(new ObservationAdditionResult(node.Content, added));
            }
        }
        catch (ValidationException ex)
        {
            throw new McpException(ex.Message);
        }

        return results;
    }

    [McpServerTool(Name = "delete_entities")]
    [Description("Delete entities from the knowledge-graph memory by name, cascading their relations. Missing names are silently skipped (official MCP memory contract). Deletion is the store's soft-supersede — history stays queryable; the entity just leaves the current graph. Writes RelationDeleted + MemorySuperseded audit events.")]
    public static async Task<EntityDeletionResult> DeleteEntities(
        IMemoryService memoryService,
        [Description("Names of the entities to delete.")] IReadOnlyList<string> entityNames,
        [Description("Optional memory namespace key (default 'knowledge-graph').")] string? scopeKey = null,
        [Description("Optional actor label recorded in the audit trail (default 'mcp-agent').")] string? actor = null,
        CancellationToken cancellationToken = default)
    {
        if (entityNames is null)
            throw new McpException("entityNames is required: pass an array of entity names to delete.");

        var key = NormalizeScopeKey(scopeKey);
        var who = NormalizeActor(actor);
        var byName = await CurrentNodesByNameAsync(memoryService, key, cancellationToken);

        // Cascade source: edges touching the DELETED nodes, fetched id-targeted so no edge is missed
        // when the namespace outgrows the capped graph read (Gemini review, PR #259).
        var doomedIds = entityNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Where(byName.ContainsKey)
            .Select(n => byName[n].Id)
            .ToHashSet();
        var cascadeEdges = await memoryService.GetMemoryNodeRelationsAsync(doomedIds, cancellationToken);

        var deleted = new List<string>();
        try
        {
            foreach (var rawName in entityNames.Where(n => !string.IsNullOrWhiteSpace(n)))
            {
                var name = rawName.Trim();
                if (!byName.TryGetValue(name, out var node))
                    continue; // silent no-op on missing names, per contract

                // Cascade: remove every knowledge-graph edge touching the node before retiring it, so
                // the deletion is explicit in the audit trail (not just invisible-by-filtering).
                foreach (var relation in cascadeEdges.Where(r => r.FromId == node.Id || r.ToId == node.Id))
                {
                    await memoryService.DeleteRelationAsync(
                        relation.FromType, relation.FromId, relation.RelationType,
                        relation.ToType, relation.ToId, who, cancellationToken);
                }

                await memoryService.DeleteNodeAsync(node.Id, who, cancellationToken);
                byName.Remove(name);
                deleted.Add(name);
            }
        }
        catch (ValidationException ex)
        {
            throw new McpException(ex.Message);
        }
        catch (MemoryNodeNotFoundException)
        {
            // Node vanished between resolution and delete (hard-deleted concurrently) — the outcome
            // the caller wanted (gone), so honor the silent-no-op contract.
        }

        return new EntityDeletionResult(deleted, $"{deleted.Count} entit{(deleted.Count == 1 ? "y" : "ies")} deleted; audit events recorded.");
    }

    [McpServerTool(Name = "delete_observations")]
    [Description("Delete specific observations from knowledge-graph entities. Missing entities or observations are silently skipped (official MCP memory contract). Writes ObservationDeleted audit events and keeps the keyword index in sync.")]
    public static async Task<ObservationDeletionResult> DeleteObservations(
        IMemoryService memoryService,
        [Description("Deletions: [{entityName, observations}]. 'observations' is an array of observation strings to remove from the named entity.")]
        IReadOnlyList<MemoryObservationDeletionInput> deletions,
        [Description("Optional memory namespace key (default 'knowledge-graph').")] string? scopeKey = null,
        [Description("Optional actor label recorded in the audit trail (default 'mcp-agent').")] string? actor = null,
        CancellationToken cancellationToken = default)
    {
        if (deletions is null)
            throw new McpException("deletions is required: pass an array of {entityName, observations}.");

        var key = NormalizeScopeKey(scopeKey);
        var who = NormalizeActor(actor);
        var byName = await CurrentNodesByNameAsync(memoryService, key, cancellationToken);

        var deletedCount = 0;
        foreach (var deletion in deletions)
        {
            if (deletion is null || string.IsNullOrWhiteSpace(deletion.EntityName))
                continue; // nothing addressable — silent no-op, per contract
            if (!byName.TryGetValue(deletion.EntityName.Trim(), out var node))
                continue; // missing entity — silent no-op, per contract

            foreach (var content in (deletion.Observations ?? []).Where(c => !string.IsNullOrWhiteSpace(c)))
            {
                try
                {
                    deletedCount += await memoryService.DeleteObservationAsync(node.Id, content, who, cancellationToken);
                }
                catch (MemoryNodeNotFoundException)
                {
                    break; // node vanished mid-batch — remaining deletes for it are no-ops
                }
            }
        }

        return new ObservationDeletionResult(deletedCount, $"{deletedCount} observation(s) deleted; audit events recorded.");
    }

    [McpServerTool(Name = "delete_relations")]
    [Description("Delete relations from the knowledge-graph memory. Relations that do not exist (or reference missing entities) are silently skipped (official MCP memory contract). Writes RelationDeleted audit events.")]
    public static async Task<RelationDeletionResult> DeleteRelations(
        IMemoryService memoryService,
        [Description("Relations to delete: [{from, to, relationType}], matched exactly.")]
        IReadOnlyList<MemoryRelationInput> relations,
        [Description("Optional memory namespace key (default 'knowledge-graph').")] string? scopeKey = null,
        [Description("Optional actor label recorded in the audit trail (default 'mcp-agent').")] string? actor = null,
        CancellationToken cancellationToken = default)
    {
        if (relations is null)
            throw new McpException("relations is required: pass an array of {from, to, relationType}.");

        var key = NormalizeScopeKey(scopeKey);
        var who = NormalizeActor(actor);
        var byName = await CurrentNodesByNameAsync(memoryService, key, cancellationToken);

        var deletedCount = 0;
        foreach (var relation in relations)
        {
            if (relation is null
                || string.IsNullOrWhiteSpace(relation.From)
                || string.IsNullOrWhiteSpace(relation.To)
                || string.IsNullOrWhiteSpace(relation.RelationType))
                continue; // nothing addressable — silent no-op, per contract
            if (!byName.TryGetValue(relation.From.Trim(), out var fromNode)
                || !byName.TryGetValue(relation.To.Trim(), out var toNode))
                continue; // missing endpoint entity — silent no-op, per contract

            deletedCount += await memoryService.DeleteRelationAsync(
                "MemoryNode", fromNode.Id, relation.RelationType.Trim(), "MemoryNode", toNode.Id,
                who, cancellationToken);
        }

        return new RelationDeletionResult(deletedCount, $"{deletedCount} relation(s) deleted; audit events recorded.");
    }

    [McpServerTool(Name = "read_graph")]
    [Description("Read the entire knowledge-graph memory: every current entity ({name, entityType, observations}) and the relations between them. Capped at the first 900 entities (oldest first). Start here to see what the graph already knows.")]
    public static async Task<KnowledgeGraph> ReadGraph(
        IMemoryService memoryService,
        [Description("Optional memory namespace key (default 'knowledge-graph').")] string? scopeKey = null,
        CancellationToken cancellationToken = default)
    {
        var graph = await memoryService.ReadGraphAsync(
            MemoryScope.Agent, NormalizeScopeKey(scopeKey), cancellationToken);
        return ShapeGraph(graph, graph.Nodes);
    }

    [McpServerTool(Name = "search_nodes")]
    [Description("Search the knowledge-graph memory with hybrid semantic + keyword retrieval (#223: FTS5 BM25 + cosine + recency + usefulness, fused with RRF; degrades to keyword+recency when embeddings are unavailable). Returns the matching entities, best first, plus only the relations BETWEEN matched entities.")]
    public static async Task<KnowledgeGraph> SearchNodes(
        IMemorySearch memorySearch,
        IMemoryService memoryService,
        [Description("Free-text query; matches entity names, types (tags), and observations.")] string query,
        [Description("Maximum entities to return (default 10; must be greater than zero; capped at 100).")] int k = 10,
        [Description("Optional memory namespace key (default 'knowledge-graph').")] string? scopeKey = null,
        CancellationToken cancellationToken = default)
    {
        // Guard blank input here — never let the service's ValidationException (or an
        // everything-matches recency scan) surface for what is a caller mistake.
        if (string.IsNullOrWhiteSpace(query))
            throw new McpException("query must be non-empty. Pass the text to search for (e.g. a name, tag, or observed fact).");

        // Ceiling only -- a non-positive k must reach the service guard so the catch below
        // rethrows the same message REST returns. See #338.
        k = Math.Min(k, 100);
        var key = NormalizeScopeKey(scopeKey);

        IReadOnlyList<MemoryHit> hits;
        try
        {
            hits = await memorySearch.SearchAsync(MemoryScope.Agent, key, query, k, opts: null, cancellationToken);
        }
        catch (ValidationException ex)
        {
            throw new McpException(ex.Message);
        }

        // The hybrid retrieval ranks EVERY candidate (recency/usefulness are query-independent), but
        // the official contract returns MATCHING entities. Keep only hits carrying a query-driven
        // signal — a keyword (BM25) or vector (cosine) rank — in their ranked order, best first.
        // Resolution is id-targeted (uncapped ListAsync + per-id reads), so a match is never
        // silently dropped by the read_graph cap (Copilot review, PR #259); the ContainsKey guard
        // only drops a hit whose node was retired between the search and this read.
        var byId = (await memoryService.ListAsync(MemoryScope.Agent, key, type: null, includeSuperseded: false, cancellationToken))
            .ToDictionary(n => n.Id);
        var matched = hits
            .Where(h => (h.KeywordRank is not null || h.VectorRank is not null) && byId.ContainsKey(h.Id))
            .Select(h => byId[h.Id])
            .ToList();

        return await ShapeSelectionAsync(memoryService, matched, cancellationToken);
    }

    [McpServerTool(Name = "open_nodes")]
    [Description("Open specific knowledge-graph entities by name: returns those entities plus only the relations between them. Missing names are silently skipped (official MCP memory contract).")]
    public static async Task<KnowledgeGraph> OpenNodes(
        IMemoryService memoryService,
        [Description("Names of the entities to open.")] IReadOnlyList<string> names,
        [Description("Optional memory namespace key (default 'knowledge-graph').")] string? scopeKey = null,
        CancellationToken cancellationToken = default)
    {
        if (names is null)
            throw new McpException("names is required: pass an array of entity names to open.");

        var key = NormalizeScopeKey(scopeKey);

        // Name resolution is uncapped (ListAsync-backed), so only genuinely MISSING names are
        // skipped — a node beyond the read_graph cap still opens (Copilot review, PR #259).
        var byName = await CurrentNodesByNameAsync(memoryService, key, cancellationToken);
        var matched = names
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.Ordinal)
            .Where(byName.ContainsKey)
            .Select(n => byName[n])
            .ToList();

        return await ShapeSelectionAsync(memoryService, matched, cancellationToken);
    }

    // Blank/omitted scopeKey ⇒ the shared agent knowledge graph, so the zero-arg defaults always work.
    private static string NormalizeScopeKey(string? scopeKey) =>
        string.IsNullOrWhiteSpace(scopeKey) ? DefaultKnowledgeGraphScopeKey : scopeKey.Trim();

    /// <summary>
    /// Name -&gt; node map over the CURRENT nodes of the knowledge-graph namespace, via the
    /// (uncapped) <see cref="IMemoryService.ListAsync"/> so mutations never mis-resolve a name that a
    /// capped graph read might miss. Duplicate contents (possible via the REST remember surface)
    /// resolve to the oldest node deterministically.
    /// </summary>
    private static async Task<Dictionary<string, MemoryNode>> CurrentNodesByNameAsync(
        IMemoryService memoryService, string scopeKey, CancellationToken ct)
    {
        var nodes = await memoryService.ListAsync(MemoryScope.Agent, scopeKey, type: null, includeSuperseded: false, ct);
        var byName = new Dictionary<string, MemoryNode>(StringComparer.Ordinal);
        foreach (var node in nodes.OrderBy(n => n.Id))
            byName.TryAdd(node.Content, node);
        return byName;
    }

    private static MemoryNode ResolveEntity(IReadOnlyDictionary<string, MemoryNode> byName, string name)
    {
        var trimmed = name.Trim();
        return byName.TryGetValue(trimmed, out var node)
            ? node
            : throw new McpException($"Entity '{trimmed}' not found in the knowledge graph. Create it with create_entities first, or list what exists with read_graph.");
    }

    /// <summary>
    /// Shape a chosen set of CURRENT nodes into the official contract's <see cref="KnowledgeGraph"/>
    /// using id-targeted reads (observations + relations for exactly those nodes) — never the capped
    /// <see cref="IMemoryService.ReadGraphAsync"/>, so search_nodes/open_nodes stay correct at any
    /// namespace size (Copilot review, PR #259). <see cref="ShapeGraph"/> keeps the between-selected
    /// relation rule.
    /// </summary>
    private static async Task<KnowledgeGraph> ShapeSelectionAsync(
        IMemoryService memoryService, IReadOnlyList<MemoryNode> selected, CancellationToken ct)
    {
        if (selected.Count == 0)
            return new KnowledgeGraph([], []);

        var ids = selected.Select(n => n.Id).ToHashSet();
        var observations = await memoryService.GetObservationsForNodesAsync(ids, ct);
        var relations = await memoryService.GetMemoryNodeRelationsAsync(ids, ct);
        return ShapeGraph(new MemoryGraph(selected, observations, relations), selected);
    }

    /// <summary>
    /// Shape a store-level <see cref="MemoryGraph"/> into the official contract's
    /// <see cref="KnowledgeGraph"/> for a chosen subset of its nodes: those entities plus only the
    /// relations BETWEEN them (edges to unselected nodes are excluded — the search_nodes/open_nodes rule).
    /// </summary>
    private static KnowledgeGraph ShapeGraph(MemoryGraph graph, IReadOnlyList<MemoryNode> selected)
    {
        // Defensive: a duplicate id in the selection (e.g. an upstream ranking edge case) must not
        // crash the ToDictionary below — first occurrence wins (Gemini review, PR #259).
        selected = selected.DistinctBy(n => n.Id).ToList();

        var observationsByNode = graph.Observations
            .GroupBy(o => o.MemoryNodeId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(o => o.Content).ToList());

        var entities = selected
            .Select(n => new MemoryEntity(
                n.Content,
                n.Tags ?? string.Empty,
                observationsByNode.GetValueOrDefault(n.Id, [])))
            .ToList();

        var selectedById = selected.ToDictionary(n => n.Id);
        var relations = graph.Relations
            .Where(r => selectedById.ContainsKey(r.FromId) && selectedById.ContainsKey(r.ToId))
            .Select(r => new MemoryRelationDto(
                selectedById[r.FromId].Content, selectedById[r.ToId].Content, r.RelationType))
            .ToList();

        return new KnowledgeGraph(entities, relations);
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
public sealed record TaskDetail(int Id, int FeatureId, string Name, string? Description, string Status, string Priority, string? Assignee, string? ClaimedBy, IReadOnlyList<ChecklistItem> AcceptanceCriteria, IReadOnlyList<ChecklistItem> Subtasks, IReadOnlyList<int> DependsOnTaskIds, IReadOnlyList<TaskCommentDetail>? Comments = null);
public sealed record TaskCommentDetail(int Id, string Text, string? Author, string Type, DateTime CreatedAt);
public sealed record NextTask(int Id, string Name, string Status, string Priority, string? ClaimedBy, string Reason);
public sealed record TaskStatusResult(int Id, string Name, string Status, string Message);
public sealed record TaskDetailResult(int Id, int FeatureId, string Name, string? Description, string Status, string Priority, string? Assignee, double SortOrder, string Message);
public sealed record TaskDeleteResult(int Id, bool Deleted, string Message);
public sealed record AcceptanceCriterionResult(int Id, int TaskItemId, string Text, bool Completed, int SortOrder, string Message);
public sealed record TaskCommentResult(int Id, int TaskItemId, string Text, string? Author, string Type, DateTime CreatedAt);
public sealed record TaskClaimResult(int Id, string Name, string? ClaimedBy, DateTime? ClaimedAt, string Message);

// ── Idea inbox DTOs (#180 Phase 3) — IdeaResult mirrors the REST MapIdea field set ──
public sealed record IdeaResult(int Id, string Title, string? Body, string? Plan, string Status, string? Tags, string? Priority, int? PromotedProjectId, DateTime CreatedAt, DateTime UpdatedAt);
public sealed record IdeaPromotionResult(int IdeaId, int ProjectId, string Message);

// ── Knowledge-graph memory DTOs (#224) — serialized camelCase by the MCP SDK, so the JSON matches
// the official memory server's shapes exactly: {name, entityType, observations} / {from, to, relationType}.
/// <summary>Input entity: <c>{name, entityType, observations}</c> (official contract shape).</summary>
public sealed record MemoryEntityInput(string Name, string EntityType, IReadOnlyList<string>? Observations = null);
/// <summary>Input/patch for add_observations: <c>{entityName, contents}</c>.</summary>
public sealed record MemoryObservationInput(string EntityName, IReadOnlyList<string>? Contents = null);
/// <summary>Input for delete_observations: <c>{entityName, observations}</c>.</summary>
public sealed record MemoryObservationDeletionInput(string EntityName, IReadOnlyList<string>? Observations = null);
/// <summary>Directed relation in active voice: <c>{from, to, relationType}</c> (input and output shape).</summary>
public sealed record MemoryRelationInput(string From, string To, string RelationType);
/// <summary>Output entity: <c>{name, entityType, observations}</c>.</summary>
public sealed record MemoryEntity(string Name, string EntityType, IReadOnlyList<string> Observations);
/// <summary>Output relation: <c>{from, to, relationType}</c>.</summary>
public sealed record MemoryRelationDto(string From, string To, string RelationType);
/// <summary>The graph payload read_graph/search_nodes/open_nodes return: <c>{entities, relations}</c>.</summary>
public sealed record KnowledgeGraph(IReadOnlyList<MemoryEntity> Entities, IReadOnlyList<MemoryRelationDto> Relations);
/// <summary>add_observations result item: <c>{entityName, addedObservations}</c> (official shape).</summary>
public sealed record ObservationAdditionResult(string EntityName, IReadOnlyList<string> AddedObservations);
/// <summary>delete_entities result: the names actually deleted (missing names are skipped).</summary>
public sealed record EntityDeletionResult(IReadOnlyList<string> Deleted, string Message);
/// <summary>delete_observations result: how many observation rows were removed.</summary>
public sealed record ObservationDeletionResult(int DeletedCount, string Message);
/// <summary>delete_relations result: how many relation rows were removed.</summary>
public sealed record RelationDeletionResult(int DeletedCount, string Message);
