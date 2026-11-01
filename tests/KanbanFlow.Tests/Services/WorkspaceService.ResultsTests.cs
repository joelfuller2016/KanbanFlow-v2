using KanbanFlow.Models;
using KanbanFlow.Models.Workflows;
using KanbanFlow.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using static KanbanFlow.Tests.WorkflowSqliteTestFactory;

namespace KanbanFlow.Tests.Services;

public sealed partial class WorkspaceServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResultReceiptFailure_RollsBackTaskStatusChecksEvidenceCommentsAndAudit(bool accepting)
    {
        using var f = new WorkflowSqliteTestFactory();
        var request = await f.PublishedAsync(Draft());
        var item = request.WorkItems.Single();
        if (accepting) request = await f.ReportAsync(request, item.Id);
        item = request.WorkItems.Single();
        var operation = accepting ? "evidence.accept" : "evidence.report";
        int auditCount;
        int commentCount;
        await using (var db = f.CreateDbContext())
        {
            auditCount = await db.ActivityEvents.CountAsync();
            commentCount = await db.TaskComments.CountAsync();
            // Constant operation literals keep this actual SQLite failure trigger independent
            // of the service implementation while targeting its final transaction receipt.
            if (accepting)
                await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_result_receipt BEFORE INSERT ON WorkflowReceipts WHEN NEW.Operation = 'evidence.accept' BEGIN SELECT RAISE(ABORT, 'injected result receipt failure'); END;");
            else
                await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_result_receipt BEFORE INSERT ON WorkflowReceipts WHEN NEW.Operation = 'evidence.report' BEGIN SELECT RAISE(ABORT, 'injected result receipt failure'); END;");
        }
        Func<Task> mutation = accepting
            ? () => f.Service.AcceptEvidenceAsync(request.Id, request.Version, item.Id, item.Evidence.Single().Id, item.Task.AcceptanceCriteria.Select(c => c.Id).ToArray(), "reviewer", "result-write")
            : () => f.Service.RecordEvidenceAsync(request.Id, request.Version, item.Id, new EvidenceDraft("Result", "Checked current behavior", "src/Workflow.cs"), "reporter", "result-write");
        var failure = await Assert.ThrowsAsync<DbUpdateException>(mutation);
        Assert.Equal(19, Assert.IsType<SqliteException>(failure.InnerException).SqliteErrorCode);
        var rolledBack = await f.LoadAsync(request.Id);
        Assert.Equal(request.Version, rolledBack.Version);
        Assert.Equal(request.Messages.Count, rolledBack.Messages.Count);
        Assert.Equal(item.Task.Status, rolledBack.WorkItems.Single().Task.Status);
        Assert.Null(rolledBack.WorkItems.Single().Task.CompletedAt);
        Assert.All(rolledBack.WorkItems.Single().Task.AcceptanceCriteria, c => Assert.False(c.Completed));
        Assert.Equal(item.Evidence.Count, rolledBack.WorkItems.Single().Evidence.Count);
        Assert.All(rolledBack.WorkItems.Single().Evidence, e => { Assert.Null(e.AcceptedAt); Assert.Null(e.AcceptedBy); });
        await using (var db = f.CreateDbContext())
        {
            Assert.Equal(auditCount, await db.ActivityEvents.CountAsync());
            Assert.Equal(commentCount, await db.TaskComments.CountAsync());
            Assert.False(await db.WorkflowReceipts.AnyAsync(r => r.Key == "result-write"));
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_result_receipt;");
        }
        await mutation();
        var retried = await f.LoadAsync(request.Id);
        Assert.Equal(request.Version + 1, retried.Version);
        Assert.Equal(accepting ? WorkStatus.Done : WorkStatus.Review, retried.WorkItems.Single().Task.Status);
        Assert.Single(retried.WorkItems.Single().Evidence);
        Assert.Equal(accepting, retried.WorkItems.Single().Evidence.Single().AcceptedAt is not null);
        await using var verify = f.CreateDbContext();
        Assert.Equal(1, await verify.WorkflowReceipts.CountAsync(r => r.Operation == operation && r.Key == "result-write"));
    }

    [Fact]
    public async Task Dependencies_BlockStartingAndAcceptingDependentTask()
    {
        using var f = new WorkflowSqliteTestFactory();
        var prerequisiteDraft = Draft("Prerequisite");
        var dependentDraft = Draft("Dependent", prerequisiteDraft.Id);
        var request = await f.PublishedAsync(prerequisiteDraft, dependentDraft);
        var prerequisiteId = request.WorkItems.Single(w => w.DraftId == prerequisiteDraft.Id).Id;
        var dependent = request.WorkItems.Single(w => w.DraftId == dependentDraft.Id);
        await RejectAsync("invalid", () => f.Service.SetTaskStateAsync(request.Id, request.Version, dependent.Id, WorkStatus.InProgress, Actor, "blocked-start"));
        request = await f.ReportAsync(request, dependent.Id);
        dependent = request.WorkItems.Single(w => w.Id == dependent.Id);
        await RejectAsync("invalid", () => f.Service.AcceptEvidenceAsync(request.Id, request.Version, dependent.Id, dependent.Evidence.Single().Id,
            dependent.Task.AcceptanceCriteria.Select(c => c.Id).ToArray(), "reviewer", "blocked-accept"));
        var blocked = (await f.LoadAsync(request.Id)).WorkItems.Single(w => w.Id == dependent.Id);
        Assert.Equal(WorkStatus.Review, blocked.Task.Status);
        Assert.All(blocked.Task.AcceptanceCriteria, c => Assert.False(c.Completed));
        Assert.Null(blocked.Evidence.Single().AcceptedAt);

        request = await f.ReportAsync(request, prerequisiteId);
        request = await f.AcceptAsync(request, prerequisiteId);
        await f.Service.SetTaskStateAsync(request.Id, request.Version, dependent.Id, WorkStatus.InProgress, Actor, "unblocked-start");
        request = await f.LoadAsync(request.Id);
        Assert.Equal(WorkStatus.InProgress, request.WorkItems.Single(w => w.Id == dependent.Id).Task.Status);
        // Completing the prerequisite changes the observed dependency context.
        // A report made while it was unfinished must be refreshed before acceptance.
        request = await f.ReportAsync(request, dependent.Id);
        request = await f.AcceptAsync(request, dependent.Id);
        Assert.All(request.WorkItems, w => Assert.Equal(WorkStatus.Done, w.Task.Status));
    }

    [Fact]
    public async Task SetTaskState_RejectsUndefinedStatusAndUsesExistingDoneGate()
    {
        using var f = new WorkflowSqliteTestFactory();
        var request = await f.PublishedAsync(Draft());
        var item = request.WorkItems.Single();
        await RejectAsync("invalid", () => f.Service.SetTaskStateAsync(request.Id, request.Version, item.Id, (WorkStatus)99, Actor, "invalid-state"));
        await Assert.ThrowsAsync<DefinitionOfDoneNotMetException>(() => f.Service.SetTaskStateAsync(request.Id, request.Version, item.Id, WorkStatus.Done, Actor, "unfinished-done"));
        var after = (await f.LoadAsync(request.Id)).WorkItems.Single();
        Assert.Equal(WorkStatus.Todo, after.Task.Status);
        Assert.Null(after.Task.CompletedAt);
        Assert.All(after.Task.AcceptanceCriteria, c => Assert.False(c.Completed));
    }

    [Theory]
    [InlineData(WorkStatus.Backlog)]
    [InlineData(WorkStatus.Todo)]
    [InlineData(WorkStatus.InProgress)]
    [InlineData(WorkStatus.Review)]
    public async Task SetTaskState_PersistsSupportedManualState(WorkStatus state)
    {
        using var f = new WorkflowSqliteTestFactory();
        var request = await f.PublishedAsync(Draft());
        var item = request.WorkItems.Single();
        var beforeChange = DateTime.UtcNow;
        await f.Service.SetTaskStateAsync(request.Id, request.Version, item.Id, state, Actor, "state");
        var after = await f.LoadAsync(request.Id);
        Assert.Equal(state, after.WorkItems.Single().Task.Status);
        Assert.Equal(request.Version + 1, after.Version);
        if (state != WorkStatus.Todo)
            Assert.InRange(after.WorkItems.Single().Task.UpdatedAt, beforeChange, DateTime.UtcNow);
        else
            Assert.Equal(item.Task.UpdatedAt, after.WorkItems.Single().Task.UpdatedAt);
        Assert.Null(after.WorkItems.Single().Task.CompletedAt);
    }

    [Fact]
    public async Task RecordEvidence_PreservesReportedTextAndCurrentSpecificationWithoutAcceptingIt()
    {
        using var f = new WorkflowSqliteTestFactory();
        var request = await f.PublishedAsync(Draft());
        var item = request.WorkItems.Single();
        var input = new EvidenceDraft("  Result\r\n", " check one\n check two  ", " document.md at abc123 ");
        await f.Service.RecordEvidenceAsync(request.Id, request.Version, item.Id, input, "reporter", "report");
        var after = (await f.LoadAsync(request.Id)).WorkItems.Single();
        var evidence = Assert.Single(after.Evidence);
        Assert.Equal(input.Summary, evidence.Summary);
        Assert.Equal(input.Checks, evidence.Checks);
        Assert.Equal(input.CodeReference, evidence.CodeReference);
        Assert.Equal("reporter", evidence.Author);
        Assert.Equal(item.SpecificationVersion, evidence.SpecificationVersion);
        Assert.Equal(item.SpecificationHash, evidence.SpecificationHash);
        Assert.Equal(f.Clock.GetUtcNow().UtcDateTime, evidence.CreatedAt);
        Assert.Null(evidence.AcceptedAt);
        Assert.Null(evidence.AcceptedBy);
        Assert.Equal(WorkStatus.Review, after.Task.Status);
        Assert.Null(after.Task.CompletedAt);
        Assert.All(after.Task.AcceptanceCriteria, c => Assert.False(c.Completed));
    }

    [Theory]
    [InlineData("summary")]
    [InlineData("checks")]
    [InlineData("reference")]
    public async Task RecordEvidence_RequiresResultChecksAndSourceReference(string missing)
    {
        using var f = new WorkflowSqliteTestFactory();
        var request = await f.PublishedAsync(Draft());
        var input = new EvidenceDraft(missing == "summary" ? " " : "Result", missing == "checks" ? " " : "Checks", missing == "reference" ? " " : "source.md");
        var item = request.WorkItems.Single();
        await RejectAsync("invalid", () => f.Service.RecordEvidenceAsync(request.Id, request.Version, item.Id, input, Actor, "bad-evidence"));
        var after = (await f.LoadAsync(request.Id)).WorkItems.Single();
        Assert.Empty(after.Evidence);
        Assert.Equal(WorkStatus.Todo, after.Task.Status);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("null")]
    [InlineData("partial")]
    [InlineData("duplicate")]
    [InlineData("extra")]
    [InlineData("foreign")]
    public async Task AcceptEvidence_RejectsMissingOrIncorrectCriteria(string selection)
    {
        using var f = new WorkflowSqliteTestFactory();
        var request = await f.PublishedAsync(Draft("Target"), Draft("Other"));
        var target = request.WorkItems.Single(w => w.Task.Name == "Target");
        request = await f.ReportAsync(request, target.Id);
        target = request.WorkItems.Single(w => w.Id == target.Id);
        var criteria = target.Task.AcceptanceCriteria.Select(c => c.Id).ToArray();
        var foreign = request.WorkItems.Single(w => w.Task.Name == "Other").Task.AcceptanceCriteria.Select(c => c.Id).ToArray();
        int[] selected = selection switch
        {
            "empty" => [],
            "null" => null!,
            "partial" => [criteria[0]],
            "duplicate" => [criteria[0], criteria[0]],
            "extra" => [.. criteria, foreign[0]],
            _ => foreign
        };
        await RejectAsync("invalid", () => f.Service.AcceptEvidenceAsync(request.Id, request.Version, target.Id, target.Evidence.Single().Id, selected, "reviewer", "bad-selection"));
        var after = await f.LoadAsync(request.Id);
        Assert.Equal(request.Version, after.Version);
        Assert.Equal(WorkStatus.Review, after.WorkItems.Single(w => w.Id == target.Id).Task.Status);
        Assert.All(after.WorkItems.SelectMany(w => w.Task.AcceptanceCriteria), c => Assert.False(c.Completed));
        Assert.Null(after.WorkItems.Single(w => w.Id == target.Id).Evidence.Single().AcceptedAt);
        await using var db = f.CreateDbContext();
        Assert.False(await db.WorkflowReceipts.AnyAsync(r => r.Key == "bad-selection"));
    }

    [Fact]
    public async Task AcceptEvidence_RejectsTaskWithNoAcceptanceChecks()
    {
        using var f = new WorkflowSqliteTestFactory();
        var request = await f.PublishedAsync(Draft());
        var item = request.WorkItems.Single();
        request = await f.ReportAsync(request, item.Id);
        // Emulates incomplete imported/corrupt board data; an empty set cannot satisfy Done.
        await using (var db = f.CreateDbContext())
        {
            db.AcceptanceCriteria.RemoveRange(await db.AcceptanceCriteria.Where(c => c.TaskItemId == item.TaskItemId).ToListAsync());
            await db.SaveChangesAsync();
        }
        item = request.WorkItems.Single();
        await RejectAsync("invalid", () => f.Service.AcceptEvidenceAsync(request.Id, request.Version, item.Id, item.Evidence.Single().Id, [], "reviewer", "no-criteria"));
        Assert.Equal(WorkStatus.Review, (await f.LoadAsync(request.Id)).WorkItems.Single().Task.Status);
    }

    [Fact]
    public async Task AcceptEvidence_CompletesEveryCurrentCriterionAndRecordsReviewer()
    {
        using var f = new WorkflowSqliteTestFactory();
        var request = await f.PublishedAsync(Draft());
        var itemId = request.WorkItems.Single().Id;
        request = await f.ReportAsync(request, itemId);
        var item = request.WorkItems.Single();
        var evidence = item.Evidence.Single();
        f.Clock.Advance(TimeSpan.FromMinutes(5));
        var acceptedAt = f.Clock.GetUtcNow().UtcDateTime;
        var beforeCompletion = DateTime.UtcNow;
        var receipt = await f.Service.AcceptEvidenceAsync(request.Id, request.Version, itemId, evidence.Id,
            item.Task.AcceptanceCriteria.Select(c => c.Id).Reverse().ToArray(), "human-reviewer", "accept");
        var after = await f.LoadAsync(request.Id);
        Assert.Equal(receipt.Version, after.Version);
        Assert.Equal(request.Version + 1, after.Version);
        var accepted = after.WorkItems.Single();
        Assert.Equal(WorkStatus.Done, accepted.Task.Status);
        Assert.InRange(accepted.Task.CompletedAt!.Value, beforeCompletion, DateTime.UtcNow);
        Assert.InRange(accepted.Task.UpdatedAt, beforeCompletion, DateTime.UtcNow);
        Assert.Equal(2, accepted.Task.AcceptanceCriteria.Count);
        Assert.All(accepted.Task.AcceptanceCriteria, c => { Assert.True(c.Completed); Assert.Equal(acceptedAt, c.UpdatedAt); });
        Assert.Equal(Actor, accepted.Evidence.Single().Author);
        Assert.Equal("human-reviewer", accepted.Evidence.Single().AcceptedBy);
        Assert.Equal(acceptedAt, accepted.Evidence.Single().AcceptedAt);
        Assert.Equal(1, Assert.Single(await f.Service.RequestsAsync()).DoneCount);
    }

    [Theory]
    [InlineData("missing-item")]
    [InlineData("foreign-evidence")]
    [InlineData("missing-evidence")]
    public async Task AcceptEvidence_RequiresEvidenceBelongingToSelectedWorkItem(string invalidCase)
    {
        using var f = new WorkflowSqliteTestFactory();
        var request = await f.PublishedAsync(Draft("Target"), Draft("Other"));
        var target = request.WorkItems.Single(w => w.Task.Name == "Target");
        var otherId = request.WorkItems.Single(w => w.Task.Name == "Other").Id;
        request = await f.ReportAsync(request, otherId);
        var foreignEvidenceId = request.WorkItems.Single(w => w.Id == otherId).Evidence.Single().Id;
        await RejectAsync("not_found", () => f.Service.AcceptEvidenceAsync(request.Id, request.Version,
            invalidCase == "missing-item" ? Guid.NewGuid() : target.Id,
            invalidCase == "missing-evidence" ? Guid.NewGuid() : foreignEvidenceId,
            target.Task.AcceptanceCriteria.Select(c => c.Id).ToArray(), Actor, "invalid-owner"));
        Assert.All((await f.LoadAsync(request.Id)).WorkItems, w => Assert.NotEqual(WorkStatus.Done, w.Task.Status));
    }

    [Theory]
    [InlineData("no-work")]
    [InlineData("unfinished")]
    [InlineData("unpublished-plan")]
    [InlineData("new-brief")]
    public async Task Conclude_RequiresAllDoneAndCurrentPublishedPlan(string invalidCase)
    {
        using var f = new WorkflowSqliteTestFactory();
        var draft = Draft();
        var request = invalidCase == "no-work" ? await f.BriefedAsync() : await f.PublishedAsync(draft);
        if (invalidCase is "unpublished-plan" or "new-brief")
        {
            var itemId = request.WorkItems.Single().Id;
            request = await f.ReportAsync(request, itemId);
            request = await f.AcceptAsync(request, itemId);
            if (invalidCase == "unpublished-plan") await f.Service.SavePlanAsync(request.Id, request.Version, [draft], Actor, "unpublished", expectedTaskStates: await f.ReadTaskTokensAsync(request.Id));
            else await f.Service.SaveBriefAsync(request.Id, request.Version, Brief("New acceptance intent"), Actor, "new-brief");
            request = await f.LoadAsync(request.Id);
        }
        await RejectAsync("invalid", () => f.Service.ConcludeAsync(request.Id, request.Version, "Completed", "", Actor, "conclusion"));
        var after = await f.LoadAsync(request.Id);
        Assert.Empty(after.Results);
        Assert.Equal(request.Version, after.Version);
        await using var db = f.CreateDbContext();
        Assert.False(await db.WorkflowReceipts.AnyAsync(r => r.Key == "conclusion"));
    }

    [Fact]
    public async Task Conclude_PersistsVersionedConclusionAndLimitationsAfterCurrentPlanIsDone()
    {
        using var f = new WorkflowSqliteTestFactory();
        var request = await f.PublishedAsync(Draft());
        var itemId = request.WorkItems.Single().Id;
        request = await f.ReportAsync(request, itemId);
        request = await f.AcceptAsync(request, itemId);
        var receipt = await f.Service.ConcludeAsync(request.Id, request.Version, "  Verified conclusion\r\n", " Local preview only\n", "reviewer", "conclude");
        var after = await f.LoadAsync(request.Id);
        var result = Assert.Single(after.Results);
        Assert.Equal("  Verified conclusion\r\n", result.Conclusion);
        Assert.Equal(" Local preview only\n", result.Limitations);
        Assert.Equal("reviewer", result.Author);
        Assert.Equal(receipt.Version, result.RequestVersion);
        Assert.Equal(after.Version, result.RequestVersion);
        Assert.Equal(f.Clock.GetUtcNow().UtcDateTime, result.CreatedAt);
    }

    [Fact]
    public async Task Archive_RejectsActiveWorkThenAllowsArchiveAndRestore()
    {
        using var f = new WorkflowSqliteTestFactory();
        var request = await f.PublishedAsync(Draft());
        var itemId = request.WorkItems.Single().Id;
        await f.Service.SetTaskStateAsync(request.Id, request.Version, itemId, WorkStatus.InProgress, Actor, "start");
        request = await f.LoadAsync(request.Id);
        await RejectAsync("invalid", () => f.Service.ArchiveAsync(request.Id, request.Version, true, Actor, "active-archive"));
        Assert.False((await f.LoadAsync(request.Id)).Archived);
        await f.Service.SetTaskStateAsync(request.Id, request.Version, itemId, WorkStatus.Todo, Actor, "stop");
        request = await f.LoadAsync(request.Id);
        await f.Service.ArchiveAsync(request.Id, request.Version, true, Actor, "archive");
        request = await f.LoadAsync(request.Id);
        Assert.True(request.Archived);
        Assert.Single(request.WorkItems);
        var archivedVersion = request.Version;
        await RejectAsync("invalid", () => f.Service.SaveBriefAsync(request.Id, request.Version, Brief("Archived edit"), Actor, "edit-archived"));
        Assert.Equal(archivedVersion, (await f.LoadAsync(request.Id)).Version);
        var restored = await f.Service.ArchiveAsync(request.Id, request.Version, false, Actor, "restore");
        Assert.False((await f.LoadAsync(request.Id)).Archived);
        await f.Service.SaveBriefAsync(request.Id, restored.Version, Brief("Restored edit"), Actor, "edit-restored");
        Assert.Equal("Restored edit", (await f.LoadAsync(request.Id)).Briefs.MaxBy(b => b.Number)!.Outcome);
    }

    [Theory]
    [InlineData("message")]
    [InlineData("plan")]
    [InlineData("publish")]
    [InlineData("state")]
    [InlineData("evidence")]
    [InlineData("accept")]
    [InlineData("conclude")]
    public async Task ArchivedRequest_BlocksWorkflowMutations(string operation)
    {
        using var f = new WorkflowSqliteTestFactory();
        var draft = Draft();
        var request = await f.PublishedAsync(draft);
        var itemId = request.WorkItems.Single().Id;
        request = await f.ReportAsync(request, itemId);
        request = await f.AcceptAsync(request, itemId);
        await f.Service.ArchiveAsync(request.Id, request.Version, true, Actor, "archive");
        request = await f.LoadAsync(request.Id);
        var item = request.WorkItems.Single();
        Func<Task> mutation = operation switch
        {
            "message" => () => f.Service.AddMessageAsync(request.Id, request.Version, "progress", "Report", Actor, "forbidden"),
            "plan" => () => f.Service.SavePlanAsync(request.Id, request.Version, [draft], Actor, "forbidden"),
            "publish" => () => f.Service.PublishPlanAsync(request.Id, request.Version, request.Plans.Single().Id, Actor, "forbidden"),
            "state" => () => f.Service.SetTaskStateAsync(request.Id, request.Version, item.Id, WorkStatus.Todo, Actor, "forbidden"),
            "evidence" => () => f.Service.RecordEvidenceAsync(request.Id, request.Version, item.Id, new EvidenceDraft("Result", "Checks", "source.md"), Actor, "forbidden"),
            "accept" => () => f.Service.AcceptEvidenceAsync(request.Id, request.Version, item.Id, item.Evidence.Single().Id, item.Task.AcceptanceCriteria.Select(c => c.Id).ToArray(), Actor, "forbidden"),
            _ => () => f.Service.ConcludeAsync(request.Id, request.Version, "Conclusion", "", Actor, "forbidden")
        };
        await RejectAsync("invalid", mutation);
        var after = await f.LoadAsync(request.Id);
        Assert.True(after.Archived);
        Assert.Equal(request.Version, after.Version);
        Assert.Equal(WorkStatus.Done, after.WorkItems.Single().Task.Status);
        Assert.Single(after.Plans);
        Assert.Single(after.WorkItems.Single().Evidence);
        Assert.Empty(after.Results);
        await using var db = f.CreateDbContext();
        Assert.False(await db.WorkflowReceipts.AnyAsync(r => r.Key == "forbidden"));
    }

    [Theory]
    [InlineData("changed-specification")]
    [InlineData("manual-reopen")]
    [InlineData("new-evidence")]
    public async Task DependencyChanges_InvalidateTransitiveAcceptedEvidenceAndRequireFreshChecks(string change)
    {
        using var f = new WorkflowSqliteTestFactory();
        var a = Draft("A");
        var b = Draft("B", a.Id);
        var c = Draft("C", b.Id);
        var independent = Draft("Independent");
        var request = await f.PublishedAsync(a, b, c, independent);
        foreach (var draftId in new[] { a.Id, b.Id, c.Id, independent.Id })
        {
            var itemId = request.WorkItems.Single(w => w.DraftId == draftId).Id;
            request = await f.ReportAsync(request, itemId);
            request = await f.AcceptAsync(request, itemId);
        }
        var before = request.WorkItems.ToDictionary(w => w.DraftId);
        var prerequisiteId = before[a.Id].Id;
        if (change == "changed-specification")
        {
            await f.Service.SavePlanAsync(request.Id, request.Version, [a with { Description = "Revised dependency contract" }, b, c, independent], Actor, "changed-plan", expectedTaskStates: await f.ReadTaskTokensAsync(request.Id));
            request = await f.LoadAsync(request.Id);
            await f.Service.PublishPlanAsync(request.Id, request.Version, request.Plans.MaxBy(p => p.Number)!.Id, Actor, "changed-publish");
        }
        else if (change == "manual-reopen")
            await f.Service.SetTaskStateAsync(request.Id, request.Version, prerequisiteId, WorkStatus.Todo, Actor, "reopen");
        else
            await f.Service.RecordEvidenceAsync(request.Id, request.Version, prerequisiteId, new EvidenceDraft("Revised result", "Rerun checks", "src/A.cs updated"), Actor, "new-report");

        request = await f.LoadAsync(request.Id);
        foreach (var draftId in new[] { b.Id, c.Id })
        {
            var invalidated = request.WorkItems.Single(w => w.DraftId == draftId);
            Assert.Equal(before[draftId].TaskItemId, invalidated.TaskItemId);
            Assert.Equal(before[draftId].SpecificationVersion + 1, invalidated.SpecificationVersion);
            Assert.Equal(WorkStatus.Todo, invalidated.Task.Status);
            Assert.Null(invalidated.Task.CompletedAt);
            Assert.All(invalidated.Task.AcceptanceCriteria, check => Assert.False(check.Completed));
            Assert.Equal(before[draftId].Evidence.Single().Id, invalidated.Evidence.Single().Id);
        }
        var unaffected = request.WorkItems.Single(w => w.DraftId == independent.Id);
        Assert.Equal(WorkStatus.Done, unaffected.Task.Status);
        Assert.Equal(before[independent.Id].SpecificationVersion, unaffected.SpecificationVersion);
        Assert.Equal(before[independent.Id].Task.CompletedAt, unaffected.Task.CompletedAt);

        if (change != "new-evidence") request = await f.ReportAsync(request, prerequisiteId);
        request = await f.AcceptAsync(request, prerequisiteId);
        // Check old evidence after its prerequisite is Done again: rejection must be due to staleness.
        foreach (var draftId in new[] { b.Id, c.Id })
        {
            var item = request.WorkItems.Single(w => w.DraftId == draftId);
            await RejectAsync("invalid", () => f.Service.AcceptEvidenceAsync(request.Id, request.Version, item.Id, before[draftId].Evidence.Single().Id,
                item.Task.AcceptanceCriteria.Select(check => check.Id).ToArray(), "reviewer", Key()));
            Assert.Equal(WorkStatus.Todo, (await f.LoadAsync(request.Id)).WorkItems.Single(w => w.Id == item.Id).Task.Status);
            request = await f.ReportAsync(request, item.Id);
            request = await f.AcceptAsync(request, item.Id);
        }
        Assert.All(request.WorkItems, item => Assert.Equal(WorkStatus.Done, item.Task.Status));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("hash")]
    public async Task AcceptEvidence_RejectsEitherSpecificationVersionOrHashMismatch(string mismatch)
    {
        using var f = new WorkflowSqliteTestFactory();
        var request = await f.PublishedAsync(Draft());
        var itemId = request.WorkItems.Single().Id;
        request = await f.ReportAsync(request, itemId);
        var item = request.WorkItems.Single();
        await using (var db = f.CreateDbContext())
        {
            var evidence = await db.TaskEvidence.SingleAsync();
            if (mismatch == "version") evidence.SpecificationVersion--;
            else evidence.SpecificationHash = new string('0', 64);
            await db.SaveChangesAsync();
        }
        await RejectAsync("invalid", () => f.Service.AcceptEvidenceAsync(request.Id, request.Version, itemId, item.Evidence.Single().Id,
            item.Task.AcceptanceCriteria.Select(c => c.Id).ToArray(), "reviewer", "mismatch"));
        var after = (await f.LoadAsync(request.Id)).WorkItems.Single();
        Assert.Equal(WorkStatus.Review, after.Task.Status);
        Assert.Null(after.Evidence.Single().AcceptedAt);
        Assert.All(after.Task.AcceptanceCriteria, c => Assert.False(c.Completed));
    }
}
