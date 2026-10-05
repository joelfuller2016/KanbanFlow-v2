using KanbanFlow.Data;
using KanbanFlow.Models;
using KanbanFlow.Services;
using Microsoft.Data.Sqlite;
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
            new IdeaUpdate(Title: "after", Status: IdeaStatus.Triaging, Tags: "x", ExpectedWorkflowVersion: 0), "editor");

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

        await svc.UpdateAsync(id, new IdeaUpdate(Title: "same", ExpectedWorkflowVersion: 0), "editor");

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

    // ── UpdateAsync + the derived, terminal `Promoted` status (#283 + #285) ─────

    /// <summary>
    /// <b>Every</b> ordered pair in <see cref="IdeaStatus"/> × <see cref="IdeaStatus"/> — all 25,
    /// including the self-transitions and <em>both</em> directions across the <c>Promoted</c> boundary.
    /// Computed from the enum, so a status added later is covered automatically.
    /// </summary>
    public static TheoryData<IdeaStatus, IdeaStatus> AllStatusTransitions()
    {
        var all = Enum.GetValues<IdeaStatus>();
        var data = new TheoryData<IdeaStatus, IdeaStatus>();
        foreach (var from in all)
        {
            foreach (var to in all)
                data.Add(from, to);
        }
        return data;
    }

    [Fact]
    public async Task Update_ToPromoted_Throws_AndNamesThePromoteEndpoint()
    {
        // #283: `Promoted` IS a defined enum value, so the old Enum.IsDefined-only guard waved it
        // straight through. The refusal must also be actionable — name the one path that promotes
        // properly, rather than just saying no.
        using var f = new SqliteTestFactory();
        var id = SeedIdea(f, "not yours to promote");
        var svc = new IdeaService(f);

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => svc.UpdateAsync(id, new IdeaUpdate(Status: IdeaStatus.Promoted), "sneaky"));

        Assert.Contains("/api/ideas/{id}/promote", ex.Message);
    }

    /// <summary>
    /// THE load-bearing invariant (#283): no public write path may leave an idea
    /// <c>Status = Promoted</c> with <c>PromotedProjectId = null</c>. That state is a one-way trap —
    /// <see cref="IdeaService.PromoteAsync"/> refuses an already-promoted idea forever, and
    /// <see cref="IdeaUpdate"/> carries no <c>PromotedProjectId</c> field, so nothing can ever
    /// repair it.
    ///
    /// Driving <see cref="IIdeaService.UpdateAsync"/> closes ALL THREE public surfaces, because each
    /// one funnels through this single method (the #88 single-audit-boundary rule):
    /// <c>PATCH /api/ideas/{id}</c> (ApiEndpoints.cs:648) and MCP <c>update_idea</c>
    /// (KanbanMcpTools.cs:454) both call it and translate its <see cref="ValidationException"/>
    /// (→ 422 / McpException), and <c>IdeaModal.razor</c> calls it directly. The only other writer of
    /// <see cref="Idea.Status"/> anywhere is <see cref="IdeaService.PromoteAsync"/>, which sets it
    /// together with <see cref="Idea.PromotedProjectId"/> inside one transaction.
    /// </summary>
    [Fact]
    public async Task Update_CannotStrandIdeaAsPromotedWithoutProject_FromAnyStatus()
    {
        using var f = new SqliteTestFactory();
        var svc = new IdeaService(f);

        foreach (var from in Enum.GetValues<IdeaStatus>().Where(s => s != IdeaStatus.Promoted))
        {
            var id = SeedIdea(f, $"from-{from}", status: from);

            // On its own...
            await Assert.ThrowsAsync<ValidationException>(
                () => svc.UpdateAsync(id, new IdeaUpdate(Status: IdeaStatus.Promoted), "sneaky"));

            // ...and smuggled in beside a legitimate field change, which must NOT partially apply.
            await Assert.ThrowsAsync<ValidationException>(
                () => svc.UpdateAsync(
                    id, new IdeaUpdate(Title: "smuggled", Status: IdeaStatus.Promoted), "sneaky"));

            using var db = f.CreateDbContext();
            var idea = await db.Ideas.FindAsync(id);
            Assert.Equal(from, idea!.Status);            // status untouched
            Assert.Equal($"from-{from}", idea.Title);    // rejected wholesale — no partial write
            Assert.Null(idea.PromotedProjectId);
            // A refused mutation is not a mutation: no IdeaUpdated event may be written for it.
            Assert.DoesNotContain(IdeaEvents(f, id), e => e.EventType == "IdeaUpdated");
        }

        // The invariant itself, asserted over the whole table: not one orphan exists.
        using var check = f.CreateDbContext();
        Assert.DoesNotContain(
            check.Ideas.ToList(),
            i => i.Status == IdeaStatus.Promoted && i.PromotedProjectId is null);
    }

    [Fact]
    public async Task Update_RejectsPromoted_ButPromoteAsync_StillPromotesProperly()
    {
        // The guard lives in UpdateAsync alone. PromoteAsync writes Status through its OWN conditional
        // ExecuteUpdateAsync (IdeaService.cs:252-257) and never routes through UpdateAsync, so the one
        // legitimate route to `Promoted` still works — and it is the route that also creates the
        // Project and sets PromotedProjectId. Bolting the side door must not jam the front door.
        using var f = new SqliteTestFactory();
        var svc = new IdeaService(f);
        var idea = await svc.CreateAsync(new IdeaCreate("promote me properly"), "tester");

        await Assert.ThrowsAsync<ValidationException>(
            () => svc.UpdateAsync(idea.Id, new IdeaUpdate(Status: IdeaStatus.Promoted), "sneaky"));

        var result = await svc.PromoteAsync(idea.Id, "promoter");

        using var db = f.CreateDbContext();
        var promoted = await db.Ideas.FindAsync(idea.Id);
        Assert.Equal(IdeaStatus.Promoted, promoted!.Status);
        Assert.Equal(result.ProjectId, promoted.PromotedProjectId); // linked, not stranded
        Assert.NotNull(await db.Projects.FindAsync(result.ProjectId));
        Assert.Single(IdeaEvents(f, idea.Id), e => e.EventType == "IdeaPromoted");
    }

    [Fact]
    public async Task Update_PromotedIdea_OtherFieldsStillEditable_WhenStatusOmitted()
    {
        // The guard refuses the *status*, it does not freeze the idea. A promoted idea stays editable
        // by omitting Status (null = "leave unchanged") — exactly what IdeaModal sends once an idea is
        // promoted — and it keeps its project link through the edit.
        using var f = new SqliteTestFactory();
        var svc = new IdeaService(f);
        var idea = await svc.CreateAsync(new IdeaCreate("promoted, still editable"), "tester");
        var result = await svc.PromoteAsync(idea.Id, "promoter");

        var updated = await svc.UpdateAsync(
            idea.Id, new IdeaUpdate(Title: "retitled after promotion", Tags: "kept", ExpectedWorkflowVersion: (await svc.GetByIdAsync(idea.Id))!.WorkflowVersion), "editor");

        Assert.Equal("retitled after promotion", updated.Title);
        Assert.Equal(IdeaStatus.Promoted, updated.Status);
        Assert.Equal(result.ProjectId, updated.PromotedProjectId);

        using var db = f.CreateDbContext();
        var persisted = await db.Ideas.FindAsync(idea.Id);
        Assert.Equal("retitled after promotion", persisted!.Title);
        Assert.Equal("kept", persisted.Tags);
        Assert.Equal(IdeaStatus.Promoted, persisted.Status);
        Assert.Equal(result.ProjectId, persisted.PromotedProjectId); // link survives the edit
    }

    [Fact]
    public async Task Update_PromotedIdea_CannotLeavePromoted_LinkSurvives_AndMessageIsActionable()
    {
        // #285 — the mirror of #283. Moving a promoted idea back to New left PromotedProjectId pointing
        // at a real Project while the idea claimed to be un-promoted, and NOTHING could ever clear it
        // (IdeaUpdate has no PromotedProjectId field). A third stuck state: says New, links to a project,
        // can never be promoted again. Promotion is terminal from the update surface.
        using var f = new SqliteTestFactory();
        var svc = new IdeaService(f);
        var idea = await svc.CreateAsync(new IdeaCreate("promoted, and staying that way"), "tester");
        var result = await svc.PromoteAsync(idea.Id, "promoter");

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => svc.UpdateAsync(idea.Id, new IdeaUpdate(Status: IdeaStatus.New), "sneaky"));

        // The refusal must be actionable, not just a "no": say promotion is permanent, and say what the
        // caller CAN still do (edit everything else).
        Assert.Contains("permanent", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("still editable", ex.Message, StringComparison.OrdinalIgnoreCase);

        // ...and smuggled in beside a legitimate field change, it must not partially apply.
        await Assert.ThrowsAsync<ValidationException>(
            () => svc.UpdateAsync(
                idea.Id, new IdeaUpdate(Title: "smuggled", Status: IdeaStatus.New), "sneaky"));

        using var db = f.CreateDbContext();
        var persisted = await db.Ideas.FindAsync(idea.Id);
        Assert.Equal(IdeaStatus.Promoted, persisted!.Status);            // still promoted
        Assert.Equal(result.ProjectId, persisted.PromotedProjectId);     // link intact, never dangling
        Assert.Equal("promoted, and staying that way", persisted.Title); // refused wholesale — no partial write
        Assert.DoesNotContain(IdeaEvents(f, idea.Id), e => e.EventType == "IdeaUpdated");
    }

    [Theory]
    [MemberData(nameof(AllStatusTransitions))]
    public async Task Update_StatusTransitionMatrix_HoldsThePromotedInvariant(IdeaStatus from, IdeaStatus to)
    {
        // THE deliverable of #283 + #285: one theory over the WHOLE transition space (5 x 5 = 25 ordered
        // pairs, self-transitions and both Promoted directions included), asserting the INVARIANT rather
        // than the guards. Guard-by-guard testing is exactly what let both holes ship — each guard had a
        // test, the space never did. This single theory would have caught #283 AND #285 on one run.
        //
        // THE SPEC, restated here independently of IdeaService.IsStatusTransitionAllowed so this test is
        // a real oracle and not an echo of the implementation: `Promoted` is DERIVED (only PromoteAsync
        // may write it — it alone also creates the Project and links it) and TERMINAL (promotion is
        // permanent — IdeaUpdate has no PromotedProjectId field, so leaving Promoted would dangle the
        // link forever). A transition is therefore legal iff it does not CROSS the Promoted boundary in
        // either direction. A self-transition changes nothing, so it can break neither rule.
        var shouldSucceed = from == to || (from != IdeaStatus.Promoted && to != IdeaStatus.Promoted);

        using var f = new SqliteTestFactory();
        var svc = new IdeaService(f);

        // Reach `from` HONESTLY. A promoted idea is one that PromoteAsync actually promoted, so it has a
        // real Project and a real PromotedProjectId. Seeding Status=Promoted directly would fabricate the
        // very stranded row this invariant exists to make impossible — and prove nothing.
        int id;
        int? linkedProjectId = null;
        if (from == IdeaStatus.Promoted)
        {
            var seeded = await svc.CreateAsync(new IdeaCreate($"{from}->{to}"), "seed");
            linkedProjectId = (await svc.PromoteAsync(seeded.Id, "seed")).ProjectId;
            id = seeded.Id;
        }
        else
        {
            id = SeedIdea(f, $"{from}->{to}", status: from);
        }

        if (shouldSucceed)
        {
            var updated = await svc.UpdateAsync(id, new IdeaUpdate(Status: to, ExpectedWorkflowVersion: (await svc.GetByIdAsync(id))!.WorkflowVersion), "editor");
            Assert.Equal(to, updated.Status);
        }
        else
        {
            await Assert.ThrowsAsync<ValidationException>(
                () => svc.UpdateAsync(id, new IdeaUpdate(Status: to), "editor"));
        }

        using var db = f.CreateDbContext();
        var persisted = await db.Ideas.FindAsync(id);
        Assert.NotNull(persisted);

        // THE INVARIANT, re-read from the DB on every one of the 25 pairs, refused or not:
        //     Status == Promoted  <=>  PromotedProjectId != null
        // (No project is deleted anywhere in this test, so the OnDelete(SetNull) orphan — the one
        // legitimate way this equivalence breaks, see Promote_OrphanedIdea_ThrowsClearMessage — is out
        // of reach here. Every state this test can produce must satisfy it.)
        Assert.Equal(persisted!.Status == IdeaStatus.Promoted, persisted.PromotedProjectId is not null);

        // ...and the status landed exactly where the spec says: at `to` if allowed, untouched if refused.
        Assert.Equal(shouldSucceed ? to : from, persisted.Status);

        // A promoted idea keeps its real project link through all of it — allowed self-transition or
        // refused escape attempt alike.
        if (from == IdeaStatus.Promoted)
            Assert.Equal(linkedProjectId, persisted.PromotedProjectId);

        // Audit (#88): a real change writes exactly one IdeaUpdated. A no-op self-transition writes none
        // (idempotent, no audit noise), and a REFUSED change writes none either — a refused mutation is
        // not a mutation.
        Assert.Equal(
            shouldSucceed && from != to ? 1 : 0,
            IdeaEvents(f, id).Count(e => e.EventType == "IdeaUpdated"));
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

        // Regression guard (#206 adversarial finding): the `after` snapshot MUST reflect the PROMOTED
        // state. Marking the tracked entity Unchanged reverts its values to the originally-loaded ones,
        // so the snapshot has to be captured before that revert — otherwise this IdeaPromoted event's
        // after-json would falsely record Status=New / promotedProjectId=null. Status serializes as its
        // enum name; PromotedProjectId as the project id (camelCase, JsonSerializerDefaults.Web).
        Assert.Contains("\"status\":\"Promoted\"", ideaPromoted.AfterJson);
        Assert.Contains($"\"promotedProjectId\":{result.ProjectId}", ideaPromoted.AfterJson);
        Assert.Contains("\"status\":\"New\"", ideaPromoted.BeforeJson);        // before ≠ after

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

    [Fact]
    public async Task Promote_Twice_SecondThrows_NoDuplicateProject()
    {
        // #206 defect A: a second promote must reject without creating a second project row or a
        // duplicate audit pair (the pre-existing double-promote guard, re-verified end to end).
        using var f = new SqliteTestFactory();
        var svc = new IdeaService(f);
        var idea = await svc.CreateAsync(new IdeaCreate("build once"), "promoter");

        var result = await svc.PromoteAsync(idea.Id, "promoter");

        using (var db = f.CreateDbContext())
        {
            Assert.Single(db.Projects.ToList());
            var linked = await db.Ideas.FindAsync(idea.Id);
            Assert.Equal(IdeaStatus.Promoted, linked!.Status);
            Assert.Equal(result.ProjectId, linked.PromotedProjectId);
            Assert.Single(db.ActivityEvents.Where(e => e.EventType == "ProjectCreatedFromIdea").ToList());
        }
        Assert.Single(IdeaEvents(f, idea.Id), e => e.EventType == "IdeaPromoted");

        await Assert.ThrowsAsync<ValidationException>(() => svc.PromoteAsync(idea.Id, "promoter"));

        // Still exactly one project and one of each audit event — no orphan, no duplicate.
        using (var db = f.CreateDbContext())
        {
            Assert.Single(db.Projects.ToList());
            Assert.Single(db.ActivityEvents.Where(e => e.EventType == "ProjectCreatedFromIdea").ToList());
        }
        Assert.Single(IdeaEvents(f, idea.Id), e => e.EventType == "IdeaPromoted");
    }

    [Fact]
    public async Task Promote_OrphanedIdea_ThrowsClearMessage()
    {
        // #206 defect B: after the promoted project is deleted, OnDelete(SetNull) nulls the link
        // but leaves Status == Promoted. Re-promoting must not render a blank "...to project ."
        // — it must explain the project was removed.
        using var f = new SqliteTestFactory();
        var svc = new IdeaService(f);
        var idea = await svc.CreateAsync(new IdeaCreate("orphan me"), null);
        var result = await svc.PromoteAsync(idea.Id, null);

        using (var db = f.CreateDbContext())
        {
            db.Projects.Remove((await db.Projects.FindAsync(result.ProjectId))!);
            await db.SaveChangesAsync();
        }
        using (var db = f.CreateDbContext())
        {
            var orphan = await db.Ideas.FindAsync(idea.Id);
            Assert.Equal(IdeaStatus.Promoted, orphan!.Status); // orphan state: promoted but unlinked
            Assert.Null(orphan.PromotedProjectId);
        }

        var ex = await Assert.ThrowsAsync<ValidationException>(() => svc.PromoteAsync(idea.Id, null));
        Assert.DoesNotContain("project .", ex.Message);
        Assert.Contains("removed", ex.Message);
    }

    [Fact]
    public async Task Promote_Concurrent_OnlyOneWins_NoOrphanProject()
    {
        // #206 defect A, true-concurrency form: two overlapping promotes of the same idea. Exactly
        // one wins; the loser's conditional UPDATE matches 0 rows and rolls back its just-inserted
        // project (the rows==0 path). Needs a file-backed DB so each context gets its own
        // connection — the shared :memory: factory cannot hold two overlapping transactions.
        using var f = new FileSqliteFactory();
        var svc = new IdeaService(f);
        int ideaId;
        using (var db = f.CreateDbContext())
        {
            var idea = new Idea { Title = "race" };
            db.Add(idea);
            db.SaveChanges();
            ideaId = idea.Id;
        }

        var t1 = Task.Run(() => svc.PromoteAsync(ideaId, "a"));
        var t2 = Task.Run(() => svc.PromoteAsync(ideaId, "b"));

        var successes = 0;
        var failures = 0;
        foreach (var t in new[] { t1, t2 })
        {
            // The loser is rejected — normally a ValidationException from the rows==0 guard, but under
            // write contention it may instead surface a transient store exception (DbUpdateException /
            // SqliteException, e.g. SQLITE_BUSY). ONLY those are acceptable "did not win" outcomes;
            // anything else (NullReference, InvalidOperation, …) is a real regression and must fail the
            // test loudly, so it propagates rather than being miscounted as a loss.
            try { await t; successes++; }
            catch (Exception ex) when (ex is ValidationException or DbUpdateException or SqliteException) { failures++; }
        }

        Assert.Equal(1, successes);
        Assert.Equal(1, failures);

        using var check = f.CreateDbContext();
        Assert.Single(check.Projects.ToList()); // no orphan from the loser's rolled-back insert
        var promoted = check.Ideas.Single(i => i.Id == ideaId);
        Assert.Equal(IdeaStatus.Promoted, promoted.Status);
        Assert.NotNull(promoted.PromotedProjectId);
        Assert.Single(check.ActivityEvents
            .Where(e => e.EventType == "IdeaPromoted" && e.EntityId == ideaId).ToList());
        Assert.Single(check.ActivityEvents
            .Where(e => e.EventType == "ProjectCreatedFromIdea").ToList());
    }

    /// <summary>
    /// A file-backed SQLite <see cref="IDbContextFactory{AppDbContext}"/> for the concurrency test.
    /// Unlike <see cref="SqliteTestFactory"/> (one shared <c>:memory:</c> connection, which cannot
    /// hold two overlapping transactions), this points every context at a temp file so each gets
    /// its OWN connection. WAL plus Microsoft.Data.Sqlite's command-timeout busy retry let the
    /// loser's write wait for the winner's commit rather than failing outright.
    /// </summary>
    private sealed class FileSqliteFactory : IDbContextFactory<AppDbContext>, IDisposable
    {
        private readonly string _path;
        private readonly DbContextOptions<AppDbContext> _options;

        public FileSqliteFactory()
        {
            _path = Path.Combine(Path.GetTempPath(), $"kf-promote-{Guid.NewGuid():N}.db");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = _path,
                // Microsoft.Data.Sqlite retries SQLITE_BUSY/LOCKED for up to DefaultTimeout seconds,
                // so a blocked concurrent writer waits for the other transaction to commit.
                DefaultTimeout = 30,
                // Disable pooling for this temp DB: Dispose can then delete the file once connections
                // close, WITHOUT a process-global SqliteConnection.ClearAllPools() that would disturb
                // other Microsoft.Data.Sqlite tests running in parallel.
                Pooling = false
            }.ToString();

            _options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connectionString)
                .Options;

            using var ctx = new AppDbContext(_options);
            ctx.Database.EnsureCreated();

            // WAL persists in the file header (set once, on a raw connection so no EF transaction
            // wraps the journal_mode change) — better reader/writer concurrency for the race.
            using var conn = new SqliteConnection(connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
            cmd.ExecuteNonQuery();
        }

        public AppDbContext CreateDbContext() => new(_options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new AppDbContext(_options));

        public void Dispose()
        {
            // Pooling is disabled on the connection string, so closed connections release the file
            // handle immediately — no process-global ClearAllPools() needed. Delete db + WAL/SHM.
            foreach (var p in new[] { _path, _path + "-wal", _path + "-shm" })
            {
                try { if (File.Exists(p)) File.Delete(p); }
                catch (IOException) { /* temp file, best-effort cleanup */ }
            }
        }
    }
}
