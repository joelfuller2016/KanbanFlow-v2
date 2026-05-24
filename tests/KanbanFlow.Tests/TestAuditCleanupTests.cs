using KanbanFlow.Models;
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

        // Projects that existed at run start: the live one (still present) and the one a user
        // deletes mid-run (id 9003). The runner's own throwaway (9001) is deliberately absent.
        var preExistingProjectIds = new List<int> { liveProjectId, preExistingDeletedMidRunProjectId };

        var deleted = await TestAuditCleanup.PurgeOrphanedSinceAsync(f, runStart, preExistingProjectIds);

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
