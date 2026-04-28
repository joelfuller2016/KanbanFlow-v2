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
        return app;
    }

    // ── Project Endpoints ──────────────────────────────────────────────

    private static void MapProjectEndpoints(this WebApplication app)
    {
        // GET /api/projects — list all with feature/task counts
        app.MapGet("/api/projects", async (AppDbContext db) =>
        {
            var projects = await db.Projects
                .Include(p => p.Features)
                    .ThenInclude(f => f.Tasks)
                .AsNoTracking()
                .ToListAsync();

            return Results.Ok(projects.Select(p => new
            {
                p.Id,
                p.Name,
                p.Description,
                p.Goals,
                p.Readme,
                p.CreatedAt,
                p.UpdatedAt,
                FeatureCount = p.Features.Count,
                TaskCount = p.Features.Sum(f => f.Tasks.Count)
            }));
        });

        // GET /api/projects/{id} — get one with features + task counts
        app.MapGet("/api/projects/{id:int}", async (int id, AppDbContext db) =>
        {
            var project = await db.Projects
                .Include(p => p.Features)
                    .ThenInclude(f => f.Tasks)
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == id);

            if (project is null)
                return Results.NotFound(new { message = $"Project {id} not found." });

            return Results.Ok(new
            {
                project.Id,
                project.Name,
                project.Description,
                project.Goals,
                project.Readme,
                project.CreatedAt,
                project.UpdatedAt,
                Features = project.Features.OrderBy(f => f.SortOrder).Select(f => new
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
            });
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

            object MapTaskContext(TaskItem task, Feature feature) => new
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
        app.MapGet("/api/projects/{id:int}/next-tasks", async (int id, int? limit, AppDbContext db) =>
        {
            var projectExists = await db.Projects.AnyAsync(p => p.Id == id);
            if (!projectExists)
                return Results.NotFound(new { message = $"Project {id} not found." });

            var take = Math.Clamp(limit ?? 5, 1, 25);

            var candidates = await db.Tasks
                .Include(t => t.Feature)
                .Where(t => t.Feature.ProjectId == id && t.Status != WorkStatus.Done)
                .AsNoTracking()
                .ToListAsync();

            var recommendations = candidates
                .OrderByDescending(t => PriorityRank(t.Priority))
                .ThenByDescending(t => StatusRank(t.Status))
                .ThenBy(t => t.UpdatedAt)
                .Take(take)
                .Select((task, index) => new
                {
                    Rank = index + 1,
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
                    task.UpdatedAt,
                    Reason = BuildNextTaskReason(task)
                });

            return Results.Ok(new
            {
                ProjectId = id,
                GeneratedAt = DateTime.UtcNow,
                Count = recommendations.Count(),
                Recommendations = recommendations
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

            project.Name = dto.Name.Trim();
            project.Description = dto.Description?.Trim();
            project.Goals = dto.Goals?.Trim();
            project.Readme = dto.Readme?.Trim();
            project.UpdatedAt = DateTime.UtcNow;

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

        // DELETE /api/projects/{id} — cascade delete
        app.MapDelete("/api/projects/{id:int}", async (int id, AppDbContext db) =>
        {
            var project = await db.Projects.FindAsync(id);
            if (project is null)
                return Results.NotFound(new { message = $"Project {id} not found." });

            await DeleteProjectChildrenAsync(db, id);
            db.Projects.Remove(project);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    // ── Activity Endpoints ─────────────────────────────────────────────

    private static void MapActivityEndpoints(this WebApplication app)
    {
        // GET /api/projects/{id}/activity — durable project event trail
        app.MapGet("/api/projects/{id:int}/activity", async (int id, int? limit, AppDbContext db) =>
        {
            var projectExists = await db.Projects.AnyAsync(p => p.Id == id);
            if (!projectExists)
                return Results.NotFound(new { message = $"Project {id} not found." });

            var take = Math.Clamp(limit ?? 100, 1, 500);
            var events = await db.ActivityEvents
                .Where(e => e.ProjectId == id)
                .AsNoTracking()
                .OrderByDescending(e => e.CreatedAt)
                .Take(take)
                .ToListAsync();

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
                Tasks = feature.Tasks.OrderBy(t => t.SortOrder).Select(t => new
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

            feature.Name = dto.Name.Trim();
            feature.Description = dto.Description?.Trim();
            if (dto.SortOrder.HasValue) feature.SortOrder = dto.SortOrder.Value;
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

                feature.ParentFeatureId = newParentFeatureId;
            }
            feature.UpdatedAt = DateTime.UtcNow;

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
        app.MapDelete("/api/features/{id:int}", async (int id, AppDbContext db) =>
        {
            var exists = await db.Features.AnyAsync(f => f.Id == id);
            if (!exists)
                return Results.NotFound(new { message = $"Feature {id} not found." });

            await DeleteFeatureTreeAsync(db, id);
            await db.SaveChangesAsync();
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
                .AsNoTracking()
                .OrderBy(t => t.SortOrder)
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
                .AsNoTracking()
                .OrderBy(t => t.SortOrder)
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

        // POST /api/features/{featureId}/tasks — create (default status: Backlog)
        app.MapPost("/api/features/{featureId:int}/tasks", async (int featureId, TaskDto dto, AppDbContext db) =>
        {
            var projectId = await db.Features
                .Where(f => f.Id == featureId)
                .Select(f => (int?)f.ProjectId)
                .SingleOrDefaultAsync();
            if (projectId is null)
                return Results.NotFound(new { message = $"Feature {featureId} not found." });

            if (string.IsNullOrWhiteSpace(dto.Name))
                return Results.UnprocessableEntity(new { message = "Name is required." });

            // Validate status enum
            WorkStatus status = WorkStatus.Backlog;
            if (dto.Status is not null)
            {
                if (!Enum.TryParse<WorkStatus>(dto.Status, ignoreCase: true, out status))
                    return Results.UnprocessableEntity(new { message = $"Invalid status: {dto.Status}. Valid values: {string.Join(", ", Enum.GetNames<WorkStatus>())}" });
            }

            // Validate priority enum
            TaskPriority priority = TaskPriority.Low;
            if (dto.Priority is not null)
            {
                if (!Enum.TryParse<TaskPriority>(dto.Priority, ignoreCase: true, out priority))
                    return Results.UnprocessableEntity(new { message = $"Invalid priority: {dto.Priority}. Valid values: {string.Join(", ", Enum.GetNames<TaskPriority>())}" });
            }

            var maxSort = await db.Tasks
                .Where(t => t.FeatureId == featureId)
                .MaxAsync(t => (int?)t.SortOrder) ?? -1;

            var task = new TaskItem
            {
                FeatureId = featureId,
                Name = dto.Name.Trim(),
                Description = dto.Description?.Trim(),
                Status = status,
                Priority = priority,
                Assignee = dto.Assignee?.Trim(),
                SortOrder = dto.SortOrder ?? maxSort + 1,
                CompletedAt = status == WorkStatus.Done ? DateTime.UtcNow : null
            };

            db.Tasks.Add(task);
            await db.SaveChangesAsync();

            var createdComment = new TaskComment
            {
                TaskItemId = task.Id,
                Text = "Task created",
                Author = "System",
                Type = CommentType.Created
            };
            db.TaskComments.Add(createdComment);
            AddActivityEvent(db, projectId.Value, "Task", task.Id, null, "TaskCreated", null, TaskActivitySnapshot(task), $"Task '{task.Name}' created");
            await db.SaveChangesAsync();

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
        });

        // PUT /api/tasks/{id} — update
        app.MapPut("/api/tasks/{id:int}", async (int id, TaskDto dto, AppDbContext db) =>
        {
            var task = await db.Tasks
                .Include(t => t.Feature)
                .FirstOrDefaultAsync(t => t.Id == id);
            if (task is null)
                return Results.NotFound(new { message = $"Task {id} not found." });

            if (string.IsNullOrWhiteSpace(dto.Name))
                return Results.UnprocessableEntity(new { message = "Name is required." });

            if (dto.Status is not null && !Enum.TryParse<WorkStatus>(dto.Status, ignoreCase: true, out _))
                return Results.UnprocessableEntity(new { message = $"Invalid status: {dto.Status}. Valid values: {string.Join(", ", Enum.GetNames<WorkStatus>())}" });

            if (dto.Priority is not null && !Enum.TryParse<TaskPriority>(dto.Priority, ignoreCase: true, out _))
                return Results.UnprocessableEntity(new { message = $"Invalid priority: {dto.Priority}. Valid values: {string.Join(", ", Enum.GetNames<TaskPriority>())}" });

            var before = TaskActivitySnapshot(task);
            var oldStatus = task.Status;
            var newStatus = dto.Status is not null ? Enum.Parse<WorkStatus>(dto.Status, ignoreCase: true) : task.Status;

            task.Name = dto.Name.Trim();
            task.Description = dto.Description?.Trim();
            task.Status = newStatus;
            task.Priority = dto.Priority is not null ? Enum.Parse<TaskPriority>(dto.Priority, ignoreCase: true) : task.Priority;
            task.Assignee = dto.Assignee?.Trim();
            if (dto.SortOrder.HasValue) task.SortOrder = dto.SortOrder.Value;
            task.UpdatedAt = DateTime.UtcNow;

            // Set or clear CompletedAt based on status transition
            if (newStatus == WorkStatus.Done && oldStatus != WorkStatus.Done)
                task.CompletedAt = DateTime.UtcNow;
            else if (newStatus != WorkStatus.Done)
                task.CompletedAt = null;

            AddActivityEvent(db, task.Feature.ProjectId, "Task", task.Id, null, oldStatus == newStatus ? "TaskUpdated" : "TaskStatusChanged", before, TaskActivitySnapshot(task), oldStatus == newStatus ? $"Task '{task.Name}' updated" : $"Task '{task.Name}' status changed from {oldStatus} to {newStatus}");
            await db.SaveChangesAsync();

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
        });

        // POST /api/tasks/{id}/claim — lightweight task claim for AI agents
        app.MapPost("/api/tasks/{id:int}/claim", async (int id, ClaimDto dto, AppDbContext db) =>
        {
            var actor = dto.Actor?.Trim();
            if (string.IsNullOrWhiteSpace(actor))
                return Results.UnprocessableEntity(new { message = "Actor is required." });

            await using var tx = await db.Database.BeginTransactionAsync();

            var task = await db.Tasks
                .Include(t => t.Feature)
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == id);
            if (task is null)
                return Results.NotFound(new { message = $"Task {id} not found." });

            if (!string.IsNullOrWhiteSpace(task.ClaimedBy) && !string.Equals(task.ClaimedBy, actor, StringComparison.OrdinalIgnoreCase))
            {
                return Results.Conflict(new
                {
                    message = $"Task {id} is already claimed by {task.ClaimedBy}.",
                    task.ClaimedBy,
                    task.ClaimedAt
                });
            }

            var before = TaskActivitySnapshot(task);
            var now = DateTime.UtcNow;
            var normalizedActor = actor.ToLowerInvariant();
            var updated = await db.Tasks
                .Where(t => t.Id == id && (t.ClaimedBy == null || t.ClaimedBy.ToLower() == normalizedActor))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(t => t.ClaimedBy, actor)
                    .SetProperty(t => t.ClaimedAt, t => t.ClaimedAt ?? now)
                    .SetProperty(t => t.UpdatedAt, now));

            if (updated == 0)
            {
                var currentClaim = await GetTaskClaimStateOrDefaultAsync(db, id);
                if (currentClaim is null)
                    return Results.NotFound(new { message = $"Task {id} not found." });

                return Results.Conflict(new
                {
                    message = $"Task {id} is already claimed by {currentClaim.ClaimedBy}.",
                    currentClaim.ClaimedBy,
                    currentClaim.ClaimedAt
                });
            }

            var claimedTask = await db.Tasks
                .Include(t => t.Feature)
                .FirstAsync(t => t.Id == id);
            AddActivityEvent(db, claimedTask.Feature.ProjectId, "Task", claimedTask.Id, actor, "TaskClaimed", before, TaskActivitySnapshot(claimedTask), $"Task '{claimedTask.Name}' claimed by {actor}");
            await db.SaveChangesAsync();
            await tx.CommitAsync();

            return Results.Ok(MapTaskClaimState(claimedTask));
        });

        // POST /api/tasks/{id}/release — release a task claimed by the same actor
        app.MapPost("/api/tasks/{id:int}/release", async (int id, ClaimDto dto, AppDbContext db) =>
        {
            var actor = dto.Actor?.Trim();
            if (string.IsNullOrWhiteSpace(actor))
                return Results.UnprocessableEntity(new { message = "Actor is required." });

            await using var tx = await db.Database.BeginTransactionAsync();

            var task = await db.Tasks
                .Include(t => t.Feature)
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == id);
            if (task is null)
                return Results.NotFound(new { message = $"Task {id} not found." });

            if (!string.IsNullOrWhiteSpace(task.ClaimedBy) && !string.Equals(task.ClaimedBy, actor, StringComparison.OrdinalIgnoreCase))
            {
                return Results.Conflict(new
                {
                    message = $"Task {id} is claimed by {task.ClaimedBy}; only that actor can release it.",
                    task.ClaimedBy,
                    task.ClaimedAt
                });
            }

            var before = TaskActivitySnapshot(task);
            var now = DateTime.UtcNow;
            var normalizedActor = actor.ToLowerInvariant();
            var updated = await db.Tasks
                .Where(t => t.Id == id && (t.ClaimedBy == null || t.ClaimedBy.ToLower() == normalizedActor))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(t => t.ClaimedBy, (string?)null)
                    .SetProperty(t => t.ClaimedAt, (DateTime?)null)
                    .SetProperty(t => t.UpdatedAt, now));

            if (updated == 0)
            {
                var currentClaim = await GetTaskClaimStateOrDefaultAsync(db, id);
                if (currentClaim is null)
                    return Results.NotFound(new { message = $"Task {id} not found." });

                return Results.Conflict(new
                {
                    message = $"Task {id} is claimed by {currentClaim.ClaimedBy}; only that actor can release it.",
                    currentClaim.ClaimedBy,
                    currentClaim.ClaimedAt
                });
            }

            var releasedTask = await db.Tasks
                .Include(t => t.Feature)
                .FirstAsync(t => t.Id == id);
            AddActivityEvent(db, releasedTask.Feature.ProjectId, "Task", releasedTask.Id, actor, "TaskReleased", before, TaskActivitySnapshot(releasedTask), $"Task '{releasedTask.Name}' released by {actor}");
            await db.SaveChangesAsync();
            await tx.CommitAsync();

            return Results.Ok(MapTaskClaimState(releasedTask));
        });

        // DELETE /api/tasks/{id} — delete
        app.MapDelete("/api/tasks/{id:int}", async (int id, AppDbContext db) =>
        {
            var task = await db.Tasks
                .Include(t => t.Feature)
                .FirstOrDefaultAsync(t => t.Id == id);
            if (task is null)
                return Results.NotFound(new { message = $"Task {id} not found." });

            AddActivityEvent(db, task.Feature.ProjectId, "Task", task.Id, null, "TaskDeleted", TaskActivitySnapshot(task), null, $"Task '{task.Name}' deleted");
            await DeleteSubtasksForTasksAsync(db, [id]);
            await DeleteAcceptanceCriteriaForTasksAsync(db, [id]);
            db.Tasks.Remove(task);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // PATCH /api/tasks/{id}/status — change status only
        app.MapPatch("/api/tasks/{id:int}/status", async (int id, StatusDto dto, AppDbContext db) =>
        {
            var task = await db.Tasks
                .Include(t => t.Feature)
                .FirstOrDefaultAsync(t => t.Id == id);
            if (task is null)
                return Results.NotFound(new { message = $"Task {id} not found." });

            if (string.IsNullOrWhiteSpace(dto.Status))
                return Results.UnprocessableEntity(new { message = "Status is required." });

            if (!Enum.TryParse<WorkStatus>(dto.Status, ignoreCase: true, out var newStatus))
                return Results.UnprocessableEntity(new { message = $"Invalid status: {dto.Status}. Valid values: {string.Join(", ", Enum.GetNames<WorkStatus>())}" });

            var before = TaskActivitySnapshot(task);
            var oldStatus = task.Status;
            task.Status = newStatus;
            task.UpdatedAt = DateTime.UtcNow;

            if (newStatus == WorkStatus.Done && oldStatus != WorkStatus.Done)
                task.CompletedAt = DateTime.UtcNow;
            else if (newStatus != WorkStatus.Done)
                task.CompletedAt = null;

            var statusComment = new TaskComment
            {
                TaskItemId = task.Id,
                Text = $"Status changed from {oldStatus} to {newStatus}",
                Author = "System",
                Type = CommentType.StatusChange
            };
            db.TaskComments.Add(statusComment);
            AddActivityEvent(db, task.Feature.ProjectId, "Task", task.Id, null, "TaskStatusChanged", before, TaskActivitySnapshot(task), $"Task '{task.Name}' status changed from {oldStatus} to {newStatus}");
            await db.SaveChangesAsync();

            return Results.Ok(new
            {
                task.Id,
                task.FeatureId,
                task.Name,
                Status = task.Status.ToString(),
                task.UpdatedAt,
                task.CompletedAt
            });
        });

        // PATCH /api/tasks/{id}/feature — reassign to different feature
        app.MapPatch("/api/tasks/{id:int}/feature", async (int id, ReassignDto dto, AppDbContext db) =>
        {
            var task = await db.Tasks.FindAsync(id);
            if (task is null)
                return Results.NotFound(new { message = $"Task {id} not found." });

            var featureExists = await db.Features.AnyAsync(f => f.Id == dto.FeatureId);
            if (!featureExists)
                return Results.NotFound(new { message = $"Feature {dto.FeatureId} not found." });

            task.FeatureId = dto.FeatureId;
            task.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync();

            return Results.Ok(new
            {
                task.Id,
                task.FeatureId,
                task.Name,
                Status = task.Status.ToString(),
                task.UpdatedAt
            });
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
            if (!Enum.TryParse<TaskDependencyType>(dto.Type ?? nameof(TaskDependencyType.DependsOn), ignoreCase: true, out var type))
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

            if (string.IsNullOrWhiteSpace(dto.Text))
                return Results.UnprocessableEntity(new { message = "Text is required." });

            subtask.Text = dto.Text.Trim();
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
        app.MapDelete("/api/subtasks/{id:int}", async (int id, AppDbContext db) =>
        {
            var exists = await db.Subtasks.AnyAsync(s => s.Id == id);
            if (!exists)
                return Results.NotFound(new { message = $"Subtask {id} not found." });

            await DeleteSubtaskTreeAsync(db, id);
            await db.SaveChangesAsync();
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

            if (string.IsNullOrWhiteSpace(dto.Text))
                return Results.UnprocessableEntity(new { message = "Text is required." });

            criterion.Text = dto.Text.Trim();
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
            if (dto.Priority is not null)
            {
                if (!Enum.TryParse<RequirementPriority>(dto.Priority, ignoreCase: true, out priority))
                    return Results.UnprocessableEntity(new { message = $"Invalid priority: {dto.Priority}. Valid values: {string.Join(", ", Enum.GetNames<RequirementPriority>())}" });
            }

            RequirementStatus status = RequirementStatus.Open;
            if (dto.Status is not null)
            {
                if (!Enum.TryParse<RequirementStatus>(dto.Status, ignoreCase: true, out status))
                    return Results.UnprocessableEntity(new { message = $"Invalid status: {dto.Status}. Valid values: {string.Join(", ", Enum.GetNames<RequirementStatus>())}" });
            }

            // Validate feature if provided
            if (dto.FeatureId.HasValue)
            {
                var featureExists = await db.Features.AnyAsync(f => f.Id == dto.FeatureId.Value);
                if (!featureExists)
                    return Results.NotFound(new { message = $"Feature {dto.FeatureId.Value} not found." });
            }

            // Validate task if provided
            if (dto.TaskItemId.HasValue)
            {
                var taskExists = await db.Tasks.AnyAsync(t => t.Id == dto.TaskItemId.Value);
                if (!taskExists)
                    return Results.NotFound(new { message = $"Task {dto.TaskItemId.Value} not found." });
            }

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
                FeatureId = dto.FeatureId,
                TaskItemId = dto.TaskItemId,
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

            if (dto.Priority is not null && !Enum.TryParse<RequirementPriority>(dto.Priority, ignoreCase: true, out _))
                return Results.UnprocessableEntity(new { message = $"Invalid priority: {dto.Priority}. Valid values: {string.Join(", ", Enum.GetNames<RequirementPriority>())}" });

            if (dto.Status is not null && !Enum.TryParse<RequirementStatus>(dto.Status, ignoreCase: true, out _))
                return Results.UnprocessableEntity(new { message = $"Invalid status: {dto.Status}. Valid values: {string.Join(", ", Enum.GetNames<RequirementStatus>())}" });

            requirement.Text = dto.Text.Trim();
            requirement.Description = dto.Description?.Trim();
            requirement.Priority = dto.Priority is not null ? Enum.Parse<RequirementPriority>(dto.Priority, ignoreCase: true) : requirement.Priority;
            requirement.Status = dto.Status is not null ? Enum.Parse<RequirementStatus>(dto.Status, ignoreCase: true) : requirement.Status;
            if (dto.FeatureId.HasValue) requirement.FeatureId = dto.FeatureId.Value == 0 ? null : dto.FeatureId;
            if (dto.TaskItemId.HasValue) requirement.TaskItemId = dto.TaskItemId.Value == 0 ? null : dto.TaskItemId;
            if (dto.SortOrder.HasValue) requirement.SortOrder = dto.SortOrder.Value;
            requirement.UpdatedAt = DateTime.UtcNow;

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

            if (dto.FeatureId.HasValue)
            {
                if (dto.FeatureId.Value == 0)
                {
                    requirement.FeatureId = null;
                }
                else
                {
                    var featureExists = await db.Features.AnyAsync(f => f.Id == dto.FeatureId.Value);
                    if (!featureExists)
                        return Results.NotFound(new { message = $"Feature {dto.FeatureId.Value} not found." });
                    requirement.FeatureId = dto.FeatureId.Value;
                }
            }

            if (dto.TaskItemId.HasValue)
            {
                if (dto.TaskItemId.Value == 0)
                {
                    requirement.TaskItemId = null;
                }
                else
                {
                    var taskExists = await db.Tasks.AnyAsync(t => t.Id == dto.TaskItemId.Value);
                    if (!taskExists)
                        return Results.NotFound(new { message = $"Task {dto.TaskItemId.Value} not found." });
                    requirement.TaskItemId = dto.TaskItemId.Value;
                }
            }

            requirement.UpdatedAt = DateTime.UtcNow;
            var entityType = requirement.TaskItemId.HasValue ? "Task" : "Requirement";
            var entityId = requirement.TaskItemId ?? requirement.Id;
            AddActivityEvent(db, requirement.ProjectId, entityType, entityId, null, "RequirementAssigned", before, RequirementActivitySnapshot(requirement), $"Requirement '{requirement.Text}' assignment updated");
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

        // POST /api/tasks/{taskId}/comments — add comment
        app.MapPost("/api/tasks/{taskId:int}/comments", async (int taskId, TaskCommentDto dto, AppDbContext db) =>
        {
            var projectId = await db.Tasks
                .Where(t => t.Id == taskId)
                .Select(t => (int?)t.Feature.ProjectId)
                .SingleOrDefaultAsync();
            if (projectId is null)
                return Results.NotFound(new { message = $"Task {taskId} not found." });

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

            var comment = new TaskComment
            {
                TaskItemId = taskId,
                Text = dto.Text.Trim(),
                Author = dto.Author?.Trim(),
                Type = commentType
            };

            db.TaskComments.Add(comment);
            AddActivityEvent(db, projectId.Value, "Task", taskId, comment.Author, "TaskCommentAdded", null, new { comment.Text, comment.Author }, "Comment added");
            await db.SaveChangesAsync();

            return Results.Created($"/api/tasks/{taskId}/comments/{comment.Id}", new
            {
                comment.Id,
                comment.TaskItemId,
                comment.Text,
                comment.Author,
                Type = comment.Type.ToString(),
                comment.CreatedAt
            });
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

    private static async Task DeleteProjectChildrenAsync(AppDbContext db, int projectId)
    {
        var projectFeatureIds = await db.Features
            .Where(f => f.ProjectId == projectId)
            .Select(f => f.Id)
            .ToListAsync();

        await DeleteFeatureTreesAsync(db, projectFeatureIds);

        var requirements = await db.Requirements
            .Where(r => r.ProjectId == projectId)
            .ToListAsync();
        db.Requirements.RemoveRange(requirements);
    }

    private static async Task DeleteFeatureTreeAsync(AppDbContext db, int rootFeatureId)
    {
        await DeleteFeatureTreesAsync(db, [rootFeatureId]);
    }

    private static async Task DeleteFeatureTreesAsync(AppDbContext db, List<int> rootFeatureIds)
    {
        if (rootFeatureIds.Count == 0) return;

        var allFeatures = await db.Features.ToListAsync();
        var featureIds = new HashSet<int>();

        void CollectFeatureAndChildren(int featureId)
        {
            if (!featureIds.Add(featureId)) return;

            foreach (var child in allFeatures.Where(f => f.ParentFeatureId == featureId))
            {
                CollectFeatureAndChildren(child.Id);
            }
        }

        foreach (var rootFeatureId in rootFeatureIds)
        {
            CollectFeatureAndChildren(rootFeatureId);
        }

        var tasks = await db.Tasks
            .Where(t => featureIds.Contains(t.FeatureId))
            .ToListAsync();
        var taskIds = tasks.Select(t => t.Id).ToList();

        var requirements = await db.Requirements
            .Where(r => (r.FeatureId.HasValue && featureIds.Contains(r.FeatureId.Value)) ||
                        (r.TaskItemId.HasValue && taskIds.Contains(r.TaskItemId.Value)))
            .ToListAsync();

        foreach (var requirement in requirements)
        {
            if (requirement.FeatureId.HasValue && featureIds.Contains(requirement.FeatureId.Value))
                requirement.FeatureId = null;
            if (requirement.TaskItemId.HasValue && taskIds.Contains(requirement.TaskItemId.Value))
                requirement.TaskItemId = null;
        }

        if (taskIds.Count > 0)
        {
            await DeleteSubtasksForTasksAsync(db, taskIds);
            await DeleteAcceptanceCriteriaForTasksAsync(db, taskIds);

            var comments = await db.TaskComments
                .Where(c => taskIds.Contains(c.TaskItemId))
                .ToListAsync();
            db.TaskComments.RemoveRange(comments);
            db.Tasks.RemoveRange(tasks);
        }

        var featuresToRemove = allFeatures
            .Where(f => featureIds.Contains(f.Id))
            .OrderByDescending(f => GetFeatureDepth(f, allFeatures))
            .ToList();
        db.Features.RemoveRange(featuresToRemove);
    }

    private static int GetFeatureDepth(Feature feature, List<Feature> allFeatures)
    {
        var depth = 0;
        var current = feature;
        while (current.ParentFeatureId.HasValue)
        {
            var parent = allFeatures.FirstOrDefault(f => f.Id == current.ParentFeatureId.Value);
            if (parent is null) break;
            depth++;
            current = parent;
        }
        return depth;
    }

    private static async Task DeleteSubtaskTreeAsync(AppDbContext db, int rootSubtaskId)
    {
        var root = await db.Subtasks.FindAsync(rootSubtaskId);
        if (root is null) return;

        var allSubtasksForTask = await db.Subtasks
            .Where(s => s.TaskItemId == root.TaskItemId)
            .ToListAsync();

        var subtaskIds = new HashSet<int>();
        CollectSubtaskAndChildren(rootSubtaskId, allSubtasksForTask, subtaskIds);

        var subtasksToRemove = allSubtasksForTask
            .Where(s => subtaskIds.Contains(s.Id))
            .OrderByDescending(s => GetSubtaskDepth(s, allSubtasksForTask))
            .ToList();

        db.Subtasks.RemoveRange(subtasksToRemove);
    }

    private static async Task DeleteSubtasksForTasksAsync(AppDbContext db, List<int> taskIds)
    {
        if (taskIds.Count == 0) return;

        var subtasks = await db.Subtasks
            .Where(s => taskIds.Contains(s.TaskItemId))
            .ToListAsync();

        var orderedSubtasks = subtasks
            .OrderByDescending(s => GetSubtaskDepth(s, subtasks))
            .ToList();

        db.Subtasks.RemoveRange(orderedSubtasks);
    }

    private static async Task DeleteAcceptanceCriteriaForTasksAsync(AppDbContext db, List<int> taskIds)
    {
        if (taskIds.Count == 0) return;

        var criteria = await db.AcceptanceCriteria
            .Where(c => taskIds.Contains(c.TaskItemId))
            .ToListAsync();

        db.AcceptanceCriteria.RemoveRange(criteria);
    }

    private static void CollectSubtaskAndChildren(int subtaskId, List<Subtask> allSubtasks, HashSet<int> subtaskIds)
    {
        if (!subtaskIds.Add(subtaskId)) return;

        foreach (var child in allSubtasks.Where(s => s.ParentSubtaskId == subtaskId))
        {
            CollectSubtaskAndChildren(child.Id, allSubtasks, subtaskIds);
        }
    }

    private static int GetSubtaskDepth(Subtask subtask, List<Subtask> allSubtasks)
    {
        var depth = 0;
        var current = subtask;
        while (current.ParentSubtaskId.HasValue)
        {
            var parent = allSubtasks.FirstOrDefault(s => s.Id == current.ParentSubtaskId.Value);
            if (parent is null) break;
            depth++;
            current = parent;
        }
        return depth;
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

    private static int PriorityRank(TaskPriority priority) => priority switch
    {
        TaskPriority.Critical => 4,
        TaskPriority.High => 3,
        TaskPriority.Medium => 2,
        TaskPriority.Low => 1,
        _ => 0
    };

    private static int StatusRank(WorkStatus status) => status switch
    {
        WorkStatus.InProgress => 3,
        WorkStatus.Todo => 2,
        WorkStatus.Backlog => 1,
        WorkStatus.Review => 0,
        WorkStatus.Done => -1,
        _ => 0
    };

    private static string BuildNextTaskReason(TaskItem task)
    {
        var status = task.Status switch
        {
            WorkStatus.InProgress => "already in progress",
            WorkStatus.Todo => "ready to start",
            WorkStatus.Backlog => "available in the backlog",
            WorkStatus.Review => "waiting for review attention",
            _ => task.Status.ToString().ToLowerInvariant()
        };

        return $"{task.Priority} priority task that is {status}; last updated {task.UpdatedAt:u}.";
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
    int? SortOrder);

public record StatusDto(string? Status);

public record ClaimDto(string? Actor);

public record ReassignDto(int FeatureId);

public record SubtaskDto(string? Text, bool? Completed, int? SortOrder, int? ParentSubtaskId);

public record AcceptanceCriterionDto(string? Text, bool? Completed, int? SortOrder);

public record RequirementDto(string? Text, string? Description, string? Priority, string? Status, int? FeatureId, int? TaskItemId, int? SortOrder);

public record RequirementAssignDto(int? FeatureId, int? TaskItemId);

public record TaskCommentDto(string? Text, string? Author, string? Type);

public record TaskDependencyDto(int? TaskItemId, int? BlockedTaskId, int? DependsOnTaskItemId, int? BlockingTaskItemId, int? BlockingTaskId, string? Type);
