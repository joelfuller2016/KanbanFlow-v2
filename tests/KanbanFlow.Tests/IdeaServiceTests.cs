using KanbanFlow.Models;
using KanbanFlow.Services;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Tests;

/// <summary>
/// Real-code tests for <see cref="IdeaService"/> against a live SQLite database
/// (<see cref="SqliteTestFactory"/>). No mocks of the data layer — every test seeds
/// real rows, runs the real service, and re-queries a fresh context to assert what was
/// actually persisted, including the <see cref="ActivityEvent"/> audit trail that the
/// Ideas feature (#180) inherits from the #88 single-audit-boundary rule.
/// </summary>
public sealed class IdeaServiceTests
{
    // ── helpers ─────────────────────────────────────────────────────────────────
    private static int SeedIdea(
        SqliteTestFactory f, string title = "Idea",
        IdeaStatus status = IdeaStatus.New, string? tags = null, string? body = null)
    {
        using var db = f.CreateDbContext();
        var idea = new Idea { Title = title, Status = status, Tags = tags, Body = body };
        db.Add(idea);
        db.SaveChanges();
        return idea.Id;
    }

    private static List<ActivityEvent> IdeaEvents(SqliteTestFactory f, int ideaId)
    {
        using var db = f.CreateDbContext();
        return db.ActivityEvents
            .Where(a => a.EntityType == "Idea" && a.EntityId == ideaId)
            .OrderBy(a => a.Id)
            .ToList();
    }

    // ── CreateAsync ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Create_PersistsIdea_AndEmitsIdeaCreatedEvent()
    {
        using var f = new SqliteTestFactory();
        var svc = new IdeaService(f);

        var idea = await svc.CreateAsync(
            new IdeaCreate("My idea", Body: "raw dump", Plan: "the plan", Tags: "ai,tools",
                Priority: TaskPriority.High), "tester");

        Assert.True(idea.Id > 0);
        Assert.Equal(IdeaStatus.New, idea.Status);

        using var db = f.CreateDbContext();
        var persisted = await db.Ideas.FindAsync(idea.Id);
        Assert.NotNull(persisted);
        Assert.Equal("My idea", persisted!.Title);
        Assert.Equal("raw dump", persisted.Body);
        Assert.Equal("the plan", persisted.Plan);
        Assert.Equal("ai,tools", persisted.Tags);
        Assert.Equal(TaskPriority.High, persisted.Priority);
        Assert.Null(persisted.PromotedProjectId);

        var events = IdeaEvents(f, idea.Id);
        var created = Assert.Single(events);
        Assert.Equal("IdeaCreated", created.EventType);
        Assert.Equal("tester", created.Actor);
        Assert.Null(created.BeforeJson);
        Assert.NotNull(created.AfterJson);
    }

    [Fact]
    public async Task Create_TrimsTitle_AndNullsBlankOptionalFields()
    {
        using var f = new SqliteTestFactory();
        var svc = new IdeaService(f);

        var idea = await svc.CreateAsync(new IdeaCreate("  spaced  ", Body: "   ", Tags: ""), null);

        using var db = f.CreateDbContext();
        var persisted = await db.Ideas.FindAsync(idea.Id);
        Assert.Equal("spaced", persisted!.Title);
        Assert.Null(persisted.Body);
        Assert.Null(persisted.Tags);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_BlankTitle_Throws(string title)
    {
        using var f = new SqliteTestFactory();
        var svc = new IdeaService(f);
        await Assert.ThrowsAsync<ValidationException>(() => svc.CreateAsync(new IdeaCreate(title), null));
    }

    [Fact]
    public async Task Create_TitleTooLong_Throws()
    {
        using var f = new SqliteTestFactory();
        var svc = new IdeaService(f);
        await Assert.ThrowsAsync<ValidationException>(
            () => svc.CreateAsync(new IdeaCreate(new string('x', 501)), null));
    }

    [Fact]
    public async Task Create_InvalidPriority_Throws()
    {
        using var f = new SqliteTestFactory();
        var svc = new IdeaService(f);
        await Assert.ThrowsAsync<ValidationException>(
            () => svc.CreateAsync(new IdeaCreate("ok", Priority: (TaskPriority)999), null));
    }

    // ── GetByIdAsync ────────────────────────────────────────────────────────────
    [Fact]
    public async Task GetById_ReturnsNull_WhenMissing()
    {
        using var f = new SqliteTestFactory();
        var svc = new IdeaService(f);
        Assert.Null(await svc.GetByIdAsync(9999));
    }

    [Fact]
    public async Task GetById_ReturnsIdea()
    {
        using var f = new SqliteTestFactory();
        var id = SeedIdea(f, "find me");
        var svc = new IdeaService(f);
        var idea = await svc.GetByIdAsync(id);
        Assert.Equal("find me", idea!.Title);
    }

    // ── ListAsync ───────────────────────────────────────────────────────────────
    [Fact]
    public async Task List_FiltersByStatus()
    {
        using var f = new SqliteTestFactory();
        SeedIdea(f, "new1", IdeaStatus.New);
        SeedIdea(f, "arch1", IdeaStatus.Archived);
        var svc = new IdeaService(f);

        var archived = await svc.ListAsync(status: IdeaStatus.Archived);
        Assert.Equal("arch1", Assert.Single(archived).Title);
    }

    [Fact]
    public async Task List_FiltersByTag_Substring()
    {
        using var f = new SqliteTestFactory();
        SeedIdea(f, "a", tags: "ai,backend");
        SeedIdea(f, "b", tags: "frontend");
        var svc = new IdeaService(f);

        // Upper-case query proves the match is BOTH substring and case-insensitive.
        var tagged = await svc.ListAsync(tag: "BACKEND");
        Assert.Equal("a", Assert.Single(tagged).Title);
    }

    [Fact]
    public async Task List_FiltersBySearch_TitleOrBody()
    {
        using var f = new SqliteTestFactory();
        SeedIdea(f, "kanban board", body: null);
        SeedIdea(f, "other", body: "mentions kanban in body");
        SeedIdea(f, "unrelated", body: "nothing here");
        var svc = new IdeaService(f);

        // Upper-case query proves case-insensitive matching across title and body.
        var hits = await svc.ListAsync(search: "KANBAN");
        Assert.Equal(2, hits.Count);
        Assert.DoesNotContain(hits, i => i.Title == "unrelated");
    }

    [Fact]
    public async Task List_OrdersNewestFirst_AndPages()
    {
        using var f = new SqliteTestFactory();
        var first = SeedIdea(f, "first");
        var second = SeedIdea(f, "second");
        var third = SeedIdea(f, "third");
        var svc = new IdeaService(f);

        var page1 = await svc.ListAsync(limit: 2, offset: 0);
        Assert.Equal(new[] { third, second }, page1.Select(i => i.Id).ToArray());

        var page2 = await svc.ListAsync(limit: 2, offset: 2);
        Assert.Equal(first, Assert.Single(page2).Id);
    }

    [Fact]
    public async Task List_ClampsLimitToAtLeastOne()
    {
        using var f = new SqliteTestFactory();
        SeedIdea(f, "a");
        SeedIdea(f, "b");
        var svc = new IdeaService(f);

        var result = await svc.ListAsync(limit: 0);
        Assert.Single(result); // clamp(0, 1, 500) == 1
    }

    // ── UpdateAsync ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Update_ChangesFields_AndEmitsIdeaUpdated()
    {
        using var f = new SqliteTestFactory();
        var id = SeedIdea(f, "before");
        var svc = new IdeaService(f);

        var updated = await svc.UpdateAsync(id,
            new IdeaUpdate(Title: "after", Status: IdeaStatus.Triaging, Tags: "x"), "editor");

        Assert.Equal("after", updated.Title);
        Assert.Equal(IdeaStatus.Triaging, updated.Status);

        using var db = f.CreateDbContext();
        var persisted = await db.Ideas.FindAsync(id);
        Assert.Equal("after", persisted!.Title);
        Assert.Equal(IdeaStatus.Triaging, persisted.Status);

        var events = IdeaEvents(f, id);
        var updatedEvent = Assert.Single(events, e => e.EventType == "IdeaUpdated");
        Assert.NotNull(updatedEvent.BeforeJson);
        Assert.NotNull(updatedEvent.AfterJson);
    }

    [Fact]
    public async Task Update_NoActualChange_IsIdempotent_NoEvent()
    {
        using var f = new SqliteTestFactory();
        var id = SeedIdea(f, "same");
        var svc = new IdeaService(f);

        await svc.UpdateAsync(id, new IdeaUpdate(Title: "same"), "editor");

        Assert.DoesNotContain(IdeaEvents(f, id), e => e.EventType == "IdeaUpdated");
    }

    [Fact]
    public async Task Update_Missing_Throws()
    {
        using var f = new SqliteTestFactory();
        var svc = new IdeaService(f);
        await Assert.ThrowsAsync<IdeaNotFoundException>(
            () => svc.UpdateAsync(123, new IdeaUpdate(Title: "x"), null));
    }

    [Fact]
    public async Task Update_InvalidStatus_Throws()
    {
        using var f = new SqliteTestFactory();
        var id = SeedIdea(f);
        var svc = new IdeaService(f);
        await Assert.ThrowsAsync<ValidationException>(
            () => svc.UpdateAsync(id, new IdeaUpdate(Status: (IdeaStatus)42), null));
    }

    // ── DeleteAsync ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Delete_RemovesIdea_AndAuditSurvives()
    {
        using var f = new SqliteTestFactory();
        var id = SeedIdea(f, "doomed");
        var svc = new IdeaService(f);

        await svc.DeleteAsync(id, "remover");

        using var db = f.CreateDbContext();
        Assert.Null(await db.Ideas.FindAsync(id));

        var deleted = Assert.Single(IdeaEvents(f, id), e => e.EventType == "IdeaDeleted");
        Assert.NotNull(deleted.BeforeJson);
        Assert.Null(deleted.AfterJson);
        Assert.Equal("remover", deleted.Actor);
    }

    [Fact]
    public async Task Delete_Missing_Throws()
    {
        using var f = new SqliteTestFactory();
        var svc = new IdeaService(f);
        await Assert.ThrowsAsync<IdeaNotFoundException>(() => svc.DeleteAsync(404, null));
    }

    // ── PromoteAsync ────────────────────────────────────────────────────────────
    [Fact]
    public async Task Promote_CreatesProject_LinksIdea_AndEmitsBothEvents()
    {
        using var f = new SqliteTestFactory();
        var svc = new IdeaService(f);
        var idea = await svc.CreateAsync(
            new IdeaCreate("Build a thing", Body: "the body", Plan: "the plan"), "tester");

        var result = await svc.PromoteAsync(idea.Id, "promoter");

        Assert.Equal(idea.Id, result.IdeaId);
        Assert.True(result.ProjectId > 0);

        using var db = f.CreateDbContext();
        var project = await db.Projects.FindAsync(result.ProjectId);
        Assert.NotNull(project);
        Assert.Equal("Build a thing", project!.Name);
        Assert.Equal("the body", project.Description);
        Assert.Equal("the plan", project.Goals);
        Assert.Contains("the body", project.Readme);
        Assert.Contains("the plan", project.Readme);

        var promoted = await db.Ideas.FindAsync(idea.Id);
        Assert.Equal(IdeaStatus.Promoted, promoted!.Status);
        Assert.Equal(result.ProjectId, promoted.PromotedProjectId);

        // Audit: both an IdeaPromoted (on the idea) and a ProjectCreatedFromIdea (on the project),
        // both attributed to the new project id.
        var ideaPromoted = Assert.Single(IdeaEvents(f, idea.Id), e => e.EventType == "IdeaPromoted");
        Assert.Equal(result.ProjectId, ideaPromoted.ProjectId);

        var projectEvent = db.ActivityEvents.Single(
            e => e.EntityType == "Project" && e.EntityId == result.ProjectId
                 && e.EventType == "ProjectCreatedFromIdea");
        Assert.Equal(result.ProjectId, projectEvent.ProjectId);
    }

    [Fact]
    public async Task Promote_AlreadyPromoted_Throws()
    {
        using var f = new SqliteTestFactory();
        var svc = new IdeaService(f);
        var idea = await svc.CreateAsync(new IdeaCreate("once"), null);
        await svc.PromoteAsync(idea.Id, null);

        await Assert.ThrowsAsync<ValidationException>(() => svc.PromoteAsync(idea.Id, null));
    }

    [Fact]
    public async Task Promote_Missing_Throws()
    {
        using var f = new SqliteTestFactory();
        var svc = new IdeaService(f);
        await Assert.ThrowsAsync<IdeaNotFoundException>(() => svc.PromoteAsync(777, null));
    }

    [Fact]
    public async Task Promote_DeletingProject_NullsLink_KeepsIdea()
    {
        // Verifies the FK OnDelete(SetNull): removing the promoted project must not delete
        // the originating idea, only clear its link.
        using var f = new SqliteTestFactory();
        var svc = new IdeaService(f);
        var idea = await svc.CreateAsync(new IdeaCreate("survivor"), null);
        var result = await svc.PromoteAsync(idea.Id, null);

        using (var db = f.CreateDbContext())
        {
            var project = await db.Projects.FindAsync(result.ProjectId);
            db.Projects.Remove(project!);
            await db.SaveChangesAsync();
        }

        using var check = f.CreateDbContext();
        var stillThere = await check.Ideas.FindAsync(idea.Id);
        Assert.NotNull(stillThere);
        Assert.Null(stillThere!.PromotedProjectId);
    }
}
