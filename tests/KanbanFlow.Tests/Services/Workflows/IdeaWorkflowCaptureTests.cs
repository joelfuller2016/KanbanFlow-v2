using KanbanFlow.Mcp;
using KanbanFlow.Models;
using KanbanFlow.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using static KanbanFlow.Tests.WorkflowSqliteTestFactory;

namespace KanbanFlow.Tests.Services.Workflows;

public sealed class IdeaWorkflowCaptureTests
{
    [Theory]
    [InlineData("title")]
    [InlineData("tags")]
    [InlineData("priority")]
    [InlineData("status")]
    public async Task OrdinaryIdeaEdits_InvalidateStaleWorkflowRevisionWhileNoOpPreservesIt(string field)
    {
        using var f = new WorkflowSqliteTestFactory();
        var ideas = new IdeaService(f);
        var idea = await ideas.CreateAsync(new IdeaCreate("Idea", "Exact original"), "author");
        var before = (await f.Service.GetForIdeaAsync(idea.Id))!;
        var update = field switch
        {
            "title" => new IdeaUpdate(Title: "Revised visible title"),
            "tags" => new IdeaUpdate(Tags: "reviewed"),
            "priority" => new IdeaUpdate(Priority: TaskPriority.Critical),
            _ => new IdeaUpdate(Status: IdeaStatus.Triaging)
        };
        await ideas.UpdateAsync(idea.Id, update, "human");
        var after = (await f.Service.GetForIdeaAsync(idea.Id))!;
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.Version + 1, after.Version);
        Assert.Equal("Exact original", Assert.Single(after.Messages).Text);
        await RejectAsync("conflict", () => f.Service.SaveBriefAsync(after.Id, before.Version, Brief(), "agent", "stale-edit"));
        await ideas.UpdateAsync(idea.Id, update, "human");
        Assert.Equal(after.Version, (await f.Service.GetForIdeaAsync(idea.Id))!.Version);
        await using var db = f.CreateDbContext();
        Assert.Equal(1, await db.ActivityEvents.CountAsync(a => a.EventType == "IdeaUpdated"));
        Assert.False(await db.WorkflowReceipts.AnyAsync(r => r.Key == "stale-edit"));
    }

    [Fact]
    public async Task OrdinaryIdeaEdit_DoesNotRestoreAnExplicitlyArchivedPromotedWorkflow()
    {
        using var f = new WorkflowSqliteTestFactory();
        var request = await f.PublishedAsync(Draft());
        await f.Service.ArchiveAsync(request.Id, request.Version, true, "human", "archive");
        var archived = await f.LoadAsync(request.Id);
        await new IdeaService(f).UpdateAsync(request.IdeaId, new IdeaUpdate(Title: "Renamed historical idea"), "human");
        var after = await f.LoadAsync(request.Id);
        Assert.True(after.Archived);
        Assert.Equal("Renamed historical idea", after.Title);
        Assert.Equal(archived.Version + 1, after.Version);
        Assert.Equal(IdeaStatus.Promoted, after.Idea.Status);
        await RejectAsync("invalid", () => f.Service.SaveBriefAsync(after.Id, after.Version, Brief(), "agent", "archived-edit"));
        Assert.Equal(request.WorkItems.Single().TaskItemId, after.WorkItems.Single().TaskItemId);
    }

    [Fact]
    public async Task ExplicitIdeaDeletion_CascadesWorkflowHistoryAndKeepsExistingBoardTask()
    {
        using var f = new WorkflowSqliteTestFactory();
        var request = await f.PublishedAsync(Draft());
        var item = request.WorkItems.Single();
        request = await f.ReportAsync(request, item.Id);
        await new IdeaService(f).DeleteAsync(request.IdeaId, "human");
        Assert.Null(await f.Service.GetAsync(request.Id));
        Assert.Null(await f.Service.GetForTaskAsync(item.TaskItemId));
        Assert.Equal(item.TaskItemId, (await KanbanMcpTools.GetTask(f, item.TaskItemId)).Id);
        await using var db = f.CreateDbContext();
        Assert.Empty(await db.WorkRequests.ToListAsync());
        Assert.Empty(await db.RequestMessages.ToListAsync());
        Assert.Empty(await db.BriefRevisions.ToListAsync());
        Assert.Empty(await db.PlanVersions.ToListAsync());
        Assert.Empty(await db.PlannedTasks.ToListAsync());
        Assert.Empty(await db.TaskEvidence.ToListAsync());
        Assert.Single(await db.Projects.ToListAsync());
        Assert.Single(await db.Tasks.ToListAsync());
        Assert.Equal(1, await db.ActivityEvents.CountAsync(a => a.EntityId == request.IdeaId && a.EventType == "IdeaDeleted"));
        await KanbanFlow.Tests.Data.WorkflowSchemaTests.AssertForeignKeysValidAsync(db);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingIdeaCreation_PreservesExactOriginalAndCreatesOneWorkflow(bool throughNativeMcp)
    {
        using var f = new WorkflowSqliteTestFactory();
        var ideas = new IdeaService(f);
        const string original = " \r\n  Actual request: café 👋\t\n  keep this wording  \r\n";
        var ideaId = throughNativeMcp
            ? (await KanbanMcpTools.CreateIdea(ideas, "  Idea title  ", original, "  Mutable plan notes  ", actor: "author")).Id
            : (await ideas.CreateAsync(new IdeaCreate("  Idea title  ", original, "  Mutable plan notes  "), "author")).Id;

        var idea = await ideas.GetByIdAsync(ideaId);
        Assert.NotNull(idea);
        Assert.Equal("Idea title", idea.Title);
        Assert.Equal(original.Trim(), idea.Body);
        Assert.Equal("Mutable plan notes", idea.Plan);
        Assert.Equal(IdeaStatus.New, idea.Status);
        var request = await f.Service.GetForIdeaAsync(ideaId);
        Assert.NotNull(request);
        Assert.Equal(ideaId, request.IdeaId);
        Assert.Null(request.ProjectId);
        var preserved = Assert.Single(request.Messages);
        Assert.Equal("original", preserved.Kind);
        Assert.Equal(original, preserved.Text);
        Assert.Equal("author", preserved.Author);
        Assert.Equal("idea", preserved.Source);
        Assert.Empty(request.Plans);
        await using var db = f.CreateDbContext();
        Assert.Equal(1, await db.Ideas.CountAsync());
        Assert.Equal(1, await db.WorkRequests.CountAsync());
        Assert.Equal(1, await db.ActivityEvents.CountAsync(a => a.EventType == "IdeaCreated" && a.EntityId == ideaId));
    }

    [Fact]
    public async Task TitleOnlyIdea_PreservesUnnormalizedTitleAsOriginal()
    {
        using var f = new WorkflowSqliteTestFactory();
        var idea = await new IdeaService(f).CreateAsync(new IdeaCreate("  Exact title\t "), null);
        var request = await f.Service.GetForIdeaAsync(idea.Id);
        Assert.Equal("Exact title", idea.Title);
        Assert.Equal("  Exact title\t ", Assert.Single(request!.Messages).Text);
        Assert.Equal("System", request.Messages.Single().Author);
    }

    [Fact]
    public async Task ExistingIdeaCreation_AuditFailureRollsBackIdeaOriginalAndWorkflow()
    {
        using var f = new WorkflowSqliteTestFactory();
        await using (var db = f.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER reject_idea_audit BEFORE INSERT ON ActivityEvents
                WHEN NEW.EventType = 'IdeaCreated'
                BEGIN SELECT RAISE(ABORT, 'injected idea audit failure'); END;
                """);
        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => new IdeaService(f).CreateAsync(new IdeaCreate("Idea", "Original"), "author"));
        Assert.Equal(19, Assert.IsType<SqliteException>(failure.InnerException).SqliteErrorCode);
        await using var verify = f.CreateDbContext();
        Assert.Empty(await verify.Ideas.ToListAsync());
        Assert.Empty(await verify.WorkRequests.ToListAsync());
        Assert.Empty(await verify.RequestMessages.ToListAsync());
        Assert.Empty(await verify.ActivityEvents.ToListAsync());
    }

    [Fact]
    public async Task ExistingIdeaEdits_RecordSummaryHistoryWithoutChangingOriginalOrPublishedPlan()
    {
        using var f = new WorkflowSqliteTestFactory();
        var request = await f.PublishedAsync(Draft());
        var original = Assert.Single(request.Messages, m => m.Kind == "original");
        var plan = request.Plans.Single();
        var taskId = request.WorkItems.Single().TaskItemId;
        await new IdeaService(f).UpdateAsync(request.IdeaId,
            new IdeaUpdate(Title: "Improved title", Body: "  Revised notes  ", Plan: "New brainstorming, not an adopted task plan"), "human-editor");

        var after = await f.Service.GetForIdeaAsync(request.IdeaId);
        Assert.NotNull(after);
        Assert.Equal(request.Id, after.Id);
        Assert.Equal("Improved title", after.Title);
        Assert.True(after.Version > request.Version);
        var preserved = Assert.Single(after.Messages, m => m.Kind == "original");
        Assert.Equal(original.Id, preserved.Id);
        Assert.Equal(original.Text, preserved.Text);
        Assert.Equal(original.Author, preserved.Author);
        Assert.Equal(original.CreatedAt, preserved.CreatedAt);
        Assert.Contains(after.Messages, m => m.Kind == "clarification" && m.Source == "idea-edit" && m.Author == "human-editor" && m.Text.Contains("Revised notes", StringComparison.Ordinal));
        Assert.Contains(after.Messages, m => m.Kind == "clarification" && m.Source == "idea-edit" && m.Text.Contains("New brainstorming", StringComparison.Ordinal));
        Assert.Equal(plan.DraftJson, Assert.Single(after.Plans).DraftJson);
        Assert.Equal(taskId, Assert.Single(after.WorkItems).TaskItemId);
    }

    [Fact]
    public async Task LegacyIdeaAdoption_LabelsCurrentSnapshotHonestlyAndDoesNotDuplicateIt()
    {
        using var f = new WorkflowSqliteTestFactory();
        int ideaId;
        await using (var db = f.CreateDbContext())
        {
            var legacy = new Idea { Title = "Legacy title", Body = "Current legacy notes", Plan = "Current plan" };
            db.Ideas.Add(legacy);
            await db.SaveChangesAsync();
            ideaId = legacy.Id;
        }
        Assert.Null(await f.Service.GetForIdeaAsync(ideaId));
        var adopted = await f.Service.AdoptIdeaAsync(ideaId, "adopter", "adopt");
        var retry = await f.Service.AdoptIdeaAsync(ideaId, "adopter", "adopt");
        var secondAdoption = await f.Service.AdoptIdeaAsync(ideaId, "adopter", "adopt-again");
        Assert.Equal(adopted, retry);
        Assert.Equal(adopted.RequestId, secondAdoption.RequestId);
        var first = await f.Service.GetForIdeaAsync(ideaId);
        var second = await f.Service.GetForIdeaAsync(ideaId);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.Id, second.Id);
        var original = Assert.Single(second.Messages);
        Assert.Equal("Current legacy notes", original.Text);
        Assert.Equal("legacy-snapshot: exact original unavailable", original.Source);
        await using var verify = f.CreateDbContext();
        Assert.Equal(1, await verify.WorkRequests.CountAsync());
        Assert.Equal(1, await verify.RequestMessages.CountAsync());
        Assert.Equal(1, await verify.Ideas.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingPromotion_AttachesSameIdeaAndWorkflowToNewOrExistingProject(bool existingProject)
    {
        using var f = new WorkflowSqliteTestFactory();
        var ideas = new IdeaService(f);
        var idea = await ideas.CreateAsync(new IdeaCreate("Idea title", "Original", "Plan notes"), "author");
        var before = await f.Service.GetForIdeaAsync(idea.Id);
        int? targetId = existingProject ? await f.Service.CreateProjectAsync("Existing project", Actor, "existing") : null;
        var promotion = await ideas.PromoteAsync(idea.Id, "promoter", existingProjectId: targetId);
        var after = await f.Service.GetForIdeaAsync(idea.Id);
        var promotedIdea = await ideas.GetByIdAsync(idea.Id);
        Assert.Equal(idea.Id, promotion.IdeaId);
        Assert.Equal(before!.Id, after!.Id);
        Assert.Equal(promotion.ProjectId, after.ProjectId);
        Assert.Equal(promotion.ProjectId, promotedIdea!.PromotedProjectId);
        Assert.Equal(IdeaStatus.Promoted, promotedIdea.Status);
        Assert.Equal("Original", Assert.Single(after.Messages).Text);
        Assert.Equal(before.Version + 1, after.Version);
        await using var db = f.CreateDbContext();
        Assert.Equal(1, await db.Projects.CountAsync());
        Assert.Equal(1, await db.WorkRequests.CountAsync());
        Assert.Equal(1, await db.ActivityEvents.CountAsync(a => a.EventType == "IdeaPromoted" && a.EntityId == idea.Id));
        Assert.Equal(existingProject ? 0 : 1, await db.ActivityEvents.CountAsync(a => a.EventType == "ProjectCreatedFromIdea"));
        Assert.Equal(existingProject ? "Existing project" : "Idea title", (await db.Projects.SingleAsync()).Name);
    }

    [Fact]
    public async Task ExistingProjectPromotion_RetriesSameTargetAndRejectsConflictingTarget()
    {
        using var f = new WorkflowSqliteTestFactory();
        var ideas = new IdeaService(f);
        var idea = await ideas.CreateAsync(new IdeaCreate("Idea", "Original"), Actor);
        var target = await f.Service.CreateProjectAsync("Target", Actor, "target");
        var other = await f.Service.CreateProjectAsync("Other", Actor, "other");
        var first = await ideas.PromoteAsync(idea.Id, Actor, existingProjectId: target);
        var retry = await ideas.PromoteAsync(idea.Id, Actor, existingProjectId: target);
        Assert.Equal(first, retry);
        await Assert.ThrowsAsync<ValidationException>(() => ideas.PromoteAsync(idea.Id, Actor, existingProjectId: other));
        var workflow = await f.Service.GetForIdeaAsync(idea.Id);
        Assert.Equal(target, workflow!.ProjectId);
        await using var db = f.CreateDbContext();
        Assert.Equal(2, await db.Projects.CountAsync());
        Assert.Equal(1, await db.WorkRequests.CountAsync());
        Assert.Equal(1, await db.ActivityEvents.CountAsync(a => a.EventType == "IdeaPromoted"));
    }

    [Fact]
    public async Task PromotionAuditFailure_RollsBackProjectIdeaAndWorkflowLinks()
    {
        using var f = new WorkflowSqliteTestFactory();
        var ideas = new IdeaService(f);
        var idea = await ideas.CreateAsync(new IdeaCreate("Idea", "Original"), Actor);
        var before = await f.Service.GetForIdeaAsync(idea.Id);
        await using (var db = f.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER reject_promotion_audit BEFORE INSERT ON ActivityEvents
                WHEN NEW.EventType = 'IdeaPromoted'
                BEGIN SELECT RAISE(ABORT, 'injected promotion audit failure'); END;
                """);
        await Assert.ThrowsAsync<DbUpdateException>(() => ideas.PromoteAsync(idea.Id, "promoter"));
        var afterIdea = await ideas.GetByIdAsync(idea.Id);
        var after = await f.Service.GetForIdeaAsync(idea.Id);
        Assert.Equal(IdeaStatus.New, afterIdea!.Status);
        Assert.Null(afterIdea.PromotedProjectId);
        Assert.Null(after!.ProjectId);
        Assert.Equal(before!.Version, after.Version);
        Assert.Equal(before.Id, after.Id);
        await using var verify = f.CreateDbContext();
        Assert.Empty(await verify.Projects.ToListAsync());
        Assert.False(await verify.ActivityEvents.AnyAsync(a => a.EventType == "IdeaPromoted" || a.EventType == "ProjectCreatedFromIdea"));
        Assert.Equal(1, await verify.WorkRequests.CountAsync());
    }
}
