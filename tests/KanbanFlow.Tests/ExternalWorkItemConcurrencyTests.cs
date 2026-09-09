using KanbanFlow.Models;
using KanbanFlow.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace KanbanFlow.Tests;

public sealed class ExternalWorkItemConcurrencyTests
{
    public enum ConcurrentDeleteScope
    {
        Task,
        Feature,
        Project
    }

    [Fact]
    public async Task ConcurrentFileSqlitePrepare_OneWins_OneGetsDeterministicConflict()
    {
        using var factory = new SqliteFileTestFactory(commandTimeoutSeconds: 30);
        var task = await SeedTaskAsync(factory);
        var service = new ExternalWorkItemService(
            factory,
            new FakeTimeProvider(new DateTimeOffset(task.UpdatedAt, TimeSpan.Zero)),
            TimeSpan.FromMinutes(2));
        var request = new PrepareExternalWriteRequest(
            task.TaskId,
            ExternalProvider.GitHub,
            "R_concurrent",
            "acme",
            "race-repo",
            "Race title",
            "Race body",
            null,
            null,
            task.UpdatedAt);

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var first = Task.Run(async () =>
        {
            await start.Task.WaitAsync(watchdog.Token);
            return await CaptureAsync(service.PrepareAsync(request, watchdog.Token));
        }, watchdog.Token);
        var second = Task.Run(async () =>
        {
            await start.Task.WaitAsync(watchdog.Token);
            return await CaptureAsync(service.PrepareAsync(request, watchdog.Token));
        }, watchdog.Token);

        start.SetResult();
        var outcomes = await Task.WhenAll(first, second).WaitAsync(watchdog.Token);

        Assert.Single(outcomes, outcome => outcome.Lease is not null);
        var loser = Assert.Single(outcomes, outcome => outcome.Error is not null);
        var conflict = Assert.IsType<ExternalWorkItemConflictException>(loser.Error);
        Assert.Equal("external-operation-exists", conflict.ConflictCode);
        await using var db = await factory.CreateDbContextAsync();
        Assert.Single(await db.ExternalWriteOperations.ToListAsync());
        Assert.Single(await db.ActivityEvents.Where(e => e.EventType == "ExternalWritePrepared").ToListAsync());
    }

    [Fact]
    public async Task ConcurrentManualRetryAuthorizations_ConvergeToOneAuditEvent()
    {
        using var factory = new SqliteFileTestFactory(commandTimeoutSeconds: 30);
        var task = await SeedTaskAsync(factory);
        var clock = new FakeTimeProvider(new DateTimeOffset(task.UpdatedAt, TimeSpan.Zero));
        var service = new ExternalWorkItemService(factory, clock, TimeSpan.FromSeconds(2));
        var prepared = await service.PrepareAsync(new PrepareExternalWriteRequest(
            task.TaskId,
            ExternalProvider.GitHub,
            "R_manual_retry",
            "acme",
            "race-repo",
            "Race title",
            "Race body",
            null,
            null,
            task.UpdatedAt));
        await service.MarkDispatchedAsync(prepared.Operation.OperationId, prepared.LeaseToken);
        clock.Advance(TimeSpan.FromSeconds(3));
        var recovery = await service.ResumeAsync(prepared.Operation.OperationId);
        await service.RecordFailureAsync(
            prepared.Operation.OperationId,
            recovery.LeaseToken,
            ExternalWriteFailureKind.AmbiguousOrIncomplete,
            "complete scan found zero matches");
        var request = new AuthorizeAmbiguousExternalWriteRetryRequest(
            prepared.Operation.OperationId,
            prepared.Operation.OperationId,
            recovery.Operation.AttemptCount,
            true,
            0,
            0,
            clock.GetUtcNow(),
            "operator",
            "prior process stopped; accept delayed visibility risk");

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var first = Task.Run(async () =>
        {
            await start.Task.WaitAsync(watchdog.Token);
            return await service.AuthorizeAmbiguousRetryAsync(request, watchdog.Token);
        }, watchdog.Token);
        var second = Task.Run(async () =>
        {
            await start.Task.WaitAsync(watchdog.Token);
            return await service.AuthorizeAmbiguousRetryAsync(request, watchdog.Token);
        }, watchdog.Token);

        start.SetResult();
        var outcomes = await Task.WhenAll(first, second).WaitAsync(watchdog.Token);

        Assert.All(outcomes, operation =>
            Assert.Equal(ExternalWriteState.ManualRetryAuthorized, operation.State));
        await using var db = await factory.CreateDbContextAsync();
        Assert.Single(await db.ExternalWriteOperations.ToListAsync());
        Assert.Single(await db.ActivityEvents
            .Where(e => e.EventType == "ExternalWriteManualRetryAuthorized")
            .ToListAsync());
    }

    [Theory]
    [InlineData(ConcurrentDeleteScope.Task)]
    [InlineData(ConcurrentDeleteScope.Feature)]
    [InlineData(ConcurrentDeleteScope.Project)]
    public async Task ConcurrentPrepareAndDelete_ProduceOnlySerializedValidOutcome(
        ConcurrentDeleteScope deleteScope)
    {
        using var factory = new SqliteFileTestFactory(commandTimeoutSeconds: 30);
        var task = await SeedTaskAsync(factory);
        var externalService = new ExternalWorkItemService(
            factory,
            new FakeTimeProvider(new DateTimeOffset(task.UpdatedAt, TimeSpan.Zero)),
            TimeSpan.FromMinutes(2));
        var taskService = new TaskService(factory);
        var request = new PrepareExternalWriteRequest(
            task.TaskId,
            ExternalProvider.GitHub,
            "R_delete_race",
            "acme",
            "race-repo",
            "Race title",
            "Race body",
            null,
            null,
            task.UpdatedAt);

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var prepareTask = Task.Run(async () =>
        {
            await start.Task.WaitAsync(watchdog.Token);
            return await CaptureAsync(externalService.PrepareAsync(request, watchdog.Token));
        }, watchdog.Token);
        var deleteTask = Task.Run(async () =>
        {
            await start.Task.WaitAsync(watchdog.Token);
            Task deletion;
            if (deleteScope == ConcurrentDeleteScope.Task)
            {
                deletion = taskService.DeleteAsync(task.TaskId, "race-test", watchdog.Token);
            }
            else if (deleteScope == ConcurrentDeleteScope.Project)
            {
                deletion = taskService.DeleteProjectAsync(
                    task.ProjectId,
                    "race-test",
                    watchdog.Token);
            }
            else
            {
                await using var db = await factory.CreateDbContextAsync(watchdog.Token);
                deletion = ApiEndpoints.DeleteFeatureAsync(task.FeatureId, db, watchdog.Token);
                return await CaptureExceptionAsync(deletion);
            }
            return await CaptureExceptionAsync(deletion);
        }, watchdog.Token);

        start.SetResult();
        await Task.WhenAll(prepareTask, deleteTask).WaitAsync(watchdog.Token);
        var prepare = await prepareTask;
        var deleteError = await deleteTask;

        await using var db = await factory.CreateDbContextAsync();
        if (prepare.Lease is not null)
        {
            // Prepare owned the first immediate transaction. The delete then observed its durable
            // nonterminal operation and lost with a deterministic conflict.
            var conflict = Assert.IsType<ExternalWorkItemConflictException>(deleteError);
            Assert.Equal("external-operation-blocks-delete", conflict.ConflictCode);
            Assert.Null(prepare.Error);
            Assert.NotNull(await db.Tasks.FindAsync(task.TaskId));
            Assert.NotNull(await db.Projects.FindAsync(task.ProjectId));
            Assert.Single(await db.ExternalWriteOperations.ToListAsync());
        }
        else
        {
            // Delete owned the first immediate transaction. Prepare runs only after the cascade
            // commits, sees no task, and cannot leave an orphan journal.
            Assert.Null(deleteError);
            Assert.IsType<TaskNotFoundException>(prepare.Error);
            Assert.Null(await db.Tasks.FindAsync(task.TaskId));
            Assert.Empty(await db.ExternalWriteOperations.ToListAsync());
        }
    }

    private static async Task<PrepareOutcome> CaptureAsync(Task<ExternalWriteLeaseResult> operation)
    {
        try
        {
            return new PrepareOutcome(await operation, null);
        }
        catch (Exception ex)
        {
            return new PrepareOutcome(null, ex);
        }
    }

    private static async Task<Exception?> CaptureExceptionAsync(Task operation)
    {
        try
        {
            await operation;
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static async Task<SeededTask> SeedTaskAsync(SqliteFileTestFactory factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        var updatedAt = new DateTime(2026, 8, 30, 20, 0, 0, DateTimeKind.Utc);
        var task = new TaskItem
        {
            Name = "Concurrent task",
            UpdatedAt = updatedAt,
            Feature = new Feature
            {
                Name = "Concurrent feature",
                Project = new Project { Name = "Concurrent project" }
            }
        };
        db.Tasks.Add(task);
        await db.SaveChangesAsync();
        return new SeededTask(task.Id, task.FeatureId, task.Feature.ProjectId, task.UpdatedAt);
    }

    private sealed record SeededTask(int TaskId, int FeatureId, int ProjectId, DateTime UpdatedAt);
    private sealed record PrepareOutcome(ExternalWriteLeaseResult? Lease, Exception? Error);
}
