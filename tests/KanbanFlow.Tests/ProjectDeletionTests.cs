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
        var ev = await db.ActivityEvents.SingleOrDefaultAsync(e => e.EventType == "ProjectDeleted" && e.ProjectId == projectId);
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

    // IDs captured while seeding the FULL graph below, so the assertions can target exact rows.
    private sealed record FullGraphIds(
        int ProjectId,
        int FeatureId,
        int TaskId,
        int SecondTaskId,
        int ParentSubtaskId,
        int ChildSubtaskId,
        int AcceptanceCriterionId,
        int CommentId,
        int DependencyId,
        int ProjectScopedRequirementId,
        int CrossProjectFeatureLinkedRequirementId,
        int OtherProjectId);

    // Seeds the WHOLE object graph hanging off a single project so DeleteProjectAsync exercises
    // every cascade/SetNull path at once:
    //   project → feature → task (nested subtask parent+child, an AcceptanceCriterion, a TaskComment)
    //           → second task + a TaskDependency linking the two (ProjectId set)
    //   + a project-scoped Requirement (linked to the task — link nulled, then row removed)
    //   + a Requirement owned by a SECOND project but FeatureId-linked into THIS project's feature
    //     (its link must be nulled but the row must survive, since it is not scoped to this project).
    private static FullGraphIds SeedFullProjectGraph(SqliteTestFactory f)
    {
        using var db = f.CreateDbContext();

        var project = new Project { Name = "Proj" };
        var feature = new Feature { Name = "Feat", Project = project };
        var task = new TaskItem { Name = "Task", Feature = feature, Status = WorkStatus.Todo };
        var secondTask = new TaskItem { Name = "Task2", Feature = feature, Status = WorkStatus.Todo };
        db.Add(task);
        db.Add(secondTask);
        db.SaveChanges();

        var parent = new Subtask { TaskItemId = task.Id, Text = "parent", SortOrder = 0 };
        db.Subtasks.Add(parent);
        db.SaveChanges();
        var child = new Subtask { TaskItemId = task.Id, ParentSubtaskId = parent.Id, Text = "child", SortOrder = 0 };
        db.Subtasks.Add(child);

        var criterion = new AcceptanceCriterion { TaskItemId = task.Id, Text = "AC", SortOrder = 0 };
        db.AcceptanceCriteria.Add(criterion);

        var comment = new TaskComment { TaskItemId = task.Id, Text = "a comment", Type = CommentType.Note };
        db.TaskComments.Add(comment);

        var dependency = new TaskDependency
        {
            ProjectId = project.Id,
            TaskItemId = secondTask.Id,
            DependsOnTaskItemId = task.Id,
            Type = TaskDependencyType.DependsOn
        };
        db.TaskDependencies.Add(dependency);

        // Requirement owned by THIS project (also linked to the task) — RemoveRange'd by the
        // project-scoped sweep in DeleteProjectChildrenAsync.
        var projectScopedReq = new Requirement
        {
            ProjectId = project.Id,
            Text = "project-scoped requirement",
            TaskItemId = task.Id
        };
        db.Requirements.Add(projectScopedReq);

        // A different project that owns a requirement which merely *links* into this project's
        // feature — used to prove the FeatureId link is SetNull but the row is NOT deleted.
        var otherProject = new Project { Name = "OtherProj" };
        db.Add(otherProject);
        db.SaveChanges();

        var crossProjectReq = new Requirement
        {
            ProjectId = otherProject.Id,
            Text = "cross-project feature-linked requirement",
            FeatureId = feature.Id
        };
        db.Requirements.Add(crossProjectReq);
        db.SaveChanges();

        return new FullGraphIds(
            project.Id, feature.Id, task.Id, secondTask.Id,
            parent.Id, child.Id, criterion.Id, comment.Id, dependency.Id,
            projectScopedReq.Id, crossProjectReq.Id, otherProject.Id);
    }

    [Fact]
    public async Task DeleteProject_WithFullGraph_RemovesAllChildrenAndAppliesRequirementSemantics()
    {
        using var f = new SqliteTestFactory();
        var ids = SeedFullProjectGraph(f);
        var svc = new TaskService(f);

        await svc.DeleteProjectAsync(ids.ProjectId, "tester");

        using var db = f.CreateDbContext();

        // Project itself is gone.
        Assert.Null(await db.Projects.FindAsync(ids.ProjectId));

        // Entire feature/task/subtask graph for this project is removed.
        Assert.Empty(await db.Features.Where(x => x.ProjectId == ids.ProjectId).ToListAsync());
        Assert.Null(await db.Tasks.FindAsync(ids.TaskId));
        Assert.Null(await db.Tasks.FindAsync(ids.SecondTaskId));
        Assert.Null(await db.Subtasks.FindAsync(ids.ParentSubtaskId));
        Assert.Null(await db.Subtasks.FindAsync(ids.ChildSubtaskId));

        // Per-task children removed: acceptance criteria, comments, and the dependency row.
        Assert.Null(await db.AcceptanceCriteria.FindAsync(ids.AcceptanceCriterionId));
        Assert.Null(await db.TaskComments.FindAsync(ids.CommentId));
        Assert.Null(await db.TaskDependencies.FindAsync(ids.DependencyId));

        // Nothing belonging to this project lingers across any child table.
        Assert.Empty(await db.Tasks.ToListAsync());
        Assert.Empty(await db.Subtasks.ToListAsync());
        Assert.Empty(await db.AcceptanceCriteria.ToListAsync());
        Assert.Empty(await db.TaskComments.ToListAsync());
        Assert.Empty(await db.TaskDependencies.ToListAsync());

        // Requirement semantics — matching DeleteProjectChildrenAsync / DeleteFeatureTreesAsync:
        //  • a requirement scoped to the deleted project is RemoveRange'd (row deleted);
        //  • a requirement owned by ANOTHER project but linked to this project's feature/task has
        //    its FeatureId/TaskItemId nulled (SetNull), and the requirement row SURVIVES.
        Assert.Null(await db.Requirements.FindAsync(ids.ProjectScopedRequirementId));

        var survivingReq = await db.Requirements.FindAsync(ids.CrossProjectFeatureLinkedRequirementId);
        Assert.NotNull(survivingReq);
        Assert.Null(survivingReq!.FeatureId);
        Assert.Null(survivingReq.TaskItemId);
        Assert.Equal(ids.OtherProjectId, survivingReq.ProjectId);

        // The unrelated second project is untouched.
        Assert.NotNull(await db.Projects.FindAsync(ids.OtherProjectId));
    }
}
