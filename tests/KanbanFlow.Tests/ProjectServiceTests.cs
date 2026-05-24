using KanbanFlow.Models;
using KanbanFlow.Services;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Tests;

/// <summary>
/// Real-SQLite tests for <see cref="ProjectService"/>. The headline case (#155) exercises BOTH
/// RESTRICT self-relationships (nested features AND nested subtasks) — a plain cascade delete
/// aborts on these, which is the bug the UI hit.
/// </summary>
public sealed class ProjectServiceTests
{
    [Fact]
    public async Task Delete_ProjectWithNestedFeatureAndSubtaskTree_Succeeds_AndAuditSurvives()
    {
        using var f = new SqliteTestFactory();
        int projectId;
        using (var db = f.CreateDbContext())
        {
            var project = new Project { Name = "P" };
            var parentFeature = new Feature { Name = "F", Project = project };
            var childFeature = new Feature { Name = "F-child", Project = project, ParentFeature = parentFeature };
            var task = new TaskItem { Name = "T", Feature = childFeature };
            var parentSub = new Subtask { Text = "s1", TaskItem = task };
            var childSub = new Subtask { Text = "s1.1", TaskItem = task, ParentSubtask = parentSub };
            db.AddRange(project, parentFeature, childFeature, task, parentSub, childSub);
            db.SaveChanges();
            projectId = project.Id;
        }

        var svc = new ProjectService(f);

        var deleted = await svc.DeleteAsync(projectId, "tester");

        Assert.True(deleted);
        using var verify = f.CreateDbContext();
        Assert.False(await verify.Projects.AnyAsync(p => p.Id == projectId));
        Assert.Equal(0, await verify.Features.CountAsync());
        Assert.Equal(0, await verify.Tasks.CountAsync());
        Assert.Equal(0, await verify.Subtasks.CountAsync());
        // The ProjectDeleted audit row has no FK to Project, so it outlives the delete.
        var ev = await verify.ActivityEvents.SingleAsync(e => e.EventType == "ProjectDeleted");
        Assert.Equal(projectId, ev.EntityId);
        Assert.Equal("tester", ev.Actor);
    }

    [Fact]
    public async Task Delete_MissingProject_ReturnsFalse()
    {
        using var f = new SqliteTestFactory();
        var svc = new ProjectService(f);

        Assert.False(await svc.DeleteAsync(999, "x"));
    }
}
