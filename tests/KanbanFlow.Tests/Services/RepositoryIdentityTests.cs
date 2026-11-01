using KanbanFlow.Models.Workflows;
using KanbanFlow.Services.Workflows;
using Microsoft.EntityFrameworkCore;
using static KanbanFlow.Tests.WorkflowSqliteTestFactory;

namespace KanbanFlow.Tests.Services;

public sealed class RepositoryIdentityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Registration_UsesOperatingSystemPathIdentity_ForRepositoriesAndWorktrees(bool worktree)
    {
        using var f = new WorkflowSqliteTestFactory();
        var project = await f.Service.CreateProjectAsync("Project", Actor, Key());
        var (firstPath, secondPath) = CreatePaths(f, worktree);

        var first = await f.Service.RegisterRepositoryAsync(project, "First spelling", firstPath, f.ScratchDirectory, Actor, Key());
        var second = await f.Service.RegisterRepositoryAsync(project, "Second spelling", secondPath, f.ScratchDirectory, Actor, Key());

        Assert.Equal(OperatingSystem.IsWindows(), first == second);
        await using var db = f.CreateDbContext();
        Assert.Equal(OperatingSystem.IsWindows() ? 1 : 2, await db.ProjectRepositories.CountAsync());
        Assert.Equal(OperatingSystem.IsWindows() ? 1 : 2,
            await db.ActivityEvents.CountAsync(e => e.EventType == "WorkspaceRepositoryRegistered"));
        Assert.Equal(2, await db.WorkflowReceipts.CountAsync(r => r.Operation == "repository.register"));
        var identity = await db.Database.SqlQuery<string>($"SELECT PathIdentity AS Value FROM ProjectRepositories WHERE Id = {first}").SingleAsync();
        Assert.Equal(OperatingSystem.IsWindows() ? firstPath.ToUpperInvariant() : firstPath, identity);
    }

    [Fact]
    public async Task Registration_PreservesLegacyAliasesAndPublishedRepositoryReferences()
    {
        using var f = new WorkflowSqliteTestFactory();
        var request = await f.BriefedAsync();
        var (firstPath, secondPath) = CreatePaths(f, worktree: true);
        int firstId;
        int secondId;
        await using (var db = f.CreateDbContext())
        {
            var first = new ProjectRepository { ProjectId = request.ProjectId!.Value, Name = "Authored first name", LocalPath = firstPath };
            var second = new ProjectRepository { ProjectId = request.ProjectId.Value, Name = "Authored second name", LocalPath = secondPath };
            db.ProjectRepositories.AddRange(first, second);
            await db.SaveChangesAsync();
            firstId = first.Id;
            secondId = second.Id;
        }
        var draft = Draft("Keep repository relationship") with { RepositoryId = secondId, FileReferences = "src/Work.cs" };
        await f.Service.SavePlanAsync(request.Id, request.Version, [draft], Actor, Key());
        request = await f.LoadAsync(request.Id);
        await f.Service.PublishPlanAsync(request.Id, request.Version, request.Plans.Single().Id, Actor, Key());
        var published = await f.LoadAsync(request.Id);

        var spelling = OperatingSystem.IsWindows() ? firstPath.ToUpperInvariant() : firstPath;
        var registered = await f.Service.RegisterRepositoryAsync(request.ProjectId!.Value, "Attempted replacement name", spelling,
            f.ScratchDirectory, Actor, Key());

        Assert.Equal(firstId, registered);
        await using var verify = f.CreateDbContext();
        var repositories = await verify.ProjectRepositories.OrderBy(r => r.Id).ToArrayAsync();
        Assert.Equal(new[] { firstId, secondId }, repositories.Select(r => r.Id));
        Assert.Equal(new[] { "Authored first name", "Authored second name" }, repositories.Select(r => r.Name));
        Assert.Equal(new[] { firstPath, secondPath }, repositories.Select(r => r.LocalPath));
        var after = await f.LoadAsync(request.Id);
        Assert.Equal(published.WorkItems.Single().TaskItemId, after.WorkItems.Single().TaskItemId);
        Assert.Equal(secondId, System.Text.Json.JsonSerializer.Deserialize<TaskDraft>(after.WorkItems.Single().SpecificationJson)!.RepositoryId);
        Assert.Equal(secondId, System.Text.Json.JsonSerializer.Deserialize<TaskDraft[]>(after.Plans.Single().DraftJson)!.Single().RepositoryId);
        Assert.True(File.Exists(Path.Combine(repositories[1].LocalPath, ".git")));
        Assert.False(await verify.ActivityEvents.AnyAsync(e => e.EventType == "WorkspaceRepositoryRegistered"));
    }

    [Fact]
    public async Task ConcurrentRegistration_CreatesOneAuditedRowPerOperatingSystemIdentity()
    {
        using var f = new WorkflowSqliteTestFactory();
        var project = await f.Service.CreateProjectAsync("Concurrent", Actor, Key());
        var (firstPath, secondPath) = CreatePaths(f, worktree: false);

        var registrations = Enumerable.Range(0, 8).Select(index => Task.Run(() =>
            f.Service.RegisterRepositoryAsync(project, "Concurrent repository", index % 2 == 0 ? firstPath : secondPath,
                f.ScratchDirectory, Actor, "concurrent-" + index))).ToArray();
        var ids = await Task.WhenAll(registrations);

        var expected = OperatingSystem.IsWindows() ? 1 : 2;
        Assert.Equal(expected, ids.Distinct().Count());
        await using var db = f.CreateDbContext();
        Assert.Equal(expected, await db.ProjectRepositories.CountAsync());
        Assert.Equal(expected, await db.ActivityEvents.CountAsync(e => e.EventType == "WorkspaceRepositoryRegistered"));
        Assert.Equal(8, await db.WorkflowReceipts.CountAsync(r => r.Operation == "repository.register"));
    }

    private static (string First, string Second) CreatePaths(WorkflowSqliteTestFactory f, bool worktree)
    {
        var first = Path.Combine(f.ScratchDirectory, "MiXeD-Répository");
        var second = Path.Combine(f.ScratchDirectory, "MIXED-RÉPOSITORY");
        foreach (var path in new[] { first, second })
        {
            Directory.CreateDirectory(path);
            if (worktree) File.WriteAllText(Path.Combine(path, ".git"), "gitdir: ../preserved-metadata");
            else Directory.CreateDirectory(Path.Combine(path, ".git"));
        }
        return (first, second);
    }
}
