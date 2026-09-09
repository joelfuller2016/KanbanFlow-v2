using System.Text.Json;
using KanbanFlow.Data;
using KanbanFlow.Models;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Services;

/// <summary>
/// Default <see cref="ITaskService"/> implementation. Uses
/// <see cref="IDbContextFactory{AppDbContext}"/> so each call gets its own
/// short-lived <see cref="AppDbContext"/> — safe for concurrent Blazor Server
/// circuits and Minimal API requests alike.
/// </summary>
/// <param name="claimTtl">
/// How long a task claim stays fresh before another actor may take it over (#429). Optional so
/// the ~850 existing <c>new TaskService(factory)</c> call sites keep compiling and keep the
/// production default; tests inject a short TTL rather than sleeping in real time.
/// </param>
/// <param name="clock">
/// Optional clock function. Defaults to <see cref="DateTime.UtcNow"/>. Injected for
/// deterministic testing so audit-age assertions do not depend on wall-clock sampling.
/// </param>
public sealed class TaskService(
    IDbContextFactory<AppDbContext> dbFactory,
    TimeSpan? claimTtl = null,
    Func<DateTime>? clock = null) : ITaskService
{
    // ClaimPolicy.ValidateTtl, not a local `<= Zero` check: the upper bound was missing here, so
    // `new TaskService(factory, TimeSpan.MaxValue)` constructed fine and then threw on the next
    // claim from inside StaleCutoff. One validator, every entry point. (codex, PR #435 round 3.)
    private readonly TimeSpan _claimTtl = ValidatedTtl(claimTtl);

    private static TimeSpan ValidatedTtl(TimeSpan? claimTtl)
    {
        if (claimTtl is not { } t) return ClaimPolicy.DefaultTtl;
        ClaimPolicy.ValidateTtl(t, nameof(claimTtl));
        return t;
    }

    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.UtcNow);

    private static readonly JsonSerializerOptions ActivityJsonOptions = new(JsonSerializerDefaults.Web);

    private static object TaskActivitySnapshot(TaskItem task) => new
    {
        task.Id,
        task.FeatureId,
        task.Name,
        task.Description,
        Status = task.Status.ToString(),
        Priority = task.Priority.ToString(),
        task.Assignee,
        task.ClaimedBy,
        task.ClaimedAt,
        task.SortOrder,
        task.CompletedAt
    };

    /// <summary>
    /// Definition-of-Done gate. Returns the unmet acceptance criteria when the caller supplied a
    /// valid override (so the caller can audit it); returns empty when the transition is clean;
    /// throws <see cref="DefinitionOfDoneNotMetException"/> when the transition must be refused.
    /// </summary>
    /// <remarks>
    /// Shared by <em>every</em> path that can reach <see cref="WorkStatus.Done"/> —
    /// <see cref="ChangeStatusAsync"/>, <see cref="MoveOnBoardAsync"/>, <see cref="ToggleDoneAsync"/>
    /// and <see cref="UpdateAsync"/>. Gating only the obvious one would leave drag-to-Done, the
    /// toggle and a plain field update as unguarded ways around it.
    /// <para>
    /// A task with zero acceptance criteria is never gated, and <c>Done → Done</c> never fires
    /// (the <paramref name="oldStatus"/> check), so the gate applies strictly to a transition
    /// <em>into</em> Done on a task that has criteria.
    /// </para>
    /// </remarks>
    private static async Task<IReadOnlyList<string>> GateDoneTransitionAsync(
        AppDbContext db, int taskId, WorkStatus oldStatus, WorkStatus newStatus,
        string? overrideReason, CancellationToken ct)
    {
        if (newStatus != WorkStatus.Done || oldStatus == WorkStatus.Done)
            return [];

        var unmet = await db.AcceptanceCriteria
            .Where(c => c.TaskItemId == taskId && !c.Completed)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Id)
            .Select(c => c.Text)
            .ToListAsync(ct);

        if (unmet.Count == 0)
            return [];

        // Whitespace is not an override — otherwise " " silently defeats the gate.
        if (string.IsNullOrWhiteSpace(overrideReason))
            throw new DefinitionOfDoneNotMetException(taskId, unmet);

        // The reason arrives from untrusted edges (REST body, MCP argument) and is persisted
        // TWICE — into TaskComment.Text and into the activity event's AfterJson and Message — so
        // an unbounded string is written to three columns. Bound it at the gate, where every
        // caller passes through, rather than at each edge.
        if (overrideReason.Trim().Length > MaxOverrideReasonLength)
            throw new ValidationException(
                $"Override reason must be {MaxOverrideReasonLength} characters or fewer.");

        return unmet;
    }

    /// <summary>Upper bound for an audited Definition-of-Done override reason.</summary>
    private const int MaxOverrideReasonLength = 1000;

    /// <summary>
    /// Records an audited Definition-of-Done override: one <see cref="CommentType.Verification"/>
    /// comment (giving that previously-unread comment type its first real consumer) and one
    /// <c>TaskDoneOverridden</c> activity event. Called inside the caller's existing
    /// <c>SaveChanges</c> so the override can never persist without its audit trail (#156).
    /// </summary>
    private static void AddDoneOverrideRecords(
        AppDbContext db, TaskItem task, int projectId, string? actor,
        string overrideReason, IReadOnlyList<string> unmet)
    {
        var reason = overrideReason.Trim();
        var criteriaList = string.Join("\n", unmet.Select(t => $"- {t}"));

        db.TaskComments.Add(new TaskComment
        {
            TaskItemId = task.Id,
            Text = $"Definition of Done overridden: {reason}\n\n"
                   + $"Unmet acceptance criteria ({unmet.Count}):\n{criteriaList}",
            Author = string.IsNullOrWhiteSpace(actor) ? "System" : actor!.Trim(),
            Type = CommentType.Verification,
            CreatedAt = DateTime.UtcNow
        });

        AddActivityEvent(
            db, projectId, "Task", task.Id, actor,
            "TaskDoneOverridden", before: null,
            after: new { unmetCount = unmet.Count, unmet, reason },
            message: $"Task '{task.Name}' marked Done with {unmet.Count} unmet acceptance criteria: {reason}");
    }

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

    /// <summary>Moves a task to <paramref name="newStatus"/>, enforcing the Definition-of-Done gate for Done-bound transitions, and records an audit event.</summary>
    public async Task<TaskItem> ChangeStatusAsync(int taskId, WorkStatus newStatus, string? actor, CancellationToken ct = default, string? overrideReason = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Serialize the Definition-of-Done read-modify-write. The gate reads AcceptanceCriteria
        // and the status write happens at SaveChanges; without a lock spanning both, a concurrent
        // criterion insert can commit in between and Done persists against criteria the gate never
        // saw. Microsoft.Data.Sqlite issues BEGIN IMMEDIATE for this overload, so the write lock is
        // taken HERE, before the reads — the same pattern MoveOnBoardAsync and MoveInOutlineAsync
        // use. (CreateAsync opens a transaction after its read; ClaimAsync opens one BEFORE its read,
        // following the same before-read pattern as these methods.)
        //
        // Scoped to Done-bound transitions ONLY. SQLite permits a single pending writer, so taking
        // the lock unconditionally would make every Backlog/Todo/Review move queue behind any other
        // writer and risk SQLITE_BUSY on a transition the gate never even inspects. A non-Done
        // transition cannot read criteria (the gate returns immediately), so it needs no span.
        await using var tx = newStatus == WorkStatus.Done
            ? await db.Database.BeginTransactionAsync(ct)
            : null;

        var task = await db.Tasks
            .Include(t => t.Feature)
            .FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null)
            throw new TaskNotFoundException(taskId);
        if (task.Feature is null)
            throw new InvalidOperationException($"Task {taskId} has no feature; cannot resolve project for audit.");

        var oldStatus = task.Status;

        // Idempotent: if no actual change, skip side effects entirely. Deliberately BEFORE the
        // Definition-of-Done gate so Done → Done stays a no-op rather than becoming a refusal.
        if (oldStatus == newStatus)
            return task;

        // Definition-of-Done gate. This is the one path that accepts an override, because it is
        // the path the task detail panel's explicit "Complete anyway" affordance calls.
        var overriddenCriteria = await GateDoneTransitionAsync(db, taskId, oldStatus, newStatus, overrideReason, ct);

        var before = TaskActivitySnapshot(task);

        task.Status = newStatus;
        task.UpdatedAt = DateTime.UtcNow;
        if (newStatus == WorkStatus.Done && oldStatus != WorkStatus.Done)
            task.CompletedAt = DateTime.UtcNow;
        else if (newStatus != WorkStatus.Done)
            task.CompletedAt = null;

        db.TaskComments.Add(new TaskComment
        {
            TaskItemId = task.Id,
            Text = $"Status changed from {oldStatus} to {newStatus}",
            Author = string.IsNullOrWhiteSpace(actor) ? "System" : actor!.Trim(),
            Type = CommentType.StatusChange,
            CreatedAt = DateTime.UtcNow
        });

        AddActivityEvent(
            db, task.Feature.ProjectId, "Task", task.Id, actor,
            "TaskStatusChanged", before, TaskActivitySnapshot(task),
            $"Task '{task.Name}' status changed from {oldStatus} to {newStatus}");

        // Atomic with the status change: an override can never persist without its audit trail.
        if (overriddenCriteria.Count > 0)
            AddDoneOverrideRecords(db, task, task.Feature.ProjectId, actor, overrideReason!, overriddenCriteria);

        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return task;
    }

    public async Task<TaskItem> MoveOnBoardAsync(
        int taskId,
        WorkStatus newStatus,
        int projectId,
        int beforeTaskId,
        string? actor,
        CancellationToken ct = default)
    {
        if (!Enum.IsDefined(newStatus))
            throw new ValidationException($"Invalid status '{newStatus}'.");
        if (beforeTaskId < 0)
            throw new ValidationException("The before-task id must be zero or a positive task id.");
        if (beforeTaskId == taskId)
            throw new ValidationException("A task cannot be positioned before itself.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Serialize the whole read-modify-write. The destination read below and the rank write at
        // the end are one logical operation: without a lock spanning them, two concurrent moves
        // into the same gap both read the same neighbours, both compute the same midpoint, and
        // both persist it — leaving duplicate ranks that TaskOrdering can only break by Id, so one
        // user's drop silently lands in the wrong place. A concurrent move could also retire the
        // anchor between the read and the write, persisting a position that was never validated.
        // Microsoft.Data.Sqlite issues BEGIN IMMEDIATE for this overload (it only defers when the
        // caller passes deferred: true), so the write lock is taken HERE, before the neighbour
        // read — the same pattern CreateAsync/ClaimAsync/ReleaseAsync already use in this file.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var task = await db.Tasks
            .Include(t => t.Feature)
            .FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null)
            throw new TaskNotFoundException(taskId);
        if (task.Feature is null)
            throw new InvalidOperationException($"Task {taskId} has no feature; cannot resolve project for audit.");
        if (task.Feature.ProjectId != projectId)
            throw new ValidationException($"Task {taskId} does not belong to project {projectId}.");

        // This is the complete persisted destination scope. The browser may have rendered any
        // subset of it; beforeTaskId is the only ordering fact received from the DOM (#307).
        var destinationTasks = await db.Tasks
            .AsNoTracking()
            .Where(t => t.Feature.ProjectId == projectId && t.Status == newStatus)
            .ToListAsync(ct);
        destinationTasks = TaskOrdering.Order(destinationTasks).ToList();
        var originalOrder = destinationTasks.Select(t => t.Id).ToArray();

        destinationTasks.RemoveAll(t => t.Id == taskId);
        var insertIndex = ResolveAnchorIndex(destinationTasks, beforeTaskId, "project/status column");
        var desiredOrder = destinationTasks.Select(t => t.Id).ToList();
        desiredOrder.Insert(insertIndex, taskId);

        var oldStatus = task.Status;
        var statusChanged = oldStatus != newStatus;
        var orderChanged = !originalOrder.SequenceEqual(desiredOrder);
        if (!statusChanged && !orderChanged)
            return task;

        // Allocate before mutating status so an invalid/exhausted rank cannot leave the tracked
        // entity partially changed. Only the dragged row receives a new rank; every sibling keeps
        // its exact persisted value, including siblings omitted from the rendered board (#307).
        var newSortOrder = orderChanged
            ? AllocateSortOrder(destinationTasks, insertIndex, taskId)
            : task.SortOrder;

        // Definition-of-Done gate. A drag cannot carry a reason, so this path never overrides —
        // the card snaps back and the human uses the detail panel's explicit override instead.
        if (statusChanged)
            await GateDoneTransitionAsync(db, taskId, oldStatus, newStatus, overrideReason: null, ct);

        var before = TaskActivitySnapshot(task);
        var now = DateTime.UtcNow;

        if (statusChanged)
        {
            task.Status = newStatus;
            task.CompletedAt = newStatus == WorkStatus.Done ? now : null;
        }

        if (orderChanged)
        {
            task.SortOrder = newSortOrder;
        }

        task.UpdatedAt = now;

        if (statusChanged)
        {
            db.TaskComments.Add(new TaskComment
            {
                TaskItemId = task.Id,
                Text = $"Status changed from {oldStatus} to {newStatus}",
                Author = string.IsNullOrWhiteSpace(actor) ? "System" : actor!.Trim(),
                Type = CommentType.StatusChange,
                CreatedAt = now
            });
            AddActivityEvent(
                db, projectId, "Task", task.Id, actor,
                "TaskStatusChanged", before, TaskActivitySnapshot(task),
                $"Task '{task.Name}' status changed from {oldStatus} to {newStatus}");
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return task;
    }

    public async Task<TaskItem> ToggleDoneAsync(int taskId, string? actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Serialize the Definition-of-Done read-modify-write: the gate reads
        // AcceptanceCriteria and the status lands at SaveChanges, so without a lock
        // spanning both, a concurrent criterion insert can commit in between and Done
        // persists against criteria the gate never saw. BEGIN IMMEDIATE takes the write
        // lock here, before the reads — same pattern as MoveOnBoardAsync.
        //
        // UNCONDITIONAL here, unlike ChangeStatusAsync/UpdateAsync: this method derives its
        // target status from the CURRENT status, so that read must already be inside the span.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var task = await db.Tasks
            .Include(t => t.Feature)
            .Include(t => t.Subtasks)
            .FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null)
            throw new TaskNotFoundException(taskId);
        if (task.Feature is null)
            throw new InvalidOperationException($"Task {taskId} has no feature; cannot resolve project for audit.");

        var oldStatus = task.Status;
        var newStatus = oldStatus == WorkStatus.Done ? WorkStatus.Todo : WorkStatus.Done;
        var markSubtasksCompleted = newStatus == WorkStatus.Done;

        // Definition-of-Done gate. The toggle carries no reason, so it refuses rather than
        // overriding; un-toggling (Done → Todo) is unaffected because the gate is Done-only.
        await GateDoneTransitionAsync(db, taskId, oldStatus, newStatus, overrideReason: null, ct);

        var before = TaskActivitySnapshot(task);

        task.Status = newStatus;
        task.UpdatedAt = DateTime.UtcNow;
        task.CompletedAt = newStatus == WorkStatus.Done ? DateTime.UtcNow : null;

        foreach (var subtask in task.Subtasks)
        {
            subtask.Completed = markSubtasksCompleted;
        }

        db.TaskComments.Add(new TaskComment
        {
            TaskItemId = task.Id,
            Text = $"Status changed from {oldStatus} to {newStatus}",
            Author = string.IsNullOrWhiteSpace(actor) ? "System" : actor!.Trim(),
            Type = CommentType.StatusChange,
            CreatedAt = DateTime.UtcNow
        });

        AddActivityEvent(
            db, task.Feature.ProjectId, "Task", task.Id, actor,
            "TaskStatusChanged", before, TaskActivitySnapshot(task),
            $"Task '{task.Name}' status changed from {oldStatus} to {newStatus}");

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return task;
    }

    public async Task<TaskItem> ReassignToFeatureAsync(int taskId, int newFeatureId, string? actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var task = await db.Tasks
            .Include(t => t.Feature)
            .FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null)
            throw new TaskNotFoundException(taskId);
        if (task.Feature is null)
            throw new InvalidOperationException($"Task {taskId} has no feature; cannot resolve project for audit.");

        var oldFeatureId = task.FeatureId;
        var oldProjectId = task.Feature.ProjectId;

        // Idempotent: same feature → no audit, no comment, no UpdatedAt churn.
        if (newFeatureId == oldFeatureId)
            return task;

        // Distinct exception (vs TaskNotFoundException) so the API layer can surface
        // a clear "target feature missing" message — the source task DOES exist here.
        var newFeature = await db.Features.FirstOrDefaultAsync(f => f.Id == newFeatureId, ct);
        if (newFeature is null)
            throw new FeatureNotFoundException(newFeatureId);

        // #83: project-boundary enforcement. Reassigning across projects would orphan
        // requirements/dependencies/activity history rooted in the source project.
        if (newFeature.ProjectId != oldProjectId)
            throw new CrossProjectReassignmentException(taskId, oldProjectId, newFeature.ProjectId);

        var before = TaskActivitySnapshot(task);

        // #207: SortOrder is per-feature, so keeping the source-feature SortOrder would collide with an
        // existing task in the destination (feature-scoped views order by SortOrder with no Id tie-break
        // → silent, non-deterministic order). Append to the END of the destination, mirroring CreateAsync.
        // MaxAsync runs before SaveChanges while the task still has its OLD FeatureId in the DB, so it
        // correctly excludes the moved task and yields a clean, collision-free append.
        var maxSort = await db.Tasks
            .Where(t => t.FeatureId == newFeatureId)
            .MaxAsync(t => (double?)t.SortOrder, ct);
        task.FeatureId = newFeatureId;
        task.SortOrder = AllocateAppendSortOrder(maxSort);
        task.UpdatedAt = DateTime.UtcNow;

        db.TaskComments.Add(new TaskComment
        {
            TaskItemId = task.Id,
            Text = $"Feature changed: was feature {oldFeatureId}, now feature {newFeatureId}",
            Author = string.IsNullOrWhiteSpace(actor) ? "System" : actor!.Trim(),
            Type = CommentType.FeatureChange,
            CreatedAt = DateTime.UtcNow
        });

        AddActivityEvent(
            db, oldProjectId, "Task", task.Id, actor,
            "TaskFeatureChanged", before, TaskActivitySnapshot(task),
            $"Task '{task.Name}' reassigned from feature {oldFeatureId} to feature {newFeatureId}");

        await db.SaveChangesAsync(ct);
        return task;
    }

    public async Task<TaskItem> MoveInOutlineAsync(
        int taskId,
        int newFeatureId,
        int beforeTaskId,
        string? actor,
        CancellationToken ct = default)
    {
        if (beforeTaskId < 0)
            throw new ValidationException("The before-task id must be zero or a positive task id.");
        if (beforeTaskId == taskId)
            throw new ValidationException("A task cannot be positioned before itself.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Same read-modify-write serialization as MoveOnBoardAsync — see the note there. The
        // outline scope is feature-scoped rather than project/status-scoped, but the race and the
        // BEGIN IMMEDIATE guarantee are identical.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var task = await db.Tasks
            .Include(t => t.Feature)
            .FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null)
            throw new TaskNotFoundException(taskId);
        if (task.Feature is null)
            throw new InvalidOperationException($"Task {taskId} has no feature; cannot resolve project for audit.");

        var destinationFeature = await db.Features.FirstOrDefaultAsync(f => f.Id == newFeatureId, ct);
        if (destinationFeature is null)
            throw new FeatureNotFoundException(newFeatureId);

        var oldFeatureId = task.FeatureId;
        var projectId = task.Feature.ProjectId;
        if (destinationFeature.ProjectId != projectId)
            throw new CrossProjectReassignmentException(taskId, projectId, destinationFeature.ProjectId);

        // Unlike the board, outline order is feature-scoped across every status. Expanded
        // subtask DOM siblings are deliberately absent from this persisted candidate list.
        var destinationTasks = await db.Tasks
            .AsNoTracking()
            .Where(t => t.FeatureId == newFeatureId)
            .ToListAsync(ct);
        destinationTasks = TaskOrdering.Order(destinationTasks).ToList();
        var originalOrder = destinationTasks.Select(t => t.Id).ToArray();

        destinationTasks.RemoveAll(t => t.Id == taskId);
        var insertIndex = ResolveAnchorIndex(destinationTasks, beforeTaskId, "feature");
        var desiredOrder = destinationTasks.Select(t => t.Id).ToList();
        desiredOrder.Insert(insertIndex, taskId);

        var featureChanged = oldFeatureId != newFeatureId;
        var orderChanged = !originalOrder.SequenceEqual(desiredOrder);
        if (!featureChanged && !orderChanged)
            return task;

        var newSortOrder = orderChanged
            ? AllocateSortOrder(destinationTasks, insertIndex, taskId)
            : task.SortOrder;

        var before = TaskActivitySnapshot(task);
        var now = DateTime.UtcNow;

        if (featureChanged)
        {
            task.FeatureId = newFeatureId;
            task.Feature = destinationFeature;
        }

        if (orderChanged)
        {
            task.SortOrder = newSortOrder;
        }

        task.UpdatedAt = now;

        if (featureChanged)
        {
            db.TaskComments.Add(new TaskComment
            {
                TaskItemId = task.Id,
                Text = $"Feature changed: was feature {oldFeatureId}, now feature {newFeatureId}",
                Author = string.IsNullOrWhiteSpace(actor) ? "System" : actor!.Trim(),
                Type = CommentType.FeatureChange,
                CreatedAt = now
            });
            AddActivityEvent(
                db, projectId, "Task", task.Id, actor,
                "TaskFeatureChanged", before, TaskActivitySnapshot(task),
                $"Task '{task.Name}' reassigned from feature {oldFeatureId} to feature {newFeatureId}");
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return task;
    }

    private static int ResolveAnchorIndex(List<TaskItem> destinationTasks, int beforeTaskId, string scopeName)
    {
        if (beforeTaskId == 0)
            return destinationTasks.Count;

        var anchorIndex = destinationTasks.FindIndex(t => t.Id == beforeTaskId);
        if (anchorIndex < 0)
        {
            throw new ValidationException(
                $"Anchor task {beforeTaskId} is not in the destination {scopeName}.");
        }

        return anchorIndex;
    }

    /// <summary>
    /// Return a rank that places one task at <paramref name="insertIndex"/> without changing
    /// any existing rank. The Id tie-break is used only when no distinct IEEE-754 value exists.
    /// If neither a value nor a safe tie can represent the requested identity order, fail visibly
    /// instead of silently renumbering siblings.
    /// </summary>
    private static double AllocateSortOrder(
        IReadOnlyList<TaskItem> orderedWithoutMoved,
        int insertIndex,
        int movingTaskId)
    {
        if (insertIndex < 0 || insertIndex > orderedWithoutMoved.Count)
            throw new ArgumentOutOfRangeException(nameof(insertIndex));

        if (orderedWithoutMoved.Count == 0)
            return 0d;

        var lower = insertIndex > 0 ? orderedWithoutMoved[insertIndex - 1] : null;
        var upper = insertIndex < orderedWithoutMoved.Count ? orderedWithoutMoved[insertIndex] : null;
        if (lower is not null) EnsureFiniteSortOrder(lower);
        if (upper is not null) EnsureFiniteSortOrder(upper);

        if (lower is null)
        {
            var upperRank = TaskOrdering.EffectiveRank(upper!);
            if (TryCandidate(upper!.SortOrder - 1d, movingTaskId, null, upper, out var beforeFirst)
                || TryCandidate(upperRank - 1d, movingTaskId, null, upper, out beforeFirst)
                || TryCandidate(Math.BitDecrement(upperRank), movingTaskId, null, upper, out beforeFirst)
                || TryCandidate(upper!.SortOrder, movingTaskId, null, upper, out beforeFirst))
            {
                return beforeFirst;
            }

            throw OrderingSpaceExhausted();
        }

        if (upper is null)
        {
            var lowerRank = TaskOrdering.EffectiveRank(lower);
            if (TryCandidate(lower.SortOrder + 1d, movingTaskId, lower, null, out var afterLast)
                || TryCandidate(lowerRank + 1d, movingTaskId, lower, null, out afterLast)
                || TryCandidate(Math.BitIncrement(lowerRank), movingTaskId, lower, null, out afterLast)
                || TryCandidate(lower.SortOrder, movingTaskId, lower, null, out afterLast))
            {
                return afterLast;
            }

            throw OrderingSpaceExhausted();
        }

        if (TaskOrdering.Compare(lower.SortOrder, lower.Id, upper) >= 0)
            throw new ValidationException("Persisted task ordering is invalid.");

        // Prefer the intuitive raw midpoint when the persisted values differ (for example,
        // 0 and 1 -> 0.5). Validation uses effective ordering, so legacy integral expansion
        // cannot accidentally put the candidate on the wrong side of either identity.
        if (lower.SortOrder < upper.SortOrder)
        {
            var rawMidpoint = Midpoint(lower.SortOrder, upper.SortOrder);
            if (TryCandidate(rawMidpoint, movingTaskId, lower, upper, out var betweenRawRanks))
                return betweenRawRanks;
        }

        // Equal integral ranks are normal across features. Their effective ranks are distinct
        // legacy positions derived from Id, so persist a fractional midpoint between them.
        // The two raw-value fallbacks below reuse a neighbour's exact rank and rely on the Id
        // tie-break in TaskOrdering.Compare. That only represents the requested order when
        // movingTaskId itself sorts between lower.Id and upper.Id — the same drag succeeds or
        // fails depending on the creation order of unrelated rows. Failing is deliberate: the
        // alternative is renumbering siblings the caller never saw (#307).
        var effectiveMidpoint = Midpoint(
            TaskOrdering.EffectiveRank(lower),
            TaskOrdering.EffectiveRank(upper));
        if (TryCandidate(effectiveMidpoint, movingTaskId, lower, upper, out var betweenEffectiveRanks)
            || TryCandidate(lower.SortOrder, movingTaskId, lower, upper, out betweenEffectiveRanks)
            || TryCandidate(upper.SortOrder, movingTaskId, lower, upper, out betweenEffectiveRanks))
        {
            return betweenEffectiveRanks;
        }

        throw OrderingSpaceExhausted();
    }

    private static double Midpoint(double lower, double upper)
        => (lower / 2d) + (upper / 2d);

    private static bool TryCandidate(
        double candidate,
        int movingTaskId,
        TaskItem? lower,
        TaskItem? upper,
        out double persistedSortOrder)
    {
        // An integral candidate is virtually expanded by Id. Its adjacent representable values
        // are fractional and therefore encode the candidate literally, so try both as fallbacks.
        var candidates = new[] { candidate, Math.BitIncrement(candidate), Math.BitDecrement(candidate) };
        foreach (var rawSortOrder in candidates)
        {
            if (!double.IsFinite(rawSortOrder))
                continue;
            if (lower is not null && TaskOrdering.Compare(rawSortOrder, movingTaskId, lower) <= 0)
                continue;
            if (upper is not null && TaskOrdering.Compare(rawSortOrder, movingTaskId, upper) >= 0)
                continue;

            persistedSortOrder = rawSortOrder;
            return true;
        }

        persistedSortOrder = default;
        return false;
    }

    private static double AllocateAppendSortOrder(double? maxSortOrder)
    {
        if (maxSortOrder is null)
            return 0d;
        if (!double.IsFinite(maxSortOrder.Value))
            throw new ValidationException("Persisted task ordering contains a non-finite rank.");

        var candidate = maxSortOrder.Value + 1d;
        if (double.IsFinite(candidate) && candidate > maxSortOrder.Value)
            return candidate;

        candidate = Math.BitIncrement(maxSortOrder.Value);
        if (double.IsFinite(candidate) && candidate > maxSortOrder.Value)
            return candidate;

        throw OrderingSpaceExhausted();
    }

    private static void EnsureFiniteSortOrder(TaskItem task)
    {
        if (!double.IsFinite(task.SortOrder))
        {
            throw new ValidationException(
                $"Task {task.Id} has a non-finite ordering rank.");
        }
    }

    private static ValidationException OrderingSpaceExhausted()
        => new("No ordering space remains between the selected tasks.");

    public async Task<TaskItem> UpdateAsync(int taskId, TaskUpdate dto, string? actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Serialize the Definition-of-Done read-modify-write: the gate reads
        // AcceptanceCriteria and the status lands at SaveChanges, so without a lock
        // spanning both, a concurrent criterion insert can commit in between and Done
        // persists against criteria the gate never saw. BEGIN IMMEDIATE takes the write
        // lock here, before the reads — same pattern as MoveOnBoardAsync.
        //
        // Scoped to a Done-bound status change ONLY. This method is the general task editor —
        // renames, priority and assignee changes all land here — and SQLite allows a single
        // pending writer, so locking unconditionally would make every field edit queue behind
        // any other writer for a gate that would not run.
        await using var tx = dto.Status == WorkStatus.Done
            ? await db.Database.BeginTransactionAsync(ct)
            : null;

        var task = await db.Tasks
            .Include(t => t.Feature)
            .FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null)
            throw new TaskNotFoundException(taskId);
        if (task.Feature is null)
            throw new InvalidOperationException($"Task {taskId} has no feature; cannot resolve project for audit.");

        // No-op: caller passed nothing to change (SortOrder included — #190).
        if (dto.Name is null && dto.Description is null && dto.Status is null && dto.Priority is null && dto.Assignee is null && dto.SortOrder is null)
            return task;

        // Validate up-front so we never do half a mutation. Service is the trust boundary
        // — every entry point (REST, MCP, future UI direct calls) flows through here.
        if (dto.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(dto.Name))
                throw new ValidationException("Name cannot be empty or whitespace.");
            if (dto.Name.Length > MaxNameLength)
                throw new ValidationException($"Name cannot exceed {MaxNameLength} characters.");
        }
        if (dto.Status is not null && !Enum.IsDefined(dto.Status.Value))
            throw new ValidationException($"Invalid status '{dto.Status.Value}'.");
        if (dto.Priority is not null && !Enum.IsDefined(dto.Priority.Value))
            throw new ValidationException($"Invalid priority '{dto.Priority.Value}'.");
        if (dto.SortOrder is not null && !double.IsFinite(dto.SortOrder.Value))
            throw new ValidationException("SortOrder must be a finite number.");

        var before = TaskActivitySnapshot(task);
        var oldStatus = task.Status;
        var changedFields = new List<string>();
        var statusChanged = false;
        var sortOrderChanged = false;
        WorkStatus newStatus = oldStatus;

        if (dto.Name is not null && task.Name != dto.Name.Trim())
        {
            task.Name = dto.Name.Trim();
            changedFields.Add("Name");
        }

        if (dto.Description is not null)
        {
            var trimmed = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim();
            if (task.Description != trimmed)
            {
                task.Description = trimmed;
                changedFields.Add("Description");
            }
        }

        if (dto.Status is not null && dto.Status.Value != task.Status)
        {
            newStatus = dto.Status.Value;

            // Definition-of-Done gate. A field update carries no reason, so this path refuses;
            // without it, PUT /api/tasks/{id} would be an unguarded way around the gate.
            await GateDoneTransitionAsync(db, taskId, oldStatus, newStatus, overrideReason: null, ct);

            task.Status = newStatus;
            // Mirror ChangeStatusAsync's CompletedAt invariant so audit history stays consistent.
            if (newStatus == WorkStatus.Done && oldStatus != WorkStatus.Done)
                task.CompletedAt = DateTime.UtcNow;
            else if (newStatus != WorkStatus.Done)
                task.CompletedAt = null;
            changedFields.Add("Status");
            statusChanged = true;
        }

        if (dto.Priority is not null && dto.Priority.Value != task.Priority)
        {
            task.Priority = dto.Priority.Value;
            changedFields.Add("Priority");
        }

        if (dto.Assignee is not null)
        {
            var trimmed = string.IsNullOrWhiteSpace(dto.Assignee) ? null : dto.Assignee.Trim();
            if (task.Assignee != trimmed)
            {
                task.Assignee = trimmed;
                changedFields.Add("Assignee");
            }
        }

        // #190: fold SortOrder into THIS save so the whole PUT is one atomic transaction
        // (the endpoint previously persisted it on a SECOND context, leaving a partial-write
        // window). SortOrder is a UI reorder concern, so it is deliberately NOT added to
        // changedFields — it rides along silently and must NOT emit an activity event or
        // comment (the #88 audit boundary).
        if (dto.SortOrder.HasValue && task.SortOrder != dto.SortOrder.Value)
        {
            task.SortOrder = dto.SortOrder.Value;
            sortOrderChanged = true;
        }

        // All proposed values matched current state → idempotent, skip save + audit.
        // sortOrderChanged keeps a SortOrder-only reorder from being dropped as a no-op (#190).
        if (changedFields.Count == 0 && !sortOrderChanged)
            return task;

        task.UpdatedAt = DateTime.UtcNow;

        // If status changed in this Update, emit a StatusChange comment + TaskStatusChanged
        // event (matching ChangeStatusAsync) so the audit trail stays consistent regardless
        // of whether the caller used the dedicated status endpoint or the bulk update path.
        // If other fields also changed, emit an additional Note comment for them.
        if (statusChanged)
        {
            db.TaskComments.Add(new TaskComment
            {
                TaskItemId = task.Id,
                Text = $"Status changed from {oldStatus} to {newStatus}",
                Author = string.IsNullOrWhiteSpace(actor) ? "System" : actor!.Trim(),
                Type = CommentType.StatusChange,
                CreatedAt = DateTime.UtcNow
            });
            AddActivityEvent(
                db, task.Feature.ProjectId, "Task", task.Id, actor,
                "TaskStatusChanged", before, TaskActivitySnapshot(task),
                $"Task '{task.Name}' status changed from {oldStatus} to {newStatus}");
        }

        var nonStatusChanges = changedFields.Where(f => f != "Status").ToList();
        if (nonStatusChanges.Count > 0)
        {
            db.TaskComments.Add(new TaskComment
            {
                TaskItemId = task.Id,
                Text = $"Updated: {string.Join(", ", nonStatusChanges)}",
                Author = string.IsNullOrWhiteSpace(actor) ? "System" : actor!.Trim(),
                Type = CommentType.Note,
                CreatedAt = DateTime.UtcNow
            });
            AddActivityEvent(
                db, task.Feature.ProjectId, "Task", task.Id, actor,
                "TaskUpdated", before, TaskActivitySnapshot(task),
                $"Task '{task.Name}' updated: {string.Join(", ", nonStatusChanges)}");
        }

        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return task;
    }

    private const int MaxNameLength = 500;

    public async Task<TaskItem> CreateAsync(TaskCreate dto, string? actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dto);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Validate up-front so we never persist a half-built task. Service is the trust
        // boundary — REST, MCP, and Blazor direct-callers all flow through here (#88).
        if (string.IsNullOrWhiteSpace(dto.Name))
            throw new ValidationException("Name cannot be empty or whitespace.");
        if (dto.Name.Length > MaxNameLength)
            throw new ValidationException($"Name cannot exceed {MaxNameLength} characters.");
        // Defensive enum guard (matches UpdateAsync): rejects undefined/out-of-range
        // values such as a cast numeric the caller failed to validate.
        if (dto.Status is not null && !Enum.IsDefined(dto.Status.Value))
            throw new ValidationException($"Invalid status '{dto.Status.Value}'.");
        if (dto.Priority is not null && !Enum.IsDefined(dto.Priority.Value))
            throw new ValidationException($"Invalid priority '{dto.Priority.Value}'.");
        if (dto.SortOrder is not null && !double.IsFinite(dto.SortOrder.Value))
            throw new ValidationException("SortOrder must be a finite number.");

        // Resolve the project for audit attribution; a missing feature is a distinct,
        // actionable error (the task itself does not exist yet).
        var projectId = await db.Features
            .Where(f => f.Id == dto.FeatureId)
            .Select(f => (int?)f.ProjectId)
            .SingleOrDefaultAsync(ct);
        if (projectId is null)
            throw new FeatureNotFoundException(dto.FeatureId);

        var status = dto.Status ?? WorkStatus.Backlog;
        var priority = dto.Priority ?? TaskPriority.Low;

        var maxSort = await db.Tasks
            .Where(t => t.FeatureId == dto.FeatureId)
            .MaxAsync(t => (double?)t.SortOrder, ct);

        var task = new TaskItem
        {
            FeatureId = dto.FeatureId,
            Name = dto.Name.Trim(),
            Description = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim(),
            Status = status,
            Priority = priority,
            Assignee = string.IsNullOrWhiteSpace(dto.Assignee) ? null : dto.Assignee.Trim(),
            SortOrder = dto.SortOrder ?? AllocateAppendSortOrder(maxSort),
            CompletedAt = status == WorkStatus.Done ? DateTime.UtcNow : null
        };

        // Atomic create + audit: the first save assigns task.Id (needed by the comment +
        // activity event), the second persists those audit rows. Wrap both in one
        // transaction so a failure can't leave a task without its Created comment /
        // TaskCreated event. EF Core + SQLite supports explicit transactions.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        db.Tasks.Add(task);
        // First save assigns task.Id so the comment + activity event can reference it.
        await db.SaveChangesAsync(ct);

        db.TaskComments.Add(new TaskComment
        {
            TaskItemId = task.Id,
            Text = "Task created",
            // Inline REST hardcoded "System"; honor the actor here for parity with the
            // other audit-aware methods (R6). The Created comment is audit-internal.
            Author = string.IsNullOrWhiteSpace(actor) ? "System" : actor!.Trim(),
            Type = CommentType.Created,
            CreatedAt = DateTime.UtcNow
        });

        AddActivityEvent(
            db, projectId.Value, "Task", task.Id, actor,
            "TaskCreated", null, TaskActivitySnapshot(task),
            $"Task '{task.Name}' created");

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return task;
    }

    public async Task DeleteAsync(int taskId, string? actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // Serialize the operation guard and graph removal with PrepareAsync. Without one write
        // transaction, a prepare could commit after the guard read and before this delete (#336).
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var task = await db.Tasks
            .Include(t => t.Feature)
            .FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null)
            throw new TaskNotFoundException(taskId);
        if (task.Feature is null)
            throw new InvalidOperationException($"Task {taskId} has no feature; cannot resolve project for audit.");

        await ProjectGraphDeleter.EnsureNoNonTerminalExternalWritesAsync(db, [taskId], ct);

        // Snapshot + emit the audit event BEFORE removal. ActivityEvent has no FK to the
        // task (intentional — audit history must survive deletion), so the row persists.
        AddActivityEvent(
            db, task.Feature.ProjectId, "Task", task.Id, actor,
            "TaskDeleted", TaskActivitySnapshot(task), null,
            $"Task '{task.Name}' deleted");

        // FK-safe cascade order (R1): Subtask.ChildSubtasks is OnDelete(Restrict)
        // (self-ref), so subtasks MUST be removed leaf-first. Acceptance criteria are
        // removed explicitly. Comments + Dependencies + BlockingDependencies are
        // OnDelete(Cascade) and Requirement.TaskItemId is OnDelete(SetNull) — EF handles
        // those on Remove(task). Leaf-first subtask removal lives in ONE place
        // (ProjectGraphDeleter), shared with the project/feature cascade.
        await ProjectGraphDeleter.DeleteSubtasksForTaskAsync(db, taskId, ct);

        var criteria = await db.AcceptanceCriteria
            .Where(c => c.TaskItemId == taskId)
            .ToListAsync(ct);
        db.AcceptanceCriteria.RemoveRange(criteria);

        db.Tasks.Remove(task);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task DeleteProjectAsync(int projectId, string? actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null)
            throw new ProjectNotFoundException(projectId);

        // Snapshot + emit the audit event BEFORE removal. ActivityEvent has no FK to the
        // project (Project is Ignored in AppDbContext), so the row survives the delete.
        AddActivityEvent(
            db, projectId, "Project", projectId, actor,
            "ProjectDeleted", ProjectActivitySnapshot(project), null,
            $"Project '{project.Name}' deleted");

        // FK-safe child cleanup (features -> tasks -> leaf-first subtasks + acceptance criteria
        // + comments; requirement links nulled) — shared with the REST delete path via the
        // service-layer ProjectGraphDeleter so both surfaces behave identically (#155). A single
        // SaveChanges makes the children removal, the project removal, and the audit row one
        // atomic unit.
        await ProjectGraphDeleter.DeleteProjectChildrenAsync(db, projectId, ct);
        db.Projects.Remove(project);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static object ProjectActivitySnapshot(Project project) => new
    {
        project.Id,
        project.Name,
        project.Description,
        project.Goals
    };

    /// <summary>
    /// Claims <paramref name="taskId"/> for <paramref name="actor"/>, taking over stale claims
    /// and throwing <see cref="TaskClaimConflictException"/> for fresh claims held by someone else (#429).
    /// </summary>
    public async Task<TaskItem> ClaimAsync(int taskId, string actor, CancellationToken ct = default)
    {
        // The REST endpoint maps a blank actor to 422; surface it as a ValidationException.
        if (string.IsNullOrWhiteSpace(actor))
            throw new ValidationException("Actor is required.");
        var trimmedActor = actor.Trim();

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Open the write lock BEFORE reading the task row so the check-then-update is fully
        // serialized (#156, #429). Without a transaction spanning both, a concurrent ClaimAsync
        // that commits between our read and our UPDATE could produce two holders for the same task.
        // Microsoft.Data.Sqlite issues BEGIN IMMEDIATE for this overload, reserving the write slot
        // up front rather than on first write — the same pattern ChangeStatusAsync uses.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // #195: AsNoTracking — the claim is persisted via the conditional ExecuteUpdateAsync
        // below, so the loaded entity is only for validation + the audit snapshot. Not tracking
        // it stops SaveChanges from emitting a redundant Tasks UPDATE next to the event insert.
        var task = await db.Tasks
            .Include(t => t.Feature)
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null)
            throw new TaskNotFoundException(taskId);
        if (task.Feature is null)
            throw new InvalidOperationException($"Task {taskId} has no feature; cannot resolve project for audit.");

        var now = _clock();

        // A claim past its TTL is abandoned and may be taken over (#429). Without this a crashed
        // or rate-limited agent holds its task forever and nothing can recover it: ClaimedAt was
        // written and displayed but never compared to the clock anywhere in src/.
        var existingClaimIsStale = ClaimPolicy.IsStale(
            task.ClaimedBy, task.ClaimedAt, now, _claimTtl);
        var previousHolder = task.ClaimedBy;
        var previousClaimedAt = task.ClaimedAt;

        // Claimed by someone else and still FRESH → conflict (API → 409). Same actor is idempotent.
        if (!string.IsNullOrWhiteSpace(task.ClaimedBy)
            && !string.Equals(task.ClaimedBy, trimmedActor, StringComparison.OrdinalIgnoreCase)
            && !existingClaimIsStale)
        {
            throw new TaskClaimConflictException(taskId, task.ClaimedBy, task.ClaimedAt);
        }

        // Same-actor re-claim IS the heartbeat (#443). It must refresh ClaimedAt on BOTH the fresh
        // and the stale path. #429/#435 turned ClaimedAt into a lease expiry but left this early
        // return in place from when it was decorative -- so the holder was the one party that could
        // not move it. A live agent calling claim_task every 5 minutes had every one of those calls
        // land here and return without a write, and lost its task at exactly the TTL anyway.
        //
        // Renewing ONLY a stale claim (as #443's ACs literally read) does not fix that: a heartbeat
        // inside the TTL is by definition not stale, so it takes the no-op branch and ClaimedAt
        // never moves. That reading satisfies AC2 while defeating AC4. This implements AC2's own
        // parenthetical instead: "no-op" means no audit churn and no UpdatedAt bump, not no write.
        if (string.Equals(task.ClaimedBy, trimmedActor, StringComparison.OrdinalIgnoreCase))
        {
            var renewedFromStale = existingClaimIsStale;
            object? beforeRenew = renewedFromStale ? TaskActivitySnapshot(task) : null;
            var heldBy = task.ClaimedBy;   // captured for the EF Core closure

            // Guarded by the holder, so a concurrent release or steal matches zero rows and falls
            // through to the same reconcile the claim path uses, rather than faking success.
            // Two ExecuteUpdateAsync calls rather than one ternary: the setter is an expression
            // tree EF must translate, so the branch has to sit in C#, not inside it.
            int renewed;
            if (renewedFromStale)
            {
                renewed = await db.Tasks
                    .Where(t => t.Id == taskId && t.ClaimedBy == heldBy)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(t => t.ClaimedAt, now)
                        .SetProperty(t => t.UpdatedAt, now), ct);
            }
            else
            {
                // Fresh heartbeat: ClaimedAt ONLY. UpdatedAt stays put so a 5-minute cadence cannot
                // fake task activity on Home or flood the timeline (AC2).
                renewed = await db.Tasks
                    .Where(t => t.Id == taskId && t.ClaimedBy == heldBy)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(t => t.ClaimedAt, now), ct);
            }

            if (renewed == 0)
            {
                var current = await db.Tasks.AsNoTracking()
                    .Where(t => t.Id == taskId)
                    .Select(t => new { t.ClaimedBy, t.ClaimedAt, t.UpdatedAt })
                    .FirstOrDefaultAsync(ct)
                    ?? throw new TaskNotFoundException(taskId);
                if (!string.IsNullOrWhiteSpace(current.ClaimedBy)
                    && !string.Equals(current.ClaimedBy, trimmedActor, StringComparison.OrdinalIgnoreCase))
                    throw new TaskClaimConflictException(taskId, current.ClaimedBy, current.ClaimedAt);
                task.ClaimedBy = current.ClaimedBy;
                task.ClaimedAt = current.ClaimedAt;
                task.UpdatedAt = current.UpdatedAt;
                if (!string.IsNullOrWhiteSpace(current.ClaimedBy))
                {
                    await tx.CommitAsync(ct);
                    return task;
                }
            }
            else
            {
                task.ClaimedAt = now;
                if (renewedFromStale)
                {
                    // Recovering an already-expired claim IS worth a timeline row: it explains why the
                    // task did not get stolen. A fresh heartbeat deliberately writes no event.
                    task.UpdatedAt = now;
                    AddActivityEvent(
                        db, task.Feature.ProjectId, "Task", task.Id, trimmedActor,
                        "TaskClaimRenewed", beforeRenew, TaskActivitySnapshot(task),
                        $"Task '{task.Name}' claim renewed by {trimmedActor} "
                        + (previousClaimedAt is { } staleSince
                            ? $"(claim was {(now - staleSince).TotalHours:0.##}h old; TTL {_claimTtl.TotalHours:0.##}h)"
                            : $"(claim had no timestamp; TTL {_claimTtl.TotalHours:0.##}h)"));
                    await db.SaveChangesAsync(ct);
                }

                // MUST commit. The early return this replaced left the BEGIN IMMEDIATE transaction to
                // roll back on dispose -- harmless only because nothing was written. Now that this path
                // writes, a missing commit would silently discard every single heartbeat.
                await tx.CommitAsync(ct);
                return task;
            }
        }

        var before = TaskActivitySnapshot(task);

        // Race-safe claim (R4): a conditional UPDATE that only fires while the row is still
        // unclaimed. Under concurrent claimers (multiple agents), exactly one UPDATE matches
        // (rows == 1); the loser sees rows == 0 and is reconciled below. This closes the
        // load-check-write gap without a rowversion column (schema uses EnsureCreated, not migrations).
        // The predicate is widened for the steal case (#429) but stays a SINGLE conditional
        // UPDATE, so two actors racing to take over the same expired claim still cannot both win:
        // exactly one matches while the row is unclaimed-or-expired, and the loser reconciles
        // below. Widening it into a load-then-write would reopen the gap this closes.
        //
        // Whitespace holders: ClaimPolicy.IsStale treats a whitespace ClaimedBy as unclaimed
        // (same as null), so ranking and the pre-check above let blank holders through. The
        // UPDATE predicate must match the exact pre-read value rather than only NULL so that
        // a legacy row with ClaimedBy=" " can be claimed without failing the race guard.
        var isUnclaimedHolder = string.IsNullOrWhiteSpace(task.ClaimedBy);
        var capturedClaimedBy = task.ClaimedBy;   // captured for EF Core closure
        var staleCutoff = ClaimPolicy.StaleCutoff(now, _claimTtl);
        // Mirrors ClaimPolicy.IsStale's future-timestamp arm. Without it the C# pre-check would
        // call a skewed claim stale while this predicate matched zero rows, so the takeover would
        // fall through to the reconcile path and report a conflict the pre-check had already
        // cleared. The two notions of "stale" must agree. (codex, PR #435.)
        var skewHorizon = now + ClaimPolicy.MaxClockSkew;
        var claimed = await db.Tasks
            .Where(t => t.Id == taskId
                        && ((isUnclaimedHolder && t.ClaimedBy == capturedClaimedBy)
                            || (!isUnclaimedHolder && existingClaimIsStale
                                && (t.ClaimedAt == null || t.ClaimedAt < staleCutoff
                                    || t.ClaimedAt > skewHorizon))))
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.ClaimedBy, trimmedActor)
                .SetProperty(t => t.ClaimedAt, now)
                .SetProperty(t => t.UpdatedAt, now), ct);

        if (claimed == 0)
        {
            // Another request claimed the task between our check and our write. Re-read the
            // current owner: same actor ⇒ idempotent success, otherwise a genuine 409 conflict.
            var current = await db.Tasks.AsNoTracking()
                .Where(t => t.Id == taskId)
                .Select(t => new { t.ClaimedBy, t.ClaimedAt, t.UpdatedAt })
                .FirstOrDefaultAsync(ct)
                ?? throw new TaskNotFoundException(taskId);
            if (string.Equals(current.ClaimedBy, trimmedActor, StringComparison.OrdinalIgnoreCase))
            {
                // Reconcile the stale loaded entity with the concurrent winner's values.
                task.ClaimedBy = current.ClaimedBy;
                task.ClaimedAt = current.ClaimedAt;
                task.UpdatedAt = current.UpdatedAt;
                return task;
            }
            throw new TaskClaimConflictException(taskId, current.ClaimedBy!, current.ClaimedAt);
        }

        // We won the claim. Mirror the persisted values onto the loaded entity so the audit
        // snapshot + return value are accurate, then emit the TaskClaimed event.
        task.ClaimedBy = trimmedActor;
        task.ClaimedAt = now;
        task.UpdatedAt = now;

        // A steal is audited as its OWN event type, not a plain TaskClaimed. Someone reading the
        // timeline needs to see that a claim was taken from a named holder, and how old it was —
        // a TaskClaimed row here would silently overwrite that fact.
        var wasSteal = existingClaimIsStale && !string.IsNullOrWhiteSpace(previousHolder);
        if (wasSteal)
        {
            AddActivityEvent(
                db, task.Feature.ProjectId, "Task", task.Id, trimmedActor,
                "TaskClaimStolen", before, TaskActivitySnapshot(task),
                $"Task '{task.Name}' claim taken over by {trimmedActor} from {previousHolder} "
                + (previousClaimedAt is { } heldSince
                    ? $"(claim was {(now - heldSince).TotalHours:0.##}h old; TTL {_claimTtl.TotalHours:0.##}h)"
                    : $"(claim had no timestamp; TTL {_claimTtl.TotalHours:0.##}h)"));
        }
        else
        {
            AddActivityEvent(
                db, task.Feature.ProjectId, "Task", task.Id, trimmedActor,
                "TaskClaimed", before, TaskActivitySnapshot(task),
                $"Task '{task.Name}' claimed by {trimmedActor}");
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return task;
    }

    public async Task<TaskItem> ReleaseAsync(int taskId, string actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actor))
            throw new ValidationException("Actor is required.");
        var trimmedActor = actor.Trim();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // #195: AsNoTracking — release is persisted via the conditional ExecuteUpdateAsync below;
        // not tracking the loaded entity stops SaveChanges from emitting a redundant Tasks UPDATE.
        var task = await db.Tasks
            .Include(t => t.Feature)
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null)
            throw new TaskNotFoundException(taskId);
        if (task.Feature is null)
            throw new InvalidOperationException($"Task {taskId} has no feature; cannot resolve project for audit.");

        // Only the claiming actor may release; anyone else → conflict (API → 409).
        if (!string.IsNullOrWhiteSpace(task.ClaimedBy)
            && !string.Equals(task.ClaimedBy, trimmedActor, StringComparison.OrdinalIgnoreCase))
        {
            throw new TaskClaimConflictException(taskId, task.ClaimedBy, task.ClaimedAt);
        }

        // Idempotent: already unclaimed → nothing to do, no audit churn.
        if (string.IsNullOrWhiteSpace(task.ClaimedBy))
            return task;

        var before = TaskActivitySnapshot(task);
        var now = DateTime.UtcNow;

        // Atomic release + audit (#156): wrap the conditional UPDATE and the TaskReleased event
        // in one transaction so a release can never persist without its audit event.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Race-safe release (R4): conditional UPDATE that only fires while THIS actor still
        // holds the claim. If the state changed underneath us, rows == 0 → reconcile below.
        var released = await db.Tasks
            .Where(t => t.Id == taskId && t.ClaimedBy != null
                        && t.ClaimedBy.ToLower() == trimmedActor.ToLower())
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.ClaimedBy, (string?)null)
                .SetProperty(t => t.ClaimedAt, (DateTime?)null)
                .SetProperty(t => t.UpdatedAt, now), ct);

        if (released == 0)
        {
            // State changed between check and write. Re-read: still claimed by another actor
            // ⇒ 409 conflict; otherwise it is already released ⇒ idempotent success.
            var current = await db.Tasks.AsNoTracking()
                .Where(t => t.Id == taskId)
                .Select(t => new { t.ClaimedBy, t.ClaimedAt, t.UpdatedAt })
                .FirstOrDefaultAsync(ct)
                ?? throw new TaskNotFoundException(taskId);
            if (!string.IsNullOrWhiteSpace(current.ClaimedBy)
                && !string.Equals(current.ClaimedBy, trimmedActor, StringComparison.OrdinalIgnoreCase))
                throw new TaskClaimConflictException(taskId, current.ClaimedBy!, current.ClaimedAt);
            // Reconcile the stale loaded entity with the already-released DB state.
            task.ClaimedBy = current.ClaimedBy;
            task.ClaimedAt = current.ClaimedAt;
            task.UpdatedAt = current.UpdatedAt;
            return task;
        }

        // Mirror onto the loaded entity for an accurate audit snapshot + return value.
        task.ClaimedBy = null;
        task.ClaimedAt = null;
        task.UpdatedAt = now;

        AddActivityEvent(
            db, task.Feature.ProjectId, "Task", task.Id, trimmedActor,
            "TaskReleased", before, TaskActivitySnapshot(task),
            $"Task '{task.Name}' released by {trimmedActor}");

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return task;
    }

    public async Task<TaskComment> AddCommentAsync(int taskId, string text, string? author, CommentType type, string? actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Resolve project (and prove the task exists) in one round-trip.
        var projectId = await db.Tasks
            .Where(t => t.Id == taskId)
            .Select(t => (int?)t.Feature.ProjectId)
            .SingleOrDefaultAsync(ct);
        if (projectId is null)
            throw new TaskNotFoundException(taskId);

        if (string.IsNullOrWhiteSpace(text))
            throw new ValidationException("Text cannot be empty or whitespace.");

        // Only user-facing comment types may be authored directly; system types
        // (StatusChange/Created/FeatureChange/…) are emitted by the service itself.
        if (!IsUserCommentType(type))
            throw new ValidationException(
                $"Invalid comment type '{type}'. Allowed values: {string.Join(", ", AllowedUserCommentTypes)}.");

        var comment = new TaskComment
        {
            TaskItemId = taskId,
            Text = text.Trim(),
            Author = string.IsNullOrWhiteSpace(author) ? null : author.Trim(),
            Type = type,
            CreatedAt = DateTime.UtcNow
        };

        db.TaskComments.Add(comment);
        AddActivityEvent(
            db, projectId.Value, "Task", taskId, actor,
            "TaskCommentAdded", null, new { comment.Text, comment.Author }, "Comment added");

        await db.SaveChangesAsync(ct);
        return comment;
    }

    // ── Comment-type policy (single source of truth) ──
    // Canonical set + order of user-authorable CommentTypes. Edge layers (REST, MCP) defer
    // to IsUserCommentType for validation and reference THIS list for their 422 message body,
    // so there is exactly one definition (R3).
    public static readonly string[] AllowedUserCommentTypes =
    [
        nameof(CommentType.Note),
        nameof(CommentType.Decision),
        nameof(CommentType.Question),
        nameof(CommentType.Blocked),
        nameof(CommentType.Handoff),
        nameof(CommentType.Verification),
        nameof(CommentType.Comment)
    ];

    public bool IsUserCommentType(CommentType type) =>
        type switch
        {
            CommentType.Note => true,
            CommentType.Decision => true,
            CommentType.Question => true,
            CommentType.Blocked => true,
            CommentType.Handoff => true,
            CommentType.Verification => true,
            CommentType.Comment => true,
            _ => false
        };

}
