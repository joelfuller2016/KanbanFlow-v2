using System.Data.Common;
using KanbanFlow.Models;
using KanbanFlow.Models.Workflows;
using KanbanFlow.Services.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit.Abstractions;
using static KanbanFlow.Tests.WorkflowSqliteTestFactory;

namespace KanbanFlow.Tests.Services;

public sealed class WorkspaceQueryTests(ITestOutputHelper output)
{
    [Fact]
    public async Task TaskStateHash_CycleTerminatesAndMissingDependencyRejectsPartialGraph()
    {
        var counter = new QueryCounter();
        using var factory = new WorkflowSqliteTestFactory(counter);
        var a = Draft("A");
        var b = Draft("B", a.Id);
        var request = await factory.PublishedAsync(a, b);
        var first = request.WorkItems.Single(t => t.DraftId == a.Id).TaskItemId;
        var second = request.WorkItems.Single(t => t.DraftId == b.Id).TaskItemId;
        await using var db = factory.CreateDbContext();
        // Legacy corruption can bypass the domain cycle guard. Reads must still finish.
        db.TaskDependencies.Add(new TaskDependency { ProjectId = request.ProjectId!.Value, TaskItemId = first, DependsOnTaskItemId = second });
        await db.SaveChangesAsync();
        counter.Reset();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var hash = await WorkspaceService.TaskStateHashAsync(db, first, timeout.Token);
        Assert.Matches("^[0-9A-F]{64}$", hash);
        Assert.InRange(counter.Reads.Count, 1, 5);

        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF");
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TaskDependencies SET DependsOnTaskItemId = {int.MaxValue} WHERE TaskItemId = {first}");
        Assert.Equal("missing-task", await WorkspaceService.TaskStateHashAsync(db, first));
        Assert.Equal("missing-task", await WorkspaceService.TaskStateHashAsync(db, int.MaxValue));
    }

    [Fact]
    public async Task TaskStateHash_ReadsPersistedReachableStateAndNeverCachesAcrossOperations()
    {
        using var factory = new WorkflowSqliteTestFactory();
        var request = await factory.PublishedAsync(Draft("Root"));
        var unrelated = await factory.PublishedAsync(Draft("Unrelated"));
        var taskId = request.WorkItems.Single().TaskItemId;
        await using var db = factory.CreateDbContext();
        var before = await WorkspaceService.TaskStateHashAsync(db, taskId);
        var tracked = await db.Tasks.SingleAsync(t => t.Id == taskId);
        tracked.Description = "Unsaved human draft";
        Assert.Equal(before, await WorkspaceService.TaskStateHashAsync(db, taskId));
        var unrelatedId = unrelated.WorkItems.Single().TaskItemId;
        await db.Tasks.Where(t => t.Id == unrelatedId).ExecuteUpdateAsync(s => s.SetProperty(t => t.Description, "Other graph changed"));
        Assert.Equal(before, await WorkspaceService.TaskStateHashAsync(db, taskId));
        await db.SaveChangesAsync();
        Assert.NotEqual(before, await WorkspaceService.TaskStateHashAsync(db, taskId));
    }

    [Fact]
    public async Task TaskStateHashes_MatchPreviouslyPublishedHashFormat()
    {
        using var factory = new WorkflowSqliteTestFactory();
        var first = Draft("First");
        var second = Draft("Second", first.Id);
        var third = Draft("Third", first.Id, second.Id);
        var request = await factory.PublishedAsync(first, second, third);
        await using var db = factory.CreateDbContext();
        var hashes = new List<string>();
        foreach (var task in request.WorkItems.OrderBy(t => t.TaskItemId))
            hashes.Add(await WorkspaceService.TaskStateHashAsync(db, task.TaskItemId));
        output.WriteLine(string.Join("\n", hashes));
        // Captured from the pre-batch implementation at 77c44fc using this fixed graph.
        Assert.Equal(new[] {
            "D62554382EA39AE138710309AF7DF4C29BD6335F252C35D241214435BF18C2DD",
            "B59D53C21150479CBA30CC4A6EEE231989E2DA73FF798063B51D02C71C60CF90",
            "2CF56DF7578433B990A8C6074DA84FD5EEE27962E2FD225B35D27772D6EAA69F"
        }, hashes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(12)]
    [InlineData(100)]
    public async Task ContextAndPlanRevision_BoundReadsAcrossDependencyChains(int count)
    {
        var counter = new QueryCounter();
        using var factory = new WorkflowSqliteTestFactory(counter);
        var drafts = new List<TaskDraft>();
        for (var i = 0; i < count; i++)
            drafts.Add(Draft($"Task {i}", i == 0 ? [] : [drafts[^1].Id]));
        var request = await factory.PublishedAsync(drafts.ToArray());

        counter.Reset();
        var context = (await factory.Service.ContextAsync(request.Id))!;
        var contextReads = counter.Reads.Count;
        output.WriteLine($"Context {count} tasks: {contextReads} reads");
        Assert.Equal(count, context.Tasks.Count);
        Assert.All(context.Tasks, task => Assert.Matches("^[0-9A-F]{64}$", task.EditToken));

        var revised = drafts.ToArray();
        revised[^1] = revised[^1] with { Description = "Revised last task" };
        counter.Reset();
        var saved = await factory.Service.SavePlanAsync(request.Id, context.Version, revised, Actor, Key(),
            expectedTaskStates: context.Tasks.ToDictionary(t => t.TaskId, t => t.EditToken));
        var saveReads = counter.Reads.Count;
        output.WriteLine($"Save {count} tasks: {saveReads} reads");
        var savedRequest = await factory.LoadAsync(request.Id);

        counter.Reset();
        await factory.Service.PublishPlanAsync(request.Id, saved.Version, savedRequest.Plans.MaxBy(p => p.Number)!.Id, Actor, Key());
        var publishReads = counter.Reads.Count;
        output.WriteLine($"Publish {count} tasks (one changed): {publishReads} reads");
        var published = await factory.LoadAsync(request.Id);
        Assert.Equal("Revised last task", published.WorkItems.Single(t => t.DraftId == revised[^1].Id).Task.Description);
        Assert.Equal(request.WorkItems.Select(t => t.TaskItemId).Order(), published.WorkItems.Select(t => t.TaskItemId).Order());

        Assert.True(contextReads <= 30, $"Context issued {contextReads} reads for {count} tasks; expected a bounded batch (at most 30).");
        Assert.True(saveReads <= 30, $"Save issued {saveReads} reads for {count} tasks; expected a bounded batch (at most 30).");
        Assert.True(publishReads <= 40, $"Publish issued {publishReads} reads for {count} tasks; expected a bounded batch (at most 40).");
    }

    private sealed class QueryCounter : DbCommandInterceptor
    {
        public List<string> Reads { get; } = [];
        public void Reset() => Reads.Clear();
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            var sql = command.CommandText.TrimStart();
            if (sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) || sql.StartsWith("WITH", StringComparison.OrdinalIgnoreCase))
                Reads.Add(sql);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
