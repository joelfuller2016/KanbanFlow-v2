using System.Text.Json;
using System.Text.Json.Serialization;
using KanbanFlow.Data;
using KanbanFlow.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace KanbanFlow.Services;

/// <summary>Audited persistence for board writes outside the established domain event methods.</summary>
public static class ChangeAudit
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<int> SaveChangesWithAuditAsync(this AppDbContext db, string? actor = null, CancellationToken ct = default)
    {
        db.ChangeTracker.DetectChanges();
        var entries = db.ChangeTracker.Entries().Where(IsCandidate).ToList();
        if (entries.Count == 0) return await db.SaveChangesAsync(ct);
        var attribution = string.IsNullOrWhiteSpace(actor) ? db.DefaultAuditActor : actor.Trim();
        if (attribution.Length > 200) throw new ValidationException("Audit actor must be at most 200 characters.");

        // Reserve the SQLite writer before reading actual persisted values. Detached UI objects
        // may already be edited when attached, so ChangeTracker originals alone are insufficient.
        await using var owned = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        var transaction = db.Database.CurrentTransaction!;
        var savepoint = owned is null ? "audit_" + Guid.NewGuid().ToString("N") : null;
        if (savepoint is not null) await transaction.CreateSavepointAsync(savepoint, ct);
        var added = db.ChangeTracker.Entries().Where(e => e.State == EntityState.Added)
            .Select(e => (Entry: e, Values: e.Properties.Select(p => (p.Metadata.Name, p.CurrentValue, p.IsTemporary)).ToList())).ToList();

        try
        {
            var pending = new List<Pending>();
            foreach (var entry in entries)
            {
                var state = entry.State;
                var type = entry.Entity is TaskItem ? "Task" : entry.Metadata.ClrType.Name;
                var beforeValues = state == EntityState.Added ? null : await entry.GetDatabaseValuesAsync(ct);
                if (state != EntityState.Added && beforeValues is null) continue; // the normal save reports the stale write
                var before = beforeValues is null ? null : Snapshot(beforeValues);
                var after = state == EntityState.Deleted ? null : before is null ? Snapshot(entry.CurrentValues) : new Dictionary<string, object?>(before);
                if (state == EntityState.Modified)
                {
                    foreach (var property in entry.Properties.Where(p => p.IsModified))
                        after![Key(property.Metadata.Name)] = property.CurrentValue;
                    if (!before!.Any(p => p.Key != "updatedAt" && !Equals(p.Value, after![p.Key])))
                    {
                        if (entry.Metadata.FindProperty("UpdatedAt") is not null)
                        {
                            entry.Property("UpdatedAt").CurrentValue = beforeValues!["UpdatedAt"];
                            entry.Property("UpdatedAt").IsModified = false;
                        }
                        continue;
                    }
                }

                var id = entry.Property("Id").CurrentValue;
                // Established Task/Idea/etc. services retain their semantic event and comment
                // contracts. Their event already describes this write; do not add a duplicate.
                var semanticEvents = id is int intId ? db.ChangeTracker.Entries<ActivityEvent>().Where(e => e.State == EntityState.Added &&
                    e.Entity.EntityType == type && e.Entity.EntityId == intId &&
                    e.Entity.EventType != "TaskCommentAdded" && e.Entity.EventType != "TaskDoneOverridden").ToList() : [];
                if (semanticEvents.Count > 0)
                {
                    // Keep the semantic contract, but reconcile it with persisted values. An
                    // ordinary edit may have read before another client changed an untouched field.
                    foreach (var semantic in semanticEvents)
                    {
                        semantic.Entity.BeforeJson = Reconcile(semantic.Entity.BeforeJson, before);
                        semantic.Entity.AfterJson = Reconcile(semantic.Entity.AfterJson, after);
                        if (string.IsNullOrWhiteSpace(semantic.Entity.Actor)) semantic.Entity.Actor = attribution;
                    }
                    continue;
                }

                var context = await ContextAsync(db, type, after ?? before!, before, ct);
                pending.Add(new(entry, state, type, before, after, context));
            }

            // Keep entity states unaccepted until BOTH the data and its audit rows commit.
            // Raw parameterized inserts avoid a second SaveChanges resubmitting pending entities.
            var affected = await db.SaveChangesAsync(acceptAllChangesOnSuccess: false, ct);
            foreach (var change in pending)
            {
                var after = change.After;
                if (change.State == EntityState.Added) after = Snapshot(change.Entry.CurrentValues);
                var values = after ?? change.Before!;
                var id = values["id"] is int intId ? intId : 0;
                var context = change.State == EntityState.Added
                    ? await ContextAsync(db, change.Type, values, null, ct) : change.Context;
                var action = change.State switch { EntityState.Added => "Created", EntityState.Deleted => "Deleted", _ => "Updated" };
                var eventType = change.Type == "Task" && change.State == EntityState.Modified &&
                    change.Before!.Any(p => p.Key == "sortOrder" && !Equals(p.Value, after![p.Key])) ? "TaskReordered" : change.Type + action;
                var label = values.GetValueOrDefault("name") ?? values.GetValueOrDefault("text") ?? values["id"];
                var message = $"{change.Type} '{label}' {action.ToLowerInvariant()}";
                var beforeJson = change.Before is null ? null : JsonSerializer.Serialize(change.Before, Json);
                var afterJson = after is null ? null : JsonSerializer.Serialize(after, Json);
                var now = DateTime.UtcNow;
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO ActivityEvents (ProjectId, EntityType, EntityId, TaskId, RelatedTaskId, Actor, EventType, BeforeJson, AfterJson, Message, CreatedAt)
                    VALUES ({context.ProjectId}, {change.Type}, {id}, {context.TaskId}, {context.RelatedTaskId}, {attribution}, {eventType}, {beforeJson}, {afterJson}, {message}, {now})
                    """, ct);
            }
            if (owned is not null) await owned.CommitAsync(ct);
            else await transaction.ReleaseSavepointAsync(savepoint!, ct);
            db.ChangeTracker.AcceptAllChanges();
            return affected;
        }
        catch
        {
            if (owned is not null) await owned.RollbackAsync(CancellationToken.None);
            else await transaction.RollbackToSavepointAsync(savepoint!, CancellationToken.None);
            // SaveChanges(false) retains modified/deleted state. Restore generated values on new
            // records too, so a failed audit cannot leave a rolled-back ID in a later retry.
            foreach (var item in added)
                foreach (var (name, value, temporary) in item.Values)
                {
                    item.Entry.Property(name).CurrentValue = value;
                    item.Entry.Property(name).IsTemporary = temporary;
                }
            throw;
        }
    }

    private static bool IsCandidate(EntityEntry entry)
    {
        if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) return false;
        // Task creation and comment insertion already have explicit, generated-ID-aware audit
        // methods. They remain the only production writers for those operations.
        if (entry.Entity is TaskItem && entry.State == EntityState.Added) return false;
        if (entry.Entity is TaskComment && entry.State != EntityState.Deleted) return false;
        return entry.Entity is Project or Feature or TaskItem or Subtask or AcceptanceCriterion or Requirement or TaskDependency or TaskComment
            || (entry.Entity is Idea && entry.State == EntityState.Modified && entry.Property("PromotedProjectId").IsModified)
            || (entry.State == EntityState.Deleted && entry.Metadata.ClrType.Namespace == "KanbanFlow.Models.Workflows")
            || (entry.Entity is Models.Workflows.WorkRequest && entry.State == EntityState.Modified &&
                ((entry.Property("ProjectId").IsModified && entry.Property("ProjectId").CurrentValue is null) ||
                 (entry.Property("FeatureId").IsModified && entry.Property("FeatureId").CurrentValue is null)));
    }

    private static Dictionary<string, object?> Snapshot(PropertyValues values) =>
        values.Properties.ToDictionary(p => Key(p.Name), p => values[p]);
    private static string? Reconcile(string? snapshot, Dictionary<string, object?>? actual)
    {
        if (snapshot is null || actual is null) return snapshot;
        var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(snapshot)!;
        return JsonSerializer.Serialize(fields.ToDictionary(p => p.Key,
            p => actual.TryGetValue(Key(p.Key), out var value) ? value : (object?)p.Value), Json);
    }
    private static string Key(string name) => JsonNamingPolicy.CamelCase.ConvertName(name);
    private static int? Int(Dictionary<string, object?> values, string key) => values.GetValueOrDefault(key) is int id && id > 0 ? id : null;

    private static async Task<AuditContext> ContextAsync(AppDbContext db, string type, Dictionary<string, object?> values,
        Dictionary<string, object?>? before, CancellationToken ct)
    {
        var taskId = type == "Task" ? Int(values, "id") : Int(values, "taskItemId");
        var previousTaskId = before is null ? null : Int(before, "taskItemId");
        var relatedTaskId = Int(values, "dependsOnTaskItemId") ?? (previousTaskId != taskId ? previousTaskId : null);
        taskId ??= previousTaskId;
        var projectId = type == "Project" ? Int(values, "id") : Int(values, "projectId");
        if (projectId is null && before is not null) projectId = Int(before, "projectId");
        if (type == "Idea") projectId = Int(values, "promotedProjectId") ?? (before is null ? null : Int(before, "promotedProjectId"));
        var featureId = type == "Feature" ? Int(values, "id") : Int(values, "featureId");
        if (projectId is null && taskId is int task)
            projectId = await db.Tasks.Where(t => t.Id == task).Select(t => (int?)t.Feature.ProjectId).FirstOrDefaultAsync(ct);
        if (projectId is null && featureId is int feature)
            projectId = await db.Features.Where(f => f.Id == feature).Select(f => (int?)f.ProjectId).FirstOrDefaultAsync(ct);
        if (projectId is null && values.GetValueOrDefault("requestId") is Guid requestId)
            projectId = await db.WorkRequests.Where(r => r.Id == requestId).Select(r => r.ProjectId).FirstOrDefaultAsync(ct);
        if (type == "TaskEvidence" && values.GetValueOrDefault("workItemId") is Guid workItemId)
        {
            var parent = await db.PlannedTasks.Where(w => w.Id == workItemId)
                .Select(w => new { w.TaskItemId, ProjectId = w.Task.Feature.ProjectId }).FirstOrDefaultAsync(ct);
            taskId = parent?.TaskItemId;
            projectId = parent?.ProjectId;
        }
        return new(projectId ?? 0, taskId, relatedTaskId);
    }

    private sealed record AuditContext(int ProjectId, int? TaskId, int? RelatedTaskId);
    private sealed record Pending(EntityEntry Entry, EntityState State, string Type, Dictionary<string, object?>? Before,
        Dictionary<string, object?>? After, AuditContext Context);
}
