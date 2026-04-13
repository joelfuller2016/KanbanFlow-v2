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
        app.MapSubtaskEndpoints();
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

        // POST /api/projects — create
        app.MapPost("/api/projects", async (ProjectDto dto, AppDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(dto.Name))
                return Results.UnprocessableEntity(new { message = "Name is required." });

            var project = new Project
            {
                Name = dto.Name.Trim(),
                Description = dto.Description?.Trim(),
                Goals = dto.Goals?.Trim()
            };

            db.Projects.Add(project);
            await db.SaveChangesAsync();

            return Results.Created($"/api/projects/{project.Id}", new
            {
                project.Id,
                project.Name,
                project.Description,
                project.Goals,
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
            project.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync();

            return Results.Ok(new
            {
                project.Id,
                project.Name,
                project.Description,
                project.Goals,
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

            db.Projects.Remove(project);
            await db.SaveChangesAsync();
            return Results.NoContent();
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
            if (dto.ParentFeatureId.HasValue) feature.ParentFeatureId = dto.ParentFeatureId.Value == 0 ? null : dto.ParentFeatureId;
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
            var feature = await db.Features.FindAsync(id);
            if (feature is null)
                return Results.NotFound(new { message = $"Feature {id} not found." });

            db.Features.Remove(feature);
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
                t.SortOrder,
                t.CreatedAt,
                t.UpdatedAt,
                t.CompletedAt,
                SubtaskCount = t.Subtasks.Count,
                SubtaskDoneCount = t.Subtasks.Count(s => s.Completed)
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
                            t.SortOrder,
                            t.CreatedAt,
                            t.UpdatedAt,
                            t.CompletedAt,
                            SubtaskCount = t.Subtasks.Count,
                            SubtaskDoneCount = t.Subtasks.Count(s => s.Completed)
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
                })
            });
        });

        // POST /api/features/{featureId}/tasks — create (default status: Backlog)
        app.MapPost("/api/features/{featureId:int}/tasks", async (int featureId, TaskDto dto, AppDbContext db) =>
        {
            var featureExists = await db.Features.AnyAsync(f => f.Id == featureId);
            if (!featureExists)
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

            return Results.Created($"/api/tasks/{task.Id}", new
            {
                task.Id,
                task.FeatureId,
                task.Name,
                task.Description,
                Status = task.Status.ToString(),
                Priority = task.Priority.ToString(),
                task.Assignee,
                task.SortOrder,
                task.CreatedAt,
                task.UpdatedAt,
                task.CompletedAt
            });
        });

        // PUT /api/tasks/{id} — update
        app.MapPut("/api/tasks/{id:int}", async (int id, TaskDto dto, AppDbContext db) =>
        {
            var task = await db.Tasks.FindAsync(id);
            if (task is null)
                return Results.NotFound(new { message = $"Task {id} not found." });

            if (string.IsNullOrWhiteSpace(dto.Name))
                return Results.UnprocessableEntity(new { message = "Name is required." });

            if (dto.Status is not null && !Enum.TryParse<WorkStatus>(dto.Status, ignoreCase: true, out _))
                return Results.UnprocessableEntity(new { message = $"Invalid status: {dto.Status}. Valid values: {string.Join(", ", Enum.GetNames<WorkStatus>())}" });

            if (dto.Priority is not null && !Enum.TryParse<TaskPriority>(dto.Priority, ignoreCase: true, out _))
                return Results.UnprocessableEntity(new { message = $"Invalid priority: {dto.Priority}. Valid values: {string.Join(", ", Enum.GetNames<TaskPriority>())}" });

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
                task.SortOrder,
                task.CreatedAt,
                task.UpdatedAt,
                task.CompletedAt
            });
        });

        // DELETE /api/tasks/{id} — delete
        app.MapDelete("/api/tasks/{id:int}", async (int id, AppDbContext db) =>
        {
            var task = await db.Tasks.FindAsync(id);
            if (task is null)
                return Results.NotFound(new { message = $"Task {id} not found." });

            db.Tasks.Remove(task);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // PATCH /api/tasks/{id}/status — change status only
        app.MapPatch("/api/tasks/{id:int}/status", async (int id, StatusDto dto, AppDbContext db) =>
        {
            var task = await db.Tasks.FindAsync(id);
            if (task is null)
                return Results.NotFound(new { message = $"Task {id} not found." });

            if (string.IsNullOrWhiteSpace(dto.Status))
                return Results.UnprocessableEntity(new { message = "Status is required." });

            if (!Enum.TryParse<WorkStatus>(dto.Status, ignoreCase: true, out var newStatus))
                return Results.UnprocessableEntity(new { message = $"Invalid status: {dto.Status}. Valid values: {string.Join(", ", Enum.GetNames<WorkStatus>())}" });

            var oldStatus = task.Status;
            task.Status = newStatus;
            task.UpdatedAt = DateTime.UtcNow;

            if (newStatus == WorkStatus.Done && oldStatus != WorkStatus.Done)
                task.CompletedAt = DateTime.UtcNow;
            else if (newStatus != WorkStatus.Done)
                task.CompletedAt = null;

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
            if (dto.ParentSubtaskId.HasValue) subtask.ParentSubtaskId = dto.ParentSubtaskId.Value == 0 ? null : dto.ParentSubtaskId;

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
            var subtask = await db.Subtasks.FindAsync(id);
            if (subtask is null)
                return Results.NotFound(new { message = $"Subtask {id} not found." });

            db.Subtasks.Remove(subtask);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    // ── Helper ─────────────────────────────────────────────────────────

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
        SubtaskDoneCount = t.Subtasks.Count(s => s.Completed)
    };
}

// ── DTOs ───────────────────────────────────────────────────────────────

public record ProjectDto(string? Name, string? Description, string? Goals);

public record FeatureDto(string? Name, string? Description, int? SortOrder, int? ParentFeatureId);

public record TaskDto(
    string? Name,
    string? Description,
    string? Status,
    string? Priority,
    string? Assignee,
    int? SortOrder);

public record StatusDto(string? Status);

public record ReassignDto(int FeatureId);

public record SubtaskDto(string? Text, bool? Completed, int? SortOrder, int? ParentSubtaskId);
