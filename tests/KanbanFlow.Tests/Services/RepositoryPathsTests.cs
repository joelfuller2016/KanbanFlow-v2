using System.Diagnostics;
using KanbanFlow.Models.Workflows;
using KanbanFlow.Services.Workflows;
using Microsoft.EntityFrameworkCore;
using static KanbanFlow.Tests.WorkflowSqliteTestFactory;

namespace KanbanFlow.Tests.Services;

public sealed class RepositoryPathsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resolve_AcceptsRepositoryOrWorktreeAndNormalizesPath(bool worktree)
    {
        using var f = new WorkflowSqliteTestFactory();
        var repository = Path.Combine(f.ScratchDirectory, "repository");
        Directory.CreateDirectory(repository);
        if (worktree) File.WriteAllText(Path.Combine(repository, ".git"), "gitdir: ../git-metadata");
        else Directory.CreateDirectory(Path.Combine(repository, ".git"));
        Assert.Equal(repository, RepositoryPaths.Resolve(repository + Path.DirectorySeparatorChar + "." + Path.DirectorySeparatorChar, f.ScratchDirectory));
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("missing")]
    [InlineData("not-git")]
    [InlineData("root-itself")]
    [InlineData("sibling-prefix")]
    [InlineData("parent-escape")]
    public void Resolve_RejectsPathsOutsideRootOrWithoutExistingGitMetadata(string invalidCase)
    {
        using var f = new WorkflowSqliteTestFactory();
        var nongit = Path.Combine(f.ScratchDirectory, "not-git");
        Directory.CreateDirectory(nongit);
        var candidate = invalidCase switch
        {
            "relative" => "relative/repository",
            "missing" => Path.Combine(f.ScratchDirectory, "missing"),
            "not-git" => nongit,
            "root-itself" => f.ScratchDirectory,
            "sibling-prefix" => f.ScratchDirectory + "-sibling",
            _ => Path.Combine(f.ScratchDirectory, "..", "outside")
        };
        Assert.Equal("invalid", Assert.Throws<WorkflowException>(() => RepositoryPaths.Resolve(candidate, f.ScratchDirectory)).Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("src/Code.cs")]
    [InlineData("docs/readme.md\n  src\\Code.cs  \n")]
    [InlineData("folder/..hidden/file.cs")]
    public void ValidateReferences_AllowsRepositoryRelativeFiles(string references) =>
        Assert.Null(Record.Exception(() => RepositoryPaths.ValidateReferences(references)));

    [Theory]
    [InlineData("/absolute/file.cs")]
    [InlineData("C:\\absolute\\file.cs")]
    [InlineData("\\\\server\\share\\file.cs")]
    [InlineData("../outside.cs")]
    [InlineData("src/../../outside.cs")]
    [InlineData("good.cs\n../bad.cs")]
    [InlineData("file.cs:stream")]
    [InlineData("bad\0path")]
    public void ValidateReferences_RejectsAbsoluteParentOrStreamReferences(string references) =>
        Assert.Equal("invalid", Assert.Throws<WorkflowException>(() => RepositoryPaths.ValidateReferences(references)).Code);

    [Fact]
    public async Task RegisterRepository_DeduplicatesNormalizedPathWithinProjectAndScopesQueries()
    {
        using var f = new WorkflowSqliteTestFactory();
        var project = await f.Service.CreateProjectAsync("Project", Actor, "project");
        var otherProject = await f.Service.CreateProjectAsync("Other", Actor, "other");
        var repository = Path.Combine(f.ScratchDirectory, "repository");
        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        var id = await f.Service.RegisterRepositoryAsync(project, "  Main repository  ", repository, f.ScratchDirectory, Actor, "register");
        var duplicate = await f.Service.RegisterRepositoryAsync(project, "Alternative name", repository + Path.DirectorySeparatorChar, f.ScratchDirectory, Actor, "same-path");
        Assert.Equal(id, duplicate);
        Assert.Empty(await f.Service.RepositoriesAsync(otherProject));
        Assert.Empty(await f.Service.RepositoriesAsync(null));
        var stored = Assert.Single(await f.Service.RepositoriesAsync(project));
        Assert.Equal("Main repository", stored.Name);
        Assert.Equal(repository, stored.LocalPath);
        Assert.Equal(project, stored.ProjectId);
        await using var db = f.CreateDbContext();
        Assert.Equal(1, await db.ProjectRepositories.CountAsync());
        Assert.Equal(1, await db.ActivityEvents.CountAsync(a => a.EventType == "WorkspaceRepositoryRegistered"));
        Assert.Equal(2, await db.WorkflowReceipts.CountAsync(r => r.Operation == "repository.register"));
        await RejectAsync("not_found", () => f.Service.RegisterRepositoryAsync(int.MaxValue, "Missing project", repository, f.ScratchDirectory, Actor, "missing-project"));
    }

    [Fact]
    public async Task Resolve_RejectsLinkedRepositoryDirectories()
    {
        using var f = new WorkflowSqliteTestFactory();
        var target = Path.Combine(f.ScratchDirectory, "actual");
        var linked = Path.Combine(f.ScratchDirectory, "linked");
        Directory.CreateDirectory(Path.Combine(target, ".git"));
        if (OperatingSystem.IsWindows())
        {
            // Junction creation needs no administrator privilege; symbolic links often do on Windows.
            var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "/c", "mklink", "/J", linked, target }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
        }
        else Directory.CreateSymbolicLink(linked, target);
        try
        {
            Assert.True((File.GetAttributes(linked) & FileAttributes.ReparsePoint) != 0);
            Assert.Equal("invalid", Assert.Throws<WorkflowException>(() => RepositoryPaths.Resolve(linked, f.ScratchDirectory)).Code);
        }
        finally { Directory.Delete(linked); }
    }
}
