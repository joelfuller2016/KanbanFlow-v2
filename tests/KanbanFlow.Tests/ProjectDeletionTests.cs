using KanbanFlow.Models;
using KanbanFlow.Services;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Tests;

/// <summary>
/// Real-SQLite tests for <see cref="TaskService.DeleteProjectAsync"/> — the FK-safe,
/// audited project deletion that fixes #155 (UI delete crashed on nested subtasks because
/// Subtask.ChildSubtasks is OnDelete(Restrict)).
/// </summary>
public sealed class ProjectDeletionTests
{
    // Seeds a project whose task has a NESTED subtask tree (parent -> child) — exactly the
    // shape that made a bare cascade Remove throw a DbUpdateException.
    private static int SeedProjectWithNestedSubtasks(SqliteTestFactory f)
    {
        using var db = f.CreateDbContext();
        var project = new Project { Name = "Proj" };
        var feature = new Feature { Name = "Feat", Project = project };
        var task = new TaskItem { Name = "Task", Feature = feature, Status = WorkStatus.Todo };
        db.Add(task);
        db.SaveChanges();

        var parent = new Subtask { TaskItemId = task.Id, Text = "parent", SortOrder = 0 };
        db.Subtasks.Add(parent);
        db.SaveChanges();

        var child = new Subtask { TaskItemId = task.Id, ParentSubtaskId = parent.Id, Text = "child", SortOrder = 0 };
        db.Subtasks.Add(child);
        db.SaveChanges();

        return project.Id;
    }

    [Fact]
    public async Task DeleteProject_WithNestedSubtaskTree_RemovesEverythingWithoutThrowing()
    {
        using var f = new SqliteTestFactory();
        var projectId = SeedProjectWithNestedSubtasks(f);
        var svc = new TaskService(f);

        // Before the fix this threw DbUpdateException (Restrict FK on Subtask.ChildSubtasks).
        await svc.DeleteProjectAsync(projectId, "tester");

        using var db = f.CreateDbContext();
        Assert.Null(await db.Projects.FindAsync(projectId));
        Assert.Empty(await db.Features.Where(x => x.ProjectId == projectId).ToListAsync());
        Assert.Empty(await db.Tasks.ToListAsync());
        Assert.Empty(await db.Subtasks.ToListAsync());
    }

    [Fact]
    public async Task DeleteProject_WritesProjectDeletedActivityEvent()
    {
        using var f = new SqliteTestFactory();
        var projectId = SeedProjectWithNestedSubtasks(f);
        var svc = new TaskService(f);

        await svc.DeleteProjectAsync(projectId, "tester");

        using var db = f.CreateDbContext();
        var ev = await db.ActivityEvents.SingleOrDefaultAsync(e => e.EventType == "ProjectDeleted");
        Assert.NotNull(ev);
        Assert.Equal(projectId, ev!.ProjectId);
        Assert.Equal("Project", ev.EntityType);
        Assert.Equal(projectId, ev.EntityId);
        Assert.Equal("tester", ev.Actor);
    }

    [Fact]
    public async Task DeleteProject_MissingId_ThrowsProjectNotFound()
    {
        using var f = new SqliteTestFactory();
        var svc = new TaskService(f);

        await Assert.ThrowsAsync<ProjectNotFoundException>(
            () => svc.DeleteProjectAsync(9999, "tester"));
    }
}
