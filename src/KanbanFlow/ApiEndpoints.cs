using System.Text.Json;
using KanbanFlow.Data;
using KanbanFlow.Models;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow;

public static class ApiEndpoints
{
    public static WebApplication MapApiEndpoints(this WebApplication app)
    {
        app.MapProjectEndpoints();
        app.MapFeatureEndpoints();
        app.MapTaskEndpoints();
        app.MapTaskDependencyEndpoints();
        app.MapSubtaskEndpoints();
        app.MapAcceptanceCriterionEndpoints();
        app.MapRequirementEndpoints();
        app.MapTaskCommentEndpoints();
        app.MapActivityEndpoints();
        app.MapIdeaEndpoints();
        app.MapMemoryEndpoints();
        return app;
    }

    // ── Project Endpoints ──────────────────────────────────────────────

    private static void MapProjectEndpoints(this WebApplication app)
    {
        // GET /api/projects — list all with feature/task counts
        app.MapGet("/api/projects", async (AppDbContext db) =>
        {
            // #157: project the counts in SQL (scalar subqueries) instead of loading the full
            // Feature->Task graph just to count it.
            var projects = await db.Projects
                .AsNoTracking()
                .Select(p => new
                {
                    p.Id,
                    p.Name,
                    p.Description,
                    p.Goals,
                    p.Readme,
                    p.CreatedAt,
                    p.UpdatedAt,
                    FeatureCount = p.Features.Count,
                    TaskCount = p.Features.SelectMany(f => f.Tasks).Count()
                })
                .ToListAsync();

            return Results.Ok(projects);
        });

        // GET /api/projects/{id} — get one with features + task counts
        app.MapGet("/api/projects/{id:int}", async (int id, AppDbContext db) =>
        {
            // #157: project features + per-feature counts in SQL instead of loading every task.
            var project = await db.Projects
                .Where(p => p.Id == id)
                .AsNoTracking()
                .Select(p => new
                {
                    p.Id,
                    p.Name,
                    p.Description,
                    p.Goals,
                    p.Readme,
                    p.CreatedAt,
                    p.UpdatedAt,
                    Features = p.Features.OrderBy(f => f.SortOrder).Select(f => new
                    {
                        f.Id,
                        f.ParentFeatureId,
                        f.Name,
                        f.Description,
                        f.SortOrder,
                        f.CreatedAt,
                        f.UpdatedAt,
                        TaskCount = f.Tasks.Count,
                        DoneCount = f.Tasks.Count(t => t.Status == WorkStatus.Done)
                    })
                })
                .FirstOrDefaultAsync();

            if (project is null)
                return Results.NotFound(new { message = $"Project {id} not found." });

            return Results.Ok(project);
        });

        // GET /api/projects/{id}/context — full AI-readable project context bundle
        app.MapGet("/api/projects/{id:int}/context", async (int id, AppDbContext db) =>
        {
            var project = await db.Projects
                .Include(p => p.Features)
                    .ThenInclude(f => f.Tasks)
                        .ThenInclude(t => t.Subtasks)
                .Include(p => p.Features)
                    .ThenInclude(f => f.Tasks)
                        .ThenInclude(t => t.AcceptanceCriteria)
                .Include(p => p.Features)
                    .ThenInclude(f => f.Tasks)
                        .ThenInclude(t => t.Comments)
                .Include(p => p.Requirements)
                    .ThenInclude(r => r.Feature)
                .Include(p => p.Requirements)
                    .ThenInclude(r => r.TaskItem)
                .Include(p => p.TaskDependencies)
                .AsSplitQuery()
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == id);

            if (project is null)
                return Results.NotFound(new { message = $"Project {id} not found." });

            var allTasks = project.Features
                .SelectMany(f => f.Tasks.Select(t => new { Task = t, Feature = f }))
                .OrderBy(x => x.Feature.SortOrder)
                    .ThenBy(x => x.Task.SortOrder)
                .ToList();

            object MapSubtaskContext(Subtask subtask, IReadOnlyCollection<Subtask> allSubtasks) => new
            {
                subtask.Id,
                subtask.ParentSubtaskId,
                subtask.Text,
                subtask.Completed,
                subtask.SortOrder,
                subtask.CreatedAt,
                Children = allSubtasks
                    .Where(s => s.ParentSubtaskId == subtask.Id)
                    .OrderBy(s => s.SortOrder)
                    .Select(child => MapSubtaskContext(child, allSubtasks))
            };

            var tasksById = allTasks.ToDictionary(x => x.Task.Id, x => x);
            var dependenciesByBlockedTask = project.TaskDependencies.ToLookup(d => d.TaskItemId);
            var dependenciesByBlockingTask = project.TaskDependencies.ToLookup(d => d.DependsOnTaskItemId);

            object MapDependencyContext(TaskDependency dependency)
            {
                var blocked = tasksById.GetValueOrDefault(dependency.TaskItemId);
                var blocker = tasksById.GetValueOrDefault(dependency.DependsOnTaskItemId);

                return new
                {
                    dependency.Id,
                    dependency.ProjectId,
                    dependency.TaskItemId,
                    TaskName = blocked?.Task.Name,
                    TaskStatus = blocked?.Task.Status.ToString(),
                    dependency.DependsOnTaskItemId,
                    DependsOnTaskName = blocker?.Task.Name,
                    DependsOnTaskStatus = blocker?.Task.Status.ToString(),
                    BlockedTaskId = dependency.TaskItemId,
                    BlockedTaskName = blocked?.Task.Name,
                    BlockedTaskStatus = blocked?.Task.Status.ToString(),
                    BlockingTaskId = dependency.DependsOnTaskItemId,
                    BlockingTaskName = blocker?.Task.Name,
                    BlockingTaskStatus = blocker?.Task.Status.ToString(),
                    IsBlockingTaskDone = blocker?.Task.Status == WorkStatus.Done,
                    IsBlockingTaskUnfinished = blocker is not null && blocker.Task.Status != WorkStatus.Done,
                    Type = dependency.Type.ToString(),
                    dependency.CreatedAt
                };
            }

            object MapTaskContext(TaskItem task, Feature feature)
            {
                var dependencies = dependenciesByBlockedTask[task.Id]
                    .OrderBy(d => d.CreatedAt)
                    .ToList();
                var blocking = dependenciesByBlockingTask[task.Id]
                    .OrderBy(d => d.CreatedAt)
                    .ToList();
                var unfinishedBlockers = dependencies
                    .Where(d => tasksById.TryGetValue(d.DependsOnTaskItemId, out var blocker) && blocker.Task.Status != WorkStatus.Done)
                    .ToList();

                return new
                {
                    task.Id,
                    task.FeatureId,
                    FeatureName = feature.Name,
                    task.Name,
                    task.Description,
                    Status = task.Status.ToString(),
                    Priority = task.Priority.ToString(),
                    task.Assignee,
                    task.ClaimedBy,
                    task.ClaimedAt,
                    task.SortOrder,
                    task.CreatedAt,
                    task.UpdatedAt,
                    task.CompletedAt,
                    IsBlocked = unfinishedBlockers.Count > 0,
                    BlockedByUnfinishedTasks = unfinishedBlockers.Count > 0,
                    Dependencies = dependencies.Select(MapDependencyContext),
                    Blocks = blocking.Select(MapDependencyContext),
                    UnfinishedBlockers = unfinishedBlockers.Select(MapDependencyContext),
                    Subtasks = task.Subtasks
                    .Where(s => s.ParentSubtaskId == null)
                    .OrderBy(s => s.SortOrder)
                    .Select(subtask => MapSubtaskContext(subtask, task.Subtasks)),
                AcceptanceCriteria = task.AcceptanceCriteria
                    .OrderBy(c => c.SortOrder)
                    .Select(c => new
                    {
                        c.Id,
                        c.Text,
                        c.Completed,
                        c.SortOrder,
                        c.CreatedAt,
                        c.UpdatedAt
                    }),
                    Comments = task.Comments
                        .OrderBy(c => c.CreatedAt)
                        .Select(c => new
                        {
                            c.Id,
                            c.Text,
                            c.Author,
                            Type = c.Type.ToString(),
                            c.CreatedAt
                        })
                };
            }

            object MapFeatureContext(Feature feature) => new
            {
                feature.Id,
                feature.ParentFeatureId,
                feature.Name,
                feature.Description,
                feature.SortOrder,
                feature.CreatedAt,
                feature.UpdatedAt,
                TaskCount = feature.Tasks.Count,
                DoneCount = feature.Tasks.Count(t => t.Status == WorkStatus.Done),
                Tasks = feature.Tasks
                    .OrderBy(t => t.SortOrder)
                    .ThenBy(t => t.Id) // #207: deterministic tie-break when SortOrder ties (legacy/concurrent dupes)
                    .Select(task => MapTaskContext(task, feature)),
                Children = project.Features
                    .Where(f => f.ParentFeatureId == feature.Id)
                    .OrderBy(f => f.SortOrder)
                    .Select(MapFeatureContext)
            };

            return Results.Ok(new
            {
                GeneratedAt = DateTime.UtcNow,
                Project = new
                {
                    project.Id,
                    project.Name,
                    project.Description,
                    project.Goals,
                    project.Readme,
                    project.CreatedAt,
                    project.UpdatedAt
                },
                Summary = new
                {
                    FeatureCount = project.Features.Count,
                    TaskCount = allTasks.Count,
                    RequirementCount = project.Requirements.Count,
                    ByStatus = Enum.GetValues<WorkStatus>()
                        .ToDictionary(status => status.ToString(), status => allTasks.Count(x => x.Task.Status == status)),
                    ByPriority = Enum.GetValues<TaskPriority>()
                        .ToDictionary(priority => priority.ToString(), priority => allTasks.Count(x => x.Task.Priority == priority))
                },
                Dependencies = project.TaskDependencies
                    .OrderBy(d => d.CreatedAt)
                    .Select(MapDependencyContext),
                Requirements = project.Requirements
                    .OrderBy(r => r.SortOrder)
                    .Select(r => new
                    {
                        r.Id,
                        r.Text,
                        r.Description,
                        Priority = r.Priority.ToString(),
                        Status = r.Status.ToString(),
                        r.FeatureId,
                        FeatureName = r.Feature?.Name,
                        r.TaskItemId,
                        TaskItemName = r.TaskItem?.Name,
                        r.SortOrder,
                        r.CreatedAt,
                        r.UpdatedAt
                    }),
                Features = project.Features
                    .Where(f => f.ParentFeatureId == null)
                    .OrderBy(f => f.SortOrder)
                    .Select(MapFeatureContext),
                Tasks = allTasks.Select(x => MapTaskContext(x.Task, x.Feature))
            });
        });

        // GET /api/projects/{id}/next-tasks — small ranked list of recommended next work
        app.MapGet("/api/projects/{id:int}/next-tasks", async (int id, int? limit, string? actor, AppDbContext db) =>
        {
            var projectExists = await db.Projects.AnyAsync(p => p.Id == id);
            if (!projectExists)
                return Results.NotFound(new { message = $"Project {id} not found." });

            var projectTasks = await db.Tasks
                .Include(t => t.Feature)
                .Where(t => t.Feature.ProjectId == id)
                .AsNoTracking()
                .ToListAsync();

            var taskIds = projectTasks.Select(t => t.Id).ToList();
            var tasksById = projectTasks.ToDictionary(t => t.Id);
            // Include EVERY dependency of a project task, even one whose target is outside the project's
            // task set: the shared ranker treats a target missing from statusById as an unfinished blocker
            // and excludes the task — matching MCP get_next_tasks (which loads all of a task's dependencies).
            // Filtering the target to in-project here would let such a task slip into recommendations (#188 review).
            var dependencies = await db.TaskDependencies
                .Where(d => d.ProjectId == id && taskIds.Contains(d.TaskItemId))
                .AsNoTracking()
                .ToListAsync();
            var dependenciesByBlockedTask = dependencies.ToLookup(d => d.TaskItemId);

            var blockedCandidates = projectTasks
                .Where(t => t.Status != WorkStatus.Done)
                .Select(task => new
                {
                    Task = task,
                    UnfinishedBlockers = dependenciesByBlockedTask[task.Id]
                        .Where(d => tasksById.TryGetValue(d.DependsOnTaskItemId, out var blocker) && blocker.Status != WorkStatus.Done)
                        .Select(d => tasksById[d.DependsOnTaskItemId])
                        .OrderBy(t => t.SortOrder)
                        .ToList()
                })
                .Where(x => x.UnfinishedBlockers.Count > 0)
                .ToList();

            // Rank via the shared TaskRanking service (#151/#188) so this endpoint and the MCP
            // get_next_tasks tool can never drift: identical canonical order, actor de-prioritisation,
            // reason strings, and 1–50 limit clamp. We hand the ranker the open tasks, the
            // project-wide status map (to resolve blockers), and a depends-on lookup; it filters
            // blocked tasks and orders the rest. Only the result ORDER changes from the old inline
            // ranking (now actor-aware + deterministic by Id) — the response shape is unchanged.
            var openTasks = projectTasks.Where(t => t.Status != WorkStatus.Done).ToList();
            var statusById = projectTasks.ToDictionary(t => t.Id, t => t.Status);
            var dependsOnByTask = dependencies.ToLookup(d => d.TaskItemId, d => d.DependsOnTaskItemId);

            var recommendations = Services.TaskRanking
                .RankNextTasks(openTasks, statusById, dependsOnByTask, actor, limit ?? 5)
                .Select((ranked, index) => new
                {
                    Rank = index + 1,
                    ranked.Task.Id,
                    ranked.Task.FeatureId,
                    FeatureName = ranked.Task.Feature.Name,
                    ranked.Task.Name,
                    ranked.Task.Description,
                    Status = ranked.Task.Status.ToString(),
                    Priority = ranked.Task.Priority.ToString(),
                    ranked.Task.Assignee,
                    ranked.Task.ClaimedBy,
                    ranked.Task.ClaimedAt,
                    ranked.Task.UpdatedAt,
                    ranked.IsBlocked,
                    BlockedByUnfinishedTasks = false,
                    ranked.Reason
                })
                .ToList();

            return Results.Ok(new
            {
                ProjectId = id,
                GeneratedAt = DateTime.UtcNow,
                Count = recommendations.Count,
                Recommendations = recommendations,
                BlockedTaskCount = blockedCandidates.Count,
                BlockedTasks = blockedCandidates.Select(x => new
                {
                    x.Task.Id,
                    x.Task.FeatureId,
                    FeatureName = x.Task.Feature.Name,
                    x.Task.Name,
                    Status = x.Task.Status.ToString(),
                    Priority = x.Task.Priority.ToString(),
                    IsBlocked = true,
                    BlockedByUnfinishedTasks = true,
                    UnfinishedBlockers = x.UnfinishedBlockers.Select(blocker => new
                    {
                        blocker.Id,
                        blocker.Name,
                        Status = blocker.Status.ToString(),
                        Priority = blocker.Priority.ToString(),
                        blocker.UpdatedAt
                    }),
                    Reason = BuildBlockedTaskReason(x.Task, x.UnfinishedBlockers)
                })
            });
        });

        // POST /api/projects — create
        app.MapPost("/api/projects", async (ProjectDto dto, AppDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(dto.Name))
                return Results.UnprocessableEntity(new { message = "Name is required." });

            var project = new Project
            {
                Name = dto.Name.Trim(),
                Description = dto.Description?.Trim(),
                Goals = dto.Goals?.Trim(),
                Readme = dto.Readme?.Trim()
            };

            db.Projects.Add(project);
            await db.SaveChangesAsync();

            return Results.Created($"/api/projects/{project.Id}", new
            {
                project.Id,
                project.Name,
                project.Description,
                project.Goals,
                project.Readme,
                project.CreatedAt,
                project.UpdatedAt
            });
        });

        // PUT /api/projects/{id} — update
        app.MapPut("/api/projects/{id:int}", async (int id, ProjectDto dto, AppDbContext db) =>
        {
            var project = await db.Projects.FindAsync(id);
            if (project is null)
                return Results.NotFound(new { message = $"Project {id} not found." });

            if (string.IsNullOrWhiteSpace(dto.Name))
                return Results.UnprocessableEntity(new { message = "Name is required." });

            // Partial update: only overwrite a field the caller actually sent. An omitted field arrives
            // as null and MUST be left untouched (otherwise an MCP rename that sends only `name` would
            // wipe the project's documentation — #203). An explicit empty/whitespace value clears the
            // column to null. UpdatedAt is bumped only when something actually changed (no-op PUTs don't
            // churn state). Mirrors TaskService.UpdateAsync's per-field guards.
            var changed = false;
            var trimmedName = dto.Name.Trim();
            if (!string.Equals(project.Name, trimmedName, StringComparison.Ordinal)) { project.Name = trimmedName; changed = true; }
            if (dto.Description is not null)
            {
                var next = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim();
                if (!string.Equals(project.Description, next, StringComparison.Ordinal)) { project.Description = next; changed = true; }
            }
            if (dto.Goals is not null)
            {
                var next = string.IsNullOrWhiteSpace(dto.Goals) ? null : dto.Goals.Trim();
                if (!string.Equals(project.Goals, next, StringComparison.Ordinal)) { project.Goals = next; changed = true; }
            }
            if (dto.Readme is not null)
            {
                var next = string.IsNullOrWhiteSpace(dto.Readme) ? null : dto.Readme.Trim();
                if (!string.Equals(project.Readme, next, StringComparison.Ordinal)) { project.Readme = next; changed = true; }
            }
            if (changed) project.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync();

            return Results.Ok(new
            {
                project.Id,
                project.Name,
                project.Description,
                project.Goals,
                project.Readme,
                project.CreatedAt,
                project.UpdatedAt
            });
        });

        // DELETE /api/projects/{id} — FK-safe, audited cascade delete.
        // Routes through ITaskService.DeleteProjectAsync so the leaf-first child cleanup
        // and the ProjectDeleted audit event are produced identically for REST and the
        // Blazor UI (#155). Mirrors the task-delete endpoint's exception mapping.
        app.MapDelete("/api/projects/{id:int}", async (int id, Services.ITaskService taskSvc, CancellationToken ct) =>
        {
            try
            {
                await taskSvc.DeleteProjectAsync(id, actor: null, ct);
                return Results.NoContent();
            }
            catch (Services.ProjectNotFoundException)
            {
                return Results.NotFound(new { message = $"Project {id} not found." });
            }
        });
    }

    // ── Activity Endpoints ─────────────────────────────────────────────

    private static void MapActivityEndpoints(this WebApplication app)
    {
        // GET /api/projects/{id}/activity — durable project event trail
        app.MapGet("/api/projects/{id:int}/activity", async (int id, int? limit, AppDbContext db) =>
        {
            // Audit events are retained without a Project FK so history survives deletion (#85).
            // Query events first; 404 only when there is genuinely nothing — no events AND the
            // project does not currently exist. A deleted project with retained events still returns them.
            var take = Math.Clamp(limit ?? 100, 1, 500);
            var events = await db.ActivityEvents
                .Where(e => e.ProjectId == id)
                .AsNoTracking()
                .OrderByDescending(e => e.CreatedAt)
                // ── Id tiebreak (#328). CreatedAt alone is NOT a total order here: TaskService
                // writes several events inside one SaveChangesAsync, so identical instants are
                // routine. Without a tiebreak the sort is unstable, and a `Take(n)` over an
                // unstable sort can return a different SET between two identical requests — not
                // merely a different order. Applied to EVERY ActivityEvent query below, not just
                // the one that surfaced it.
                .ThenByDescending(e => e.Id)
                .Take(take)
                .ToListAsync();

            if (events.Count == 0 && !await db.Projects.AnyAsync(p => p.Id == id))
                return Results.NotFound(new { message = $"Project {id} not found." });

            return Results.Ok(events.Select(MapActivityEvent));
        });

        // GET /api/tasks/{id}/activity — durable task event trail
        app.MapGet("/api/tasks/{id:int}/activity", async (int id, int? limit, AppDbContext db) =>
        {
            var take = Math.Clamp(limit ?? 100, 1, 500);
            var events = await db.ActivityEvents
                .Where(e => e.EntityType == "Task" && e.EntityId == id)
                .AsNoTracking()
                .OrderByDescending(e => e.CreatedAt)
                .ThenByDescending(e => e.Id)   // total order — see the note on /api/projects/{id}/activity
                .Take(take)
                .ToListAsync();

            if (events.Count == 0)
            {
                var taskExists = await db.Tasks.AnyAsync(t => t.Id == id);
                if (!taskExists)
                    return Results.NotFound(new { message = $"Task {id} not found." });
            }

            return Results.Ok(events.Select(MapActivityEvent));
        });
    }

    // ── Idea Endpoints ─────────────────────────────────────────────────
    // The Ideas inbox (#180). Every mutation routes through IIdeaService so each
    // change emits a uniform ActivityEvent — the single-audit-boundary rule (#88).
    // Idea activity events are keyed by EntityType = "Idea" + the idea id, mirroring
    // how tasks use EntityType == "Task" (see the /api/tasks/{id}/activity endpoint).

    private static void MapIdeaEndpoints(this WebApplication app)
    {
        // GET /api/ideas — list newest-first with optional filters.
        app.MapGet("/api/ideas", async (string? status, string? tag, string? q, int? limit, int? offset, Services.IIdeaService ideaSvc, CancellationToken ct) =>
        {
            // Parse the status filter at the edge. A blank/absent status means "no
            // filter"; a present-but-invalid value is a client error (422) rather
            // than being silently ignored.
            IdeaStatus? statusFilter = null;
            if (!string.IsNullOrWhiteSpace(status))
            {
                if (!Enum.TryParse<IdeaStatus>(status, ignoreCase: true, out var parsed)
                    || !Enum.IsDefined(typeof(IdeaStatus), parsed))
                {
                    return Results.UnprocessableEntity(new { message = $"Invalid status: {status}. Valid values: {string.Join(", ", Enum.GetNames<IdeaStatus>())}" });
                }
                statusFilter = parsed;
            }

            // The service clamps limit (1..500) and floors offset (>= 0) itself.
            var items = await ideaSvc.ListAsync(statusFilter, tag, q, limit ?? 100, offset ?? 0, ct);
            return Results.Ok(items.Select(MapIdea));
        });

        // GET /api/ideas/{id} — one idea, or 404.
        app.MapGet("/api/ideas/{id:int}", async (int id, Services.IIdeaService ideaSvc, CancellationToken ct) =>
        {
            var idea = await ideaSvc.GetByIdAsync(id, ct);
            if (idea is null)
                return Results.NotFound(new { message = $"Idea {id} not found." });

            return Results.Ok(MapIdea(idea));
        });

        // POST /api/ideas — create (201). Priority arrives as a string and is parsed
        // at the edge (a present-but-invalid value is 422); the service validates the
        // title (non-empty, <= 500 chars).
        app.MapPost("/api/ideas", async (IdeaCreateDto dto, Services.IIdeaService ideaSvc, CancellationToken ct) =>
        {
            TaskPriority? priority = null;
            if (dto.Priority is not null)
            {
                var raw = dto.Priority.Trim();
                if (raw.Length == 0 || !Enum.TryParse<TaskPriority>(raw, ignoreCase: true, out var p) || !Enum.IsDefined(p))
                    return Results.UnprocessableEntity(new { message = $"Invalid priority: {dto.Priority}. Valid values: {string.Join(", ", Enum.GetNames<TaskPriority>())}" });
                priority = p;
            }

            try
            {
                var create = new Services.IdeaCreate(
                    Title: dto.Title,
                    Body: dto.Body,
                    Plan: dto.Plan,
                    Tags: dto.Tags,
                    Priority: priority);
                var idea = await ideaSvc.CreateAsync(create, dto.Actor, ct);

                return Results.Created($"/api/ideas/{idea.Id}", MapIdea(idea));
            }
            catch (Services.ValidationException ex)
            {
                return Results.UnprocessableEntity(new { message = ex.Message });
            }
        });

        // PATCH /api/ideas/{id} — partial update (200). null/absent DTO fields mean
        // "leave unchanged". Status/Priority arrive as strings and are parsed at the
        // edge (a present-but-invalid value is 422), mirroring the GET ?status= filter.
        app.MapPatch("/api/ideas/{id:int}", async (int id, IdeaUpdateDto dto, Services.IIdeaService ideaSvc, CancellationToken ct) =>
        {
            IdeaStatus? status = null;
            if (dto.Status is not null)
            {
                var raw = dto.Status.Trim();
                if (raw.Length == 0 || !Enum.TryParse<IdeaStatus>(raw, ignoreCase: true, out var s) || !Enum.IsDefined(s))
                    return Results.UnprocessableEntity(new { message = $"Invalid status: {dto.Status}. Valid values: {string.Join(", ", Enum.GetNames<IdeaStatus>())}" });
                status = s;
            }

            TaskPriority? priority = null;
            if (dto.Priority is not null)
            {
                var raw = dto.Priority.Trim();
                if (raw.Length == 0 || !Enum.TryParse<TaskPriority>(raw, ignoreCase: true, out var p) || !Enum.IsDefined(p))
                    return Results.UnprocessableEntity(new { message = $"Invalid priority: {dto.Priority}. Valid values: {string.Join(", ", Enum.GetNames<TaskPriority>())}" });
                priority = p;
            }

            try
            {
                var update = new Services.IdeaUpdate(
                    Title: dto.Title,
                    Body: dto.Body,
                    Plan: dto.Plan,
                    Status: status,
                    Tags: dto.Tags,
                    Priority: priority);
                var idea = await ideaSvc.UpdateAsync(id, update, dto.Actor, ct);

                return Results.Ok(MapIdea(idea));
            }
            catch (Services.IdeaNotFoundException)
            {
                return Results.NotFound(new { message = $"Idea {id} not found." });
            }
            catch (Services.ValidationException ex)
            {
                return Results.UnprocessableEntity(new { message = ex.Message });
            }
        });

        // DELETE /api/ideas/{id} — delete (204). No actor param on this route, matching
        // the task DELETE; the audit row is written before removal by the service.
        app.MapDelete("/api/ideas/{id:int}", async (int id, Services.IIdeaService ideaSvc, CancellationToken ct) =>
        {
            try
            {
                await ideaSvc.DeleteAsync(id, actor: null, ct);
                return Results.NoContent();
            }
            catch (Services.IdeaNotFoundException)
            {
                return Results.NotFound(new { message = $"Idea {id} not found." });
            }
        });

        // POST /api/ideas/{id}/promote — promote the idea into a new project (200).
        // The body is optional (it only carries an actor), so dto may be null.
        // Promoting an already-promoted idea throws ValidationException → 422 (not 409).
        app.MapPost("/api/ideas/{id:int}/promote", async (int id, IdeaPromoteDto? dto, Services.IIdeaService ideaSvc, CancellationToken ct) =>
        {
            try
            {
                var result = await ideaSvc.PromoteAsync(id, dto?.Actor, ct);
                return Results.Ok(new { ideaId = result.IdeaId, projectId = result.ProjectId });
            }
            catch (Services.IdeaNotFoundException)
            {
                return Results.NotFound(new { message = $"Idea {id} not found." });
            }
            catch (Services.ValidationException ex)
            {
                return Results.UnprocessableEntity(new { message = ex.Message });
            }
        });

        // GET /api/ideas/{id}/activity — durable idea event trail. Mirrors the
        // /api/tasks/{id}/activity endpoint: events are keyed by EntityType = "Idea".
        app.MapGet("/api/ideas/{id:int}/activity", async (int id, int? limit, AppDbContext db, CancellationToken ct) =>
        {
            var take = Math.Clamp(limit ?? 100, 1, 500);
            var events = await db.ActivityEvents
                .Where(e => e.EntityType == "Idea" && e.EntityId == id)
                .AsNoTracking()
                .OrderByDescending(e => e.CreatedAt)
                .ThenByDescending(e => e.Id)   // total order — see the note on /api/projects/{id}/activity
                .Take(take)
                .ToListAsync(ct);

            // Only probe for existence when there are no events, so a real-but-quiet
            // idea returns an empty trail rather than a 404.
            if (events.Count == 0 && !await db.Ideas.AnyAsync(i => i.Id == id, ct))
                return Results.NotFound(new { message = $"Idea {id} not found." });

            return Results.Ok(events.Select(MapActivityEvent));
        });
    }

    // ── Memory Endpoints (#224) ────────────────────────────────────────
    // mem0-style remember/recall over the bi-temporal memory store (#221) + hybrid retrieval
    // (#223). Remember routes through IMemoryService (one MemoryCreated ActivityEvent, #88);
    // recall is a read via IMemorySearch (no event — only the un-audited access-stat bump).
    // Open access by charter: no auth, no RequireHost — trust the network.

    private static void MapMemoryEndpoints(this WebApplication app)
    {
        // POST /api/memory — remember. Defaults: type=Semantic, scope=Agent,
        // scopeKey="knowledge-graph" (the same namespace the MCP knowledge-graph tools use, so a
        // REST-remembered fact is recallable by search_nodes and vice versa). Enums arrive as
        // strings and are parsed at the edge (present-but-invalid ⇒ 422, mirroring the idea
        // endpoints); the service validates content/scopeKey.
        app.MapPost("/api/memory", async (MemoryCreateDto? dto, Services.IMemoryService memorySvc, CancellationToken ct) =>
        {
            // A literal JSON `null` body binds dto as null — return a clean 400 instead of an NRE
            // 500 (Gemini review, PR #259).
            if (dto is null)
                return Results.BadRequest(new { message = "Request body is required: { content, type?, scope?, scopeKey?, tags?, source?, confidence?, actor? }." });

            var type = MemoryType.Semantic;
            if (dto.Type is not null)
            {
                var raw = dto.Type.Trim();
                if (raw.Length == 0 || !Enum.TryParse<MemoryType>(raw, ignoreCase: true, out var parsedType) || !Enum.IsDefined(parsedType))
                    return Results.UnprocessableEntity(new { message = $"Invalid type: {dto.Type}. Valid values: {string.Join(", ", Enum.GetNames<MemoryType>())}" });
                type = parsedType;
            }

            var scope = MemoryScope.Agent;
            if (dto.Scope is not null)
            {
                var raw = dto.Scope.Trim();
                if (raw.Length == 0 || !Enum.TryParse<MemoryScope>(raw, ignoreCase: true, out var parsedScope) || !Enum.IsDefined(parsedScope))
                    return Results.UnprocessableEntity(new { message = $"Invalid scope: {dto.Scope}. Valid values: {string.Join(", ", Enum.GetNames<MemoryScope>())}" });
                scope = parsedScope;
            }

            var scopeKey = string.IsNullOrWhiteSpace(dto.ScopeKey) ? "knowledge-graph" : dto.ScopeKey;

            try
            {
                var node = await memorySvc.CreateNodeAsync(
                    new Services.MemoryNodeCreate(type, scope, scopeKey, dto.Content, dto.Tags, dto.Source, dto.Confidence),
                    dto.Actor, ct);
                return Results.Created($"/api/memory/{node.Id}", MapMemoryNode(node));
            }
            catch (Services.ValidationException ex)
            {
                return Results.UnprocessableEntity(new { message = ex.Message });
            }
        });

        // GET /api/memory/search — recall: ranked hybrid retrieval with per-signal fields so a
        // result is explainable. Defaults: scope=Agent, scopeKey="knowledge-graph", k=10.
        // Blank q ⇒ 422 (an everything-matches recency scan is a caller mistake at this edge);
        // bad scope ⇒ 422; bad k ⇒ 422 (the service's ValidationException surfaces as 422).
        app.MapGet("/api/memory/search", async (string? q, string? scope, string? scopeKey, int? k, Services.IMemorySearch memorySearch, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q))
                return Results.UnprocessableEntity(new { message = "Query parameter 'q' is required and must be non-empty." });

            var parsedScope = MemoryScope.Agent;
            if (!string.IsNullOrWhiteSpace(scope))
            {
                if (!Enum.TryParse<MemoryScope>(scope.Trim(), ignoreCase: true, out var s) || !Enum.IsDefined(s))
                    return Results.UnprocessableEntity(new { message = $"Invalid scope: {scope}. Valid values: {string.Join(", ", Enum.GetNames<MemoryScope>())}" });
                parsedScope = s;
            }

            var key = string.IsNullOrWhiteSpace(scopeKey) ? "knowledge-graph" : scopeKey.Trim();
            // Clamp like the search_nodes MCP tool (1-100): keeps ranking work and payload size
            // bounded on this open endpoint (Copilot review, PR #259).
            var take = Math.Clamp(k ?? 10, 1, 100);

            try
            {
                var hits = await memorySearch.SearchAsync(parsedScope, key, q, take, opts: null, ct);
                return Results.Ok(new
                {
                    Query = q,
                    Scope = parsedScope.ToString(),
                    ScopeKey = key,
                    K = take,
                    Count = hits.Count,
                    Hits = hits.Select(h => new
                    {
                        h.Id,
                        h.Content,
                        h.FusedScore,
                        h.VectorRank,
                        h.KeywordRank,
                        h.RecencyScore,
                        h.UsefulnessScore
                    })
                });
            }
            catch (Services.ValidationException ex)
            {
                // e.g. k <= 0 — a caller mistake, same 422 contract as the enum parses above.
                return Results.UnprocessableEntity(new { message = ex.Message });
            }
        });
    }

    // ── Feature Endpoints ──────────────────────────────────────────────

    private static void MapFeatureEndpoints(this WebApplication app)
    {
        // GET /api/projects/{projectId}/features — list features for project
        app.MapGet("/api/projects/{projectId:int}/features", async (int projectId, AppDbContext db) =>
        {
            var projectExists = await db.Projects.AnyAsync(p => p.Id == projectId);
            if (!projectExists)
                return Results.NotFound(new { message = $"Project {projectId} not found." });

            var features = await db.Features
                .Where(f => f.ProjectId == projectId)
                .Include(f => f.Tasks)
                .AsNoTracking()
                .OrderBy(f => f.SortOrder)
                .ToListAsync();

            return Results.Ok(features.Select(f => new
            {
                f.Id,
                f.ProjectId,
                f.ParentFeatureId,
                f.Name,
                f.Description,
                f.SortOrder,
                f.CreatedAt,
                f.UpdatedAt,
                TaskCount = f.Tasks.Count,
                DoneCount = f.Tasks.Count(t => t.Status == WorkStatus.Done)
            }));
        });

        // GET /api/features/{id} — get one with task counts and children
        app.MapGet("/api/features/{id:int}", async (int id, AppDbContext db) =>
        {
            var feature = await db.Features
                .Include(f => f.Tasks)
                .Include(f => f.ChildFeatures)
                .AsNoTracking()
                .FirstOrDefaultAsync(f => f.Id == id);

            if (feature is null)
                return Results.NotFound(new { message = $"Feature {id} not found." });

            return Results.Ok(new
            {
                feature.Id,
                feature.ProjectId,
                feature.ParentFeatureId,
                feature.Name,
                feature.Description,
                feature.SortOrder,
                feature.CreatedAt,
                feature.UpdatedAt,
                TaskCount = feature.Tasks.Count,
                DoneCount = feature.Tasks.Count(t => t.Status == WorkStatus.Done),
                Tasks = feature.Tasks.OrderBy(t => t.SortOrder).ThenBy(t => t.Id).Select(t => new
                {
                    t.Id,
                    t.Name,
                    Status = t.Status.ToString(),
                    Priority = t.Priority.ToString(),
                    t.Assignee,
                    t.SortOrder,
                    t.CreatedAt,
                    t.UpdatedAt,
                    t.CompletedAt
                }),
                ChildFeatures = feature.ChildFeatures.OrderBy(c => c.SortOrder).Select(c => new
                {
                    c.Id,
                    c.Name,
                    c.SortOrder
                })
            });
        });

        // POST /api/projects/{projectId}/features — create (optionally nested under parentFeatureId)
        app.MapPost("/api/projects/{projectId:int}/features", async (int projectId, FeatureDto dto, AppDbContext db) =>
        {
            var projectExists = await db.Projects.AnyAsync(p => p.Id == projectId);
            if (!projectExists)
                return Results.NotFound(new { message = $"Project {projectId} not found." });

            if (string.IsNullOrWhiteSpace(dto.Name))
                return Results.UnprocessableEntity(new { message = "Name is required." });

            // Validate parent feature if provided
            if (dto.ParentFeatureId.HasValue)
            {
                var parentExists = await db.Features.AnyAsync(f => f.Id == dto.ParentFeatureId.Value && f.ProjectId == projectId);
                if (!parentExists)
                    return Results.NotFound(new { message = $"Parent feature {dto.ParentFeatureId.Value} not found in project {projectId}." });
            }

            var siblingQuery = dto.ParentFeatureId.HasValue
                ? db.Features.Where(f => f.ParentFeatureId == dto.ParentFeatureId.Value)
                : db.Features.Where(f => f.ProjectId == projectId && f.ParentFeatureId == null);

            var maxSort = await siblingQuery.MaxAsync(f => (int?)f.SortOrder) ?? -1;

            var feature = new Feature
            {
                ProjectId = projectId,
                ParentFeatureId = dto.ParentFeatureId,
                Name = dto.Name.Trim(),
                Description = dto.Description?.Trim(),
                SortOrder = dto.SortOrder ?? maxSort + 1
            };

            db.Features.Add(feature);
            await db.SaveChangesAsync();

            return Results.Created($"/api/features/{feature.Id}", new
            {
                feature.Id,
                feature.ProjectId,
                feature.ParentFeatureId,
                feature.Name,
                feature.Description,
                feature.SortOrder,
                feature.CreatedAt,
                feature.UpdatedAt
            });
        });

        // PUT /api/features/{id} — update
        app.MapPut("/api/features/{id:int}", async (int id, FeatureDto dto, AppDbContext db) =>
        {
            var feature = await db.Features.FindAsync(id);
            if (feature is null)
                return Results.NotFound(new { message = $"Feature {id} not found." });

            if (string.IsNullOrWhiteSpace(dto.Name))
                return Results.UnprocessableEntity(new { message = "Name is required." });

            // Partial update (#203): only overwrite Description when sent (null preserves, empty clears).
            // UpdatedAt is bumped only when something actually changed.
            var changed = false;
            var trimmedName = dto.Name.Trim();
            if (!string.Equals(feature.Name, trimmedName, StringComparison.Ordinal)) { feature.Name = trimmedName; changed = true; }
            if (dto.Description is not null)
            {
                var next = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim();
                if (!string.Equals(feature.Description, next, StringComparison.Ordinal)) { feature.Description = next; changed = true; }
            }
            if (dto.SortOrder.HasValue && feature.SortOrder != dto.SortOrder.Value) { feature.SortOrder = dto.SortOrder.Value; changed = true; }
            if (dto.ParentFeatureId.HasValue)
            {
                var newParentFeatureId = dto.ParentFeatureId.Value == 0 ? (int?)null : dto.ParentFeatureId.Value;
                if (newParentFeatureId == id)
                    return Results.UnprocessableEntity(new { message = "A feature cannot be its own parent." });

                if (newParentFeatureId.HasValue)
                {
                    var allProjectFeatures = await db.Features
                        .Where(f => f.ProjectId == feature.ProjectId)
                        .AsNoTracking()
                        .ToListAsync();

                    var parentExists = allProjectFeatures.Any(f => f.Id == newParentFeatureId.Value);
                    if (!parentExists)
                        return Results.NotFound(new { message = $"Parent feature {newParentFeatureId.Value} not found in project {feature.ProjectId}." });

                    if (WouldCreateFeatureCycle(id, newParentFeatureId.Value, allProjectFeatures))
                        return Results.UnprocessableEntity(new { message = "A feature cannot be moved under one of its descendants." });
                }

                if (feature.ParentFeatureId != newParentFeatureId) { feature.ParentFeatureId = newParentFeatureId; changed = true; }
            }
            if (changed) feature.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync();

            return Results.Ok(new
            {
                feature.Id,
                feature.ProjectId,
                feature.ParentFeatureId,
                feature.Name,
                feature.Description,
                feature.SortOrder,
                feature.CreatedAt,
                feature.UpdatedAt
            });
        });

        // DELETE /api/features/{id} — cascade delete
        app.MapDelete("/api/features/{id:int}", async (int id, AppDbContext db, CancellationToken ct) =>
        {
            var exists = await db.Features.AnyAsync(f => f.Id == id, ct);
            if (!exists)
                return Results.NotFound(new { message = $"Feature {id} not found." });

            await KanbanFlow.Services.ProjectGraphDeleter.DeleteFeatureTreeAsync(db, id, ct);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    // ── Task Endpoints ─────────────────────────────────────────────────

    private static void MapTaskEndpoints(this WebApplication app)
    {
        // GET /api/features/{featureId}/tasks — list tasks for feature
        app.MapGet("/api/features/{featureId:int}/tasks", async (int featureId, AppDbContext db) =>
        {
            var featureExists = await db.Features.AnyAsync(f => f.Id == featureId);
            if (!featureExists)
                return Results.NotFound(new { message = $"Feature {featureId} not found." });

            var tasks = await db.Tasks
                .Where(t => t.FeatureId == featureId)
                .Include(t => t.Subtasks)
                .Include(t => t.AcceptanceCriteria)
                .AsSplitQuery() // #157: avoid the tasks x subtasks x criteria cartesian blow-up
                .AsNoTracking()
                .OrderBy(t => t.SortOrder)
                .ThenBy(t => t.Id) // #207: deterministic tie-break when SortOrder ties
                .ToListAsync();

            return Results.Ok(tasks.Select(MapTaskSummary));
        });

        // GET /api/projects/{projectId}/tasks — list ALL tasks in project (flat)
        app.MapGet("/api/projects/{projectId:int}/tasks", async (int projectId, AppDbContext db) =>
        {
            var projectExists = await db.Projects.AnyAsync(p => p.Id == projectId);
            if (!projectExists)
                return Results.NotFound(new { message = $"Project {projectId} not found." });

            var tasks = await db.Tasks
                .Where(t => t.Feature.ProjectId == projectId)
                .Include(t => t.Feature)
                .Include(t => t.Subtasks)
                .Include(t => t.AcceptanceCriteria)
                .AsSplitQuery() // #157: avoid the tasks x subtasks x criteria cartesian blow-up
                .AsNoTracking()
                .OrderBy(t => t.Feature.SortOrder)
                    .ThenBy(t => t.SortOrder)
                .ToListAsync();

            return Results.Ok(tasks.Select(t => new
            {
                t.Id,
                t.FeatureId,
                FeatureName = t.Feature.Name,
                t.Name,
                t.Description,
                Status = t.Status.ToString(),
                Priority = t.Priority.ToString(),
                t.Assignee,
                t.ClaimedBy,
                t.ClaimedAt,
                t.SortOrder,
                t.CreatedAt,
                t.UpdatedAt,
                t.CompletedAt,
                SubtaskCount = t.Subtasks.Count,
                SubtaskDoneCount = t.Subtasks.Count(s => s.Completed),
                AcceptanceCriteriaCount = t.AcceptanceCriteria.Count,
                AcceptanceCriteriaDoneCount = t.AcceptanceCriteria.Count(c => c.Completed)
            }));
        });

        // GET /api/projects/{projectId}/board — tasks grouped by status (for board view)
        app.MapGet("/api/projects/{projectId:int}/board", async (int projectId, AppDbContext db) =>
        {
            var projectExists = await db.Projects.AnyAsync(p => p.Id == projectId);
            if (!projectExists)
                return Results.NotFound(new { message = $"Project {projectId} not found." });

            var tasks = await db.Tasks
                .Where(t => t.Feature.ProjectId == projectId)
                .Include(t => t.Feature)
                .Include(t => t.Subtasks)
                .Include(t => t.AcceptanceCriteria)
                .AsSplitQuery() // #157: avoid the tasks x subtasks x criteria cartesian blow-up
                .AsNoTracking()
                .OrderBy(t => t.SortOrder)
                .ThenBy(t => t.Id) // #207: deterministic tie-break when SortOrder ties
                .ToListAsync();

            var board = Enum.GetValues<WorkStatus>()
                .ToDictionary(
                    status => status.ToString(),
                    status => tasks
                        .Where(t => t.Status == status)
                        .Select(t => new
                        {
                            t.Id,
                            t.FeatureId,
                            FeatureName = t.Feature.Name,
                            t.Name,
                            t.Description,
                            Status = t.Status.ToString(),
                            Priority = t.Priority.ToString(),
                            t.Assignee,
                            t.ClaimedBy,
                            t.ClaimedAt,
                            t.SortOrder,
                            t.CreatedAt,
                            t.UpdatedAt,
                            t.CompletedAt,
                            SubtaskCount = t.Subtasks.Count,
                            SubtaskDoneCount = t.Subtasks.Count(s => s.Completed),
                            AcceptanceCriteriaCount = t.AcceptanceCriteria.Count,
                            AcceptanceCriteriaDoneCount = t.AcceptanceCriteria.Count(c => c.Completed)
                        })
                        .ToList()
                );

            return Results.Ok(board);
        });

        // GET /api/tasks/{id} — get one with subtasks
        app.MapGet("/api/tasks/{id:int}", async (int id, AppDbContext db) =>
        {
            var task = await db.Tasks
                .Include(t => t.Feature)
                .Include(t => t.Subtasks.OrderBy(s => s.SortOrder))
                .Include(t => t.AcceptanceCriteria.OrderBy(c => c.SortOrder))
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == id);

            if (task is null)
                return Results.NotFound(new { message = $"Task {id} not found." });

            return Results.Ok(new
            {
                task.Id,
                task.FeatureId,
                FeatureName = task.Feature.Name,
                task.Name,
                task.Description,
                Status = task.Status.ToString(),
                Priority = task.Priority.ToString(),
                task.Assignee,
                task.ClaimedBy,
                task.ClaimedAt,
                task.SortOrder,
                task.CreatedAt,
                task.UpdatedAt,
                task.CompletedAt,
                Subtasks = task.Subtasks.Select(s => new
                {
                    s.Id,
                    s.ParentSubtaskId,
                    s.Text,
                    s.Completed,
                    s.SortOrder,
                    s.CreatedAt
                }),
                AcceptanceCriteria = task.AcceptanceCriteria.Select(c => new
                {
                    c.Id,
                    c.Text,
                    c.Completed,
                    c.SortOrder,
                    c.CreatedAt,
                    c.UpdatedAt
                })
            });
        });

        // GET /api/tasks/{id}/context — single-call AI bundle (R-1.6, R-3.4): task + feature
        // breadcrumb + criteria + recursive subtasks + resolved dependencies + recent comments
        // + related requirements + recent activity. Designed so an agent claiming a task can
        // act with full context in one round-trip.
        app.MapGet("/api/tasks/{id:int}/context", async (int id, AppDbContext db) =>
        {
            var task = await db.Tasks
                .Include(t => t.Feature).ThenInclude(f => f.Project)
                .Include(t => t.AcceptanceCriteria.OrderBy(c => c.SortOrder).ThenBy(c => c.Id))
                .Include(t => t.Subtasks.OrderBy(s => s.SortOrder).ThenBy(s => s.Id))
                .AsSplitQuery()
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == id);
            if (task is null)
                return Results.NotFound(new { message = $"Task {id} not found." });

            var projectId = task.Feature.ProjectId;

            // Recursive subtask materialisation. Pre-build a parent→children index so the
            // tree walk is O(n) instead of the previous O(n²) Where-scan-per-node.
            var subtasksFlat = task.Subtasks.ToList();
            var subtasksByParent = subtasksFlat
                .Where(s => s.ParentSubtaskId.HasValue)
                .GroupBy(s => s.ParentSubtaskId!.Value)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderBy(c => c.SortOrder).ThenBy(c => c.Id).ToList());
            object MapSubtask(Subtask s) => new
            {
                s.Id,
                s.ParentSubtaskId,
                s.Text,
                s.Completed,
                s.SortOrder,
                s.CreatedAt,
                Children = subtasksByParent.TryGetValue(s.Id, out var kids)
                    ? kids.Select(MapSubtask).ToList()
                    : (IReadOnlyList<object>)Array.Empty<object>()
            };
            var subtaskTree = subtasksFlat
                .Where(s => s.ParentSubtaskId == null)
                .OrderBy(s => s.SortOrder).ThenBy(s => s.Id)
                .Select(MapSubtask)
                .ToList();

            // Dependencies — both directions, with names + status of the other side.
            // Stable ordering (Id) so AI agents see deterministic output across runs.
            var deps = await db.TaskDependencies
                .Where(d => d.TaskItemId == id || d.DependsOnTaskItemId == id)
                .Include(d => d.TaskItem)
                .Include(d => d.DependsOnTaskItem)
                .OrderBy(d => d.Id)
                .AsNoTracking()
                .ToListAsync();
            var dependencies = deps.Select(d => new
            {
                d.Id,
                Direction = d.TaskItemId == id ? "DependsOn" : "Blocks",
                Type = d.Type.ToString(),
                Other = d.TaskItemId == id
                    ? new
                    {
                        d.DependsOnTaskItem!.Id,
                        d.DependsOnTaskItem.Name,
                        Status = d.DependsOnTaskItem.Status.ToString(),
                        d.DependsOnTaskItem.ClaimedBy,
                        IsComplete = d.DependsOnTaskItem.Status == WorkStatus.Done
                    }
                    : new
                    {
                        d.TaskItem!.Id,
                        d.TaskItem.Name,
                        Status = d.TaskItem.Status.ToString(),
                        d.TaskItem.ClaimedBy,
                        IsComplete = d.TaskItem.Status == WorkStatus.Done
                    },
                d.CreatedAt
            }).ToList();
            var openBlockerCount = dependencies.Count(x => x.Direction == "DependsOn" && !x.Other.IsComplete);

            // Recent comments (latest first, capped)
            var recentComments = await db.TaskComments
                .Where(c => c.TaskItemId == id)
                .OrderByDescending(c => c.CreatedAt)
                .Take(20)
                .AsNoTracking()
                .Select(c => new
                {
                    c.Id,
                    c.Text,
                    c.Author,
                    Type = c.Type.ToString(),
                    c.CreatedAt
                })
                .ToListAsync();

            // Requirements that link to this task (or to its feature). Project-scope filter
            // guards against cross-project leakage if any inconsistent linkage exists.
            // Sort by Priority ASC so P0 (highest priority) appears first — RequirementPriority
            // is defined as P0, P1, P2 with P0 as the most critical, so the smallest enum
            // value is the most important. (PR #106 had this backwards; it's the bug this fix
            // is correcting.)
            var relatedRequirements = await db.Requirements
                .Where(r => r.ProjectId == projectId
                    && (r.TaskItemId == id || r.FeatureId == task.FeatureId))
                .OrderBy(r => r.Priority).ThenBy(r => r.SortOrder).ThenBy(r => r.Id)
                .AsNoTracking()
                .Select(r => new
                {
                    r.Id,
                    r.Text,
                    r.Description,
                    Priority = r.Priority.ToString(),
                    Status = r.Status.ToString(),
                    Linkage = r.TaskItemId == id ? "Task" : "Feature"
                })
                .ToListAsync();

            // Recent activity touching this task (capped)
            var recentActivity = await db.ActivityEvents
                .Where(e => e.EntityType == "Task" && e.EntityId == id)
                .OrderByDescending(e => e.CreatedAt)
                .ThenByDescending(e => e.Id)   // total order — see the note on /api/projects/{id}/activity
                .Take(20)
                .AsNoTracking()
                .Select(e => new
                {
                    e.Id,
                    e.EventType,
                    e.Actor,
                    e.Message,
                    e.BeforeJson,
                    e.AfterJson,
                    OccurredAt = e.CreatedAt
                })
                .ToListAsync();

            return Results.Ok(new
            {
                Task = new
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
                    task.CreatedAt,
                    task.UpdatedAt,
                    task.CompletedAt
                },
                Feature = new
                {
                    task.Feature.Id,
                    task.Feature.Name,
                    ProjectId = projectId,
                    ProjectName = task.Feature.Project.Name
                },
                AcceptanceCriteria = task.AcceptanceCriteria.Select(c => new
                {
                    c.Id,
                    c.Text,
                    c.Completed,
                    c.SortOrder,
                    c.CreatedAt,
                    c.UpdatedAt
                }),
                Subtasks = subtaskTree,
                Dependencies = dependencies,
                OpenBlockerCount = openBlockerCount,
                IsBlocked = openBlockerCount > 0,
                RecentComments = recentComments,
                RelatedRequirements = relatedRequirements,
                RecentActivity = recentActivity
            });
        });

        // POST /api/features/{featureId}/tasks — create (default status: Backlog).
        // Routes through ITaskService.CreateAsync so the Created comment + TaskCreated
        // audit event fire identically whether the REST API, MCP, or a Blazor UI handler
        // creates the task (#88). Enums are validated at the edge to preserve the exact
        // 422 messages; the service re-validates defensively.
        app.MapPost("/api/features/{featureId:int}/tasks", async (int featureId, TaskDto dto, Services.ITaskService taskSvc, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(dto.Name))
                return Results.UnprocessableEntity(new { message = "Name is required." });

            // Validate status enum. Enum.TryParse alone accepts numeric strings like "999"
            // (any in-range int), so pair it with Enum.IsDefined to reject out-of-range
            // values at the edge — mirroring the PUT /api/tasks/{id} validation exactly.
            WorkStatus? status = null;
            if (dto.Status is not null)
            {
                if (!Enum.TryParse<WorkStatus>(dto.Status, ignoreCase: true, out var parsedStatus)
                    || !Enum.IsDefined(typeof(WorkStatus), parsedStatus))
                {
                    return Results.UnprocessableEntity(new { message = $"Invalid status: {dto.Status}. Valid values: {string.Join(", ", Enum.GetNames<WorkStatus>())}" });
                }
                status = parsedStatus;
            }

            // Validate priority enum (same numeric-string guard as status).
            TaskPriority? priority = null;
            if (dto.Priority is not null)
            {
                if (!Enum.TryParse<TaskPriority>(dto.Priority, ignoreCase: true, out var parsedPriority)
                    || !Enum.IsDefined(typeof(TaskPriority), parsedPriority))
                {
                    return Results.UnprocessableEntity(new { message = $"Invalid priority: {dto.Priority}. Valid values: {string.Join(", ", Enum.GetNames<TaskPriority>())}" });
                }
                priority = parsedPriority;
            }

            try
            {
                var create = new Services.TaskCreate(
                    FeatureId: featureId,
                    Name: dto.Name,
                    Description: dto.Description,
                    Status: status,
                    Priority: priority,
                    Assignee: dto.Assignee,
                    SortOrder: dto.SortOrder);
                var task = await taskSvc.CreateAsync(create, dto.Actor, ct);

                return Results.Created($"/api/tasks/{task.Id}", new
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
                    task.CreatedAt,
                    task.UpdatedAt,
                    task.CompletedAt
                });
            }
            catch (Services.FeatureNotFoundException)
            {
                return Results.NotFound(new { message = $"Feature {featureId} not found." });
            }
            catch (Services.ValidationException ex)
            {
                return Results.UnprocessableEntity(new { message = ex.Message });
            }
        });

        // PUT /api/tasks/{id} — update. Routes through ITaskService.UpdateAsync so every
        // field change emits a uniform audit event regardless of caller (#88), and so the
        // whole update — audited fields AND the SortOrder reorder — persists in ONE atomic
        // transaction (#190). SortOrder rides along non-audited: it emits no activity
        // event/comment, and the response still reflects task.SortOrder exactly as before.
        app.MapPut("/api/tasks/{id:int}", async (int id, TaskDto dto, Services.ITaskService taskSvc, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(dto.Name))
                return Results.UnprocessableEntity(new { message = "Name is required." });

            // Validate enums up-front so the service never sees a malformed payload.
            WorkStatus? newStatus = null;
            if (dto.Status is not null)
            {
                if (!Enum.TryParse<WorkStatus>(dto.Status, ignoreCase: true, out var parsed)
                    || !Enum.IsDefined(typeof(WorkStatus), parsed))
                {
                    return Results.UnprocessableEntity(new { message = $"Invalid status: {dto.Status}. Valid values: {string.Join(", ", Enum.GetNames<WorkStatus>())}" });
                }
                newStatus = parsed;
            }

            TaskPriority? newPriority = null;
            if (dto.Priority is not null)
            {
                if (!Enum.TryParse<TaskPriority>(dto.Priority, ignoreCase: true, out var parsed)
                    || !Enum.IsDefined(typeof(TaskPriority), parsed))
                {
                    return Results.UnprocessableEntity(new { message = $"Invalid priority: {dto.Priority}. Valid values: {string.Join(", ", Enum.GetNames<TaskPriority>())}" });
                }
                newPriority = parsed;
            }

            try
            {
                // SortOrder folds into the single service transaction (#190) — it persists
                // atomically with the audited fields, non-audited, in one SaveChanges.
                var update = new Services.TaskUpdate(
                    Name: dto.Name,
                    Description: dto.Description,
                    Status: newStatus,
                    Priority: newPriority,
                    Assignee: dto.Assignee,
                    SortOrder: dto.SortOrder);
                var task = await taskSvc.UpdateAsync(id, update, dto.Actor, ct);

                return Results.Ok(new
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
                    task.CreatedAt,
                    task.UpdatedAt,
                    task.CompletedAt
                });
            }
            catch (Services.TaskNotFoundException)
            {
                return Results.NotFound(new { message = $"Task {id} not found." });
            }
            catch (Services.ValidationException ex)
            {
                return Results.UnprocessableEntity(new { message = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.UnprocessableEntity(new { message = ex.Message });
            }
        });

        // POST /api/tasks/{id}/claim — lightweight task claim for AI agents.
        // Routes through ITaskService.ClaimAsync so the TaskClaimed audit event is
        // emitted uniformly with UI-driven claims (#88). Conflict body shape is
        // preserved here from the exception's claimant fields.
        app.MapPost("/api/tasks/{id:int}/claim", async (int id, ClaimDto dto, Services.ITaskService taskSvc, CancellationToken ct) =>
        {
            var actor = dto.Actor?.Trim();
            if (string.IsNullOrWhiteSpace(actor))
                return Results.UnprocessableEntity(new { message = "Actor is required." });

            try
            {
                var claimedTask = await taskSvc.ClaimAsync(id, actor, ct);
                return Results.Ok(MapTaskClaimState(claimedTask));
            }
            catch (Services.TaskNotFoundException)
            {
                return Results.NotFound(new { message = $"Task {id} not found." });
            }
            catch (Services.TaskClaimConflictException ex)
            {
                return Results.Conflict(new
                {
                    message = $"Task {id} is already claimed by {ex.ClaimedBy}.",
                    ex.ClaimedBy,
                    ex.ClaimedAt
                });
            }
        });

        // POST /api/tasks/{id}/release — release a task claimed by the same actor.
        // Routes through ITaskService.ReleaseAsync so the TaskReleased audit event is
        // emitted uniformly with UI-driven releases (#88). The release-specific conflict
        // message ("only that actor can release it") is reconstructed here so the
        // response stays byte-identical to the previous inline handler.
        app.MapPost("/api/tasks/{id:int}/release", async (int id, ClaimDto dto, Services.ITaskService taskSvc, CancellationToken ct) =>
        {
            var actor = dto.Actor?.Trim();
            if (string.IsNullOrWhiteSpace(actor))
                return Results.UnprocessableEntity(new { message = "Actor is required." });

            try
            {
                var releasedTask = await taskSvc.ReleaseAsync(id, actor, ct);
                return Results.Ok(MapTaskClaimState(releasedTask));
            }
            catch (Services.TaskNotFoundException)
            {
                return Results.NotFound(new { message = $"Task {id} not found." });
            }
            catch (Services.TaskClaimConflictException ex)
            {
                return Results.Conflict(new
                {
                    message = $"Task {id} is claimed by {ex.ClaimedBy}; only that actor can release it.",
                    ex.ClaimedBy,
                    ex.ClaimedAt
                });
            }
        });

        // DELETE /api/tasks/{id} — delete.
        // Routes through ITaskService.DeleteAsync so the TaskDeleted audit event and the
        // FK-safe child cleanup (leaf-first subtasks + acceptance criteria) are produced
        // uniformly with UI-driven deletes (#88). No actor param exists on this route.
        app.MapDelete("/api/tasks/{id:int}", async (int id, Services.ITaskService taskSvc, CancellationToken ct) =>
        {
            try
            {
                await taskSvc.DeleteAsync(id, actor: null, ct);
                return Results.NoContent();
            }
            catch (Services.TaskNotFoundException)
            {
                return Results.NotFound(new { message = $"Task {id} not found." });
            }
        });

        // PATCH /api/tasks/{id}/status — change status only
        // Routed through ITaskService so the same audit/comment side effects fire
        // whether this endpoint or a Blazor UI handler triggers the change (#88).
        app.MapPatch("/api/tasks/{id:int}/status", async (int id, StatusDto dto, Services.ITaskService taskSvc, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(dto.Status))
                return Results.UnprocessableEntity(new { message = "Status is required." });

            if (!Enum.TryParse<WorkStatus>(dto.Status, ignoreCase: true, out var newStatus)
                || !Enum.IsDefined(typeof(WorkStatus), newStatus))
            {
                return Results.UnprocessableEntity(new { message = $"Invalid status: {dto.Status}. Valid values: {string.Join(", ", Enum.GetNames<WorkStatus>())}" });
            }

            try
            {
                var task = await taskSvc.ChangeStatusAsync(id, newStatus, dto.Actor, ct);
                return Results.Ok(new
                {
                    task.Id,
                    task.FeatureId,
                    task.Name,
                    Status = task.Status.ToString(),
                    task.UpdatedAt,
                    task.CompletedAt
                });
            }
            catch (Services.TaskNotFoundException)
            {
                return Results.NotFound(new { message = $"Task {id} not found." });
            }
        });

        // PATCH /api/tasks/{id}/feature — reassign to different feature.
        // Routes through ITaskService so the project-boundary check (#83) and audit
        // event are produced uniformly with UI-driven reassignments.
        app.MapPatch("/api/tasks/{id:int}/feature", async (int id, ReassignDto dto, Services.ITaskService taskSvc, CancellationToken ct) =>
        {
            try
            {
                var task = await taskSvc.ReassignToFeatureAsync(id, dto.FeatureId, dto.Actor, ct);
                return Results.Ok(new
                {
                    task.Id,
                    task.FeatureId,
                    task.Name,
                    Status = task.Status.ToString(),
                    task.UpdatedAt
                });
            }
            catch (Services.TaskNotFoundException)
            {
                return Results.NotFound(new { message = $"Task {id} not found." });
            }
            catch (Services.FeatureNotFoundException)
            {
                return Results.NotFound(new { message = $"Feature {dto.FeatureId} not found." });
            }
            catch (Services.CrossProjectReassignmentException ex)
            {
                return Results.UnprocessableEntity(new
                {
                    message = "Cannot reassign task across projects.",
                    taskId = ex.TaskId,
                    sourceProjectId = ex.SourceProjectId,
                    targetProjectId = ex.TargetProjectId
                });
            }
        });
    }

    // ── Task Dependency Endpoints ─────────────────────────────────────

    private static void MapTaskDependencyEndpoints(this WebApplication app)
    {
        // GET /api/tasks/{taskId}/dependencies — list dependencies involving a task
        app.MapGet("/api/tasks/{taskId:int}/dependencies", async (int taskId, AppDbContext db) =>
        {
            var taskExists = await db.Tasks.AnyAsync(t => t.Id == taskId);
            if (!taskExists)
                return Results.NotFound(new { message = $"Task {taskId} not found." });

            var dependencies = await db.TaskDependencies
                .Where(d => d.TaskItemId == taskId || d.DependsOnTaskItemId == taskId)
                .Include(d => d.TaskItem)
                .Include(d => d.DependsOnTaskItem)
                .AsNoTracking()
                .OrderBy(d => d.CreatedAt)
                .ToListAsync();

            return Results.Ok(dependencies.Select(MapTaskDependency));
        });

        // GET /api/task-dependencies/{id} — get a single dependency
        app.MapGet("/api/task-dependencies/{id:int}", async (int id, AppDbContext db) =>
        {
            var dependency = await db.TaskDependencies
                .Include(d => d.TaskItem)
                .Include(d => d.DependsOnTaskItem)
                .AsNoTracking()
                .SingleOrDefaultAsync(d => d.Id == id);

            if (dependency is null)
                return Results.NotFound(new { message = $"Task dependency {id} not found." });

            return Results.Ok(MapTaskDependency(dependency));
        });

        // POST /api/tasks/{taskId}/dependencies — create a dependency for a task
        app.MapPost("/api/tasks/{taskId:int}/dependencies", async (int taskId, TaskDependencyDto dto, AppDbContext db) =>
        {
            if (!TryParseDefinedEnum<TaskDependencyType>(dto.Type ?? nameof(TaskDependencyType.DependsOn), out var type))
                return Results.UnprocessableEntity(new { message = $"Invalid dependency type: {dto.Type}. Valid values: {string.Join(", ", Enum.GetNames<TaskDependencyType>())}" });

            var (blockedTaskId, blockingTaskId, missingTaskMessage) = ResolveDependencyTaskIds(taskId, dto, type);
            if (!blockedTaskId.HasValue || !blockingTaskId.HasValue)
                return Results.UnprocessableEntity(new { message = missingTaskMessage });

            if (blockedTaskId.Value == blockingTaskId.Value)
                return Results.UnprocessableEntity(new { message = "A task cannot depend on itself." });

            var tasks = await db.Tasks
                .Where(t => t.Id == blockedTaskId.Value || t.Id == blockingTaskId.Value)
                .Include(t => t.Feature)
                .ToListAsync();

            var blockedTask = tasks.SingleOrDefault(t => t.Id == blockedTaskId.Value);
            var blockingTask = tasks.SingleOrDefault(t => t.Id == blockingTaskId.Value);

            if (blockedTask is null)
                return Results.NotFound(new { message = $"Task {blockedTaskId.Value} not found." });
            if (blockingTask is null)
                return Results.NotFound(new { message = $"Task {blockingTaskId.Value} not found." });

            var projectId = blockedTask.Feature.ProjectId;
            if (blockingTask.Feature.ProjectId != projectId)
                return Results.UnprocessableEntity(new { message = "Task dependencies must stay within the same project." });

            var duplicateExists = await db.TaskDependencies.AnyAsync(d =>
                d.TaskItemId == blockedTaskId.Value &&
                d.DependsOnTaskItemId == blockingTaskId.Value);
            if (duplicateExists)
                return Results.Conflict(new { message = "This task dependency already exists." });

            var projectDependencies = await db.TaskDependencies
                .Where(d => d.ProjectId == projectId)
                .AsNoTracking()
                .ToListAsync();

            if (WouldCreateTaskDependencyCycle(blockedTaskId.Value, blockingTaskId.Value, projectDependencies))
                return Results.UnprocessableEntity(new { message = "This dependency would create a cycle." });

            var dependency = new TaskDependency
            {
                ProjectId = projectId,
                TaskItemId = blockedTaskId.Value,
                DependsOnTaskItemId = blockingTaskId.Value,
                Type = type
            };

            db.TaskDependencies.Add(dependency);
            await db.SaveChangesAsync();

            return Results.Created($"/api/task-dependencies/{dependency.Id}", MapTaskDependency(dependency, blockedTask.Name, blockingTask.Name));
        });

        // DELETE /api/task-dependencies/{id} — delete a dependency
        app.MapDelete("/api/task-dependencies/{id:int}", async (int id, AppDbContext db) =>
        {
            var dependency = await db.TaskDependencies.FindAsync(id);
            if (dependency is null)
                return Results.NotFound(new { message = $"Task dependency {id} not found." });

            db.TaskDependencies.Remove(dependency);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    // ── Subtask Endpoints ──────────────────────────────────────────────

    private static void MapSubtaskEndpoints(this WebApplication app)
    {
        // GET /api/tasks/{taskId}/subtasks — list
        app.MapGet("/api/tasks/{taskId:int}/subtasks", async (int taskId, AppDbContext db) =>
        {
            var taskExists = await db.Tasks.AnyAsync(t => t.Id == taskId);
            if (!taskExists)
                return Results.NotFound(new { message = $"Task {taskId} not found." });

            var subtasks = await db.Subtasks
                .Where(s => s.TaskItemId == taskId)
                .AsNoTracking()
                .OrderBy(s => s.SortOrder)
                .Select(s => new
                {
                    s.Id,
                    s.TaskItemId,
                    s.ParentSubtaskId,
                    s.Text,
                    s.Completed,
                    s.SortOrder,
                    s.CreatedAt
                })
                .ToListAsync();

            return Results.Ok(subtasks);
        });

        // POST /api/tasks/{taskId}/subtasks — create (optionally nested under parentSubtaskId)
        app.MapPost("/api/tasks/{taskId:int}/subtasks", async (int taskId, SubtaskDto dto, AppDbContext db) =>
        {
            var taskExists = await db.Tasks.AnyAsync(t => t.Id == taskId);
            if (!taskExists)
                return Results.NotFound(new { message = $"Task {taskId} not found." });

            if (string.IsNullOrWhiteSpace(dto.Text))
                return Results.UnprocessableEntity(new { message = "Text is required." });

            // Validate parent subtask if provided
            if (dto.ParentSubtaskId.HasValue)
            {
                var parentExists = await db.Subtasks.AnyAsync(s => s.Id == dto.ParentSubtaskId.Value && s.TaskItemId == taskId);
                if (!parentExists)
                    return Results.NotFound(new { message = $"Parent subtask {dto.ParentSubtaskId.Value} not found in task {taskId}." });
            }

            var siblingQuery = dto.ParentSubtaskId.HasValue
                ? db.Subtasks.Where(s => s.ParentSubtaskId == dto.ParentSubtaskId.Value)
                : db.Subtasks.Where(s => s.TaskItemId == taskId && s.ParentSubtaskId == null);

            var maxSort = await siblingQuery.MaxAsync(s => (int?)s.SortOrder) ?? -1;

            var subtask = new Subtask
            {
                TaskItemId = taskId,
                ParentSubtaskId = dto.ParentSubtaskId,
                Text = dto.Text.Trim(),
                Completed = dto.Completed ?? false,
                SortOrder = dto.SortOrder ?? maxSort + 1
            };

            db.Subtasks.Add(subtask);
            await db.SaveChangesAsync();

            return Results.Created($"/api/subtasks/{subtask.Id}", new
            {
                subtask.Id,
                subtask.TaskItemId,
                subtask.ParentSubtaskId,
                subtask.Text,
                subtask.Completed,
                subtask.SortOrder,
                subtask.CreatedAt
            });
        });

        // PUT /api/subtasks/{id} — update
        app.MapPut("/api/subtasks/{id:int}", async (int id, SubtaskDto dto, AppDbContext db) =>
        {
            var subtask = await db.Subtasks.FindAsync(id);
            if (subtask is null)
                return Results.NotFound(new { message = $"Subtask {id} not found." });

            if (dto.Text is not null)
            {
                if (string.IsNullOrWhiteSpace(dto.Text))
                    return Results.UnprocessableEntity(new { message = "Text cannot be empty." });
                subtask.Text = dto.Text.Trim();
            }
            if (dto.Completed.HasValue) subtask.Completed = dto.Completed.Value;
            if (dto.SortOrder.HasValue) subtask.SortOrder = dto.SortOrder.Value;
            if (dto.ParentSubtaskId.HasValue)
            {
                var newParentSubtaskId = dto.ParentSubtaskId.Value == 0 ? (int?)null : dto.ParentSubtaskId.Value;
                if (newParentSubtaskId == id)
                    return Results.UnprocessableEntity(new { message = "A subtask cannot be its own parent." });

                if (newParentSubtaskId.HasValue)
                {
                    var allTaskSubtasks = await db.Subtasks
                        .Where(s => s.TaskItemId == subtask.TaskItemId)
                        .AsNoTracking()
                        .ToListAsync();

                    var parentExists = allTaskSubtasks.Any(s => s.Id == newParentSubtaskId.Value);
                    if (!parentExists)
                        return Results.NotFound(new { message = $"Parent subtask {newParentSubtaskId.Value} not found in task {subtask.TaskItemId}." });

                    if (WouldCreateSubtaskCycle(id, newParentSubtaskId.Value, allTaskSubtasks))
                        return Results.UnprocessableEntity(new { message = "A subtask cannot be moved under one of its descendants." });
                }

                subtask.ParentSubtaskId = newParentSubtaskId;
            }

            await db.SaveChangesAsync();

            return Results.Ok(new
            {
                subtask.Id,
                subtask.TaskItemId,
                subtask.ParentSubtaskId,
                subtask.Text,
                subtask.Completed,
                subtask.SortOrder,
                subtask.CreatedAt
            });
        });

        // DELETE /api/subtasks/{id} — delete
        app.MapDelete("/api/subtasks/{id:int}", async (int id, AppDbContext db, CancellationToken ct) =>
        {
            var exists = await db.Subtasks.AnyAsync(s => s.Id == id, ct);
            if (!exists)
                return Results.NotFound(new { message = $"Subtask {id} not found." });

            await KanbanFlow.Services.ProjectGraphDeleter.DeleteSubtaskTreeAsync(db, id, ct);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    // ── Acceptance Criterion Endpoints ─────────────────────────────────────────

    private static void MapAcceptanceCriterionEndpoints(this WebApplication app)
    {
        // GET /api/tasks/{taskId}/acceptance-criteria — list acceptance criteria for a task
        app.MapGet("/api/tasks/{taskId:int}/acceptance-criteria", async (int taskId, AppDbContext db) =>
        {
            var taskExists = await db.Tasks.AnyAsync(t => t.Id == taskId);
            if (!taskExists)
                return Results.NotFound(new { message = $"Task {taskId} not found." });

            var criteria = await db.AcceptanceCriteria
                .Where(c => c.TaskItemId == taskId)
                .AsNoTracking()
                .OrderBy(c => c.SortOrder)
                .Select(c => new
                {
                    c.Id,
                    c.TaskItemId,
                    c.Text,
                    c.Completed,
                    c.SortOrder,
                    c.CreatedAt,
                    c.UpdatedAt
                })
                .ToListAsync();

            return Results.Ok(criteria);
        });

        // POST /api/tasks/{taskId}/acceptance-criteria — create
        app.MapPost("/api/tasks/{taskId:int}/acceptance-criteria", async (int taskId, AcceptanceCriterionDto dto, AppDbContext db) =>
        {
            var taskExists = await db.Tasks.AnyAsync(t => t.Id == taskId);
            if (!taskExists)
                return Results.NotFound(new { message = $"Task {taskId} not found." });

            if (string.IsNullOrWhiteSpace(dto.Text))
                return Results.UnprocessableEntity(new { message = "Text is required." });

            var maxSort = await db.AcceptanceCriteria
                .Where(c => c.TaskItemId == taskId)
                .MaxAsync(c => (int?)c.SortOrder) ?? -1;

            var criterion = new AcceptanceCriterion
            {
                TaskItemId = taskId,
                Text = dto.Text.Trim(),
                Completed = dto.Completed ?? false,
                SortOrder = dto.SortOrder ?? maxSort + 1
            };

            db.AcceptanceCriteria.Add(criterion);
            await db.SaveChangesAsync();

            return Results.Created($"/api/acceptance-criteria/{criterion.Id}", new
            {
                criterion.Id,
                criterion.TaskItemId,
                criterion.Text,
                criterion.Completed,
                criterion.SortOrder,
                criterion.CreatedAt,
                criterion.UpdatedAt
            });
        });

        // PUT /api/acceptance-criteria/{id} — update
        app.MapPut("/api/acceptance-criteria/{id:int}", async (int id, AcceptanceCriterionDto dto, AppDbContext db) =>
        {
            var criterion = await db.AcceptanceCriteria.FindAsync(id);
            if (criterion is null)
                return Results.NotFound(new { message = $"Acceptance criterion {id} not found." });

            if (dto.Text is not null)
            {
                if (string.IsNullOrWhiteSpace(dto.Text))
                    return Results.UnprocessableEntity(new { message = "Text cannot be empty." });
                criterion.Text = dto.Text.Trim();
            }
            if (dto.Completed.HasValue) criterion.Completed = dto.Completed.Value;
            if (dto.SortOrder.HasValue) criterion.SortOrder = dto.SortOrder.Value;
            criterion.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync();

            return Results.Ok(new
            {
                criterion.Id,
                criterion.TaskItemId,
                criterion.Text,
                criterion.Completed,
                criterion.SortOrder,
                criterion.CreatedAt,
                criterion.UpdatedAt
            });
        });

        // DELETE /api/acceptance-criteria/{id} — delete
        app.MapDelete("/api/acceptance-criteria/{id:int}", async (int id, AppDbContext db) =>
        {
            var criterion = await db.AcceptanceCriteria.FindAsync(id);
            if (criterion is null)
                return Results.NotFound(new { message = $"Acceptance criterion {id} not found." });

            db.AcceptanceCriteria.Remove(criterion);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    // ── Requirement Endpoints ─────────────────────────────────────────

    private static void MapRequirementEndpoints(this WebApplication app)
    {
        // GET /api/projects/{projectId}/requirements — list all requirements for a project
        app.MapGet("/api/projects/{projectId:int}/requirements", async (int projectId, AppDbContext db) =>
        {
            var projectExists = await db.Projects.AnyAsync(p => p.Id == projectId);
            if (!projectExists)
                return Results.NotFound(new { message = $"Project {projectId} not found." });

            var requirements = await db.Requirements
                .Where(r => r.ProjectId == projectId)
                .Include(r => r.Feature)
                .Include(r => r.TaskItem)
                .AsNoTracking()
                .OrderBy(r => r.SortOrder)
                .ToListAsync();

            return Results.Ok(requirements.Select(r => new
            {
                r.Id,
                r.ProjectId,
                r.Text,
                r.Description,
                Priority = r.Priority.ToString(),
                Status = r.Status.ToString(),
                r.FeatureId,
                FeatureName = r.Feature?.Name,
                r.TaskItemId,
                TaskItemName = r.TaskItem?.Name,
                r.SortOrder,
                r.CreatedAt,
                r.UpdatedAt
            }));
        });

        // POST /api/projects/{projectId}/requirements — create
        app.MapPost("/api/projects/{projectId:int}/requirements", async (int projectId, RequirementDto dto, AppDbContext db) =>
        {
            var projectExists = await db.Projects.AnyAsync(p => p.Id == projectId);
            if (!projectExists)
                return Results.NotFound(new { message = $"Project {projectId} not found." });

            if (string.IsNullOrWhiteSpace(dto.Text))
                return Results.UnprocessableEntity(new { message = "Text is required." });

            RequirementPriority priority = RequirementPriority.P1;
            if (dto.Priority is not null && !TryParseDefinedEnum<RequirementPriority>(dto.Priority, out priority))
                return Results.UnprocessableEntity(new { message = $"Invalid priority: {dto.Priority}. Valid values: {string.Join(", ", Enum.GetNames<RequirementPriority>())}" });

            RequirementStatus status = RequirementStatus.Open;
            if (dto.Status is not null && !TryParseDefinedEnum<RequirementStatus>(dto.Status, out status))
                return Results.UnprocessableEntity(new { message = $"Invalid status: {dto.Status}. Valid values: {string.Join(", ", Enum.GetNames<RequirementStatus>())}" });

            // Validate feature/task links exist AND belong to this project (#82). Missing → 404,
            // cross-project → 422 (mirrors the CrossProjectReassignmentException convention).
            var linkValidation = await ValidateRequirementLinksScopedAsync(projectId, dto.FeatureId, dto.TaskItemId, db);
            if (MapRequirementLinkError(linkValidation) is { } linkError)
                return linkError;

            var maxSort = await db.Requirements
                .Where(r => r.ProjectId == projectId)
                .MaxAsync(r => (int?)r.SortOrder) ?? -1;

            var requirement = new Requirement
            {
                ProjectId = projectId,
                Text = dto.Text.Trim(),
                Description = dto.Description?.Trim(),
                Priority = priority,
                Status = status,
                // Normalize the 0 "clear-link" sentinel to null so it matches the
                // validator (which skips 0) — persisting a raw 0 would violate the
                // Feature/Task FK (no entity has Id 0) and 500 (#82 regression).
                FeatureId = NormalizeLink(dto.FeatureId),
                TaskItemId = NormalizeLink(dto.TaskItemId),
                SortOrder = dto.SortOrder ?? maxSort + 1
            };

            db.Requirements.Add(requirement);
            await db.SaveChangesAsync();

            return Results.Created($"/api/requirements/{requirement.Id}", new
            {
                requirement.Id,
                requirement.ProjectId,
                requirement.Text,
                requirement.Description,
                Priority = requirement.Priority.ToString(),
                Status = requirement.Status.ToString(),
                requirement.FeatureId,
                requirement.TaskItemId,
                requirement.SortOrder,
                requirement.CreatedAt,
                requirement.UpdatedAt
            });
        });

        // PUT /api/requirements/{id} — update
        app.MapPut("/api/requirements/{id:int}", async (int id, RequirementDto dto, AppDbContext db) =>
        {
            var requirement = await db.Requirements.FindAsync(id);
            if (requirement is null)
                return Results.NotFound(new { message = $"Requirement {id} not found." });

            if (string.IsNullOrWhiteSpace(dto.Text))
                return Results.UnprocessableEntity(new { message = "Text is required." });

            // Parse each enum ONCE at validation time and reuse the captured value below (#153 — the
            // handler previously validated with `out _` then RE-PARSED via Enum.Parse). TryParseDefinedEnum
            // pairs Enum.TryParse with Enum.IsDefined so numeric strings like "99" are rejected (#205),
            // matching the create path and the task endpoints.
            RequirementPriority? newPriority = null;
            if (dto.Priority is not null)
            {
                if (!TryParseDefinedEnum<RequirementPriority>(dto.Priority, out var parsedPriority))
                    return Results.UnprocessableEntity(new { message = $"Invalid priority: {dto.Priority}. Valid values: {string.Join(", ", Enum.GetNames<RequirementPriority>())}" });
                newPriority = parsedPriority;
            }

            RequirementStatus? newStatus = null;
            if (dto.Status is not null)
            {
                if (!TryParseDefinedEnum<RequirementStatus>(dto.Status, out var parsedStatus))
                    return Results.UnprocessableEntity(new { message = $"Invalid status: {dto.Status}. Valid values: {string.Join(", ", Enum.GetNames<RequirementStatus>())}" });
                newStatus = parsedStatus;
            }

            // Validate feature/task links exist AND belong to this requirement's project (#82).
            // PUT previously applied NO link validation, so it could point a requirement at a
            // non-existent or cross-project feature/task — now identical to POST/PATCH.
            // Missing → 404, cross-project → 422.
            var linkValidation = await ValidateRequirementLinksScopedAsync(requirement.ProjectId, dto.FeatureId, dto.TaskItemId, db);
            if (MapRequirementLinkError(linkValidation) is { } linkError)
                return linkError;

            // Partial update mirrors PUT /api/projects/{id} (#203): only overwrite a field the caller
            // actually sent, and bump UpdatedAt only when something truly changed so a no-op PUT (identical
            // values resent) doesn't churn state. An omitted Description (null) is PRESERVED; an explicit
            // empty/whitespace value clears it (#260) — single-sourced via NormalizeOptionalText. Enums
            // apply only when explicitly provided; 0 clears a link (→ null) via NormalizeLink (#82).
            var changed = false;
            var trimmedText = dto.Text.Trim();
            if (!string.Equals(requirement.Text, trimmedText, StringComparison.Ordinal)) { requirement.Text = trimmedText; changed = true; }
            var nextDescription = NormalizeOptionalText(dto.Description, requirement.Description);
            if (!string.Equals(requirement.Description, nextDescription, StringComparison.Ordinal)) { requirement.Description = nextDescription; changed = true; }
            if (newPriority is not null && requirement.Priority != newPriority.Value) { requirement.Priority = newPriority.Value; changed = true; }
            if (newStatus is not null && requirement.Status != newStatus.Value) { requirement.Status = newStatus.Value; changed = true; }
            if (dto.FeatureId.HasValue) { var next = NormalizeLink(dto.FeatureId); if (requirement.FeatureId != next) { requirement.FeatureId = next; changed = true; } }
            if (dto.TaskItemId.HasValue) { var next = NormalizeLink(dto.TaskItemId); if (requirement.TaskItemId != next) { requirement.TaskItemId = next; changed = true; } }
            if (dto.SortOrder.HasValue && requirement.SortOrder != dto.SortOrder.Value) { requirement.SortOrder = dto.SortOrder.Value; changed = true; }
            if (changed) requirement.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync();

            return Results.Ok(new
            {
                requirement.Id,
                requirement.ProjectId,
                requirement.Text,
                requirement.Description,
                Priority = requirement.Priority.ToString(),
                Status = requirement.Status.ToString(),
                requirement.FeatureId,
                requirement.TaskItemId,
                requirement.SortOrder,
                requirement.CreatedAt,
                requirement.UpdatedAt
            });
        });

        // DELETE /api/requirements/{id} — delete
        app.MapDelete("/api/requirements/{id:int}", async (int id, AppDbContext db) =>
        {
            var requirement = await db.Requirements.FindAsync(id);
            if (requirement is null)
                return Results.NotFound(new { message = $"Requirement {id} not found." });

            db.Requirements.Remove(requirement);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // PATCH /api/requirements/{id}/assign — assign to feature or task
        app.MapPatch("/api/requirements/{id:int}/assign", async (int id, RequirementAssignDto dto, AppDbContext db) =>
        {
            var requirement = await db.Requirements.FindAsync(id);
            if (requirement is null)
                return Results.NotFound(new { message = $"Requirement {id} not found." });

            var before = RequirementActivitySnapshot(requirement);

            // Validate feature/task links exist AND belong to this requirement's project (#82)
            // before mutating. A value of 0 clears the link and is skipped by the validator.
            // Missing → 404, cross-project → 422.
            var linkValidation = await ValidateRequirementLinksScopedAsync(requirement.ProjectId, dto.FeatureId, dto.TaskItemId, db);
            if (MapRequirementLinkError(linkValidation) is { } linkError)
                return linkError;

            // 0 clears the link (→ null); a non-zero id was validated above. Same rule as the
            // validator-skip and the create/update paths — single-sourced via NormalizeLink (#82).
            if (dto.FeatureId.HasValue)
                requirement.FeatureId = NormalizeLink(dto.FeatureId);

            if (dto.TaskItemId.HasValue)
                requirement.TaskItemId = NormalizeLink(dto.TaskItemId);

            requirement.UpdatedAt = DateTime.UtcNow;
            // Always emit as a Requirement-typed event so entityType matches the
            // RequirementActivitySnapshot shape. Previously this wrote entityType="Task"/entityId=taskId
            // when assigned to a task, dropping a Requirement-shaped snapshot into the task's activity
            // stream (a different schema than every other "Task" event). The snapshot already carries
            // TaskItemId/FeatureId for consumers that want the link. (#186)
            AddActivityEvent(db, requirement.ProjectId, "Requirement", requirement.Id, null, "RequirementAssigned", before, RequirementActivitySnapshot(requirement), $"Requirement '{requirement.Text}' assignment updated");
            await db.SaveChangesAsync();

            return Results.Ok(new
            {
                requirement.Id,
                requirement.ProjectId,
                requirement.Text,
                requirement.FeatureId,
                requirement.TaskItemId,
                requirement.UpdatedAt
            });
        });
    }

    // ── TaskComment Endpoints ────────────────────────────────────────────

    private static void MapTaskCommentEndpoints(this WebApplication app)
    {
        // GET /api/tasks/{taskId}/comments — list comments/activity
        app.MapGet("/api/tasks/{taskId:int}/comments", async (int taskId, AppDbContext db) =>
        {
            var taskExists = await db.Tasks.AnyAsync(t => t.Id == taskId);
            if (!taskExists)
                return Results.NotFound(new { message = $"Task {taskId} not found." });

            var comments = await db.TaskComments
                .Where(c => c.TaskItemId == taskId)
                .AsNoTracking()
                .OrderBy(c => c.CreatedAt)
                .Select(c => new
                {
                    c.Id,
                    c.TaskItemId,
                    c.Text,
                    c.Author,
                    Type = c.Type.ToString(),
                    c.CreatedAt
                })
                .ToListAsync();

            return Results.Ok(comments);
        });

        // POST /api/tasks/{taskId}/comments — add comment.
        // Routes through ITaskService.AddCommentAsync so the TaskCommentAdded audit event
        // is emitted uniformly with the Blazor comment writers (#88). Comment-type parsing
        // stays at the edge so the {message, allowedTypes} 422 body remains byte-identical;
        // the typed CommentType is then handed to the service (R3).
        app.MapPost("/api/tasks/{taskId:int}/comments", async (int taskId, TaskCommentDto dto, Services.ITaskService taskSvc, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(dto.Text))
                return Results.UnprocessableEntity(new { message = "Text is required." });

            if (!TryParseCommentType(dto.Type, out var commentType))
            {
                return Results.UnprocessableEntity(new
                {
                    message = $"Invalid comment type '{dto.Type}'. Allowed values: {string.Join(", ", AllowedUserCommentTypes)}.",
                    allowedTypes = AllowedUserCommentTypes
                });
            }

            try
            {
                var comment = await taskSvc.AddCommentAsync(taskId, dto.Text, dto.Author, commentType, actor: dto.Author, ct);
                return Results.Created($"/api/tasks/{taskId}/comments/{comment.Id}", new
                {
                    comment.Id,
                    comment.TaskItemId,
                    comment.Text,
                    comment.Author,
                    Type = comment.Type.ToString(),
                    comment.CreatedAt
                });
            }
            catch (Services.TaskNotFoundException)
            {
                return Results.NotFound(new { message = $"Task {taskId} not found." });
            }
            catch (Services.ValidationException ex)
            {
                return Results.UnprocessableEntity(new { message = ex.Message });
            }
        });

        // DELETE /api/comments/{id}
        app.MapDelete("/api/comments/{id:int}", async (int id, AppDbContext db) =>
        {
            var comment = await db.TaskComments.FindAsync(id);
            if (comment is null)
                return Results.NotFound(new { message = $"Comment {id} not found." });

            db.TaskComments.Remove(comment);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    // ── Helper ─────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions ActivityJsonOptions = new(JsonSerializerDefaults.Web);

    private static object MapActivityEvent(ActivityEvent e) => new
    {
        e.Id,
        e.ProjectId,
        e.EntityType,
        e.EntityId,
        e.Actor,
        e.EventType,
        e.BeforeJson,
        e.AfterJson,
        e.Message,
        e.CreatedAt
    };

    private static void AddActivityEvent(AppDbContext db, int projectId, string entityType, int entityId, string? actor, string eventType, object? before, object? after, string? message)
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

    private static object RequirementActivitySnapshot(Requirement requirement) => new
    {
        requirement.Id,
        requirement.ProjectId,
        requirement.Text,
        requirement.FeatureId,
        requirement.TaskItemId,
        Status = requirement.Status.ToString(),
        Priority = requirement.Priority.ToString()
    };

    /// <summary>
    /// Validates that a requirement's optional Feature/Task links exist AND belong to the
    /// SAME project as the requirement (#82). A linked Feature is checked against its
    /// <see cref="Feature.ProjectId"/>; a linked TaskItem has no direct ProjectId, so its
    /// project is its parent <see cref="Feature.ProjectId"/>. A value of 0 means "clear the
    /// link" and is skipped here (the caller maps 0 → null).
    /// </summary>
    /// <returns>
    /// A <see cref="RequirementLinkValidation"/> distinguishing the outcome so callers can map it
    /// to the right HTTP status: a missing entity → <c>404 NotFound</c>; an entity that exists but
    /// lives in another project → <c>422 UnprocessableEntity</c> (matching the
    /// <c>CrossProjectReassignmentException</c> precedent for the task-move endpoint). Both error
    /// kinds carry an actionable message; <see cref="RequirementLinkValidation.Valid"/> means OK.
    /// </returns>
    internal static async Task<RequirementLinkValidation> ValidateRequirementLinksScopedAsync(
        int projectId, int? featureId, int? taskItemId, AppDbContext db)
    {
        if (featureId.HasValue && featureId.Value != 0)
        {
            // Project ids are positive autoincrement values, so a null projection means "no such feature".
            var featureProjectId = await db.Features
                .Where(f => f.Id == featureId.Value)
                .Select(f => (int?)f.ProjectId)
                .FirstOrDefaultAsync();
            if (featureProjectId is null)
                return RequirementLinkValidation.NotFound($"Feature {featureId.Value} not found.");
            if (featureProjectId.Value != projectId)
                return RequirementLinkValidation.CrossProject($"Feature {featureId.Value} does not belong to project {projectId}.");
        }

        if (taskItemId.HasValue && taskItemId.Value != 0)
        {
            // A TaskItem has no ProjectId of its own — its project is its parent Feature's project.
            var taskProjectId = await db.Tasks
                .Where(t => t.Id == taskItemId.Value)
                .Select(t => (int?)t.Feature.ProjectId)
                .FirstOrDefaultAsync();
            if (taskProjectId is null)
                return RequirementLinkValidation.NotFound($"Task {taskItemId.Value} not found.");
            if (taskProjectId.Value != projectId)
                return RequirementLinkValidation.CrossProject($"Task {taskItemId.Value} does not belong to project {projectId}.");
        }

        return RequirementLinkValidation.Valid;
    }

    /// <summary>
    /// Maps a <see cref="RequirementLinkValidation"/> error to the HTTP result the requirement
    /// endpoints return, keeping the create/update/assign paths DRY and on the same status
    /// convention: <c>404 NotFound</c> for a missing entity, <c>422 UnprocessableEntity</c> for an
    /// entity that exists but belongs to another project (mirrors <c>CrossProjectReassignmentException</c>
    /// at the task-move endpoint). Returns <c>null</c> when the validation passed.
    /// </summary>
    private static IResult? MapRequirementLinkError(RequirementLinkValidation validation) => validation.Kind switch
    {
        RequirementLinkErrorKind.NotFound => Results.NotFound(new { message = validation.Message }),
        RequirementLinkErrorKind.CrossProject => Results.UnprocessableEntity(new { message = validation.Message }),
        _ => null
    };

    /// <summary>
    /// Normalizes a requirement Feature/Task link value for persistence: both <c>null</c> and the
    /// <c>0</c> "clear-link" sentinel mean "no link" and become <c>null</c>; any other id is kept
    /// as-is. This MUST agree with <see cref="ValidateRequirementLinksScopedAsync"/>, which skips
    /// <c>0</c> during validation — so the create/update/assign paths persist <c>null</c> rather
    /// than a raw <c>0</c> that would violate the Feature/Task foreign key (no entity has Id 0) and
    /// surface as a 500 (#82 regression).
    /// </summary>
    internal static int? NormalizeLink(int? id) => id is null or 0 ? null : id;

    /// <summary>
    /// Parses <paramref name="raw"/> to <typeparamref name="TEnum"/> case-insensitively, rejecting
    /// null/blank AND undefined values. Enum.TryParse alone accepts any in-range numeric string
    /// (e.g. "99"), so it is paired with Enum.IsDefined — mirroring the task-endpoint validation (#89/#205).
    /// </summary>
    internal static bool TryParseDefinedEnum<TEnum>(string? raw, out TEnum value) where TEnum : struct, Enum
    {
        value = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        return Enum.TryParse(raw, ignoreCase: true, out value) && Enum.IsDefined(value);
    }

    /// <summary>
    /// Applies a partial-update string field: an omitted value (<paramref name="incoming"/> is null)
    /// PRESERVES <paramref name="current"/>; an explicit empty/whitespace value CLEARS the column to
    /// null; otherwise the trimmed value is used. Single-sources the #203/#260 partial-update rule —
    /// a rename that omits the field must NOT wipe it — so it stays consistent and unit-testable.
    /// </summary>
    internal static string? NormalizeOptionalText(string? incoming, string? current) =>
        incoming is null ? current : (string.IsNullOrWhiteSpace(incoming) ? null : incoming.Trim());

    private static object MapTaskSummary(TaskItem t) => new
    {
        t.Id,
        t.FeatureId,
        t.Name,
        t.Description,
        Status = t.Status.ToString(),
        Priority = t.Priority.ToString(),
        t.Assignee,
        t.SortOrder,
        t.CreatedAt,
        t.UpdatedAt,
        t.CompletedAt,
        SubtaskCount = t.Subtasks.Count,
        SubtaskDoneCount = t.Subtasks.Count(s => s.Completed),
        AcceptanceCriteriaCount = t.AcceptanceCriteria.Count,
        AcceptanceCriteriaDoneCount = t.AcceptanceCriteria.Count(c => c.Completed)
    };

    // Idea list/single/create/update projection. Field set deliberately matches
    // IdeaService.IdeaActivitySnapshot so REST output and audit snapshots stay aligned.
    private static object MapIdea(Idea idea) => new
    {
        idea.Id,
        idea.Title,
        idea.Body,
        idea.Plan,
        status = idea.Status.ToString(),
        idea.Tags,
        priority = idea.Priority?.ToString(),   // nullable enum
        idea.PromotedProjectId,
        idea.CreatedAt,
        idea.UpdatedAt
    };

    // Memory node projection (#224). Enums → strings (no JsonStringEnumConverter in this repo);
    // vector bytes are never serialized — presence + shape only, matching MemoryService's
    // audit-snapshot convention.
    private static object MapMemoryNode(MemoryNode node) => new
    {
        node.Id,
        Type = node.Type.ToString(),
        Scope = node.Scope.ToString(),
        node.ScopeKey,
        node.Content,
        node.Tags,
        node.Source,
        node.Confidence,
        node.CreatedAt,
        node.ValidFrom,
        node.ValidTo,
        node.SupersededById,
        node.AccessCount,
        node.LastAccessedAt,
        HasEmbedding = node.EmbeddingBlob is not null,
        node.EmbeddingDim
    };

    private static readonly string[] AllowedUserCommentTypes =
    [
        nameof(CommentType.Note),
        nameof(CommentType.Decision),
        nameof(CommentType.Question),
        nameof(CommentType.Blocked),
        nameof(CommentType.Handoff),
        nameof(CommentType.Verification),
        nameof(CommentType.Comment)
    ];

    private static bool TryParseCommentType(string? value, out CommentType commentType)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            commentType = CommentType.Note;
            return true;
        }

        if (Enum.TryParse<CommentType>(value.Trim(), ignoreCase: true, out var parsed) &&
            AllowedUserCommentTypes.Any(type => string.Equals(type, parsed.ToString(), StringComparison.Ordinal)))
        {
            commentType = parsed;
            return true;
        }

        commentType = default;
        return false;
    }

    private static object MapTaskDependency(TaskDependency d) => MapTaskDependency(d, d.TaskItem.Name, d.DependsOnTaskItem.Name);

    private static object MapTaskDependency(TaskDependency d, string? taskName, string? dependsOnTaskName) => new
    {
        d.Id,
        d.ProjectId,
        d.TaskItemId,
        TaskName = taskName,
        d.DependsOnTaskItemId,
        DependsOnTaskName = dependsOnTaskName,
        BlockedTaskId = d.TaskItemId,
        BlockingTaskId = d.DependsOnTaskItemId,
        Type = d.Type.ToString(),
        d.CreatedAt
    };

    private static (int? BlockedTaskId, int? BlockingTaskId, string MissingTaskMessage) ResolveDependencyTaskIds(int routeTaskId, TaskDependencyDto dto, TaskDependencyType type)
    {
        if (type == TaskDependencyType.Blocks)
        {
            // For Blocks, the route task is the blocker; require the blocked task via explicit blocked aliases.
            // Do not treat DependsOnTaskItemId as a blocked-task alias because that inverts the relationship.
            var blockedTaskId = dto.BlockedTaskId ?? dto.TaskItemId;
            return (blockedTaskId, routeTaskId, "BlockedTaskId or TaskItemId is required when type is Blocks.");
        }

        // For DependsOn, the route task is blocked by another task; accept the blocking-task aliases only.
        var blockingTaskId = dto.DependsOnTaskItemId ?? dto.BlockingTaskItemId ?? dto.BlockingTaskId;
        return (routeTaskId, blockingTaskId, "DependsOnTaskItemId, BlockingTaskItemId, or BlockingTaskId is required.");
    }

    private static bool WouldCreateTaskDependencyCycle(int blockedTaskId, int blockingTaskId, IReadOnlyCollection<TaskDependency> projectDependencies)
    {
        var dependenciesByBlockedTask = projectDependencies.ToLookup(d => d.TaskItemId, d => d.DependsOnTaskItemId);
        var visited = new HashSet<int>();

        bool HasPath(int currentTaskId, int targetTaskId)
        {
            if (currentTaskId == targetTaskId)
                return true;

            if (!visited.Add(currentTaskId))
                return false;

            foreach (var nextTaskId in dependenciesByBlockedTask[currentTaskId])
            {
                if (HasPath(nextTaskId, targetTaskId))
                    return true;
            }

            return false;
        }

        return HasPath(blockingTaskId, blockedTaskId);
    }

    private static bool WouldCreateFeatureCycle(int featureId, int proposedParentFeatureId, IReadOnlyCollection<Feature> allProjectFeatures)
    {
        var visited = new HashSet<int>();
        var currentParentId = proposedParentFeatureId;

        while (visited.Add(currentParentId))
        {
            if (currentParentId == featureId)
                return true;

            var current = allProjectFeatures.FirstOrDefault(f => f.Id == currentParentId);
            if (current?.ParentFeatureId is not int nextParentId)
                return false;

            currentParentId = nextParentId;
        }

        return false;
    }

    private static bool WouldCreateSubtaskCycle(int subtaskId, int proposedParentSubtaskId, IReadOnlyCollection<Subtask> allTaskSubtasks)
    {
        var visited = new HashSet<int>();
        var currentParentId = proposedParentSubtaskId;

        while (visited.Add(currentParentId))
        {
            if (currentParentId == subtaskId)
                return true;

            var current = allTaskSubtasks.FirstOrDefault(s => s.Id == currentParentId);
            if (current?.ParentSubtaskId is not int nextParentId)
                return false;

            currentParentId = nextParentId;
        }

        return false;
    }

    private static object MapTaskClaimState(TaskItem task) => new
    {
        task.Id,
        task.Name,
        task.ClaimedBy,
        task.ClaimedAt,
        task.UpdatedAt
    };

    private static async Task<TaskClaimState?> GetTaskClaimStateOrDefaultAsync(AppDbContext db, int id)
    {
        return await db.Tasks
            .Where(task => task.Id == id)
            .Select(task => new TaskClaimState(task.Id, task.Name, task.ClaimedBy, task.ClaimedAt, task.UpdatedAt))
            .SingleOrDefaultAsync();
    }

    private sealed record TaskClaimState(int Id, string Name, string? ClaimedBy, DateTime? ClaimedAt, DateTime UpdatedAt);

    private static string BuildBlockedTaskReason(TaskItem task, IReadOnlyCollection<TaskItem> unfinishedBlockers)
    {
        var blockerNames = unfinishedBlockers
            .OrderBy(t => t.SortOrder)
            .Select(t => $"{t.Name} ({t.Status})");

        return $"{task.Priority} priority task is blocked until {string.Join(", ", blockerNames)} is Done.";
    }
}

// ── DTOs ───────────────────────────────────────────────────────────────

public record ProjectDto(string? Name, string? Description, string? Goals, string? Readme);

public record FeatureDto(string? Name, string? Description, int? SortOrder, int? ParentFeatureId);

public record TaskDto(
    string? Name,
    string? Description,
    string? Status,
    string? Priority,
    string? Assignee,
    int? SortOrder,
    string? Actor = null);

public record StatusDto(string? Status, string? Actor = null);

public record ClaimDto(string? Actor);

public record ReassignDto(int FeatureId, string? Actor = null);

public record SubtaskDto(string? Text, bool? Completed, int? SortOrder, int? ParentSubtaskId);

public record AcceptanceCriterionDto(string? Text, bool? Completed, int? SortOrder);

/// <summary>
/// The kind of failure produced by <see cref="ApiEndpoints.ValidateRequirementLinksScopedAsync"/>,
/// used to pick the HTTP status: <see cref="None"/> = valid; <see cref="NotFound"/> = the linked
/// Feature/Task does not exist (→ 404); <see cref="CrossProject"/> = it exists but belongs to a
/// different project (→ 422).
/// </summary>
internal enum RequirementLinkErrorKind
{
    None,
    NotFound,
    CrossProject
}

/// <summary>
/// Result of validating a requirement's Feature/Task links (#82). Distinguishes "not found" from
/// "exists but cross-project" so the create/update/assign endpoints can return the matching status
/// (404 vs 422) while sharing a single validator.
/// </summary>
internal readonly record struct RequirementLinkValidation(RequirementLinkErrorKind Kind, string? Message)
{
    /// <summary>The links are absent or in-project — no error.</summary>
    public static readonly RequirementLinkValidation Valid = new(RequirementLinkErrorKind.None, null);

    /// <summary>The linked Feature/Task does not exist (caller returns 404).</summary>
    public static RequirementLinkValidation NotFound(string message) => new(RequirementLinkErrorKind.NotFound, message);

    /// <summary>The linked Feature/Task exists but belongs to another project (caller returns 422).</summary>
    public static RequirementLinkValidation CrossProject(string message) => new(RequirementLinkErrorKind.CrossProject, message);

    /// <summary><c>true</c> when there is no error.</summary>
    public bool IsValid => Kind == RequirementLinkErrorKind.None;
}

public record RequirementDto(string? Text, string? Description, string? Priority, string? Status, int? FeatureId, int? TaskItemId, int? SortOrder);

public record RequirementAssignDto(int? FeatureId, int? TaskItemId);

public record TaskCommentDto(string? Text, string? Author, string? Type);

public record TaskDependencyDto(int? TaskItemId, int? BlockedTaskId, int? DependsOnTaskItemId, int? BlockingTaskItemId, int? BlockingTaskId, string? Type);

// Idea inbox DTOs (#180). Priority/Status are freeform strings parsed at the edge:
// the repo has no JsonStringEnumConverter, so typed enum fields can't bind from JSON
// strings like "High"/"Triaging" (they'd 400). This mirrors the /api/tasks/{id}/status
// convention — accept the enum as a string and parse it manually.
public record IdeaCreateDto(string Title, string? Body, string? Plan, string? Tags, string? Priority, string? Actor);

public record IdeaUpdateDto(string? Title, string? Body, string? Plan, string? Status, string? Tags, string? Priority, string? Actor);

public record IdeaPromoteDto(string? Actor);

// Remember payload (#224). Type/Scope arrive as strings and are parsed at the edge (422 on a
// present-but-invalid value); null Type/Scope/ScopeKey take the documented defaults
// (Semantic / Agent / "knowledge-graph"). Actor is recorded in the audit trail by the service.
public record MemoryCreateDto(string Content, string? Type, string? Scope, string? ScopeKey, string? Tags, string? Source, double? Confidence, string? Actor);
