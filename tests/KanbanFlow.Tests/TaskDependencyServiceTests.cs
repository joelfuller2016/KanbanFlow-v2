using KanbanFlow.Models;
using KanbanFlow.Services;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Tests;

/// <summary>
/// Contract tests for the shared dependency-creation boundary introduced by #414.
/// These use real SQLite and re-query persisted rows instead of mocking the graph.
/// </summary>
public sealed class TaskDependencyServiceTests
{
    private static int[] SeedTasks(SqliteTestFactory factory, params string[] names)
    {
        using var db = factory.CreateDbContext();
        var feature = new Feature
        {
            Name = "Dependency feature",
            Project = new Project { Name = $"Project {Guid.NewGuid():N}" }
        };
        var tasks = names
            .Select(name => new TaskItem { Name = name, Feature = feature })
            .ToArray();

        db.Tasks.AddRange(tasks);
        db.SaveChanges();
        return tasks.Select(t => t.Id).ToArray();
    }

    [Fact]
    public async Task Create_PersistsCanonicalEdgeAndReturnsTaskNames()
    {
        using var factory = new SqliteTestFactory();
        var ids = SeedTasks(factory, "blocked", "blocker");
        var service = new TaskDependencyService(factory);

        var result = await service.CreateAsync(new TaskDependencyCreate(
            BlockedTaskId: ids[0],
            BlockingTaskId: ids[1],
            Type: TaskDependencyType.Blocks));

        Assert.Equal("blocked", result.BlockedTaskName);
        Assert.Equal("blocker", result.BlockingTaskName);
        Assert.True(result.Dependency.Id > 0);

        using var db = factory.CreateDbContext();
        var persisted = await db.TaskDependencies.AsNoTracking().SingleAsync();
        Assert.Equal(ids[0], persisted.TaskItemId);
        Assert.Equal(ids[1], persisted.DependsOnTaskItemId);
        Assert.Equal(TaskDependencyType.Blocks, persisted.Type);
    }

    [Fact]
    public async Task Create_RejectsUndefinedTypeBeforeWriting()
    {
        using var factory = new SqliteTestFactory();
        var ids = SeedTasks(factory, "blocked", "blocker");
        var service = new TaskDependencyService(factory);

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            service.CreateAsync(new TaskDependencyCreate(ids[0], ids[1], (TaskDependencyType)99)));

        Assert.Equal("Invalid dependency type '99'.", error.Message);
        using var db = factory.CreateDbContext();
        Assert.Empty(await db.TaskDependencies.ToListAsync());
    }

    [Fact]
    public async Task Create_RejectsSelfDependency()
    {
        using var factory = new SqliteTestFactory();
        var taskId = SeedTasks(factory, "self").Single();
        var service = new TaskDependencyService(factory);

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            service.CreateAsync(new TaskDependencyCreate(
                taskId,
                taskId,
                TaskDependencyType.DependsOn)));

        Assert.Equal("A task cannot depend on itself.", error.Message);
    }

    [Fact]
    public async Task Create_ReportsBlockedTaskMissingBeforeBlockingTask()
    {
        using var factory = new SqliteTestFactory();
        var service = new TaskDependencyService(factory);

        var error = await Assert.ThrowsAsync<TaskNotFoundException>(() =>
            service.CreateAsync(new TaskDependencyCreate(
                BlockedTaskId: 41,
                BlockingTaskId: 42,
                Type: TaskDependencyType.DependsOn)));

        Assert.Equal(41, error.TaskId);
        Assert.Equal("Task 41 not found.", error.Message);
    }

    [Fact]
    public async Task Create_ReportsMissingBlockingTask()
    {
        using var factory = new SqliteTestFactory();
        var blockedTaskId = SeedTasks(factory, "blocked").Single();
        var service = new TaskDependencyService(factory);

        var error = await Assert.ThrowsAsync<TaskNotFoundException>(() =>
            service.CreateAsync(new TaskDependencyCreate(
                blockedTaskId,
                BlockingTaskId: 42,
                Type: TaskDependencyType.DependsOn)));

        Assert.Equal(42, error.TaskId);
        Assert.Equal("Task 42 not found.", error.Message);
    }

    [Fact]
    public async Task Create_RejectsCrossProjectDependency()
    {
        using var factory = new SqliteTestFactory();
        var blockedTaskId = SeedTasks(factory, "blocked").Single();
        var blockingTaskId = SeedTasks(factory, "blocker").Single();
        var service = new TaskDependencyService(factory);

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            service.CreateAsync(new TaskDependencyCreate(
                blockedTaskId,
                blockingTaskId,
                TaskDependencyType.DependsOn)));

        Assert.Equal("Task dependencies must stay within the same project.", error.Message);
    }

    [Fact]
    public async Task Create_RejectsExactDuplicateWithoutAddingAnotherRow()
    {
        using var factory = new SqliteTestFactory();
        var ids = SeedTasks(factory, "blocked", "blocker");
        var service = new TaskDependencyService(factory);
        var request = new TaskDependencyCreate(ids[0], ids[1], TaskDependencyType.DependsOn);
        await service.CreateAsync(request);

        var error = await Assert.ThrowsAsync<TaskDependencyAlreadyExistsException>(() =>
            service.CreateAsync(request));

        Assert.Equal(ids[0], error.BlockedTaskId);
        Assert.Equal(ids[1], error.BlockingTaskId);
        Assert.Equal("This task dependency already exists.", error.Message);
        using var db = factory.CreateDbContext();
        Assert.Equal(1, await db.TaskDependencies.CountAsync());
    }

    [Fact]
    public async Task Create_RejectsTransitiveCycleWithoutAddingClosingEdge()
    {
        using var factory = new SqliteTestFactory();
        var ids = SeedTasks(factory, "A", "B", "C");
        var service = new TaskDependencyService(factory);
        await service.CreateAsync(new TaskDependencyCreate(ids[0], ids[1], TaskDependencyType.DependsOn));
        await service.CreateAsync(new TaskDependencyCreate(ids[1], ids[2], TaskDependencyType.DependsOn));

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            service.CreateAsync(new TaskDependencyCreate(
                ids[2],
                ids[0],
                TaskDependencyType.DependsOn)));

        Assert.Equal("This dependency would create a cycle.", error.Message);
        using var db = factory.CreateDbContext();
        Assert.Equal(2, await db.TaskDependencies.CountAsync());
    }
}
