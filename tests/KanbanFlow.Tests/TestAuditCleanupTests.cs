using KanbanFlow.Models;
using KanbanFlow.Models.Workflows;
using KanbanFlow.Services;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Tests;

/// <summary>
/// Real-SQLite tests for <see cref="TestAuditCleanup.PurgeOrphanedSinceAsync"/> (issue #150).
/// The in-app test runner leaves orphaned <see cref="ActivityEvent"/> rows behind when it
/// deletes its throwaway test projects (ActivityEvent has no project FK). The purge must
/// remove ONLY those — events created during the run whose project was BOTH created and
/// deleted by the run — while preserving (a) real deleted-project audit history that predates
/// the run (issue #85) and (b) audit for a real, pre-existing project a user deletes mid-run.
/// </summary>
public sealed class TestAuditCleanupTests
{
    [Fact]
    public async Task PurgePreservesRealIdeaMemoryAndProjectAuditWrittenDuringTheRun()
    {
        using var factory = new SqliteTestFactory();
        var runStart = DateTime.UtcNow;
        await using var db = await factory.CreateDbContextAsync();
        Assert.Contains(":memory:", db.Database.GetDbConnection().ConnectionString, StringComparison.Ordinal);
        var preExistingProjectIds = await db.Projects.Select(p => p.Id).ToListAsync();

        var ideas = new IdeaService(factory);
        var idea = await ideas.CreateAsync(new IdeaCreate("User idea"), "user");
        await ideas.UpdateAsync(idea.Id, new IdeaUpdate(Title: "Renamed user idea", ExpectedWorkflowVersion: idea.WorkflowVersion), "user");
        await ideas.DeleteAsync(idea.Id, "user");

        var memory = new MemoryService(factory);
        foreach (var (scope, scopeKey) in new[]
        {
            (MemoryScope.Feature, "7"),
            (MemoryScope.Agent, "agent-1"),
            (MemoryScope.Session, "session-1"),
            (MemoryScope.Project, "424242")
        })
        {
            await memory.CreateNodeAsync(new MemoryNodeCreate(MemoryType.Decision, scope, scopeKey, "Keep this memory"), "agent-1");
        }

        var project = new Project { Name = "User project created during the run" };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        await new TaskService(factory).DeleteProjectAsync(project.Id, "user");

        var before = await db.ActivityEvents.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
        Assert.Equal(new[]
        {
            "IdeaCreated", "IdeaUpdated", "IdeaDeleted", "WorkRequestDeleted", "RequestMessageDeleted",
            "MemoryCreated", "MemoryCreated", "MemoryCreated", "MemoryCreated", "ProjectDeleted"
        }, before.Select(e => e.EventType));
        Assert.All(before, e => Assert.True(e.CreatedAt >= runStart));

        var fixtureProject = new Project { Name = "Runner project" };
        db.Projects.Add(fixtureProject);
        await db.SaveChangesAsync();
        await new TaskService(factory).DeleteProjectAsync(fixtureProject.Id, "test-runner");
        var fixtureIdea = await ideas.CreateAsync(new IdeaCreate("Runner idea"), "test-runner");
        await ideas.DeleteAsync(fixtureIdea.Id, "test-runner");

        // A project-scoped memory can independently refer to the now-deleted fixture's ID.
        var fixtureScope = fixtureProject.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var memoryForMissingFixture = await memory.CreateNodeAsync(
            new MemoryNodeCreate(MemoryType.Decision, MemoryScope.Project, fixtureScope, "User memory"), "agent-1");
        var keepMemoryEventId = await db.ActivityEvents
            .Where(e => e.EntityType == "MemoryNode" && e.EntityId == memoryForMissingFixture.Id)
            .Select(e => e.Id).SingleAsync();

        var purged = await TestAuditCleanup.PurgeOrphanedSinceAsync(
            factory, runStart, preExistingProjectIds, [fixtureProject.Id], [fixtureIdea.Id]);

        Assert.Equal(5, purged); // Project delete plus the idea create/delete and its two cascade events.
        var after = await db.ActivityEvents.OrderBy(e => e.Id).Select(e => e.Id).ToListAsync();
        Assert.Equal(before.Select(e => e.Id).Append(keepMemoryEventId), after);
    }

    [Fact]
    public async Task PurgePreservesUnrelatedRequestAuditWhenItsFixtureProjectIsDeleted()
    {
        using var factory = new SqliteTestFactory();
        var runStart = DateTime.UtcNow;
        await using var db = await factory.CreateDbContextAsync();
        var fixtureProject = new Project { Name = "Runner project" };
        db.Projects.Add(fixtureProject);
        await db.SaveChangesAsync();

        var ideas = new IdeaService(factory);
        var userIdea = await ideas.CreateAsync(new IdeaCreate("User idea"), "user");
        await ideas.PromoteAsync(userIdea.Id, "user", fixtureProject.Id);

        var deletedIdea = await ideas.CreateAsync(new IdeaCreate("Another user idea"), "user");
        await ideas.PromoteAsync(deletedIdea.Id, "user", fixtureProject.Id);
        var requestId = await db.WorkRequests.Where(r => r.IdeaId == deletedIdea.Id).Select(r => r.Id).SingleAsync();
        db.BriefRevisions.Add(new BriefRevision
        {
            RequestId = requestId,
            Number = 1,
            Outcome = "User brief",
            Acceptance = "Keep the audit",
            Author = "user"
        });
        await db.SaveChangesAsync();
        await ideas.DeleteAsync(deletedIdea.Id, "user");
        await new TaskService(factory).DeleteProjectAsync(fixtureProject.Id, "test-runner");

        var before = await db.ActivityEvents.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
        Assert.Contains(before, e => e is { EntityType: "WorkRequest", EventType: "WorkRequestUpdated" }
            && e.ProjectId == fixtureProject.Id);
        Assert.Contains(before, e => e is { EntityType: "BriefRevision", EventType: "BriefRevisionDeleted" }
            && e.ProjectId == fixtureProject.Id);
        var userAuditIds = before.Where(e => e.EntityType != "Project").Select(e => e.Id);
        await TestAuditCleanup.PurgeOrphanedSinceAsync(factory, runStart, [], [fixtureProject.Id], []);

        var after = await db.ActivityEvents.OrderBy(e => e.Id).Select(e => e.Id).ToListAsync();
        Assert.Equal(userAuditIds, after);
    }

    [Fact]
    public async Task PurgeOrphanedSince_DeletesOnlyThisRunsOrphans_PreservesPreExistingMidRunAndLive()
    {
        using var f = new SqliteTestFactory();
        var runStart = DateTime.UtcNow;

        int testDroppingId, realDeletedAuditId, liveEventId, preExistingDeletedMidRunId, liveProjectId;

        // A project that existed at run start but is deleted mid-run (case D). Its id is in the
        // pre-existing snapshot but there is no Project row, mimicking a user deleting a real
        // project while a suite runs against the live DB.
        const int preExistingDeletedMidRunProjectId = 9003;

        await using (var db = await f.CreateDbContextAsync())
        {
            // (C) A real project that still exists; its event must be KEPT.
            var liveProject = new Project { Name = "Live project" };
            db.Projects.Add(liveProject);
            await db.SaveChangesAsync();
            liveProjectId = liveProject.Id;

            // (A) Test-runner dropping: created during this run, project did NOT exist at run
            // start and no longer exists → DELETE. This is the runner's own throwaway project.
            var testDropping = new ActivityEvent
            {
                ProjectId = 9001, // non-existent, not in the pre-existing snapshot
                EntityType = "Project",
                EntityId = 9001,
                EventType = "ProjectDeleted",
                CreatedAt = runStart.AddSeconds(5),
            };

            // (B) Real deleted-project audit (#85): created BEFORE the run, project gone → KEEP.
            var realDeletedAudit = new ActivityEvent
            {
                ProjectId = 9002, // non-existent
                EntityType = "Project",
                EntityId = 9002,
                EventType = "ProjectDeleted",
                CreatedAt = runStart.AddMinutes(-10),
            };

            // (C) Event for the still-existing project, created during this run → KEEP.
            var liveEvent = new ActivityEvent
            {
                ProjectId = liveProjectId,
                EntityType = "Project",
                EntityId = liveProjectId,
                EventType = "ProjectUpdated",
                CreatedAt = runStart.AddSeconds(5),
            };

            // (D) A real, pre-existing project deleted mid-run: event created during the run and
            // its project row is now gone, BUT the id was present at run start → KEEP. Without the
            // pre-existing exclusion this real audit would be wrongly purged (Copilot's concern).
            var preExistingDeletedMidRun = new ActivityEvent
            {
                ProjectId = preExistingDeletedMidRunProjectId, // in snapshot, no Project row now
                EntityType = "Project",
                EntityId = preExistingDeletedMidRunProjectId,
                EventType = "ProjectDeleted",
                CreatedAt = runStart.AddSeconds(5),
            };

            db.ActivityEvents.AddRange(testDropping, realDeletedAudit, liveEvent, preExistingDeletedMidRun);
            await db.SaveChangesAsync();

            testDroppingId = testDropping.Id;
            realDeletedAuditId = realDeletedAudit.Id;
            liveEventId = liveEvent.Id;
            preExistingDeletedMidRunId = preExistingDeletedMidRun.Id;
        }

        // Supply each ID as an ownership candidate so the time, pre-existing and live-project
        // guards each have to rule out a separate row.
        var deleted = await TestAuditCleanup.PurgeOrphanedSinceAsync(f, runStart,
            preExistingProjectIds: [preExistingDeletedMidRunProjectId],
            fixtureProjectIds: [9001, 9002, liveProjectId, preExistingDeletedMidRunProjectId],
            fixtureIdeaIds: []);

        // Only the single test-runner dropping (A) should have been removed.
        Assert.Equal(1, deleted);

        await using (var db = await f.CreateDbContextAsync())
        {
            // (A) gone.
            Assert.Null(await db.ActivityEvents.FindAsync(testDroppingId));
            // (B) preserved — real deleted-project audit predates runStart (#85).
            Assert.NotNull(await db.ActivityEvents.FindAsync(realDeletedAuditId));
            // (C) preserved — its project still exists.
            Assert.NotNull(await db.ActivityEvents.FindAsync(liveEventId));
            // (D) preserved — real project deleted mid-run; its id was in the pre-existing snapshot.
            Assert.NotNull(await db.ActivityEvents.FindAsync(preExistingDeletedMidRunId));
            // Exactly the three keepers remain.
            Assert.Equal(3, await db.ActivityEvents.CountAsync());
        }
    }
}
