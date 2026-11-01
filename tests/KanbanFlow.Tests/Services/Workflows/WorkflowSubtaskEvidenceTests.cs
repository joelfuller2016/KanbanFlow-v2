using KanbanFlow.Models;
using KanbanFlow.Models.Workflows;
using KanbanFlow.Services;
using KanbanFlow.Services.Workflows;
using Microsoft.EntityFrameworkCore;
using static KanbanFlow.Tests.WorkflowSqliteTestFactory;

namespace KanbanFlow.Tests.Services.Workflows;

public sealed class WorkflowSubtaskEvidenceTests
{
    [Theory]
    [InlineData("order", false)]
    [InlineData("hierarchy", false)]
    [InlineData("order", true)]
    [InlineData("hierarchy", true)]
    [InlineData("completion", true)]
    public async Task SubtaskScopeOrPrerequisiteProgressChange_InvalidatesReportAndRejectsAcceptance(string change, bool prerequisite)
    {
        using var f = new WorkflowSqliteTestFactory();
        var first = Draft("First");
        var root = prerequisite ? Draft("Dependent", first.Id) : first;
        var request = await f.PublishedAsync(prerequisite ? [first, root] : [root]);
        var item = request.WorkItems.Single(w => w.DraftId == root.Id);
        var observedTaskId = request.WorkItems.Single(w => w.DraftId == first.Id).TaskItemId;
        var (childId, alternateParentId) = await AddSubtasksAsync(f, observedTaskId);
        request = await f.ReportAsync(request, item.Id);
        var oldReport = request.WorkItems.Single(w => w.Id == item.Id).Evidence.Single();
        Assert.True((await f.Service.ContextAsync(request.Id))!.Tasks.Single(t => t.Id == item.Id).Evidence.Single().Current);

        await using (var db = f.CreateDbContext())
        {
            var child = await db.Subtasks.SingleAsync(s => s.Id == childId);
            if (change == "order") child.SortOrder += 10;
            else if (change == "hierarchy") child.ParentSubtaskId = alternateParentId;
            else child.Completed = false;
            await db.SaveChangesWithAuditAsync("human");
            Assert.NotEqual(oldReport.TaskStateHash, await WorkspaceService.TaskStateHashAsync(db, item.TaskItemId));
        }

        var context = (await f.Service.ContextAsync(request.Id))!;
        Assert.False(context.Tasks.Single(t => t.Id == item.Id).Evidence.Single().Current);
        await RejectAsync("invalid", () => f.Service.AcceptEvidenceAsync(request.Id, request.Version, item.Id, oldReport.Id,
            item.Task.AcceptanceCriteria.Select(c => c.Id).ToArray(), "reviewer", "stale-subtask"));
        var unchanged = (await f.LoadAsync(request.Id)).WorkItems.Single(w => w.Id == item.Id);
        Assert.Null(unchanged.Evidence.Single().AcceptedAt);
        Assert.Equal(WorkStatus.Review, unchanged.Task.Status);
        request = await f.ReportAsync(request, item.Id);
        var reports = (await f.Service.ContextAsync(request.Id))!.Tasks.Single(t => t.Id == item.Id).Evidence;
        Assert.False(reports.Single(e => e.Id == oldReport.Id).Current);
        Assert.Single(reports, e => e.Current);
    }

    [Fact]
    public async Task OwnSubtaskCompletion_RemainsProgress_AndAcceptanceDoesNotInvalidateItsReport()
    {
        using var f = new WorkflowSqliteTestFactory();
        var request = await f.PublishedAsync(Draft());
        var item = request.WorkItems.Single();
        var (childId, _) = await AddSubtasksAsync(f, item.TaskItemId);
        request = await f.ReportAsync(request, item.Id);
        var hash = request.WorkItems.Single().Evidence.Single().TaskStateHash;
        await using (var db = f.CreateDbContext())
        {
            (await db.Subtasks.SingleAsync(s => s.Id == childId)).Completed = false;
            await db.SaveChangesWithAuditAsync("human");
            Assert.Equal(hash, await WorkspaceService.TaskStateHashAsync(db, item.TaskItemId));
        }
        Assert.True((await f.Service.ContextAsync(request.Id))!.Tasks.Single().Evidence.Single().Current);
        request = await f.AcceptAsync(request, item.Id);
        Assert.Equal(WorkStatus.Done, request.WorkItems.Single().Task.Status);
        Assert.True((await f.Service.ContextAsync(request.Id))!.Tasks.Single().Evidence.Single().Current);
    }

    [Fact]
    public async Task AcceptedSubtaskScopeChange_PreservesHistoryAndInvalidatesConclusion()
    {
        using var f = new WorkflowSqliteTestFactory();
        var request = await f.PublishedAsync(Draft());
        var item = request.WorkItems.Single();
        var (childId, _) = await AddSubtasksAsync(f, item.TaskItemId);
        request = await f.ReportAsync(request, item.Id);
        request = await f.AcceptAsync(request, item.Id);
        await f.Service.ConcludeAsync(request.Id, request.Version, "Checked nested scope", "Original limitations", "reviewer", "initial-conclusion");
        request = await f.LoadAsync(request.Id);
        Assert.True((await f.Service.ContextAsync(request.Id))!.Results.Single().Current);
        await using (var db = f.CreateDbContext())
        {
            (await db.Subtasks.SingleAsync(s => s.Id == childId)).SortOrder += 10;
            await db.SaveChangesWithAuditAsync("human");
        }
        var context = (await f.Service.ContextAsync(request.Id))!;
        Assert.False(context.Tasks.Single().Evidence.Single().Current);
        Assert.NotNull(context.Tasks.Single().Evidence.Single().AcceptedAt);
        Assert.False(context.Results.Single().Current);
        Assert.Equal("Checked nested scope", context.Results.Single().Conclusion);
        await RejectAsync("invalid", () => f.Service.ConcludeAsync(request.Id, request.Version, "Unverified conclusion", "", "reviewer", "stale-conclusion"));
        Assert.Single((await f.LoadAsync(request.Id)).Results);
    }

    [Fact]
    public async Task ReportsWithSubtasks_UseAnExplicitVersionedStateHash()
    {
        using var f = new WorkflowSqliteTestFactory();
        var request = await f.PublishedAsync(Draft());
        var item = request.WorkItems.Single();
        await AddSubtasksAsync(f, item.TaskItemId);
        request = await f.ReportAsync(request, item.Id);
        Assert.Matches("^task-state-v2:[0-9A-F]{64}$", request.WorkItems.Single().Evidence.Single().TaskStateHash);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacySubtaskReport_RemainsReadableAndHistorical_AndCannotAuthorizeCompletion(bool accepted)
    {
        using var f = new WorkflowSqliteTestFactory();
        var request = await f.PublishedAsync(Draft("First"));
        var item = request.WorkItems.Single();
        await AddSubtasksAsync(f, item.TaskItemId);
        request = await f.ReportAsync(request, item.Id);
        item = request.WorkItems.Single();
        var report = item.Evidence.Single();
        var legacyHash = LegacySingleTaskHash(item.Task);
        await using (var db = f.CreateDbContext())
        {
            var stored = await db.TaskEvidence.SingleAsync();
            stored.TaskStateHash = legacyHash;
            if (accepted)
            {
                stored.AcceptedAt = f.Clock.GetUtcNow().UtcDateTime;
                stored.AcceptedBy = "legacy-reviewer";
                (await db.Tasks.SingleAsync(t => t.Id == item.TaskItemId)).Status = WorkStatus.Done;
                foreach (var criterion in await db.AcceptanceCriteria.ToArrayAsync()) criterion.Completed = true;
            }
            await db.SaveChangesAsync();
        }

        var historical = (await f.Service.ContextAsync(request.Id))!.Tasks.Single().Evidence.Single();
        Assert.Equal(report.Summary, historical.Summary);
        Assert.Equal(report.Checks, historical.Checks);
        Assert.Equal(report.CodeReference, historical.CodeReference);
        Assert.Equal(report.Author, historical.Author);
        Assert.Equal(accepted ? "legacy-reviewer" : null, historical.AcceptedBy);
        Assert.False(historical.Current);
        var error = await RejectAsync("invalid", () => f.Service.AcceptEvidenceAsync(request.Id, request.Version, item.Id, report.Id,
            item.Task.AcceptanceCriteria.Select(c => c.Id).ToArray(), "reviewer", "legacy-accept"));
        Assert.Contains("older report", error.Message, StringComparison.OrdinalIgnoreCase);
        if (accepted)
            await RejectAsync("invalid", () => f.Service.ConcludeAsync(request.Id, request.Version, "Unverified legacy result", "", "reviewer", "legacy-conclusion"));
        await using var verify = f.CreateDbContext();
        Assert.Equal(legacyHash, (await verify.TaskEvidence.SingleAsync()).TaskStateHash);
        Assert.False(await verify.WorkflowReceipts.AnyAsync(r => r.Key == "legacy-accept"));
    }

    [Fact]
    public async Task LegacySubtaskFreeReport_CanStillBeAcceptedAndConcludedWithoutRerecording()
    {
        using var f = new WorkflowSqliteTestFactory();
        var request = await f.PublishedAsync(Draft("First"));
        var item = request.WorkItems.Single();
        request = await f.ReportAsync(request, item.Id);
        const string legacyHash = "D62554382EA39AE138710309AF7DF4C29BD6335F252C35D241214435BF18C2DD";
        Assert.Equal(legacyHash, request.WorkItems.Single().Evidence.Single().TaskStateHash);
        request = await f.AcceptAsync(request, item.Id);
        await f.Service.ConcludeAsync(request.Id, request.Version, "Existing report remains valid", "", "reviewer", "compatible-conclusion");
        var context = (await f.Service.ContextAsync(request.Id))!;
        Assert.True(context.Tasks.Single().Evidence.Single().Current);
        Assert.True(context.Results.Single().Current);
        Assert.Single(context.Tasks.Single().Evidence);
    }

    // The unversioned persisted contract, retained here as a legacy fixture rather than
    // derived from the current production hash implementation.
    private static string LegacySingleTaskHash(TaskItem task) => WorkspaceService.Hash(new[] { new
    {
        Key = task.Id, task.FeatureId, task.Name, task.Description,
        Criteria = task.AcceptanceCriteria.OrderBy(c => c.Id).Select(c => new { c.Id, c.Text }),
        Subtasks = task.Subtasks.OrderBy(c => c.Id).Select(c => new { c.Id, c.Text }),
        Dependencies = task.Dependencies.OrderBy(d => d.DependsOnTaskItemId).Select(d => new { d.DependsOnTaskItemId, d.Type }),
        PrerequisiteStatus = (WorkStatus?)null, PrerequisiteCompletedAt = (DateTime?)null, PrerequisiteChecks = (bool[]?)null
    } });

    private static async Task<(int ChildId, int AlternateParentId)> AddSubtasksAsync(WorkflowSqliteTestFactory f, int taskId)
    {
        await using var db = f.CreateDbContext();
        var first = new Subtask { TaskItemId = taskId, Text = "First parent", Completed = true, SortOrder = 0 };
        var second = new Subtask { TaskItemId = taskId, Text = "Second parent", Completed = true, SortOrder = 1 };
        var child = new Subtask { TaskItemId = taskId, Text = "Implementation step", Completed = true, SortOrder = 0, ParentSubtask = first };
        db.Subtasks.AddRange(first, second, child);
        await db.SaveChangesAsync();
        return (child.Id, second.Id);
    }
}
