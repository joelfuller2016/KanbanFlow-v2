using System.Text.Json;
using KanbanFlow.Models;
using KanbanFlow.Models.Workflows;
using KanbanFlow.Services.Workflows;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using static KanbanFlow.Tests.WorkflowSqliteTestFactory;

namespace KanbanFlow.Tests.Services;

public sealed partial class WorkspaceServiceTests
{
    public static IEnumerable<object[]> InvalidDraftGraphs()
    {
        var a = Draft("A");
        var b = Draft("B");
        var c = Draft("C");
        yield return ["null-plan", null!];
        yield return ["null-draft", new TaskDraft[] { null! }];
        yield return ["empty-id", new[] { a with { Id = Guid.Empty } }];
        yield return ["duplicate-id", new[] { a, a }];
        yield return ["self-dependency", new[] { a with { Dependencies = [a.Id] } }];
        yield return ["cycle", new[] { a with { Dependencies = [b.Id] }, b with { Dependencies = [c.Id] }, c with { Dependencies = [a.Id] } }];
        yield return ["missing-dependency", new[] { a with { Dependencies = [b.Id] } }];
        yield return ["duplicate-dependency", new[] { a, b with { Dependencies = [a.Id, a.Id] } }];
        yield return ["null-dependencies", new[] { a with { Dependencies = null! } }];
        yield return ["invalid-priority", new[] { a with { Priority = (TaskPriority)99 } }];
        yield return ["no-acceptance", new[] { a with { Acceptance = " \n " } }];
        yield return ["references-without-repository", new[] { a with { FileReferences = "src/Code.cs" } }];
        yield return ["parent-reference", new[] { a with { RepositoryId = 1, FileReferences = "../outside.cs" } }];
    }

    [Theory]
    [MemberData(nameof(InvalidDraftGraphs))]
    public async Task SavePlan_RejectsInvalidGraphWithoutMutation(string invalidCase, TaskDraft[] drafts)
    {
        using var f = new WorkflowSqliteTestFactory();
        var request = await f.BriefedAsync();
        await RejectAsync("invalid", () => f.Service.SavePlanAsync(request.Id, request.Version, drafts, Actor, invalidCase));
        var after = await f.LoadAsync(request.Id);
        Assert.Empty(after.Plans);
        Assert.Empty(after.WorkItems);
        Assert.Equal(request.Version, after.Version);
        await using var db = f.CreateDbContext();
        Assert.False(await db.WorkflowReceipts.AnyAsync(r => r.Key == invalidCase));
        Assert.False(await db.ActivityEvents.AnyAsync(a => a.EventType == "plan.save"));
    }

    [Fact]
    public void ValidatePlan_AcceptsZeroAndHundredTasksButRejectsHundredAndOne()
    {
        WorkspaceService.ValidatePlan([]);
        var hundred = Enumerable.Range(1, 100).Select(i => Draft($"Task {i}")).ToArray();
        WorkspaceService.ValidatePlan(hundred);
        Assert.Equal("invalid", Assert.Throws<WorkflowException>(() => WorkspaceService.ValidatePlan([.. hundred, Draft("Overflow")])).Code);
        Assert.Empty(WorkspaceService.Drafts(null));
    }

    [Fact]
    public async Task SavePlan_RejectsDependencyOnTaskInAnotherRequest()
    {
        using var f = new WorkflowSqliteTestFactory();
        var foreignDraft = Draft("Foreign task");
        var foreign = await f.PublishedAsync(foreignDraft);
        var request = await f.BriefedAsync();
        await RejectAsync("invalid", () => f.Service.SavePlanAsync(request.Id, request.Version, [Draft("Dependent", foreignDraft.Id)], Actor, "cross-request"));
        Assert.Empty((await f.LoadAsync(request.Id)).Plans);
        Assert.Equal(foreign.WorkItems.Single().TaskItemId, (await f.LoadAsync(foreign.Id)).WorkItems.Single().TaskItemId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavePlan_RejectsNonexistentOrOtherProjectRepository(bool repositoryExistsElsewhere)
    {
        using var f = new WorkflowSqliteTestFactory();
        var request = await f.BriefedAsync();
        var repositoryId = int.MaxValue;
        if (repositoryExistsElsewhere)
        {
            var other = await f.Service.CreateProjectAsync("Other", Actor, "other");
            await using var db = f.CreateDbContext();
            var repository = new ProjectRepository { ProjectId = other, Name = "Other repository", LocalPath = Path.Combine(f.ScratchDirectory, "other") };
            db.ProjectRepositories.Add(repository);
            await db.SaveChangesAsync();
            repositoryId = repository.Id;
        }
        await RejectAsync("invalid", () => f.Service.SavePlanAsync(request.Id, request.Version,
            [Draft() with { RepositoryId = repositoryId, FileReferences = "src/Code.cs" }], Actor, "bad-repository"));
        Assert.Empty((await f.LoadAsync(request.Id)).Plans);
    }

    [Fact]
    public async Task SavePlan_RequiresBriefAndPreservesDraftAsAuthored()
    {
        using var f = new WorkflowSqliteTestFactory();
        var receipt = await f.Service.CaptureAsync(new CaptureRequest("Request", "Original", null), Actor, "capture");
        var draft = Draft("  Authored title  ") with { Description = "  Description\r\n", Acceptance = "  One check  \n\nTwo checks", Assignee = "  assignee  " };
        await RejectAsync("invalid", () => f.Service.SavePlanAsync(receipt.RequestId, receipt.Version, [draft], Actor, "no-brief"));
        var brief = await f.Service.SaveBriefAsync(receipt.RequestId, receipt.Version, Brief(), Actor, "brief");
        await f.Service.SavePlanAsync(receipt.RequestId, brief.Version, [draft], Actor, "plan");
        var request = await f.LoadAsync(receipt.RequestId);
        var plan = Assert.Single(request.Plans);
        Assert.Equal(request.Briefs.Single().Id, plan.BriefRevisionId);
        Assert.Null(plan.PublishedAt);
        var reloadedDraft = Assert.Single(WorkspaceService.Drafts(plan));
        Assert.Equal(draft.Id, reloadedDraft.Id);
        Assert.Equal(draft.Title, reloadedDraft.Title);
        Assert.Equal(draft.Description, reloadedDraft.Description);
        Assert.Equal(draft.Acceptance, reloadedDraft.Acceptance);
        Assert.Equal(draft.Assignee, reloadedDraft.Assignee);
        Assert.Empty(request.WorkItems);
    }

    [Fact]
    public async Task PublishPlan_PersistsCompleteGraphAuditAndReceipt()
    {
        using var f = new WorkflowSqliteTestFactory();
        var first = Draft("  First task  ") with { Acceptance = "  Criterion one\n\nCriterion two  ", Assignee = "  owner  " };
        var second = Draft("Second task", first.Id);
        var third = Draft("Third task", first.Id, second.Id);
        var request = await f.PlannedAsync(first, second, third);
        var planId = request.Plans.Single().Id;
        var publishedAt = f.Clock.GetUtcNow().UtcDateTime;
        var receipt = await f.Service.PublishPlanAsync(request.Id, request.Version, planId, "publisher", "publish");

        var after = await f.LoadAsync(request.Id);
        Assert.Equal(request.Version + 1, receipt.Version);
        Assert.Equal(receipt.Version, after.Version);
        Assert.Equal(publishedAt, after.Plans.Single().PublishedAt);
        Assert.Equal(3, after.WorkItems.Count);
        var firstItem = after.WorkItems.Single(w => w.DraftId == first.Id);
        var secondItem = after.WorkItems.Single(w => w.DraftId == second.Id);
        var thirdItem = after.WorkItems.Single(w => w.DraftId == third.Id);
        Assert.Equal("First task", firstItem.Task.Name);
        Assert.Equal(first.Description, firstItem.Task.Description);
        Assert.Equal(TaskPriority.High, firstItem.Task.Priority);
        Assert.Equal("owner", firstItem.Task.Assignee);
        Assert.Equal(new[] { 0d, 1d, 2d }, new[] { firstItem.Task.SortOrder, secondItem.Task.SortOrder, thirdItem.Task.SortOrder });
        Assert.Collection(firstItem.Task.AcceptanceCriteria.OrderBy(c => c.SortOrder),
            c => { Assert.Equal("Criterion one", c.Text); Assert.Equal(0, c.SortOrder); Assert.False(c.Completed); },
            c => { Assert.Equal("Criterion two", c.Text); Assert.Equal(1, c.SortOrder); Assert.False(c.Completed); });
        Assert.All(after.WorkItems, item =>
        {
            Assert.Equal(planId, item.PlanVersionId);
            Assert.Equal(request.Id, item.RequestId);
            Assert.Equal(1, item.SpecificationVersion);
            Assert.Equal(64, item.SpecificationHash.Length);
            Assert.Equal(item.DraftId, JsonSerializer.Deserialize<TaskDraft>(item.SpecificationJson)!.Id);
            Assert.Equal(WorkStatus.Todo, item.Task.Status);
            Assert.Null(item.Task.CompletedAt);
        });
        Assert.Empty(firstItem.Task.Dependencies);
        Assert.Equal(firstItem.TaskItemId, Assert.Single(secondItem.Task.Dependencies).DependsOnTaskItemId);
        Assert.Equal(new[] { firstItem.TaskItemId, secondItem.TaskItemId }.Order(), thirdItem.Task.Dependencies.Select(d => d.DependsOnTaskItemId).Order());

        await using var db = f.CreateDbContext();
        var feature = Assert.Single(await db.Features.ToListAsync());
        Assert.Equal(request.ProjectId, feature.ProjectId);
        Assert.Equal(request.Title, feature.Name);
        Assert.All(after.WorkItems, w => Assert.Equal(feature.Id, w.Task.FeatureId));
        Assert.Equal(3, await db.Tasks.CountAsync());
        Assert.Equal(6, await db.AcceptanceCriteria.CountAsync());
        Assert.All(await db.TaskDependencies.ToListAsync(), d => Assert.Equal(request.ProjectId, d.ProjectId));
        var audit = Assert.Single(await db.ActivityEvents.Where(a => a.EventType == "plan.publish").ToListAsync());
        Assert.Equal("publisher", audit.Actor);
        Assert.Equal(request.Id, JsonDocument.Parse(audit.AfterJson!).RootElement.GetProperty("requestId").GetGuid());
        var storedReceipt = Assert.Single(await db.WorkflowReceipts.Where(r => r.Operation == "plan.publish").ToListAsync());
        Assert.Equal("publisher", storedReceipt.Actor);
        Assert.Equal("publish", storedReceipt.Key);
        Assert.Equal(receipt, JsonSerializer.Deserialize<MutationReceipt>(storedReceipt.ResultJson));
    }

    [Fact]
    public async Task PublishPlan_DatabaseFailureRollsBackGraphAuditAndReceipt()
    {
        using var f = new WorkflowSqliteTestFactory();
        var first = Draft("First");
        var request = await f.PlannedAsync(first, Draft("Dependent", first.Id));
        var planId = request.Plans.Single().Id;
        int originalAuditCount;
        int originalReceiptCount;
        await using (var db = f.CreateDbContext())
        {
            originalAuditCount = await db.ActivityEvents.CountAsync();
            originalReceiptCount = await db.WorkflowReceipts.CountAsync();
            // Fails at the final receipt insert, after PublishPlan's intermediate SaveChanges calls.
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER reject_publish_receipt BEFORE INSERT ON WorkflowReceipts
                WHEN NEW.Operation = 'plan.publish'
                BEGIN SELECT RAISE(ABORT, 'publication failure injected by test'); END;
                """);
        }
        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => f.Service.PublishPlanAsync(request.Id, request.Version, planId, Actor, "publish"));
        var sqlite = Assert.IsType<SqliteException>(failure.InnerException);
        Assert.Equal(19, sqlite.SqliteErrorCode);
        Assert.Contains("publication failure injected by test", sqlite.Message);
        var rolledBack = await f.LoadAsync(request.Id);
        Assert.Equal(request.Version, rolledBack.Version);
        Assert.Null(rolledBack.Plans.Single().PublishedAt);
        Assert.Empty(rolledBack.WorkItems);
        await using (var db = f.CreateDbContext())
        {
            Assert.Empty(await db.Features.ToListAsync());
            Assert.Empty(await db.Tasks.ToListAsync());
            Assert.Empty(await db.AcceptanceCriteria.ToListAsync());
            Assert.Empty(await db.TaskDependencies.ToListAsync());
            Assert.Equal(originalAuditCount, await db.ActivityEvents.CountAsync());
            Assert.Equal(originalReceiptCount, await db.WorkflowReceipts.CountAsync());
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_publish_receipt;");
        }
        // The failed operation key remains usable after the database fault is removed.
        await f.Service.PublishPlanAsync(request.Id, request.Version, planId, Actor, "publish");
        Assert.Equal(2, (await f.LoadAsync(request.Id)).WorkItems.Count);
    }

    [Fact]
    public async Task PublishPlan_RetryDoesNotDuplicatePublishedGraph()
    {
        using var f = new WorkflowSqliteTestFactory();
        var first = Draft("First");
        var request = await f.PlannedAsync(first, Draft("Second", first.Id));
        var planId = request.Plans.Single().Id;
        var receipt = await f.Service.PublishPlanAsync(request.Id, request.Version, planId, Actor, "publish");
        var retry = await f.Service.PublishPlanAsync(request.Id, request.Version, planId, Actor, "publish");
        Assert.Equal(receipt, retry);
        var initialItems = (await f.LoadAsync(request.Id)).WorkItems.Select(w => (w.Id, w.TaskItemId)).OrderBy(w => w.Id).ToArray();
        // A fresh operation against the already published plan also cannot materialize it twice.
        await f.Service.PublishPlanAsync(request.Id, receipt.Version, planId, Actor, "publish-again");
        Assert.Equal(initialItems, (await f.LoadAsync(request.Id)).WorkItems.Select(w => (w.Id, w.TaskItemId)).OrderBy(w => w.Id).ToArray());
        await using var db = f.CreateDbContext();
        Assert.Equal(1, await db.Features.CountAsync());
        Assert.Equal(2, await db.Tasks.CountAsync());
        Assert.Equal(4, await db.AcceptanceCriteria.CountAsync());
        Assert.Equal(1, await db.TaskDependencies.CountAsync());
        Assert.Equal(1, await db.WorkflowReceipts.CountAsync(r => r.Operation == "plan.publish" && r.Key == "publish"));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("unassigned")]
    [InlineData("older-plan")]
    [InlineData("changed-brief")]
    public async Task PublishPlan_RequiresTasksProjectAndLatestBriefAndPlan(string invalidCase)
    {
        using var f = new WorkflowSqliteTestFactory();
        var request = await f.PlannedAsync(invalidCase == "empty" ? [] : [Draft()]);
        var planId = request.Plans.Single().Id;
        if (invalidCase == "unassigned")
        {
            await using var db = f.CreateDbContext();
            (await db.WorkRequests.FindAsync(request.Id))!.ProjectId = null;
            await db.SaveChangesAsync();
        }
        if (invalidCase == "older-plan") await f.Service.SavePlanAsync(request.Id, request.Version, [Draft("New plan")], Actor, "new-plan");
        if (invalidCase == "changed-brief") await f.Service.SaveBriefAsync(request.Id, request.Version, Brief("Changed brief"), Actor, "new-brief");
        request = await f.LoadAsync(request.Id);
        await RejectAsync("invalid", () => f.Service.PublishPlanAsync(request.Id, request.Version, planId, Actor, "invalid-publish"));
        Assert.Empty((await f.LoadAsync(request.Id)).WorkItems);
        await using var verify = f.CreateDbContext();
        Assert.Empty(await verify.Tasks.ToListAsync());
        Assert.False(await verify.WorkflowReceipts.AnyAsync(r => r.Key == "invalid-publish"));
    }

    [Fact]
    public async Task SavePlan_CannotRetireAnAlreadyPublishedTask()
    {
        using var f = new WorkflowSqliteTestFactory();
        var draft = Draft();
        var request = await f.PublishedAsync(draft);
        var taskTokens = await f.ReadTaskTokensAsync(request.Id);
        await RejectAsync("invalid", () => f.Service.SavePlanAsync(request.Id, request.Version, [Draft("Replacement identity")], Actor, "retire", expectedTaskStates: taskTokens));
        var after = await f.LoadAsync(request.Id);
        Assert.Equal(draft.Id, Assert.Single(after.WorkItems).DraftId);
        Assert.Single(after.Plans);
    }

    [Fact]
    public async Task PublishPlan_CannotChangeSpecificationWhileTaskIsInProgress()
    {
        using var f = new WorkflowSqliteTestFactory();
        var draft = Draft();
        var request = await f.PublishedAsync(draft);
        var item = request.WorkItems.Single();
        await f.Service.SetTaskStateAsync(request.Id, request.Version, item.Id, WorkStatus.InProgress, Actor, "start");
        request = await f.LoadAsync(request.Id);
        await f.Service.SavePlanAsync(request.Id, request.Version, [draft with { Description = "Changed specification" }], Actor, "replan", expectedTaskStates: await f.ReadTaskTokensAsync(request.Id));
        request = await f.LoadAsync(request.Id);
        await RejectAsync("invalid", () => f.Service.PublishPlanAsync(request.Id, request.Version, request.Plans.MaxBy(p => p.Number)!.Id, Actor, "republish"));
        var after = await f.LoadAsync(request.Id);
        Assert.Equal(WorkStatus.InProgress, after.WorkItems.Single().Task.Status);
        Assert.Equal(item.SpecificationVersion, after.WorkItems.Single().SpecificationVersion);
        Assert.Equal(item.SpecificationHash, after.WorkItems.Single().SpecificationHash);
        Assert.Null(after.Plans.MaxBy(p => p.Number)!.PublishedAt);
    }

    [Fact]
    public async Task Replan_PreservesTaskIdentityAndRejectsEvidenceForOldSpecification()
    {
        using var f = new WorkflowSqliteTestFactory();
        var draft = Draft();
        var request = await f.PublishedAsync(draft);
        var original = request.WorkItems.Single();
        request = await f.ReportAsync(request, original.Id);
        request = await f.AcceptAsync(request, original.Id);
        var oldEvidence = request.WorkItems.Single().Evidence.Single();
        var oldCriterionIds = request.WorkItems.Single().Task.AcceptanceCriteria.Select(c => c.Id).ToArray();
        await f.Service.SavePlanAsync(request.Id, request.Version, [draft with { Description = "Changed specification", Acceptance = "Replacement acceptance" }], Actor, "replan", expectedTaskStates: await f.ReadTaskTokensAsync(request.Id));
        request = await f.LoadAsync(request.Id);
        var newPlan = request.Plans.MaxBy(p => p.Number)!;
        await f.Service.PublishPlanAsync(request.Id, request.Version, newPlan.Id, Actor, "republish");
        request = await f.LoadAsync(request.Id);
        var changed = Assert.Single(request.WorkItems);
        Assert.Equal(original.Id, changed.Id);
        Assert.Equal(original.TaskItemId, changed.TaskItemId);
        Assert.Equal(draft.Id, changed.DraftId);
        Assert.Equal(2, changed.SpecificationVersion);
        Assert.NotEqual(original.SpecificationHash, changed.SpecificationHash);
        Assert.Equal(newPlan.Id, changed.PlanVersionId);
        Assert.Equal("Changed specification", changed.Task.Description);
        Assert.Equal(WorkStatus.Todo, changed.Task.Status);
        Assert.Null(changed.Task.CompletedAt);
        var criterion = Assert.Single(changed.Task.AcceptanceCriteria);
        Assert.Equal("Replacement acceptance", criterion.Text);
        Assert.False(criterion.Completed);
        Assert.DoesNotContain(criterion.Id, oldCriterionIds);
        Assert.Equal(oldEvidence.Id, Assert.Single(changed.Evidence).Id);
        Assert.Equal(1, changed.Evidence.Single().SpecificationVersion);
        await RejectAsync("invalid", () => f.Service.AcceptEvidenceAsync(request.Id, request.Version, changed.Id, oldEvidence.Id, [criterion.Id], Actor, "old-evidence"));
        Assert.Equal(WorkStatus.Todo, (await f.LoadAsync(request.Id)).WorkItems.Single().Task.Status);
        request = await f.ReportAsync(request, changed.Id);
        request = await f.AcceptAsync(request, changed.Id);
        Assert.Equal(WorkStatus.Done, request.WorkItems.Single().Task.Status);
        Assert.Equal(2, request.WorkItems.Single().Evidence.Count);
    }

    [Fact]
    public async Task Replan_UnchangedSpecificationKeepsAcceptedTaskAndEvidence()
    {
        using var f = new WorkflowSqliteTestFactory();
        var draft = Draft();
        var request = await f.PublishedAsync(draft);
        var itemId = request.WorkItems.Single().Id;
        request = await f.ReportAsync(request, itemId);
        request = await f.AcceptAsync(request, itemId);
        var before = request.WorkItems.Single();
        await f.Service.SavePlanAsync(request.Id, request.Version, [draft], Actor, "same-spec", expectedTaskStates: await f.ReadTaskTokensAsync(request.Id));
        request = await f.LoadAsync(request.Id);
        await f.Service.PublishPlanAsync(request.Id, request.Version, request.Plans.MaxBy(p => p.Number)!.Id, Actor, "same-publish");
        var after = (await f.LoadAsync(request.Id)).WorkItems.Single();
        Assert.Equal(before.TaskItemId, after.TaskItemId);
        Assert.Equal(before.SpecificationVersion, after.SpecificationVersion);
        Assert.Equal(before.SpecificationHash, after.SpecificationHash);
        Assert.Equal(WorkStatus.Done, after.Task.Status);
        Assert.Equal(before.Task.CompletedAt, after.Task.CompletedAt);
        Assert.Equal(before.Evidence.Single().Id, after.Evidence.Single().Id);
        Assert.Equal(before.Task.AcceptanceCriteria.Select(c => c.Id).Order(), after.Task.AcceptanceCriteria.Select(c => c.Id).Order());
        Assert.All(after.Task.AcceptanceCriteria, c => Assert.True(c.Completed));
    }

    [Fact]
    public async Task Replan_ReplacesDependencyEdgesAndKeepsTaskIdentity()
    {
        using var f = new WorkflowSqliteTestFactory();
        var a = Draft("A");
        var b = Draft("B", a.Id);
        var c = Draft("C");
        var request = await f.PublishedAsync(a, b, c);
        var oldB = request.WorkItems.Single(w => w.DraftId == b.Id);
        var aTaskId = request.WorkItems.Single(w => w.DraftId == a.Id).TaskItemId;
        var cTaskId = request.WorkItems.Single(w => w.DraftId == c.Id).TaskItemId;
        await f.Service.SavePlanAsync(request.Id, request.Version, [a, b with { Dependencies = [c.Id] }, c], Actor, "different-edge", expectedTaskStates: await f.ReadTaskTokensAsync(request.Id));
        request = await f.LoadAsync(request.Id);
        await f.Service.PublishPlanAsync(request.Id, request.Version, request.Plans.MaxBy(p => p.Number)!.Id, Actor, "publish-different-edge");
        var afterB = (await f.LoadAsync(request.Id)).WorkItems.Single(w => w.DraftId == b.Id);
        Assert.Equal(oldB.Id, afterB.Id);
        Assert.Equal(oldB.TaskItemId, afterB.TaskItemId);
        Assert.Equal(2, afterB.SpecificationVersion);
        Assert.Equal(cTaskId, Assert.Single(afterB.Task.Dependencies).DependsOnTaskItemId);
        await using var db = f.CreateDbContext();
        Assert.Equal(1, await db.TaskDependencies.CountAsync());
        Assert.False(await db.TaskDependencies.AnyAsync(d => d.DependsOnTaskItemId == aTaskId));
        Assert.Equal(3, await db.Tasks.CountAsync());
    }

    [Theory]
    [InlineData("repository-transferred")]
    [InlineData("repository-removed")]
    public async Task PublishPlan_RevalidatesRepositoryOwnershipAndExistenceAfterDrafting(string change)
    {
        using var f = new WorkflowSqliteTestFactory();
        var request = await f.BriefedAsync();
        var path = Path.Combine(f.ScratchDirectory, "repository");
        Directory.CreateDirectory(Path.Combine(path, ".git"));
        var repositoryId = await f.Service.RegisterRepositoryAsync(request.ProjectId!.Value, "Repository", path, f.ScratchDirectory, Actor, "repository");
        var draft = Draft() with { RepositoryId = repositoryId, FileReferences = "src/Implementation.cs\ndocs/design.md" };
        await f.Service.SavePlanAsync(request.Id, request.Version, [draft], Actor, "valid-plan");
        request = await f.LoadAsync(request.Id);
        var persistedDraft = Assert.Single(WorkspaceService.Drafts(request.Plans.Single()));
        Assert.Equal(repositoryId, persistedDraft.RepositoryId);
        Assert.Equal(draft.FileReferences, persistedDraft.FileReferences);
        if (change == "repository-transferred")
        {
            var newProject = await f.Service.CreateProjectAsync("Other project", Actor, "new-project");
            await using var db = f.CreateDbContext();
            (await db.ProjectRepositories.FindAsync(repositoryId))!.ProjectId = newProject;
            await db.SaveChangesAsync();
        }
        else
        {
            await using var db = f.CreateDbContext();
            db.ProjectRepositories.Remove((await db.ProjectRepositories.FindAsync(repositoryId))!);
            await db.SaveChangesAsync();
        }
        request = await f.LoadAsync(request.Id);
        await RejectAsync("invalid", () => f.Service.PublishPlanAsync(request.Id, request.Version, request.Plans.Single().Id, Actor, "stale-repository-publish"));
        var after = await f.LoadAsync(request.Id);
        Assert.Equal(request.Version, after.Version);
        Assert.Empty(after.WorkItems);
        Assert.Null(after.Plans.Single().PublishedAt);
        await using var verify = f.CreateDbContext();
        Assert.Empty(await verify.Tasks.ToListAsync());
        Assert.False(await verify.WorkflowReceipts.AnyAsync(r => r.Key == "stale-repository-publish"));
    }
}
