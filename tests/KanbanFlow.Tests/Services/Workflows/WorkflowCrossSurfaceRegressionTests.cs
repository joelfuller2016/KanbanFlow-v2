using KanbanFlow.Mcp;
using KanbanFlow.Models;
using KanbanFlow.Models.Workflows;
using KanbanFlow.Services;
using Microsoft.EntityFrameworkCore;
using static KanbanFlow.Tests.WorkflowSqliteTestFactory;

namespace KanbanFlow.Tests.Services.Workflows;

public sealed class WorkflowCrossSurfaceRegressionTests
{
    [Theory]
    [InlineData("title")]
    [InlineData("description")]
    [InlineData("criterion-add")]
    public async Task SavingPlanFromEarlierContext_ConflictsAfterHumanEditWithoutResamplingItsTokens(string change)
    {
        using var f = new WorkflowSqliteTestFactory();
        var draft = Draft("Task as originally read");
        var request = await f.PublishedAsync(draft);
        var contextReadByPlanner = await WorkspaceMcpTools.GetRequestContext(f.Service, request.Id);
        var taskReadByPlanner = Assert.Single(contextReadByPlanner.Tasks);
        var originalTokens = contextReadByPlanner.Tasks.ToDictionary(t => t.TaskId, t => t.EditToken);
        Assert.False(string.IsNullOrWhiteSpace(taskReadByPlanner.EditToken));
        var plannedEdit = draft with { Title = "Planner title based on old context", Description = "Planner scope based on old context", Acceptance = "Planner acceptance based on old context" };

        if (change == "title") await new TaskService(f).UpdateAsync(taskReadByPlanner.TaskId, new TaskUpdate(Name: "Human title after context read"), "human");
        else if (change == "description") await KanbanMcpTools.UpdateTask(new TaskService(f), taskReadByPlanner.TaskId, new TaskUpdate(Description: "Human scope after context read"), "human");
        else await KanbanMcpTools.CreateAcceptanceCriterion(f, taskReadByPlanner.TaskId, "Human acceptance after context read");
        // These exact tokens were read before the human edit. Do not refresh them
        // merely because the planner is ready to save; doing so concealed this race.
        await RejectAsync("conflict", () => f.Service.SavePlanAsync(request.Id, contextReadByPlanner.Version, [plannedEdit], "planner", "stale-context-plan", expectedTaskStates: originalTokens));
        var after = await f.LoadAsync(request.Id);
        var actualTask = after.WorkItems.Single().Task;
        Assert.Equal(contextReadByPlanner.Version, after.Version);
        Assert.Equal(taskReadByPlanner.TaskId, actualTask.Id);
        Assert.Single(after.Plans);
        Assert.NotNull(after.Plans.Single().PublishedAt);
        Assert.Equal(request.WorkItems.Single().SpecificationHash, after.WorkItems.Single().SpecificationHash);
        if (change == "title") Assert.Equal("Human title after context read", actualTask.Name);
        else if (change == "description") Assert.Equal("Human scope after context read", actualTask.Description);
        else Assert.Contains(actualTask.AcceptanceCriteria, c => c.Text == "Human acceptance after context read");
        await using (var db = f.CreateDbContext())
        {
            Assert.False(await db.WorkflowReceipts.AnyAsync(r => r.Key == "stale-context-plan"));
            Assert.Equal(1, await db.ActivityEvents.CountAsync(a => a.EventType == "plan.save"));
        }

        // A deliberate reread lets the planner incorporate the human's actual fields
        // and author a new revision. The failed key still has no committed receipt.
        var reviewed = await WorkspaceMcpTools.GetRequestContext(f.Service, request.Id);
        var reviewedTask = reviewed.Tasks.Single();
        Assert.NotEqual(taskReadByPlanner.EditToken, reviewedTask.EditToken);
        var reviewedTokens = reviewed.Tasks.ToDictionary(t => t.TaskId, t => t.EditToken);
        var refreshed = plannedEdit with { Title = reviewedTask.Name, Description = reviewedTask.Description ?? "", Acceptance = string.Join('\n', reviewedTask.AcceptanceCriteria.Select(c => c.Text)) };
        await f.Service.SavePlanAsync(request.Id, reviewed.Version, [refreshed], "planner", "stale-context-plan", expectedTaskStates: reviewedTokens);
        after = await f.LoadAsync(request.Id);
        await f.Service.PublishPlanAsync(request.Id, after.Version, after.Plans.MaxBy(p => p.Number)!.Id, "planner", "reviewed-publication");
        var publishedTask = (await WorkspaceMcpTools.GetTaskWorkflow(f.Service, reviewedTask.TaskId)).Tasks.Single();
        Assert.Equal(reviewedTask.TaskId, publishedTask.TaskId);
        Assert.Equal(reviewedTask.Name, publishedTask.Name);
        Assert.Equal(reviewedTask.Description, publishedTask.Description);
        Assert.Equal(reviewedTask.AcceptanceCriteria.Select(c => c.Text), publishedTask.AcceptanceCriteria.Select(c => c.Text));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("partial")]
    [InlineData("foreign-task")]
    [InlineData("invalid-token")]
    public async Task SavingExistingPlan_RequiresCompleteTaskTokensFromTheReadContext(string invalidCase)
    {
        using var f = new WorkflowSqliteTestFactory();
        var a = Draft("A");
        var b = Draft("B");
        var request = await f.PublishedAsync(a, b);
        var context = await WorkspaceMcpTools.GetRequestContext(f.Service, request.Id);
        var tokens = context.Tasks.ToDictionary(t => t.TaskId, t => t.EditToken);
        var firstId = context.Tasks.First().TaskId;
        IReadOnlyDictionary<int, string>? supplied = tokens;
        switch (invalidCase)
        {
            case "missing": supplied = null; break;
            case "empty": supplied = new Dictionary<int, string>(); break;
            case "partial": tokens.Remove(firstId); break;
            case "foreign-task": tokens[int.MaxValue] = tokens[firstId]; tokens.Remove(firstId); break;
            default: tokens[firstId] = "invalid-observed-token"; break;
        }
        await RejectAsync(invalidCase == "invalid-token" ? "conflict" : "invalid", () => f.Service.SavePlanAsync(request.Id, context.Version, [a, b], "planner", "invalid-tokens", expectedTaskStates: supplied));
        var after = await f.LoadAsync(request.Id);
        Assert.Equal(context.Version, after.Version);
        Assert.Single(after.Plans);
        Assert.Equal(context.Tasks.Select(t => t.TaskId).Order(), after.WorkItems.Select(w => w.TaskItemId).Order());
        await using var db = f.CreateDbContext();
        Assert.False(await db.WorkflowReceipts.AnyAsync(r => r.Key == "invalid-tokens"));
        Assert.Equal(1, await db.ActivityEvents.CountAsync(audit => audit.EventType == "plan.save"));
    }

    [Fact]
    public async Task PublishingRevisedTask_CannotCreateCycleWithDependencyAddedOnUnchangedBoardTask()
    {
        using var f = new WorkflowSqliteTestFactory();
        var a = Draft("A");
        var b = Draft("B");
        var request = await f.PublishedAsync(a, b);
        var aItem = request.WorkItems.Single(w => w.DraftId == a.Id);
        var bItem = request.WorkItems.Single(w => w.DraftId == b.Id);
        await new TaskDependencyService(f).CreateAsync(new TaskDependencyCreate(aItem.TaskItemId, bItem.TaskItemId, TaskDependencyType.DependsOn));
        // The draft alone is acyclic (B -> A). A's existing board edge (A -> B)
        // remains because its draft is unchanged, so publication must check their union.
        await f.Service.SavePlanAsync(request.Id, request.Version, [a, b with { Dependencies = [a.Id] }], "planner", "revised-plan", expectedTaskStates: await f.ReadTaskTokensAsync(request.Id));
        request = await f.LoadAsync(request.Id);
        var unpublished = request.Plans.MaxBy(p => p.Number)!;
        var rejection = await RejectAsync("invalid", () => f.Service.PublishPlanAsync(request.Id, request.Version, unpublished.Id, "publisher", "cyclic-publication"));
        Assert.Contains("cycle", rejection.Message, StringComparison.OrdinalIgnoreCase);
        var after = await f.LoadAsync(request.Id);
        Assert.Equal(request.Version, after.Version);
        Assert.Null(after.Plans.Single(p => p.Id == unpublished.Id).PublishedAt);
        Assert.Equal(2, after.WorkItems.Count);
        var afterA = after.WorkItems.Single(w => w.DraftId == a.Id);
        var afterB = after.WorkItems.Single(w => w.DraftId == b.Id);
        Assert.Equal(aItem.TaskItemId, afterA.TaskItemId);
        Assert.Equal(bItem.TaskItemId, afterB.TaskItemId);
        Assert.Equal(bItem.TaskItemId, Assert.Single(afterA.Task.Dependencies).DependsOnTaskItemId);
        Assert.Empty(afterB.Task.Dependencies);
        Assert.Equal(bItem.SpecificationHash, afterB.SpecificationHash);
        Assert.Equal(bItem.SpecificationVersion, afterB.SpecificationVersion);
        Assert.Equal(bItem.Task.AcceptanceCriteria.Select(c => c.Id).Order(), afterB.Task.AcceptanceCriteria.Select(c => c.Id).Order());
        await using var db = f.CreateDbContext();
        Assert.Equal(2, await db.Tasks.CountAsync());
        Assert.Equal(1, await db.TaskDependencies.CountAsync());
        Assert.Equal(1, await db.ActivityEvents.CountAsync(audit => audit.EventType == "plan.publish"));
        Assert.False(await db.WorkflowReceipts.AnyAsync(r => r.Key == "cyclic-publication"));
        await KanbanFlow.Tests.Data.WorkflowSchemaTests.AssertForeignKeysValidAsync(db);
    }

    [Theory]
    [InlineData("title")]
    [InlineData("description")]
    [InlineData("criterion-add")]
    public async Task PublishingSavedRevision_ConflictsAfterHumanChangesSameTaskAndPreservesTheEdit(string change)
    {
        using var f = new WorkflowSqliteTestFactory();
        var draft = Draft("Original task");
        var request = await f.PublishedAsync(draft);
        var item = request.WorkItems.Single();
        var revised = draft with { Title = "Planned replacement title", Description = "Planned replacement scope", Acceptance = "Planned replacement check" };
        await f.Service.SavePlanAsync(request.Id, request.Version, [revised], "planner", "saved-revision", expectedTaskStates: await f.ReadTaskTokensAsync(request.Id));
        request = await f.LoadAsync(request.Id);
        var savedPlanId = request.Plans.MaxBy(p => p.Number)!.Id;
        if (change == "title") await new TaskService(f).UpdateAsync(item.TaskItemId, new TaskUpdate(Name: "Human task title after plan save"), "human");
        else if (change == "description") await KanbanMcpTools.UpdateTask(new TaskService(f), item.TaskItemId, new TaskUpdate(Description: "Human scope after plan save"), "human");
        else await KanbanMcpTools.CreateAcceptanceCriterion(f, item.TaskItemId, "Human acceptance after plan save");
        var humanTask = await KanbanMcpTools.GetTask(f, item.TaskItemId);
        await RejectAsync("conflict", () => f.Service.PublishPlanAsync(request.Id, request.Version, savedPlanId, "publisher", "stale-publish"));
        var after = await f.LoadAsync(request.Id);
        var actual = after.WorkItems.Single();
        Assert.Equal(request.Version, after.Version);
        Assert.Null(after.Plans.Single(p => p.Id == savedPlanId).PublishedAt);
        Assert.Equal(item.TaskItemId, actual.TaskItemId);
        Assert.Equal(item.SpecificationVersion, actual.SpecificationVersion);
        Assert.Equal(item.SpecificationHash, actual.SpecificationHash);
        Assert.Equal(humanTask.Name, actual.Task.Name);
        Assert.Equal(humanTask.Description, actual.Task.Description);
        Assert.Equal(humanTask.AcceptanceCriteria.Select(c => (c.Id, c.Text)), actual.Task.AcceptanceCriteria.OrderBy(c => c.SortOrder).Select(c => (c.Id, c.Text)));
        if (change == "title") Assert.Equal("Human task title after plan save", actual.Task.Name);
        else if (change == "description") Assert.Equal("Human scope after plan save", actual.Task.Description);
        else Assert.Contains(actual.Task.AcceptanceCriteria, c => c.Text == "Human acceptance after plan save");
        await using (var db = f.CreateDbContext())
        {
            Assert.False(await db.WorkflowReceipts.AnyAsync(r => r.Key == "stale-publish"));
            Assert.Equal(1, await db.ActivityEvents.CountAsync(a => a.EventType == "plan.publish"));
        }
        // An explicitly refreshed revision incorporates the human's current values.
        var refreshed = revised with { Title = humanTask.Name, Description = humanTask.Description ?? "", Acceptance = string.Join('\n', humanTask.AcceptanceCriteria.Select(c => c.Text)) };
        await f.Service.SavePlanAsync(after.Id, after.Version, [refreshed], "planner", "refreshed-plan", expectedTaskStates: await f.ReadTaskTokensAsync(after.Id));
        after = await f.LoadAsync(after.Id);
        await f.Service.PublishPlanAsync(after.Id, after.Version, after.Plans.MaxBy(p => p.Number)!.Id, "publisher", "refreshed-publish");
        actual = (await f.LoadAsync(after.Id)).WorkItems.Single();
        Assert.Equal(item.TaskItemId, actual.TaskItemId);
        Assert.Equal(humanTask.Name, actual.Task.Name);
        Assert.Equal(humanTask.Description, actual.Task.Description);
        Assert.Equal(humanTask.AcceptanceCriteria.Select(c => c.Text), actual.Task.AcceptanceCriteria.OrderBy(c => c.SortOrder).Select(c => c.Text));
    }

    [Theory]
    [InlineData("clarification")]
    [InlineData("decision")]
    [InlineData("idea-body")]
    public async Task ChangedRequestIntent_MakesConclusionHistoricalUntilBriefAndPlanAreRefreshed(string change)
    {
        using var f = new WorkflowSqliteTestFactory();
        var draft = Draft();
        var request = await f.PublishedAsync(draft);
        var itemId = request.WorkItems.Single().Id;
        request = await f.ReportAsync(request, itemId);
        request = await f.AcceptAsync(request, itemId);
        await f.Service.ConcludeAsync(request.Id, request.Version, "Original accepted conclusion", "", "reviewer", "original-result");
        request = await f.LoadAsync(request.Id);
        var resultId = request.Results.Single().Id;
        Assert.True((await f.Service.ContextAsync(request.Id))!.Results.Single().Current);
        if (change == "idea-body") await new IdeaService(f).UpdateAsync(request.IdeaId, new IdeaUpdate(Body: "Human changed the requested outcome", ExpectedWorkflowVersion: request.Version), "human");
        else await f.Service.AddMessageAsync(request.Id, request.Version, change, "Human changed the requested outcome", "human", "changed-intent");
        request = await f.LoadAsync(request.Id);
        var context = await WorkspaceMcpTools.GetRequestContext(f.Service, request.Id);
        Assert.Equal(resultId, context.Results.Single().Id);
        Assert.Equal("Original accepted conclusion", context.Results.Single().Conclusion);
        Assert.False(context.Results.Single().Current);
        Assert.Equal("Done", context.Tasks.Single().Status);
        await RejectAsync("invalid", () => f.Service.ConcludeAsync(request.Id, request.Version, "Cannot reuse old intent", "", "reviewer", "stale-intent-result"));
        await f.Service.SaveBriefAsync(request.Id, request.Version, Brief("Reviewed the changed outcome"), "human", "refreshed-brief");
        request = await f.LoadAsync(request.Id);
        await RejectAsync("invalid", () => f.Service.ConcludeAsync(request.Id, request.Version, "Plan is still old", "", "reviewer", "stale-plan-result"));
        await f.Service.SavePlanAsync(request.Id, request.Version, [draft], "human", "refreshed-plan", expectedTaskStates: await f.ReadTaskTokensAsync(request.Id));
        request = await f.LoadAsync(request.Id);
        await f.Service.PublishPlanAsync(request.Id, request.Version, request.Plans.MaxBy(p => p.Number)!.Id, "human", "refreshed-publish");
        request = await f.LoadAsync(request.Id);
        await f.Service.ConcludeAsync(request.Id, request.Version, "Verified against refreshed intent", "", "reviewer", "current-result");
        context = await WorkspaceMcpTools.GetRequestContext(f.Service, request.Id);
        Assert.Equal(2, context.Results.Count);
        Assert.False(context.Results.Single(r => r.Id == resultId).Current);
        Assert.Equal("Verified against refreshed intent", Assert.Single(context.Results, r => r.Current).Conclusion);
        await using var db = f.CreateDbContext();
        Assert.False(await db.WorkflowReceipts.AnyAsync(r => r.Key == "stale-intent-result" || r.Key == "stale-plan-result"));
    }

    [Fact]
    public async Task ProgressMessage_PreservesCurrentConclusionAndItsOriginalEvidence()
    {
        using var f = new WorkflowSqliteTestFactory();
        var request = await f.PublishedAsync(Draft());
        var itemId = request.WorkItems.Single().Id;
        request = await f.ReportAsync(request, itemId);
        request = await f.AcceptAsync(request, itemId);
        await f.Service.ConcludeAsync(request.Id, request.Version, "Checked conclusion", "", "reviewer", "result");
        request = await f.LoadAsync(request.Id);
        await f.Service.AddMessageAsync(request.Id, request.Version, "progress", "Shared the existing result with the reviewer", "agent", "progress-note");
        var context = await WorkspaceMcpTools.GetRequestContext(f.Service, request.Id);
        Assert.Equal(request.Results.Single().Id, context.Results.Single().Id);
        Assert.True(context.Results.Single().Current);
        Assert.True(context.Tasks.Single().Evidence.Single().Current);
        Assert.NotNull(context.Tasks.Single().Evidence.Single().AcceptedAt);
        Assert.Contains(context.Messages, m => m.Kind == "progress" && m.Text == "Shared the existing result with the reviewer");
    }
}
