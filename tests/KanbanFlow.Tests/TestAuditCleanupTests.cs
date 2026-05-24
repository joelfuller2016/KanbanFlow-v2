using KanbanFlow.Models;
using KanbanFlow.Services;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Tests;

/// <summary>
/// Real-SQLite tests for <see cref="TestAuditCleanup.PurgeOrphanedSinceAsync"/> (issue #150).
/// The in-app test runner leaves orphaned <see cref="ActivityEvent"/> rows behind when it
/// deletes its throwaway test projects (ActivityEvent has no project FK). The purge must
/// remove ONLY those — events created during the run whose project no longer exists —
/// while preserving real deleted-project audit history (issue #85), which predates the run.
/// </summary>
public sealed class TestAuditCleanupTests
{
    [Fact]
    public async Task PurgeOrphanedSince_DeletesOnlyThisRunsOrphans_PreservesPreExistingAndLive()
    {
        using var f = new SqliteTestFactory();
        var runStart = DateTime.UtcNow;

        int testDroppingId, realDeletedAuditId, liveEventId, liveProjectId;

        await using (var db = await f.CreateDbContextAsync())
        {
            // (C) A real project that still exists; its event must be KEPT.
            var liveProject = new Project { Name = "Live project" };
            db.Projects.Add(liveProject);
            await db.SaveChangesAsync();
            liveProjectId = liveProject.Id;

            // (A) Test-runner dropping: created during this run, project no longer exists → DELETE.
            var testDropping = new ActivityEvent
            {
                ProjectId = 9001, // non-existent
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

            db.ActivityEvents.AddRange(testDropping, realDeletedAudit, liveEvent);
            await db.SaveChangesAsync();

            testDroppingId = testDropping.Id;
            realDeletedAuditId = realDeletedAudit.Id;
            liveEventId = liveEvent.Id;
        }

        var deleted = await TestAuditCleanup.PurgeOrphanedSinceAsync(f, runStart);

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
            // Exactly the two keepers remain.
            Assert.Equal(2, await db.ActivityEvents.CountAsync());
        }
    }
}
