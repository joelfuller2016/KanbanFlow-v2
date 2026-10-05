using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using KanbanFlow.Data;
using KanbanFlow.Models;
using KanbanFlow.Models.Workflows;
using KanbanFlow.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace KanbanFlow.Tests;

public sealed class AuditBatchTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(100)]
    public async Task DeleteGraph_BatchesFreshSnapshotsAndParentLookups(int count)
    {
        using var factory = new SqliteTestFactory();
        var reads = new ReadCounter();
        var options = new DbContextOptionsBuilder<AppDbContext>(factory.Options).AddInterceptors(reads).Options;
        await using var db = new AppDbContext(options);
        var project = new Project { Name = "Batch audit" };
        var feature = new Feature { Name = "Feature", Project = project };
        var tasks = Enumerable.Range(0, count).Select(i => new TaskItem { Name = $"Task {i}", Feature = feature }).ToArray();
        var children = tasks.Select(t => new Subtask { TaskItem = t, Text = "Checklist" }).ToArray();
        var criteria = tasks.Select(t => new AcceptanceCriterion { TaskItem = t, Text = "Acceptance" }).ToArray();
        db.Tasks.AddRange(tasks);
        db.Subtasks.AddRange(children);
        db.AcceptanceCriteria.AddRange(criteria);
        await db.SaveChangesAsync();
        db.RemoveRange(children);
        db.RemoveRange(criteria);
        db.RemoveRange(tasks);
        reads.Count = 0;

        await db.SaveChangesWithAuditAsync("reviewer");

        // Three entity batches and two parent batches, independent of graph size.
        Assert.InRange(reads.Count, 1, 5);
        var events = await db.ActivityEvents.ToListAsync();
        Assert.Equal(3 * count, events.Count);
        Assert.All(events, e =>
        {
            Assert.Equal(project.Id, e.ProjectId);
            Assert.Contains(e.TaskId, tasks.Select(t => (int?)t.Id));
            Assert.Equal(e.EntityId.ToString(CultureInfo.InvariantCulture), e.EntityKey);
            Assert.Equal("reviewer", e.Actor);
            Assert.NotNull(e.BeforeJson);
            Assert.Null(e.AfterJson);
        });
        Assert.Empty(await db.Tasks.ToListAsync());
    }

    [Fact]
    public async Task Delete_RecordsLatestPersistedContentDespiteStaleTrackedEntity()
    {
        using var factory = new SqliteTestFactory();
        await using var deleting = factory.CreateDbContext();
        var project = new Project { Name = "Original" };
        deleting.Projects.Add(project);
        await deleting.SaveChangesAsync();
        await using (var concurrent = factory.CreateDbContext())
            await concurrent.Projects.Where(p => p.Id == project.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(p => p.Name, "Human saved before deletion"));
        deleting.Remove(project);

        await deleting.SaveChangesWithAuditAsync("reviewer");

        var audit = await deleting.ActivityEvents.SingleAsync();
        using var before = JsonDocument.Parse(audit.BeforeJson!);
        Assert.Equal("Human saved before deletion", before.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task WorkflowDeletes_RetainDistinctGuidAndLongKeysAndCanBeReadByKey()
    {
        using var factory = new SqliteTestFactory();
        await using var db = factory.CreateDbContext();
        var project = new Project { Name = "Workflow audit" };
        var requests = Enumerable.Range(0, 2).Select(i => new WorkRequest
        {
            Project = project, Idea = new Idea { Title = $"Idea {i}" }, Title = $"Request {i}"
        }).ToArray();
        var receipt = new WorkflowReceipt
        {
            Id = (long)int.MaxValue + 42, Actor = "reviewer", Operation = "capture", Key = "retry-key",
            PayloadHash = "hash", ResultJson = "{}"
        };
        db.WorkRequests.AddRange(requests);
        db.WorkflowReceipts.Add(receipt);
        await db.SaveChangesAsync();
        db.RemoveRange(requests);
        db.Remove(receipt);

        await db.SaveChangesWithAuditAsync("reviewer");

        Assert.Equal(2, await ActivityLogReader.Query(db, entityType: nameof(WorkRequest)).CountAsync());
        foreach (var request in requests)
        {
            var page = await ActivityLogReader.ReadAsync(db, projectId: project.Id,
                entityType: nameof(WorkRequest), entityKey: request.Id.ToString("B").ToUpperInvariant());
            var audit = Assert.Single(page.Items);
            Assert.Equal(request.Id.ToString("D"), audit.EntityKey);
            using var before = JsonDocument.Parse(audit.BeforeJson!);
            Assert.Equal(request.Id, before.RootElement.GetProperty("id").GetGuid());
        }
        var longKey = receipt.Id.ToString(CultureInfo.InvariantCulture);
        var longAudit = await ActivityLogReader.Query(db, entityType: nameof(WorkflowReceipt), entityKey: longKey).SingleAsync();
        Assert.Equal(longKey, longAudit.EntityKey);
        using var receiptBefore = JsonDocument.Parse(longAudit.BeforeJson!);
        Assert.Equal(receipt.Id, receiptBefore.RootElement.GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task EntityFilters_KeepLegacyIntegerHistoryAndTypeOnlyQueries()
    {
        using var factory = new SqliteTestFactory();
        await using var db = factory.CreateDbContext();
        db.ActivityEvents.AddRange(
            new ActivityEvent { EntityType = "Task", EntityId = 42, EventType = "TaskCreated" },
            new ActivityEvent { EntityType = "Task", EntityId = 43, EntityKey = "43", EventType = "TaskUpdated" },
            new ActivityEvent { EntityType = "Feature", EntityId = 42, EventType = "FeatureCreated" });
        await db.SaveChangesAsync();
        Assert.Equal(2, await ActivityLogReader.Query(db, entityType: "Task").CountAsync());
        Assert.Equal(42, (await ActivityLogReader.Query(db, entityType: "Task", entityKey: "42").SingleAsync()).EntityId);
        Assert.Equal("43", (await ActivityLogReader.Query(db, entityType: "Task", entityId: 43).SingleAsync()).EntityKey);
        Assert.Throws<ValidationException>(() => ActivityLogReader.Query(db, entityId: 42, entityKey: "42"));
    }

    [Fact]
    public async Task RequestChanges_UseWorkflowGuidWhileKeepingLegacyIdeaIdentity()
    {
        using var factory = new WorkflowSqliteTestFactory();
        var request = await factory.BriefedAsync();
        await using var db = factory.CreateDbContext();
        var page = await ActivityLogReader.ReadAsync(db, projectId: request.ProjectId,
            entityType: "WorkRequest", entityKey: request.Id.ToString("D"));
        Assert.Equal(2, page.Total);
        Assert.All(page.Items, e => Assert.Equal(request.IdeaId, e.EntityId));
        Assert.Contains(page.Items, e => e.EventType == "RequestCaptured");
        Assert.Contains(page.Items, e => e.EventType == "brief.save");
    }

    [Fact]
    public async Task SchemaPatch_RecoversLegacyWorkflowKeysWithoutRewritingSnapshots()
    {
        using var factory = new SqliteTestFactory();
        await using var db = factory.CreateDbContext();
        var guid = Guid.NewGuid();
        var snapshots = new[] { $"{{\"id\":\"{guid:D}\",\"text\":\"  preserved  \"}}", "{\"id\":2147483689}", "malformed historical JSON" };
        foreach (var snapshot in snapshots)
            db.ActivityEvents.Add(new ActivityEvent { EntityType = "LegacyWorkflow", EntityId = 0, EventType = "Deleted", BeforeJson = snapshot });
        var legacyAfter = $"{{\"requestId\":\"{guid:D}\",\"operation\":\"brief.save\"}}";
        db.ActivityEvents.Add(new ActivityEvent { EntityType = "WorkRequest", EntityId = 23, EventType = "brief.save", AfterJson = legacyAfter });
        await db.SaveChangesAsync();
        SchemaPatcher.EnsureActivityContextColumns(db.Database.GetDbConnection());
        SchemaPatcher.EnsureActivityContextColumns(db.Database.GetDbConnection());
        var rows = await db.ActivityEvents.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
        Assert.Equal(snapshots, rows.Take(3).Select(e => e.BeforeJson));
        Assert.Equal(new string?[] { guid.ToString("D"), "2147483689", null, guid.ToString("D") }, rows.Select(e => e.EntityKey));
        Assert.Equal(23, rows[^1].EntityId);
        Assert.Equal(legacyAfter, rows[^1].AfterJson);
    }

    private sealed class ReadCounter : DbCommandInterceptor
    {
        public int Count { get; set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)) Count++;
            return ValueTask.FromResult(result);
        }
    }
}
