using System.Text.Json;
using KanbanFlow.Data;
using KanbanFlow.Models;
using KanbanFlow.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Tests;

public class ChangeAuditTests
{
    private static readonly DateTime T0 = new(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("Task", false)]
    [InlineData("Feature", false)]
    [InlineData("Project", false)]
    [InlineData("Task", true)]
    [InlineData("Feature", true)]
    [InlineData("Project", true)]
    public async Task Delete_ExternalLinkAuditCommitsAtomicallyWithGraph(string scope, bool failAudit)
    {
        using var factory = new SqliteTestFactory();
        var fixture = await SeedAsync(factory);
        int linkId;
        await using (var db = factory.CreateDbContext())
        {
            var link = new ExternalWorkItemLink
            {
                TaskId = fixture.TaskId, RepositoryExternalId = "R_audit",
                RepositoryOwner = "owner", RepositoryName = "repo", ExternalNodeId = "I_audit",
                ExternalNumber = 17, Url = "https://github.com/owner/repo/issues/17"
            };
            db.ExternalWorkItemLinks.Add(link);
            await db.SaveChangesAsync();
            linkId = link.Id;
            if (failAudit)
                await db.Database.ExecuteSqlRawAsync("""
                    CREATE TRIGGER reject_external_deletion_audit BEFORE INSERT ON ActivityEvents
                    WHEN NEW.EventType = 'ExternalWorkItemLinkDeleted'
                    BEGIN SELECT RAISE(ABORT, 'forced external deletion audit failure'); END;
                    """);
        }

        async Task DeleteAsync()
        {
            var service = new TaskService(factory);
            if (scope == "Task") await service.DeleteAsync(fixture.TaskId, "Codex");
            else if (scope == "Project") await service.DeleteProjectAsync(fixture.ProjectId, "Codex");
            else
            {
                await using var db = factory.CreateDbContext();
                await ApiEndpoints.DeleteFeatureAsync(fixture.FeatureId, db);
            }
        }

        if (failAudit)
        {
            var error = await Assert.ThrowsAsync<SqliteException>(DeleteAsync);
            Assert.Contains("forced external deletion audit failure", error.Message);
        }
        else await DeleteAsync();

        await using var verify = factory.CreateDbContext();
        Assert.Equal(failAudit, await verify.Tasks.AnyAsync(t => t.Id == fixture.TaskId));
        Assert.Equal(failAudit, await verify.ExternalWorkItemLinks.AnyAsync(l => l.Id == linkId));
        var events = await verify.ActivityEvents.Where(e => e.EventType == "ExternalWorkItemLinkDeleted").ToListAsync();
        if (failAudit) Assert.Empty(events);
        else
        {
            var audit = Assert.Single(events);
            Assert.Equal(fixture.ProjectId, audit.ProjectId);
            Assert.Equal(fixture.TaskId, audit.TaskId);
            Assert.Equal(linkId, audit.EntityId);
            Assert.Null(audit.AfterJson);
            using var before = JsonDocument.Parse(audit.BeforeJson!);
            Assert.Equal("I_audit", before.RootElement.GetProperty("externalNodeId").GetString());
        }
    }

    [Theory]
    [InlineData("Subtask")]
    [InlineData("AcceptanceCriterion")]
    [InlineData("Requirement")]
    [InlineData("TaskDependency")]
    public async Task SaveChangesWithAuditAsync_ChildLifecycle_RecordsValuesActorAndIdentity(string kind)
    {
        using var factory = new SqliteTestFactory();
        var fixture = await SeedAsync(factory);
        await using var db = factory.CreateDbContext();
        var child = CreateChild(kind, fixture);
        db.Add(child);

        await db.SaveChangesWithAuditAsync("  Joel  ");

        var id = (int)db.Entry(child).Property("Id").CurrentValue!;
        Assert.True(id > 0);
        var created = Assert.Single(await EventsAsync(factory));
        AssertIdentity(created, fixture, kind, id, "Joel", kind == "TaskDependency" ? fixture.OtherTaskId : null);
        Assert.Equal(kind + "Created", created.EventType);
        Assert.Null(created.BeforeJson);
        AssertChildSnapshot(created.AfterJson, kind, fixture, id, changed: false);

        ChangeChild(child);
        await db.SaveChangesWithAuditAsync("Codex");

        var updatedEvents = await EventsAsync(factory);
        Assert.Equal(2, updatedEvents.Count);
        var updated = updatedEvents[1];
        AssertIdentity(updated, fixture, kind, id, "Codex", kind == "TaskDependency" ? fixture.OtherTaskId : null);
        Assert.Equal(kind + "Updated", updated.EventType);
        AssertChildSnapshot(updated.BeforeJson, kind, fixture, id, changed: false);
        AssertChildSnapshot(updated.AfterJson, kind, fixture, id, changed: true);
        await using (var persisted = factory.CreateDbContext())
        {
            var row = await persisted.FindAsync(child.GetType(), id);
            Assert.NotNull(row);
            switch (row)
            {
                case Subtask subtask:
                    Assert.Equal("Revised checklist", subtask.Text);
                    Assert.True(subtask.Completed);
                    break;
                case AcceptanceCriterion criterion:
                    Assert.Equal("Revised checklist", criterion.Text);
                    Assert.True(criterion.Completed);
                    break;
                case Requirement requirement:
                    Assert.Equal("Revised checklist", requirement.Text);
                    Assert.Equal(RequirementPriority.P0, requirement.Priority);
                    break;
                case TaskDependency dependency:
                    Assert.Equal(TaskDependencyType.Blocks, dependency.Type);
                    break;
            }
        }

        db.Remove(child);
        await db.SaveChangesWithAuditAsync("Joel");

        await using var deletedState = factory.CreateDbContext();
        Assert.Null(await deletedState.FindAsync(child.GetType(), id));
        var events = await EventsAsync(factory);
        Assert.Equal(3, events.Count);
        var deleted = events[2];
        AssertIdentity(deleted, fixture, kind, id, "Joel", kind == "TaskDependency" ? fixture.OtherTaskId : null);
        Assert.Equal(kind + "Deleted", deleted.EventType);
        AssertChildSnapshot(deleted.BeforeJson, kind, fixture, id, changed: true);
        Assert.Null(deleted.AfterJson);
    }

    [Fact]
    public async Task SaveChangesWithAuditAsync_DetachedPartialUpdate_UsesPersistedBeforeAndUntouchedValues()
    {
        using var factory = new SqliteTestFactory();
        var fixture = await SeedAsync(factory);
        TaskItem detached;
        await using (var original = factory.CreateDbContext())
            detached = await original.Tasks.AsNoTracking().SingleAsync(t => t.Id == fixture.TaskId);
        await using (var concurrent = factory.CreateDbContext())
        {
            var task = await concurrent.Tasks.SingleAsync(t => t.Id == fixture.TaskId);
            task.Description = "Saved by the human while the editor was open";
            task.Priority = TaskPriority.High;
            await concurrent.SaveChangesAsync();
        }

        detached.Name = "Agent revision";
        await using (var editing = factory.CreateDbContext())
        {
            editing.Attach(detached);
            editing.Entry(detached).Property(t => t.Name).IsModified = true;
            await editing.SaveChangesWithAuditAsync("Codex");
        }

        var change = Assert.Single(await EventsAsync(factory));
        AssertIdentity(change, fixture, "Task", fixture.TaskId, "Codex");
        Assert.Equal("TaskUpdated", change.EventType);
        using var before = JsonDocument.Parse(change.BeforeJson!);
        using var after = JsonDocument.Parse(change.AfterJson!);
        Assert.Equal("Initial task", before.RootElement.GetProperty("name").GetString());
        Assert.Equal("Agent revision", after.RootElement.GetProperty("name").GetString());
        foreach (var snapshot in new[] { before.RootElement, after.RootElement })
        {
            Assert.Equal("Saved by the human while the editor was open", snapshot.GetProperty("description").GetString());
            Assert.Equal("High", snapshot.GetProperty("priority").GetString());
            Assert.Equal("Joel", snapshot.GetProperty("assignee").GetString());
        }
        await using var persisted = factory.CreateDbContext();
        var saved = await persisted.Tasks.SingleAsync(t => t.Id == fixture.TaskId);
        Assert.Equal("Agent revision", saved.Name);
        Assert.Equal("Saved by the human while the editor was open", saved.Description);
        Assert.Equal(TaskPriority.High, saved.Priority);
    }

    [Fact]
    public async Task SaveChangesWithAuditAsync_DetachedPartialUpdate_ReconcilesExistingSemanticEventWithPersistedValues()
    {
        using var factory = new SqliteTestFactory();
        var fixture = await SeedAsync(factory);
        TaskItem detached;
        await using (var original = factory.CreateDbContext())
            detached = await original.Tasks.AsNoTracking().SingleAsync(t => t.Id == fixture.TaskId);
        await using (var concurrent = factory.CreateDbContext())
        {
            var task = await concurrent.Tasks.SingleAsync(t => t.Id == fixture.TaskId);
            task.Description = "Human's concurrent description";
            task.Priority = TaskPriority.Critical;
            await concurrent.SaveChangesAsync();
        }
        detached.Name = "Agent revision";
        await using (var editing = factory.CreateDbContext())
        {
            editing.Attach(detached);
            editing.Entry(detached).Property(t => t.Name).IsModified = true;
            var semantic = TaskEvent(fixture, "TaskUpdated");
            semantic.BeforeJson = """
                {"name":"Initial task","description":"Initial description","priority":"Low","reason":"Existing semantic detail"}
                """;
            semantic.AfterJson = """
                {"name":"Agent revision","description":"Initial description","priority":"Low","reason":"Existing semantic detail"}
                """;
            editing.ActivityEvents.Add(semantic);

            await editing.SaveChangesWithAuditAsync("Codex");
        }

        var change = Assert.Single(await EventsAsync(factory));
        AssertIdentity(change, fixture, "Task", fixture.TaskId, "service");
        Assert.Equal("TaskUpdated", change.EventType);
        Assert.Equal("Established event", change.Message);
        using var before = JsonDocument.Parse(change.BeforeJson!);
        using var after = JsonDocument.Parse(change.AfterJson!);
        Assert.Equal("Initial task", before.RootElement.GetProperty("name").GetString());
        Assert.Equal("Agent revision", after.RootElement.GetProperty("name").GetString());
        foreach (var snapshot in new[] { before.RootElement, after.RootElement })
        {
            Assert.Equal("Human's concurrent description", snapshot.GetProperty("description").GetString());
            Assert.Equal("Critical", snapshot.GetProperty("priority").GetString());
            Assert.Equal("Existing semantic detail", snapshot.GetProperty("reason").GetString());
        }
        await using var persisted = factory.CreateDbContext();
        var saved = await persisted.Tasks.SingleAsync(t => t.Id == fixture.TaskId);
        Assert.Equal("Agent revision", saved.Name);
        Assert.Equal("Human's concurrent description", saved.Description);
        Assert.Equal(TaskPriority.Critical, saved.Priority);
    }

    [Fact]
    public async Task SaveChangesWithAuditAsync_AuditInsertFailure_RollsBackDataAndAllowsRetry()
    {
        using var factory = new SqliteTestFactory();
        var fixture = await SeedAsync(factory);
        await using var db = factory.CreateDbContext();
        var subtask = await db.Subtasks.SingleAsync(s => s.Id == fixture.SubtaskId);
        subtask.Text = "Retried edit";
        await BlockAuditAsync(db);

        var failure = await Assert.ThrowsAsync<SqliteException>(() => db.SaveChangesWithAuditAsync("Codex"));

        Assert.Equal(19, failure.SqliteErrorCode);
        Assert.Contains("audit unavailable", failure.Message, StringComparison.Ordinal);
        Assert.Equal(EntityState.Modified, db.Entry(subtask).State);
        await using (var failedState = factory.CreateDbContext())
        {
            Assert.Equal("Initial subtask", (await failedState.Subtasks.SingleAsync(s => s.Id == fixture.SubtaskId)).Text);
            Assert.Empty(await failedState.ActivityEvents.ToListAsync());
        }

        await UnblockAuditAsync(db);
        await db.SaveChangesWithAuditAsync("Codex");

        Assert.Equal(EntityState.Unchanged, db.Entry(subtask).State);
        var change = Assert.Single(await EventsAsync(factory));
        using var before = JsonDocument.Parse(change.BeforeJson!);
        using var after = JsonDocument.Parse(change.AfterJson!);
        Assert.Equal("Initial subtask", before.RootElement.GetProperty("text").GetString());
        Assert.Equal("Retried edit", after.RootElement.GetProperty("text").GetString());
        await using var persisted = factory.CreateDbContext();
        Assert.Equal("Retried edit", (await persisted.Subtasks.SingleAsync(s => s.Id == fixture.SubtaskId)).Text);
    }

    [Fact]
    public async Task SaveChangesWithAuditAsync_SecondAuditInsertFailure_RollsBackEntireBatch()
    {
        using var factory = new SqliteTestFactory();
        var fixture = await SeedAsync(factory);
        await using var db = factory.CreateDbContext();
        var task = await db.Tasks.SingleAsync(t => t.Id == fixture.TaskId);
        var subtask = await db.Subtasks.SingleAsync(s => s.Id == fixture.SubtaskId);
        task.Name = "First changed entity";
        subtask.Text = "Second changed entity";
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER BlockSecondAudit BEFORE INSERT ON ActivityEvents
            WHEN (SELECT COUNT(*) FROM ActivityEvents) = 1
            BEGIN SELECT RAISE(ABORT, 'second audit unavailable'); END;
            """);

        var failure = await Assert.ThrowsAsync<SqliteException>(() => db.SaveChangesWithAuditAsync("Codex"));

        // The trigger can fire only after the first audit INSERT succeeded in the same transaction.
        Assert.Contains("second audit unavailable", failure.Message, StringComparison.Ordinal);
        await using var persisted = factory.CreateDbContext();
        Assert.Equal("Initial task", (await persisted.Tasks.SingleAsync(t => t.Id == fixture.TaskId)).Name);
        Assert.Equal("Initial subtask", (await persisted.Subtasks.SingleAsync(s => s.Id == fixture.SubtaskId)).Text);
        Assert.Empty(await persisted.ActivityEvents.ToListAsync());
        Assert.Equal(EntityState.Modified, db.Entry(task).State);
        Assert.Equal(EntityState.Modified, db.Entry(subtask).State);
    }

    [Fact]
    public async Task SaveChangesWithAuditAsync_OuterRollback_RemovesDataAndAuditTogether()
    {
        using var factory = new SqliteTestFactory();
        var fixture = await SeedAsync(factory);
        await using (var db = factory.CreateDbContext())
        {
            await using var outer = await db.Database.BeginTransactionAsync();
            var subtask = await db.Subtasks.SingleAsync(s => s.Id == fixture.SubtaskId);
            subtask.Text = "Outer transaction edit";

            await db.SaveChangesWithAuditAsync("Codex");

            Assert.Same(outer, db.Database.CurrentTransaction);
            Assert.Equal("Outer transaction edit", (await db.Subtasks.AsNoTracking().SingleAsync(s => s.Id == fixture.SubtaskId)).Text);
            Assert.Single(await db.ActivityEvents.ToListAsync());
            await outer.RollbackAsync();
        }

        await using var persisted = factory.CreateDbContext();
        Assert.Equal("Initial subtask", (await persisted.Subtasks.SingleAsync(s => s.Id == fixture.SubtaskId)).Text);
        Assert.Empty(await persisted.ActivityEvents.ToListAsync());
    }

    [Fact]
    public async Task SaveChangesWithAuditAsync_AuditInsertFailureInOuterTransaction_PreservesEarlierWorkWithoutUnloggedChange()
    {
        using var factory = new SqliteTestFactory();
        var fixture = await SeedAsync(factory);
        await using (var db = factory.CreateDbContext())
        {
            await BlockAuditAsync(db);
            await using var outer = await db.Database.BeginTransactionAsync();
            // This earlier write belongs to the caller's transaction, outside the failing savepoint.
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Projects SET Description = {"Earlier outer work"} WHERE Id = {fixture.ProjectId}");
            var subtask = await db.Subtasks.SingleAsync(s => s.Id == fixture.SubtaskId);
            subtask.Text = "Must not commit without its audit";

            var failure = await Assert.ThrowsAsync<SqliteException>(() => db.SaveChangesWithAuditAsync("Codex"));

            Assert.Equal(19, failure.SqliteErrorCode);
            Assert.Same(outer, db.Database.CurrentTransaction);
            Assert.Equal("Initial subtask", (await db.Subtasks.AsNoTracking().SingleAsync(s => s.Id == fixture.SubtaskId)).Text);
            Assert.Equal("Earlier outer work", (await db.Projects.AsNoTracking().SingleAsync(p => p.Id == fixture.ProjectId)).Description);
            await outer.CommitAsync();
        }

        await using var persisted = factory.CreateDbContext();
        Assert.Equal("Earlier outer work", (await persisted.Projects.SingleAsync(p => p.Id == fixture.ProjectId)).Description);
        Assert.Equal("Initial subtask", (await persisted.Subtasks.SingleAsync(s => s.Id == fixture.SubtaskId)).Text);
        Assert.Empty(await persisted.ActivityEvents.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveChangesWithAuditAsync_NewEntityAuditFailure_RestoresIdentityForRetry(bool useOuterTransaction)
    {
        using var factory = new SqliteTestFactory();
        var fixture = await SeedAsync(factory);
        await using var db = factory.CreateDbContext();
        await BlockAuditAsync(db);
        await using var outer = useOuterTransaction ? await db.Database.BeginTransactionAsync() : null;
        var subtask = new Subtask { TaskItemId = fixture.TaskId, Text = "New item to retry", CreatedAt = T0 };
        db.Subtasks.Add(subtask);
        var originalKey = db.Entry(subtask).Property(s => s.Id).CurrentValue;

        await Assert.ThrowsAsync<SqliteException>(() => db.SaveChangesWithAuditAsync("Codex"));

        Assert.Equal(EntityState.Added, db.Entry(subtask).State);
        Assert.Equal(originalKey, db.Entry(subtask).Property(s => s.Id).CurrentValue);
        Assert.True(db.Entry(subtask).Property(s => s.Id).IsTemporary);
        Assert.False(await db.Subtasks.AsNoTracking().AnyAsync(s => s.Text == "New item to retry"));
        Assert.Empty(await db.ActivityEvents.ToListAsync());

        await UnblockAuditAsync(db);
        await db.SaveChangesWithAuditAsync("Codex");
        if (outer is not null) await outer.CommitAsync();

        await using var persisted = factory.CreateDbContext();
        var saved = Assert.Single(await persisted.Subtasks.Where(s => s.Text == "New item to retry").ToListAsync());
        Assert.True(saved.Id > 0);
        Assert.Equal(saved.Id, subtask.Id);
        Assert.Equal(EntityState.Unchanged, db.Entry(subtask).State);
        var change = Assert.Single(await EventsAsync(factory));
        AssertIdentity(change, fixture, "Subtask", saved.Id, "Codex");
        Assert.Equal("SubtaskCreated", change.EventType);
        Assert.Null(change.BeforeJson);
        using var after = JsonDocument.Parse(change.AfterJson!);
        Assert.Equal(saved.Id, after.RootElement.GetProperty("id").GetInt32());
        Assert.Equal("New item to retry", after.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task SaveChangesWithAuditAsync_NewGraphAuditFailure_RetryUsesGeneratedParentAndChildIds()
    {
        using var factory = new SqliteTestFactory();
        var fixture = await SeedAsync(factory);
        await using var db = factory.CreateDbContext();
        var feature = new Feature { ProjectId = fixture.ProjectId, Name = "New feature" };
        var task = new TaskItem { Feature = feature, Name = "New task" };
        var subtask = new Subtask { TaskItem = task, Text = "New graph child" };
        db.Subtasks.Add(subtask);
        await BlockAuditAsync(db);

        await Assert.ThrowsAsync<SqliteException>(() => db.SaveChangesWithAuditAsync("Codex"));

        Assert.False(await db.Features.AsNoTracking().AnyAsync(f => f.Name == "New feature"));
        Assert.False(await db.Tasks.AsNoTracking().AnyAsync(t => t.Name == "New task"));
        Assert.False(await db.Subtasks.AsNoTracking().AnyAsync(s => s.Text == "New graph child"));
        Assert.Empty(await db.ActivityEvents.ToListAsync());
        await UnblockAuditAsync(db);

        await db.SaveChangesWithAuditAsync("Codex");

        await using var persisted = factory.CreateDbContext();
        var saved = Assert.Single(await persisted.Subtasks.Include(s => s.TaskItem).ThenInclude(t => t.Feature)
            .Where(s => s.Text == "New graph child").ToListAsync());
        Assert.Equal(fixture.ProjectId, saved.TaskItem.Feature.ProjectId);
        Assert.Equal(feature.Id, saved.TaskItem.FeatureId);
        Assert.Equal(task.Id, saved.TaskItemId);
        Assert.Equal(subtask.Id, saved.Id);
        var events = await EventsAsync(factory);
        // Task creation remains TaskService's explicit event contract, outside this helper.
        Assert.Equal(2, events.Count);
        var childEvent = Assert.Single(events, e => e.EntityType == "Subtask");
        Assert.Equal(saved.Id, childEvent.EntityId);
        Assert.Equal(saved.TaskItemId, childEvent.TaskId);
        Assert.Equal(fixture.ProjectId, childEvent.ProjectId);
        var featureEvent = Assert.Single(events, e => e.EntityType == "Feature");
        Assert.Equal(saved.TaskItem.FeatureId, featureEvent.EntityId);
        Assert.Equal(fixture.ProjectId, featureEvent.ProjectId);
    }

    [Fact]
    public async Task SaveChangesWithAuditAsync_NoOpSaves_DoNotAppendEvents()
    {
        using var factory = new SqliteTestFactory();
        var fixture = await SeedAsync(factory);
        await using var db = factory.CreateDbContext();
        var criterion = await db.AcceptanceCriteria.SingleAsync(c => c.Id == fixture.CriterionId);

        Assert.Equal(0, await db.SaveChangesWithAuditAsync("Joel"));
        criterion.Text = "Initial criterion";
        db.Entry(criterion).Property(c => c.Text).IsModified = true;
        await db.SaveChangesWithAuditAsync("Joel");

        await using var persisted = factory.CreateDbContext();
        var saved = await persisted.AcceptanceCriteria.SingleAsync(c => c.Id == fixture.CriterionId);
        Assert.Equal("Initial criterion", saved.Text);
        Assert.False(saved.Completed);
        Assert.Equal(T0, saved.UpdatedAt);
        Assert.Empty(await persisted.ActivityEvents.ToListAsync());
    }

    [Fact]
    public async Task SaveChangesWithAuditAsync_TimestampOnlySave_LeavesPersistedTimestampUnchanged()
    {
        using var factory = new SqliteTestFactory();
        var fixture = await SeedAsync(factory);
        await using var db = factory.CreateDbContext();
        var criterion = await db.AcceptanceCriteria.SingleAsync(c => c.Id == fixture.CriterionId);
        criterion.UpdatedAt = T0.AddHours(1);

        var affected = await db.SaveChangesWithAuditAsync("Joel");

        Assert.Equal(0, affected);
        Assert.Equal(T0, criterion.UpdatedAt);
        Assert.Equal(EntityState.Unchanged, db.Entry(criterion).State);
        await using var persisted = factory.CreateDbContext();
        var saved = await persisted.AcceptanceCriteria.SingleAsync(c => c.Id == fixture.CriterionId);
        Assert.Equal(T0, saved.UpdatedAt);
        Assert.Equal("Initial criterion", saved.Text);
        Assert.False(saved.Completed);
        Assert.Empty(await persisted.ActivityEvents.ToListAsync());
    }

    [Fact]
    public async Task SaveChangesWithAuditAsync_ExistingSemanticEvent_DeduplicatesOnlyItsEntity()
    {
        using var factory = new SqliteTestFactory();
        var fixture = await SeedAsync(factory);
        await using var db = factory.CreateDbContext();
        var task = await db.Tasks.SingleAsync(t => t.Id == fixture.TaskId);
        task.Status = WorkStatus.InProgress;
        var subtask = await db.Subtasks.SingleAsync(s => s.Id == fixture.SubtaskId);
        subtask.Text = "Edited alongside the task";
        db.ActivityEvents.Add(TaskEvent(fixture, "TaskStatusChanged"));

        await db.SaveChangesWithAuditAsync("Codex");

        var events = await EventsAsync(factory);
        Assert.Equal(2, events.Count);
        var semantic = Assert.Single(events, e => e.EntityType == "Task");
        Assert.Equal("TaskStatusChanged", semantic.EventType);
        Assert.Equal("service", semantic.Actor);
        Assert.Equal("Established event", semantic.Message);
        var child = Assert.Single(events, e => e.EntityType == "Subtask");
        AssertIdentity(child, fixture, "Subtask", fixture.SubtaskId, "Codex");
        Assert.Equal("SubtaskUpdated", child.EventType);
        using var before = JsonDocument.Parse(child.BeforeJson!);
        using var after = JsonDocument.Parse(child.AfterJson!);
        Assert.Equal("Initial subtask", before.RootElement.GetProperty("text").GetString());
        Assert.Equal("Edited alongside the task", after.RootElement.GetProperty("text").GetString());
        await using var persisted = factory.CreateDbContext();
        Assert.Equal(WorkStatus.InProgress, (await persisted.Tasks.SingleAsync(t => t.Id == fixture.TaskId)).Status);
    }

    [Theory]
    [InlineData("TaskCommentAdded")]
    [InlineData("TaskDoneOverridden")]
    public async Task SaveChangesWithAuditAsync_CompanionTaskEvent_DoesNotSuppressTaskUpdate(string eventType)
    {
        using var factory = new SqliteTestFactory();
        var fixture = await SeedAsync(factory);
        await using var db = factory.CreateDbContext();
        var task = await db.Tasks.SingleAsync(t => t.Id == fixture.TaskId);
        task.Name = "Edited with companion event";
        db.ActivityEvents.Add(TaskEvent(fixture, eventType));

        await db.SaveChangesWithAuditAsync("Codex");

        var events = await EventsAsync(factory);
        Assert.Equal(2, events.Count);
        Assert.Single(events, e => e.EventType == eventType && e.Actor == "service");
        var updated = Assert.Single(events, e => e.EventType == "TaskUpdated");
        AssertIdentity(updated, fixture, "Task", fixture.TaskId, "Codex");
        using var before = JsonDocument.Parse(updated.BeforeJson!);
        using var after = JsonDocument.Parse(updated.AfterJson!);
        Assert.Equal("Initial task", before.RootElement.GetProperty("name").GetString());
        Assert.Equal("Edited with companion event", after.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task SaveChangesWithAuditAsync_ReassignedRequirement_KeepsBothTaskIdentities()
    {
        using var factory = new SqliteTestFactory();
        var fixture = await SeedAsync(factory);
        await using var db = factory.CreateDbContext();
        var requirement = new Requirement { ProjectId = fixture.ProjectId, TaskItemId = fixture.TaskId, Text = "Moved requirement" };
        db.Requirements.Add(requirement);
        await db.SaveChangesAsync();
        requirement.TaskItemId = fixture.OtherTaskId;

        await db.SaveChangesWithAuditAsync("Joel");

        var change = Assert.Single(await EventsAsync(factory));
        Assert.Equal("RequirementUpdated", change.EventType);
        Assert.Equal(requirement.Id, change.EntityId);
        Assert.Equal(fixture.ProjectId, change.ProjectId);
        Assert.Equal(fixture.OtherTaskId, change.TaskId);
        Assert.Equal(fixture.TaskId, change.RelatedTaskId);
        using var before = JsonDocument.Parse(change.BeforeJson!);
        using var after = JsonDocument.Parse(change.AfterJson!);
        Assert.Equal(fixture.TaskId, before.RootElement.GetProperty("taskItemId").GetInt32());
        Assert.Equal(fixture.OtherTaskId, after.RootElement.GetProperty("taskItemId").GetInt32());
        await using var persisted = factory.CreateDbContext();
        Assert.Equal(fixture.OtherTaskId, (await persisted.Requirements.SingleAsync(r => r.Id == requirement.Id)).TaskItemId);
    }

    [Fact]
    public async Task SaveChangesWithAuditAsync_FractionalReorder_RecordsRankAndTaskContext()
    {
        using var factory = new SqliteTestFactory();
        var fixture = await SeedAsync(factory);
        await using var db = factory.CreateDbContext();
        var task = await db.Tasks.SingleAsync(t => t.Id == fixture.TaskId);
        task.SortOrder = 1.75;

        await db.SaveChangesWithAuditAsync("Joel");

        var change = Assert.Single(await EventsAsync(factory));
        AssertIdentity(change, fixture, "Task", fixture.TaskId, "Joel");
        Assert.Equal("TaskReordered", change.EventType);
        using var before = JsonDocument.Parse(change.BeforeJson!);
        using var after = JsonDocument.Parse(change.AfterJson!);
        Assert.Equal(4, before.RootElement.GetProperty("sortOrder").GetDouble());
        Assert.Equal(1.75, after.RootElement.GetProperty("sortOrder").GetDouble());
        await using var persisted = factory.CreateDbContext();
        Assert.Equal(1.75, (await persisted.Tasks.SingleAsync(t => t.Id == fixture.TaskId)).SortOrder);
        Assert.Equal(8, (await persisted.Tasks.SingleAsync(t => t.Id == fixture.OtherTaskId)).SortOrder);
    }

    [Fact]
    public async Task SaveChangesWithAuditAsync_LoadedTaskCascade_RecordsDeletedChildContext()
    {
        using var factory = new SqliteTestFactory();
        var fixture = await SeedAsync(factory);
        await using var db = factory.CreateDbContext();
        var task = await db.Tasks.Include(t => t.Subtasks).Include(t => t.AcceptanceCriteria)
            .SingleAsync(t => t.Id == fixture.TaskId);
        db.Tasks.Remove(task);

        await db.SaveChangesWithAuditAsync("Joel");

        await using var persisted = factory.CreateDbContext();
        Assert.False(await persisted.Tasks.AnyAsync(t => t.Id == fixture.TaskId));
        Assert.False(await persisted.Subtasks.AnyAsync(s => s.Id == fixture.SubtaskId));
        Assert.False(await persisted.AcceptanceCriteria.AnyAsync(c => c.Id == fixture.CriterionId));
        Assert.True(await persisted.Tasks.AnyAsync(t => t.Id == fixture.OtherTaskId));
        var events = await EventsAsync(factory);
        Assert.Equal(3, events.Count);
        foreach (var (type, id, text) in new[]
        {
            ("Task", fixture.TaskId, "Initial task"),
            ("Subtask", fixture.SubtaskId, "Initial subtask"),
            ("AcceptanceCriterion", fixture.CriterionId, "Initial criterion")
        })
        {
            var change = Assert.Single(events, e => e.EntityType == type);
            AssertIdentity(change, fixture, type, id, "Joel");
            Assert.Equal(type + "Deleted", change.EventType);
            Assert.Null(change.AfterJson);
            using var before = JsonDocument.Parse(change.BeforeJson!);
            Assert.Equal(text, before.RootElement.GetProperty(type == "Task" ? "name" : "text").GetString());
        }
    }

    [Theory]
    [InlineData(null, null, "/", "System")]
    [InlineData("  ", null, "/api/subtasks", "api")]
    [InlineData(null, "  Joel  ", "/", "Joel")]
    [InlineData("  Codex  ", "Joel", "/api/subtasks", "Codex")]
    public async Task SaveChangesWithAuditAsync_ActorSelection_UsesExplicitActorOrContextDefault(
        string? actor, string? header, string path, string expected)
    {
        using var factory = new SqliteTestFactory();
        var fixture = await SeedAsync(factory);
        var http = new DefaultHttpContext();
        http.Request.Path = path;
        if (header is not null) http.Request.Headers["X-KanbanFlow-Actor"] = header;
        await using var db = new AppDbContext(factory.Options, new HttpContextAccessor { HttpContext = http });
        var subtask = await db.Subtasks.SingleAsync(s => s.Id == fixture.SubtaskId);
        subtask.Text = "Attributed edit";

        await db.SaveChangesWithAuditAsync(actor);

        var change = Assert.Single(await EventsAsync(factory));
        AssertIdentity(change, fixture, "Subtask", fixture.SubtaskId, expected);
        Assert.Equal("SubtaskUpdated", change.EventType);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(201)]
    [InlineData(202)]
    public async Task SaveChangesWithAuditAsync_ActorLengthBoundary_RejectsOnlyOversizedAttribution(int length)
    {
        using var factory = new SqliteTestFactory();
        var fixture = await SeedAsync(factory);
        await using var db = factory.CreateDbContext();
        var subtask = await db.Subtasks.SingleAsync(s => s.Id == fixture.SubtaskId);
        subtask.Text = "Attributed edit";
        var actor = new string('a', length);

        if (length > 200)
        {
            await Assert.ThrowsAsync<ValidationException>(() => db.SaveChangesWithAuditAsync(actor));
            await using var rejected = factory.CreateDbContext();
            Assert.Equal("Initial subtask", (await rejected.Subtasks.SingleAsync(s => s.Id == fixture.SubtaskId)).Text);
            Assert.Empty(await rejected.ActivityEvents.ToListAsync());
        }
        else
        {
            await db.SaveChangesWithAuditAsync(actor);
            var change = Assert.Single(await EventsAsync(factory));
            Assert.Equal(actor, change.Actor);
            await using var accepted = factory.CreateDbContext();
            Assert.Equal("Attributed edit", (await accepted.Subtasks.SingleAsync(s => s.Id == fixture.SubtaskId)).Text);
        }
    }

    [Fact]
    public async Task EnsureActivityContextColumns_LegacyTable_PreservesRowsAndIsIdempotent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var legacy = connection.CreateCommand())
        {
            legacy.CommandText = """
                CREATE TABLE ActivityEvents (
                    Id INTEGER PRIMARY KEY, ProjectId INTEGER NOT NULL, EntityType TEXT NOT NULL,
                    EntityId INTEGER NOT NULL, Actor TEXT, EventType TEXT NOT NULL,
                    BeforeJson TEXT, AfterJson TEXT, Message TEXT, CreatedAt TEXT NOT NULL);
                INSERT INTO ActivityEvents VALUES (
                    42, 11, 'Task', 7, 'Joel', 'TaskUpdated', '{"name":"old"}', '{"name":"new"}',
                    'Retain this historical message', '2026-09-07T12:00:00Z');
                """;
            await legacy.ExecuteNonQueryAsync();
        }

        SchemaPatcher.EnsureActivityContextColumns(connection);
        SchemaPatcher.EnsureActivityContextColumns(connection);

        await using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT Id, ProjectId, EntityType, EntityId, Actor, EventType, BeforeJson, AfterJson, Message, CreatedAt, TaskId, RelatedTaskId FROM ActivityEvents";
            await using var reader = await read.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(42, reader.GetInt32(0));
            Assert.Equal(11, reader.GetInt32(1));
            Assert.Equal("Task", reader.GetString(2));
            Assert.Equal(7, reader.GetInt32(3));
            Assert.Equal("Joel", reader.GetString(4));
            Assert.Equal("TaskUpdated", reader.GetString(5));
            Assert.Equal("{\"name\":\"old\"}", reader.GetString(6));
            Assert.Equal("{\"name\":\"new\"}", reader.GetString(7));
            Assert.Equal("Retain this historical message", reader.GetString(8));
            Assert.Equal("2026-09-07T12:00:00Z", reader.GetString(9));
            Assert.True(reader.IsDBNull(10));
            Assert.True(reader.IsDBNull(11));
            Assert.False(await reader.ReadAsync());
        }
        await using var columns = connection.CreateCommand();
        columns.CommandText = "SELECT name, type, \"notnull\" FROM pragma_table_info('ActivityEvents') WHERE name IN ('TaskId', 'RelatedTaskId') ORDER BY name";
        await using var columnReader = await columns.ExecuteReaderAsync();
        foreach (var expected in new[] { "RelatedTaskId", "TaskId" })
        {
            Assert.True(await columnReader.ReadAsync());
            Assert.Equal(expected, columnReader.GetString(0));
            Assert.Equal("INTEGER", columnReader.GetString(1));
            Assert.Equal(0, columnReader.GetInt32(2));
        }
        Assert.False(await columnReader.ReadAsync());
    }

    private static object CreateChild(string kind, Fixture fixture) => kind switch
    {
        "Subtask" => new Subtask { TaskItemId = fixture.TaskId, Text = "Initial checklist", CreatedAt = T0 },
        "AcceptanceCriterion" => new AcceptanceCriterion
        {
            TaskItemId = fixture.TaskId, Text = "Initial checklist", CreatedAt = T0, UpdatedAt = T0
        },
        "Requirement" => new Requirement
        {
            ProjectId = fixture.ProjectId, TaskItemId = fixture.TaskId, Text = "Initial checklist", CreatedAt = T0, UpdatedAt = T0
        },
        "TaskDependency" => new TaskDependency
        {
            ProjectId = fixture.ProjectId, TaskItemId = fixture.TaskId,
            DependsOnTaskItemId = fixture.OtherTaskId, CreatedAt = T0
        },
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static void ChangeChild(object child)
    {
        switch (child)
        {
            case Subtask subtask:
                subtask.Text = "Revised checklist";
                subtask.Completed = true;
                break;
            case AcceptanceCriterion criterion:
                criterion.Text = "Revised checklist";
                criterion.Completed = true;
                break;
            case Requirement requirement:
                requirement.Text = "Revised checklist";
                requirement.Priority = RequirementPriority.P0;
                break;
            case TaskDependency dependency:
                dependency.Type = TaskDependencyType.Blocks;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(child));
        }
    }

    private static void AssertChildSnapshot(string? json, string kind, Fixture fixture, int id, bool changed)
    {
        Assert.NotNull(json);
        using var document = JsonDocument.Parse(json);
        var snapshot = document.RootElement;
        Assert.Equal(id, snapshot.GetProperty("id").GetInt32());
        Assert.Equal(fixture.TaskId, snapshot.GetProperty("taskItemId").GetInt32());
        Assert.Equal(T0, snapshot.GetProperty("createdAt").GetDateTime());
        if (kind == "TaskDependency")
        {
            Assert.Equal(fixture.OtherTaskId, snapshot.GetProperty("dependsOnTaskItemId").GetInt32());
            Assert.Equal(changed ? "Blocks" : "DependsOn", snapshot.GetProperty("type").GetString());
        }
        else
        {
            Assert.Equal(changed ? "Revised checklist" : "Initial checklist", snapshot.GetProperty("text").GetString());
            if (kind == "Requirement")
                Assert.Equal(changed ? "P0" : "P1", snapshot.GetProperty("priority").GetString());
            else
                Assert.Equal(changed, snapshot.GetProperty("completed").GetBoolean());
        }
    }

    private static void AssertIdentity(ActivityEvent change, Fixture fixture, string type, int id, string actor, int? relatedTaskId = null)
    {
        Assert.Equal(fixture.ProjectId, change.ProjectId);
        Assert.Equal(type, change.EntityType);
        Assert.Equal(id, change.EntityId);
        Assert.Equal(fixture.TaskId, change.TaskId);
        Assert.Equal(relatedTaskId, change.RelatedTaskId);
        Assert.Equal(actor, change.Actor);
    }

    private static ActivityEvent TaskEvent(Fixture fixture, string eventType) => new()
    {
        ProjectId = fixture.ProjectId, EntityType = "Task", EntityId = fixture.TaskId, TaskId = fixture.TaskId,
        Actor = "service", EventType = eventType, Message = "Established event",
        BeforeJson = "{\"status\":\"Todo\"}", AfterJson = "{\"status\":\"InProgress\"}", CreatedAt = T0
    };

    private static Task BlockAuditAsync(AppDbContext db) => db.Database.ExecuteSqlRawAsync("""
        CREATE TRIGGER BlockAudit BEFORE INSERT ON ActivityEvents
        BEGIN SELECT RAISE(ABORT, 'audit unavailable'); END;
        """);

    private static Task UnblockAuditAsync(AppDbContext db) => db.Database.ExecuteSqlRawAsync("DROP TRIGGER BlockAudit;");

    private static async Task<List<ActivityEvent>> EventsAsync(SqliteTestFactory factory)
    {
        await using var db = factory.CreateDbContext();
        return await db.ActivityEvents.OrderBy(e => e.Id).ToListAsync();
    }

    private static async Task<Fixture> SeedAsync(SqliteTestFactory factory)
    {
        await using var db = factory.CreateDbContext();
        var project = new Project { Name = "Audit project", Description = "Initial project", CreatedAt = T0, UpdatedAt = T0 };
        var feature = new Feature { Project = project, Name = "Audit feature", CreatedAt = T0, UpdatedAt = T0 };
        var task = new TaskItem
        {
            Feature = feature, Name = "Initial task", Description = "Initial description", Status = WorkStatus.Todo,
            Priority = TaskPriority.Low, Assignee = "Joel", SortOrder = 4, CreatedAt = T0, UpdatedAt = T0
        };
        var otherTask = new TaskItem { Feature = feature, Name = "Other task", SortOrder = 8, CreatedAt = T0, UpdatedAt = T0 };
        var subtask = new Subtask { TaskItem = task, Text = "Initial subtask", CreatedAt = T0 };
        var criterion = new AcceptanceCriterion { TaskItem = task, Text = "Initial criterion", CreatedAt = T0, UpdatedAt = T0 };
        db.Tasks.AddRange(task, otherTask);
        db.Subtasks.Add(subtask);
        db.AcceptanceCriteria.Add(criterion);
        await db.SaveChangesAsync();
        return new(project.Id, feature.Id, task.Id, otherTask.Id, subtask.Id, criterion.Id);
    }

    private sealed record Fixture(int ProjectId, int FeatureId, int TaskId, int OtherTaskId, int SubtaskId, int CriterionId);
}
