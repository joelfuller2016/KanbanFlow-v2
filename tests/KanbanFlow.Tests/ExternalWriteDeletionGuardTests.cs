using KanbanFlow.Data;
using KanbanFlow.Models;
using KanbanFlow.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Tests;

/// <summary>
/// Deletion is the last line of defense for the durable external-write journal. An in-flight
/// create still needs its originating task for reconciliation/finalization, while terminal journal
/// evidence intentionally survives deletion because the operation has no task foreign key.
/// </summary>
public sealed class ExternalWriteDeletionGuardTests
{
    public enum DeleteScope
    {
        Task,
        Feature,
        Project
    }

    public static TheoryData<DeleteScope, ExternalWriteState> BlockingCases => new()
    {
        { DeleteScope.Task, ExternalWriteState.Prepared },
        { DeleteScope.Task, ExternalWriteState.Dispatched },
        { DeleteScope.Task, ExternalWriteState.NeedsReview },
        { DeleteScope.Task, ExternalWriteState.ManualRetryAuthorized },
        { DeleteScope.Feature, ExternalWriteState.Prepared },
        { DeleteScope.Feature, ExternalWriteState.Dispatched },
        { DeleteScope.Feature, ExternalWriteState.NeedsReview },
        { DeleteScope.Feature, ExternalWriteState.ManualRetryAuthorized },
        { DeleteScope.Project, ExternalWriteState.Prepared },
        { DeleteScope.Project, ExternalWriteState.Dispatched },
        { DeleteScope.Project, ExternalWriteState.NeedsReview },
        { DeleteScope.Project, ExternalWriteState.ManualRetryAuthorized }
    };

    public static TheoryData<DeleteScope, ExternalWriteState> TerminalCases => new()
    {
        { DeleteScope.Task, ExternalWriteState.Completed },
        { DeleteScope.Task, ExternalWriteState.DefinitiveFailure },
        { DeleteScope.Feature, ExternalWriteState.Completed },
        { DeleteScope.Feature, ExternalWriteState.DefinitiveFailure },
        { DeleteScope.Project, ExternalWriteState.Completed },
        { DeleteScope.Project, ExternalWriteState.DefinitiveFailure }
    };

    [Theory]
    [MemberData(nameof(BlockingCases))]
    public async Task Delete_RejectsNonTerminalExternalWrite(
        DeleteScope scope,
        ExternalWriteState state)
    {
        using var factory = new SqliteTestFactory();
        var seeded = await SeedGraphAndOperationAsync(factory, state);

        var error = await Assert.ThrowsAsync<ExternalWorkItemConflictException>(
            () => DeleteAsync(factory, seeded, scope));

        Assert.Equal("external-operation-blocks-delete", error.ConflictCode);
        Assert.Equal(seeded.TaskId, error.TaskId);
        Assert.Equal(ExternalProvider.GitHub, error.Provider);
        Assert.Equal(seeded.OperationId, error.OperationId);
        Assert.Equal(state, error.OperationState);

        await using var verify = await factory.CreateDbContextAsync();
        Assert.NotNull(await verify.Projects.FindAsync(seeded.ProjectId));
        Assert.NotNull(await verify.Features.FindAsync(seeded.FeatureId));
        Assert.NotNull(await verify.Tasks.FindAsync(seeded.TaskId));
        var journal = await verify.ExternalWriteOperations.SingleAsync();
        Assert.Equal(seeded.OperationId, journal.OperationId);
        Assert.Equal(state, journal.State);
    }

    [Theory]
    [MemberData(nameof(TerminalCases))]
    public async Task Delete_AllowsTerminalExternalWrite_AndRetainsJournal(
        DeleteScope scope,
        ExternalWriteState state)
    {
        using var factory = new SqliteTestFactory();
        var seeded = await SeedGraphAndOperationAsync(factory, state);

        await DeleteAsync(factory, seeded, scope);

        await using var verify = await factory.CreateDbContextAsync();
        Assert.Null(await verify.Tasks.FindAsync(seeded.TaskId));
        Assert.Equal(scope == DeleteScope.Task, await verify.Features.AnyAsync(f => f.Id == seeded.FeatureId));
        Assert.Equal(scope != DeleteScope.Project, await verify.Projects.AnyAsync(p => p.Id == seeded.ProjectId));

        var journal = await verify.ExternalWriteOperations.SingleAsync();
        Assert.Equal(seeded.OperationId, journal.OperationId);
        Assert.Equal(seeded.TaskId, journal.TaskId);
        Assert.Equal(seeded.ProjectId, journal.ProjectId);
        Assert.Equal(state, journal.State);
    }

    [Theory]
    [InlineData(DeleteScope.Feature)]
    [InlineData(DeleteScope.Project)]
    public async Task Delete_IgnoresNonTerminalExternalWriteFromUnrelatedGraph(DeleteScope scope)
    {
        using var factory = new SqliteTestFactory();
        var unrelated = await SeedGraphAndOperationAsync(factory, ExternalWriteState.Dispatched);
        var target = await SeedGraphWithoutOperationAsync(factory);

        await DeleteAsync(factory, target, scope);

        await using var verify = await factory.CreateDbContextAsync();
        Assert.Null(await verify.Tasks.FindAsync(target.TaskId));
        Assert.Null(await verify.Features.FindAsync(target.FeatureId));
        Assert.Equal(
            scope == DeleteScope.Feature,
            await verify.Projects.AnyAsync(project => project.Id == target.ProjectId));

        var unrelatedProject = await verify.Projects.AsNoTracking()
            .SingleAsync(project => project.Id == unrelated.ProjectId);
        var unrelatedFeature = await verify.Features.AsNoTracking()
            .SingleAsync(feature => feature.Id == unrelated.FeatureId);
        var unrelatedTask = await verify.Tasks.AsNoTracking()
            .SingleAsync(task => task.Id == unrelated.TaskId);
        Assert.Equal("Deletion guard project", unrelatedProject.Name);
        Assert.Equal("Deletion guard feature", unrelatedFeature.Name);
        Assert.Equal("Deletion guard task", unrelatedTask.Name);

        var journal = await verify.ExternalWriteOperations.AsNoTracking().SingleAsync();
        Assert.Equal(unrelated.OperationId, journal.OperationId);
        Assert.Equal(unrelated.ProjectId, journal.ProjectId);
        Assert.Equal(unrelated.TaskId, journal.TaskId);
        Assert.Equal(ExternalWriteState.Dispatched, journal.State);
        Assert.NotNull(journal.DispatchedAt);
        Assert.Null(journal.CompletedAt);
    }

    private static async Task DeleteAsync(
        SqliteTestFactory factory,
        SeededGraph seeded,
        DeleteScope scope)
    {
        if (scope == DeleteScope.Task)
        {
            await new TaskService(factory).DeleteAsync(seeded.TaskId, "test-deleter");
            return;
        }

        if (scope == DeleteScope.Project)
        {
            await new TaskService(factory).DeleteProjectAsync(seeded.ProjectId, "test-deleter");
            return;
        }

        await using var db = await factory.CreateDbContextAsync();
        var result = await ApiEndpoints.DeleteFeatureAsync(seeded.FeatureId, db);
        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status204NoContent, status.StatusCode);
    }

    private static async Task<SeededGraph> SeedGraphAndOperationAsync(
        IDbContextFactory<AppDbContext> factory,
        ExternalWriteState state)
    {
        await using var db = await factory.CreateDbContextAsync();
        var task = new TaskItem
        {
            Name = "Deletion guard task",
            Feature = new Feature
            {
                Name = "Deletion guard feature",
                Project = new Project { Name = "Deletion guard project" }
            }
        };
        db.Tasks.Add(task);
        await db.SaveChangesAsync();

        var operationId = Guid.NewGuid();
        var now = new DateTime(2026, 8, 30, 20, 0, 0, DateTimeKind.Utc);
        db.ExternalWriteOperations.Add(new ExternalWriteOperation
        {
            OperationId = operationId,
            TaskId = task.Id,
            ProjectId = task.Feature.ProjectId,
            Provider = ExternalProvider.GitHub,
            RepositoryExternalId = "R_delete_guard",
            RepositoryOwner = "acme",
            RepositoryName = "widgets",
            Title = "Frozen title",
            Body = $"Frozen body\n\n<!-- kanbanflow-push:{operationId:D} -->",
            RequestHash = new string('a', 64),
            ExpectedTaskUpdatedAt = task.UpdatedAt,
            State = state,
            AttemptCount = 1,
            CreatedAt = now,
            UpdatedAt = now,
            DispatchedAt = state is ExternalWriteState.Dispatched or
                ExternalWriteState.NeedsReview or
                ExternalWriteState.ManualRetryAuthorized
                ? now
                : null,
            CompletedAt = state == ExternalWriteState.Completed ? now : null
        });
        await db.SaveChangesAsync();

        return new SeededGraph(
            task.Feature.ProjectId,
            task.FeatureId,
            task.Id,
            operationId);
    }

    private static async Task<SeededGraph> SeedGraphWithoutOperationAsync(
        IDbContextFactory<AppDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        var task = new TaskItem
        {
            Name = "Delete target task",
            Feature = new Feature
            {
                Name = "Delete target feature",
                Project = new Project { Name = "Delete target project" }
            }
        };
        db.Tasks.Add(task);
        await db.SaveChangesAsync();

        return new SeededGraph(
            task.Feature.ProjectId,
            task.FeatureId,
            task.Id,
            Guid.Empty);
    }

    private sealed record SeededGraph(
        int ProjectId,
        int FeatureId,
        int TaskId,
        Guid OperationId);
}
