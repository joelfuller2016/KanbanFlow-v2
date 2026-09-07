using System.Text.Json;
using KanbanFlow.Mcp;
using KanbanFlow.Models;
using KanbanFlow.Models.Workflows;
using KanbanFlow.Services;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using static KanbanFlow.Tests.WorkflowSqliteTestFactory;

namespace KanbanFlow.Tests.Services.Workflows;

public sealed class UnifiedTaskWorkflowTests
{
    [Fact]
    public async Task PublicationAndNormalTaskEdits_AreVisibleThroughExistingBoardAndNativeContexts()
    {
        using var f = new WorkflowSqliteTestFactory();
        var first = Draft("Prerequisite");
        var second = Draft("Shared task", first.Id);
        var request = await f.PublishedAsync(first, second);
        var item = request.WorkItems.Single(w => w.DraftId == second.Id);
        var tasks = new TaskService(f);
        await tasks.UpdateAsync(item.TaskItemId, new TaskUpdate(Name: "Human renamed task", Description: "Human clarified scope", Priority: TaskPriority.Critical, Assignee: "human"), "human");
        await tasks.AddCommentAsync(item.TaskItemId, "Observed through the ordinary task panel", "human", CommentType.Comment, "human");
        await tasks.ChangeStatusAsync(item.TaskItemId, WorkStatus.Review, "human");

        var board = await KanbanMcpTools.GetProjectContext(f, request.ProjectId!.Value);
        var card = board.Tasks.Single(t => t.Id == item.TaskItemId);
        var detail = await KanbanMcpTools.GetTask(f, item.TaskItemId);
        var context = await WorkspaceMcpTools.GetTaskWorkflow(f.Service, item.TaskItemId);
        var workflowTask = context.Tasks.Single(t => t.TaskId == item.TaskItemId);
        Assert.Equal(request.Id, context.Id);
        Assert.Equal(request.IdeaId, context.IdeaId);
        Assert.Equal(request.ProjectId, context.ProjectId);
        Assert.Equal(item.Id, workflowTask.Id);
        Assert.Equal(second.Id, workflowTask.DraftId);
        Assert.Equal("Human renamed task", card.Name);
        Assert.Equal(card.Name, detail.Name);
        Assert.Equal(detail.Name, workflowTask.Name);
        Assert.Equal("Human clarified scope", workflowTask.Description);
        Assert.Equal("Review", card.Status);
        Assert.Equal(card.Status, detail.Status);
        Assert.Equal(detail.Status, workflowTask.Status);
        Assert.Equal("Critical", workflowTask.Priority);
        Assert.Equal("human", workflowTask.Assignee);
        Assert.Equal(detail.DependsOnTaskIds, workflowTask.DependsOnTaskIds);
        Assert.Equal(detail.AcceptanceCriteria.Select(c => (c.Id, c.Text, c.Completed)), workflowTask.AcceptanceCriteria.Select(c => (c.Id, c.Text, c.Completed)));
        Assert.Contains(workflowTask.Comments, c => c.Text == "Observed through the ordinary task panel" && c.Author == "human" && c.Type == "Comment");
        Assert.Contains(workflowTask.Comments, c => c.Type == "Created");
        Assert.Contains(workflowTask.Comments, c => c.Type == "StatusChange");
        Assert.Equal(detail.Comments!.Select(c => (c.Id, c.Text, c.Author, c.Type)), workflowTask.Comments.Select(c => (c.Id, c.Text, c.Author, c.Type)));
        Assert.Equal($"/ideas?idea={request.IdeaId}", context.IdeaUrl);
        Assert.Equal($"/project/{request.ProjectId}?request={request.Id}", context.BoardUrl);
        Assert.Equal($"/project/{request.ProjectId}?task={item.TaskItemId}", workflowTask.Url);
        // The immutable plan retains what was published; current task fields expose later edits.
        Assert.Equal("Shared task", workflowTask.Specification.Title);
        Assert.Equal(second.Description, workflowTask.Specification.Description);
        Assert.Equal("Original request", context.Messages.Single(m => m.Kind == "original").Text);
        Assert.Equal(request.Id, (await f.Service.GetForIdeaAsync(request.IdeaId))!.Id);
        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(context));
        Assert.Equal(item.TaskItemId, serialized.RootElement.GetProperty("Tasks").EnumerateArray().Single(t => t.GetProperty("TaskId").GetInt32() == item.TaskItemId).GetProperty("TaskId").GetInt32());
        await using var db = f.CreateDbContext();
        Assert.Equal(2, await db.Tasks.CountAsync());
        Assert.Equal(2, await db.ActivityEvents.CountAsync(a => a.EventType == "TaskCreated"));
        Assert.Contains(await db.ActivityEvents.Where(a => a.EntityId == item.TaskItemId).ToListAsync(), a => a.EventType == "TaskStatusChanged" && a.Actor == "human");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NormalDoneAndAuditedOverride_KeepSameTaskWithoutInventingAcceptedEvidence(bool useOverride)
    {
        using var f = new WorkflowSqliteTestFactory();
        var request = await f.PublishedAsync(Draft());
        var item = request.WorkItems.Single();
        var tasks = new TaskService(f);
        await Assert.ThrowsAsync<DefinitionOfDoneNotMetException>(() => tasks.ChangeStatusAsync(item.TaskItemId, WorkStatus.Done, "human"));
        if (useOverride)
            await tasks.ChangeStatusAsync(item.TaskItemId, WorkStatus.Done, "human", overrideReason: "Approved exception for the existing board");
        else
        {
            foreach (var criterion in item.Task.AcceptanceCriteria) await KanbanMcpTools.MarkAcceptanceCriterionDone(f, criterion.Id);
            await f.Service.SetTaskStateAsync(request.Id, request.Version, item.Id, WorkStatus.Done, "human", "normal-done");
        }
        request = await f.LoadAsync(request.Id);
        Assert.Equal(item.TaskItemId, request.WorkItems.Single().TaskItemId);
        Assert.Equal(WorkStatus.Done, request.WorkItems.Single().Task.Status);
        Assert.NotNull(request.WorkItems.Single().Task.CompletedAt);
        Assert.Empty(request.WorkItems.Single().Evidence);
        Assert.All(request.WorkItems.Single().Task.AcceptanceCriteria, c => Assert.Equal(!useOverride, c.Completed));
        await RejectAsync("invalid", () => f.Service.ConcludeAsync(request.Id, request.Version, "Would be an unsupported conclusion", "", "human", "conclude"));
        var context = await WorkspaceMcpTools.GetTaskWorkflow(f.Service, item.TaskItemId);
        Assert.Equal("Done", Assert.Single(context.Tasks).Status);
        Assert.Empty(context.Results);
        if (useOverride)
            Assert.Contains(context.Tasks.Single().Comments, c => c.Text.Contains("Approved exception for the existing board", StringComparison.Ordinal));
        await using var db = f.CreateDbContext();
        Assert.Equal(1, await db.Tasks.CountAsync());
        Assert.Equal(1, await db.ActivityEvents.CountAsync(a => a.EntityId == item.TaskItemId && a.EventType == "TaskStatusChanged"));
        Assert.False(await db.WorkflowReceipts.AnyAsync(r => r.Key == "conclude"));
    }

    [Theory]
    [InlineData("task-name")]
    [InlineData("task-description")]
    [InlineData("native-task-update")]
    [InlineData("criterion-text")]
    [InlineData("criterion-add")]
    [InlineData("criterion-remove")]
    [InlineData("subtask-add")]
    [InlineData("dependency-add")]
    public async Task OrdinaryBoardScopeChanges_InvalidatePriorReportAndFreshReportCanBeAccepted(string change)
    {
        using var f = new WorkflowSqliteTestFactory();
        var request = await f.PublishedAsync(Draft());
        var item = request.WorkItems.Single();
        request = await f.ReportAsync(request, item.Id);
        var oldReport = request.WorkItems.Single().Evidence.Single();
        await ChangeScopeAsync(f, item.TaskItemId, change);

        request = await f.LoadAsync(request.Id);
        var current = request.WorkItems.Single();
        Assert.Equal(item.TaskItemId, current.TaskItemId);
        Assert.Equal(item.SpecificationVersion, current.SpecificationVersion);
        Assert.Equal(item.SpecificationHash, current.SpecificationHash);
        Assert.Equal("Review", (await KanbanMcpTools.GetTask(f, item.TaskItemId)).Status);
        var context = await WorkspaceMcpTools.GetTaskWorkflow(f.Service, item.TaskItemId);
        Assert.False(Assert.Single(context.Tasks.Single().Evidence).Current);
        var rejection = await RejectAsync("invalid", () => f.Service.AcceptEvidenceAsync(request.Id, request.Version, item.Id, oldReport.Id,
            current.Task.AcceptanceCriteria.Select(c => c.Id).ToArray(), "reviewer", "stale-report"));
        Assert.Contains("current", rejection.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null((await f.LoadAsync(request.Id)).WorkItems.Single().Evidence.Single().AcceptedAt);

        request = await f.ReportAsync(request, item.Id);
        request = await f.AcceptAsync(request, item.Id);
        Assert.Equal(WorkStatus.Done, request.WorkItems.Single().Task.Status);
        var evidence = (await WorkspaceMcpTools.GetTaskWorkflow(f.Service, item.TaskItemId)).Tasks.Single().Evidence;
        Assert.False(evidence.Single(e => e.Id == oldReport.Id).Current);
        var accepted = Assert.Single(evidence, e => e.Current);
        Assert.NotEqual(oldReport.Id, accepted.Id);
        Assert.NotNull(accepted.AcceptedAt);
        Assert.Equal("reviewer", accepted.AcceptedBy);
        await using var db = f.CreateDbContext();
        Assert.False(await db.WorkflowReceipts.AnyAsync(r => r.Key == "stale-report"));
    }

    [Theory]
    [InlineData("description")]
    [InlineData("reopen")]
    public async Task LegacyChangesToTransitivePrerequisite_MarkAcceptedDescendantReportHistorical(string change)
    {
        using var f = new WorkflowSqliteTestFactory();
        var a = Draft("A");
        var b = Draft("B", a.Id);
        var c = Draft("C", b.Id);
        var request = await f.PublishedAsync(a, b, c);
        foreach (var draft in new[] { a, b, c })
        {
            var id = request.WorkItems.Single(w => w.DraftId == draft.Id).Id;
            request = await f.ReportAsync(request, id);
            request = await f.AcceptAsync(request, id);
        }
        await f.Service.ConcludeAsync(request.Id, request.Version, "Verified original work", "No remaining limitations", "reviewer", "first-result");
        request = await f.LoadAsync(request.Id);
        Assert.True((await f.Service.ContextAsync(request.Id))!.Results.Single().Current);
        var prerequisite = request.WorkItems.Single(w => w.DraftId == a.Id);
        if (change == "description") await new TaskService(f).UpdateAsync(prerequisite.TaskItemId, new TaskUpdate(Description: "Changed transitive requirement"), "human");
        else await new TaskService(f).ChangeStatusAsync(prerequisite.TaskItemId, WorkStatus.Todo, "human");
        var context = await WorkspaceMcpTools.GetRequestContext(f.Service, request.Id);
        var descendant = context.Tasks.Single(t => t.DraftId == c.Id);
        Assert.Equal("Done", descendant.Status);
        Assert.False(descendant.Evidence.Single().Current);
        Assert.NotNull(descendant.Evidence.Single().AcceptedAt);
        Assert.False(context.Results.Single().Current);
        await RejectAsync("invalid", () => f.Service.ConcludeAsync(request.Id, request.Version, "False conclusion after prerequisite changed", "", "reviewer", "stale-conclusion"));
        Assert.Single((await f.LoadAsync(request.Id)).Results);
    }

    [Theory]
    [InlineData("criterion-add")]
    [InlineData("criterion-reopen")]
    [InlineData("task-description")]
    [InlineData("task-reopen")]
    public async Task OrdinaryChangesAfterConclusion_KeepHistoryAndPreventItLookingCurrent(string change)
    {
        using var f = new WorkflowSqliteTestFactory();
        var request = await f.PublishedAsync(Draft());
        var item = request.WorkItems.Single();
        request = await f.ReportAsync(request, item.Id);
        request = await f.AcceptAsync(request, item.Id);
        await f.Service.ConcludeAsync(request.Id, request.Version, "Initial checked conclusion", "Initial limitations", "reviewer", "initial-conclusion");
        request = await f.LoadAsync(request.Id);
        var resultId = request.Results.Single().Id;
        Assert.True((await WorkspaceMcpTools.GetRequestContext(f.Service, request.Id)).Results.Single().Current);
        if (change == "criterion-reopen") await KanbanMcpTools.MarkAcceptanceCriterionDone(f, item.Task.AcceptanceCriteria.First().Id, completed: false);
        else if (change == "task-reopen") await new TaskService(f).ChangeStatusAsync(item.TaskItemId, WorkStatus.Todo, "human");
        else await ChangeScopeAsync(f, item.TaskItemId, change);
        var context = await WorkspaceMcpTools.GetRequestContext(f.Service, request.Id);
        Assert.Equal(resultId, Assert.Single(context.Results).Id);
        Assert.Equal("Initial checked conclusion", context.Results.Single().Conclusion);
        Assert.False(context.Results.Single().Current);
        await RejectAsync("invalid", () => f.Service.ConcludeAsync(request.Id, request.Version, "Unverified next conclusion", "", "reviewer", "blocked-result"));
        Assert.Single((await f.LoadAsync(request.Id)).Results);
    }

    [Fact]
    public async Task DeletingRequiredTask_RemovesItsMappingButCannotSilentlyCompleteTheRemainingPlan()
    {
        using var f = new WorkflowSqliteTestFactory();
        var request = await f.PublishedAsync(Draft("First"), Draft("Second"));
        foreach (var itemId in request.WorkItems.Select(w => w.Id).ToArray())
        {
            request = await f.ReportAsync(request, itemId);
            request = await f.AcceptAsync(request, itemId);
        }
        await f.Service.ConcludeAsync(request.Id, request.Version, "Initially complete", "", "reviewer", "before-delete");
        request = await f.LoadAsync(request.Id);
        var deleted = request.WorkItems.First();
        await new TaskService(f).DeleteAsync(deleted.TaskItemId, "human");
        request = await f.LoadAsync(request.Id);
        Assert.Single(request.WorkItems);
        Assert.Equal(WorkStatus.Done, request.WorkItems.Single().Task.Status);
        Assert.Null(await f.Service.GetForTaskAsync(deleted.TaskItemId));
        Assert.Equal(2, JsonSerializer.Deserialize<TaskDraft[]>(request.Plans.Single().DraftJson)!.Length);
        await RejectAsync("invalid", () => f.Service.ConcludeAsync(request.Id, request.Version, "Missing required task", "", "reviewer", "after-delete"));
        // Retrying the already-published plan must not recreate a task deleted from the board.
        await f.Service.PublishPlanAsync(request.Id, request.Version, request.Plans.Single().Id, "human", "repeat-publication");
        var context = await WorkspaceMcpTools.GetRequestContext(f.Service, request.Id);
        Assert.Single(context.Tasks);
        Assert.False(Assert.Single(context.Results).Current);
        await using var db = f.CreateDbContext();
        Assert.Equal(1, await db.Tasks.CountAsync());
        Assert.False(await db.TaskEvidence.AnyAsync(e => e.WorkItemId == deleted.Id));
        Assert.Equal(1, await db.ActivityEvents.CountAsync(a => a.EntityId == deleted.TaskItemId && a.EventType == "TaskDeleted"));
        await KanbanFlow.Tests.Data.WorkflowSchemaTests.AssertForeignKeysValidAsync(db);
    }

    [Fact]
    public async Task DeletingProject_PreservesReadableIdeaHistoryAndPreventsConclusion()
    {
        using var f = new WorkflowSqliteTestFactory();
        var request = await f.PublishedAsync(Draft());
        var item = request.WorkItems.Single();
        request = await f.ReportAsync(request, item.Id);
        await new TaskService(f).DeleteProjectAsync(request.ProjectId!.Value, "human");
        request = await f.LoadAsync(request.Id);
        Assert.Null(request.ProjectId);
        Assert.Null(request.FeatureId);
        Assert.Empty(request.WorkItems);
        Assert.Equal("Original request", request.Messages.Single(m => m.Kind == "original").Text);
        Assert.Contains(request.Messages, m => m.Kind == "assistant-report");
        Assert.Single(request.Plans);
        var context = await WorkspaceMcpTools.GetRequestContext(f.Service, request.Id);
        Assert.Null(context.BoardUrl);
        Assert.Equal(request.IdeaId, context.IdeaId);
        Assert.Empty(context.Tasks);
        await RejectAsync("invalid", () => f.Service.ConcludeAsync(request.Id, request.Version, "No tasks is not completion", "", "human", "deleted-project"));
        await using var db = f.CreateDbContext();
        Assert.Empty(await db.Tasks.ToListAsync());
        Assert.Empty(await db.TaskEvidence.ToListAsync());
        Assert.Single(await db.Ideas.ToListAsync());
        await KanbanFlow.Tests.Data.WorkflowSchemaTests.AssertForeignKeysValidAsync(db);
    }

    private static async Task ChangeScopeAsync(WorkflowSqliteTestFactory f, int taskId, string change)
    {
        var tasks = new TaskService(f);
        if (change == "task-name") await tasks.UpdateAsync(taskId, new TaskUpdate(Name: "Edited through ordinary task service"), "human");
        else if (change == "task-description") await tasks.UpdateAsync(taskId, new TaskUpdate(Description: "Updated scope from the normal panel"), "human");
        else if (change == "native-task-update") await KanbanMcpTools.UpdateTask(tasks, taskId, new TaskUpdate(Name: "Native edited task", Description: "Native revised scope"), "agent");
        else if (change == "criterion-add") await KanbanMcpTools.CreateAcceptanceCriterion(f, taskId, "Additional current requirement");
        else if (change == "criterion-text")
        {
            var task = await KanbanMcpTools.GetTask(f, taskId);
            await KanbanMcpTools.UpdateAcceptanceCriterion(f, task.AcceptanceCriteria.First().Id, text: "Changed criterion through native MCP");
        }
        else if (change == "dependency-add")
        {
            var task = await KanbanMcpTools.GetTask(f, taskId);
            var prerequisite = await tasks.CreateAsync(new TaskCreate(task.FeatureId, "External prerequisite", Status: WorkStatus.Done), "human");
            await new TaskDependencyService(f).CreateAsync(new TaskDependencyCreate(taskId, prerequisite.Id, TaskDependencyType.DependsOn));
        }
        else
        {
            // These existing panel operations persist directly through AppDbContext.
            await using var db = f.CreateDbContext();
            if (change == "criterion-remove") db.AcceptanceCriteria.Remove(await db.AcceptanceCriteria.FirstAsync(c => c.TaskItemId == taskId));
            else if (change == "subtask-add") db.Subtasks.Add(new Subtask { TaskItemId = taskId, Text = "New implementation step" });
            else throw new ArgumentOutOfRangeException(nameof(change), change, "Unknown scope edit.");
            await db.SaveChangesAsync();
        }
    }
}
