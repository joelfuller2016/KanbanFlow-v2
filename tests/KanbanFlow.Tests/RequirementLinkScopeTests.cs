using KanbanFlow;
using KanbanFlow.Models;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Tests;

/// <summary>
/// Real-SQLite regression tests for #82 — requirement Feature/Task links must be scoped to the
/// requirement's own project. The create (POST), update (PUT), and assign (PATCH) endpoints all
/// funnel their link validation through <see cref="ApiEndpoints.ValidateRequirementLinksScopedAsync"/>,
/// so these tests exercise that exact code path against the real EF Core schema. The validator now
/// distinguishes two failure kinds so the endpoints can return the right status:
/// <list type="bullet">
///   <item><description><see cref="RequirementLinkErrorKind.CrossProject"/> — entity exists but
///   belongs to another project → endpoints return <c>422 UnprocessableEntity</c> (mirrors the
///   <c>CrossProjectReassignmentException</c> precedent for the task-move endpoint).</description></item>
///   <item><description><see cref="RequirementLinkErrorKind.NotFound"/> — entity does not exist
///   → endpoints return <c>404 NotFound</c> (unchanged).</description></item>
/// </list>
/// <see cref="RequirementLinkValidation.IsValid"/> means the link is absent or in-project.
/// </summary>
public sealed class RequirementLinkScopeTests
{
    // Two fully-populated projects, each with its own Feature + TaskItem, plus a Requirement that
    // lives in project 1. Ids let the tests link project-2 entities into the project-1 requirement
    // (the cross-project attack) and confirm same-project links still pass.
    private sealed record Seeded(
        int Project1Id,
        int Project2Id,
        int Req1Id,
        int Feature1Id,
        int Feature2Id,
        int Task1Id,
        int Task2Id);

    private static Seeded SeedTwoProjects(SqliteTestFactory f)
    {
        using var db = f.CreateDbContext();

        var p1 = new Project { Name = "Project One" };
        var f1 = new Feature { Name = "Feature One", Project = p1 };
        var t1 = new TaskItem { Name = "Task One", Feature = f1, Status = WorkStatus.Todo };

        var p2 = new Project { Name = "Project Two" };
        var f2 = new Feature { Name = "Feature Two", Project = p2 };
        var t2 = new TaskItem { Name = "Task Two", Feature = f2, Status = WorkStatus.Todo };

        db.AddRange(t1, t2);
        db.SaveChanges();

        var req1 = new Requirement { ProjectId = p1.Id, Text = "Req in project one" };
        db.Requirements.Add(req1);
        db.SaveChanges();

        return new Seeded(p1.Id, p2.Id, req1.Id, f1.Id, f2.Id, t1.Id, t2.Id);
    }

    // ── CREATE (POST) — uses the route projectId as the scope ───────────────────────────────

    [Fact]
    public async Task Create_RejectsFeatureFromAnotherProject_AsCrossProject()
    {
        using var f = new SqliteTestFactory();
        var ids = SeedTwoProjects(f);
        using var db = f.CreateDbContext();

        var result = await ApiEndpoints.ValidateRequirementLinksScopedAsync(
            ids.Project1Id, featureId: ids.Feature2Id, taskItemId: null, db);

        // Exists-but-cross-project → 422 kind (NOT 404), with an actionable message.
        Assert.Equal(RequirementLinkErrorKind.CrossProject, result.Kind);
        Assert.False(result.IsValid);
        Assert.NotNull(result.Message);
        Assert.Contains($"Feature {ids.Feature2Id}", result.Message);
        Assert.Contains($"project {ids.Project1Id}", result.Message);
    }

    [Fact]
    public async Task Create_RejectsTaskWhoseFeatureBelongsToAnotherProject_AsCrossProject()
    {
        using var f = new SqliteTestFactory();
        var ids = SeedTwoProjects(f);
        using var db = f.CreateDbContext();

        var result = await ApiEndpoints.ValidateRequirementLinksScopedAsync(
            ids.Project1Id, featureId: null, taskItemId: ids.Task2Id, db);

        Assert.Equal(RequirementLinkErrorKind.CrossProject, result.Kind);
        Assert.NotNull(result.Message);
        Assert.Contains($"Task {ids.Task2Id}", result.Message);
        Assert.Contains($"project {ids.Project1Id}", result.Message);
    }

    [Fact]
    public async Task Create_AcceptsSameProjectFeatureAndTask()
    {
        using var f = new SqliteTestFactory();
        var ids = SeedTwoProjects(f);
        using var db = f.CreateDbContext();

        var result = await ApiEndpoints.ValidateRequirementLinksScopedAsync(
            ids.Project1Id, featureId: ids.Feature1Id, taskItemId: ids.Task1Id, db);

        Assert.True(result.IsValid);
        Assert.Equal(RequirementLinkErrorKind.None, result.Kind);
        Assert.Null(result.Message);
    }

    // ── UPDATE (PUT) — scoped to the existing requirement's ProjectId ───────────────────────

    [Fact]
    public async Task Update_RejectsFeatureFromAnotherProject_AsCrossProject()
    {
        using var f = new SqliteTestFactory();
        var ids = SeedTwoProjects(f);
        using var db = f.CreateDbContext();

        var req = await db.Requirements.FindAsync(ids.Req1Id);
        var result = await ApiEndpoints.ValidateRequirementLinksScopedAsync(
            req!.ProjectId, featureId: ids.Feature2Id, taskItemId: null, db);

        Assert.Equal(RequirementLinkErrorKind.CrossProject, result.Kind);
        Assert.NotNull(result.Message);
        Assert.Contains($"Feature {ids.Feature2Id}", result.Message);
    }

    [Fact]
    public async Task Update_RejectsTaskFromAnotherProject_AsCrossProject()
    {
        using var f = new SqliteTestFactory();
        var ids = SeedTwoProjects(f);
        using var db = f.CreateDbContext();

        var req = await db.Requirements.FindAsync(ids.Req1Id);
        var result = await ApiEndpoints.ValidateRequirementLinksScopedAsync(
            req!.ProjectId, featureId: null, taskItemId: ids.Task2Id, db);

        Assert.Equal(RequirementLinkErrorKind.CrossProject, result.Kind);
        Assert.NotNull(result.Message);
        Assert.Contains($"Task {ids.Task2Id}", result.Message);
    }

    [Fact]
    public async Task Update_AcceptsSameProjectFeatureAndTask()
    {
        using var f = new SqliteTestFactory();
        var ids = SeedTwoProjects(f);
        using var db = f.CreateDbContext();

        var req = await db.Requirements.FindAsync(ids.Req1Id);
        var result = await ApiEndpoints.ValidateRequirementLinksScopedAsync(
            req!.ProjectId, featureId: ids.Feature1Id, taskItemId: ids.Task1Id, db);

        Assert.True(result.IsValid);
    }

    // ── ASSIGN (PATCH) — same scope; a 0 link is "clear" and must be allowed ─────────────────

    [Fact]
    public async Task Assign_RejectsFeatureFromAnotherProject_AsCrossProject()
    {
        using var f = new SqliteTestFactory();
        var ids = SeedTwoProjects(f);
        using var db = f.CreateDbContext();

        var req = await db.Requirements.FindAsync(ids.Req1Id);
        var result = await ApiEndpoints.ValidateRequirementLinksScopedAsync(
            req!.ProjectId, featureId: ids.Feature2Id, taskItemId: null, db);

        Assert.Equal(RequirementLinkErrorKind.CrossProject, result.Kind);
        Assert.NotNull(result.Message);
        Assert.Contains($"Feature {ids.Feature2Id}", result.Message);
    }

    [Fact]
    public async Task Assign_RejectsTaskFromAnotherProject_AsCrossProject()
    {
        using var f = new SqliteTestFactory();
        var ids = SeedTwoProjects(f);
        using var db = f.CreateDbContext();

        var req = await db.Requirements.FindAsync(ids.Req1Id);
        var result = await ApiEndpoints.ValidateRequirementLinksScopedAsync(
            req!.ProjectId, featureId: null, taskItemId: ids.Task2Id, db);

        Assert.Equal(RequirementLinkErrorKind.CrossProject, result.Kind);
        Assert.NotNull(result.Message);
        Assert.Contains($"Task {ids.Task2Id}", result.Message);
    }

    [Fact]
    public async Task Assign_AcceptsSameProjectFeatureAndTask()
    {
        using var f = new SqliteTestFactory();
        var ids = SeedTwoProjects(f);
        using var db = f.CreateDbContext();

        var req = await db.Requirements.FindAsync(ids.Req1Id);
        var result = await ApiEndpoints.ValidateRequirementLinksScopedAsync(
            req!.ProjectId, featureId: ids.Feature1Id, taskItemId: ids.Task1Id, db);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Assign_AllowsClearingLinksWithZero()
    {
        using var f = new SqliteTestFactory();
        var ids = SeedTwoProjects(f);
        using var db = f.CreateDbContext();

        // 0 means "clear the link" — must never be treated as a cross-project link.
        var result = await ApiEndpoints.ValidateRequirementLinksScopedAsync(
            ids.Project1Id, featureId: 0, taskItemId: 0, db);

        Assert.True(result.IsValid);
        Assert.Equal(RequirementLinkErrorKind.None, result.Kind);
    }

    // ── Preserve existing not-found behavior (distinct from the cross-project kind → 404) ────

    [Fact]
    public async Task Validation_PreservesNotFoundForMissingFeature()
    {
        using var f = new SqliteTestFactory();
        var ids = SeedTwoProjects(f);
        using var db = f.CreateDbContext();

        var result = await ApiEndpoints.ValidateRequirementLinksScopedAsync(
            ids.Project1Id, featureId: 999999, taskItemId: null, db);

        // Genuinely-missing entity stays 404 (NotFound kind), NOT 422 (CrossProject).
        Assert.Equal(RequirementLinkErrorKind.NotFound, result.Kind);
        Assert.NotEqual(RequirementLinkErrorKind.CrossProject, result.Kind);
        Assert.NotNull(result.Message);
        Assert.Contains("not found", result.Message);
    }

    [Fact]
    public async Task Validation_PreservesNotFoundForMissingTask()
    {
        using var f = new SqliteTestFactory();
        var ids = SeedTwoProjects(f);
        using var db = f.CreateDbContext();

        var result = await ApiEndpoints.ValidateRequirementLinksScopedAsync(
            ids.Project1Id, featureId: null, taskItemId: 999999, db);

        Assert.Equal(RequirementLinkErrorKind.NotFound, result.Kind);
        Assert.NotEqual(RequirementLinkErrorKind.CrossProject, result.Kind);
        Assert.NotNull(result.Message);
        Assert.Contains("not found", result.Message);
    }

    // ── PERSISTENCE regression for the 0 "clear-link" sentinel (#82) ──────────────────────────
    //
    // The validator above SKIPS 0 ("no link, nothing to validate"), so a client sending
    // FeatureId=0 / TaskItemId=0 passes validation. The bug: the create path persisted that raw 0,
    // and 0 is not a valid Feature/Task id → FK violation → 500. The fix normalizes 0 → null at
    // persistence (ApiEndpoints.NormalizeLink) so the link is stored as "absent". These tests prove
    // BOTH halves on REAL SQLite: (a) the normalized null saves cleanly, and (b) a raw 0 genuinely
    // violates the FK — which is exactly why normalization is required, not cosmetic.

    [Theory]
    [InlineData(null, null)]   // absent stays absent
    [InlineData(0, null)]      // the "clear-link" sentinel becomes a real null
    [InlineData(5, 5)]         // a real id is preserved unchanged
    public void NormalizeLink_MapsZeroAndNullToNull_KeepsRealIds(int? input, int? expected)
    {
        Assert.Equal(expected, ApiEndpoints.NormalizeLink(input));
    }

    [Fact]
    public async Task Create_PersistsNormalizedZeroLinksAsNull_NoFkViolation()
    {
        using var f = new SqliteTestFactory();
        var ids = SeedTwoProjects(f);
        using var db = f.CreateDbContext();

        // Mirrors the create path: a client sent FeatureId=0 / TaskItemId=0 ("clear"), which the
        // endpoint now funnels through NormalizeLink before persisting.
        var requirement = new Requirement
        {
            ProjectId = ids.Project1Id,
            Text = "Created with cleared links",
            FeatureId = ApiEndpoints.NormalizeLink(0),
            TaskItemId = ApiEndpoints.NormalizeLink(0)
        };
        db.Requirements.Add(requirement);

        // No throw == the fix works: real SQLite (FK enforcement on) accepts the row.
        await db.SaveChangesAsync();

        using var verifyDb = f.CreateDbContext();
        var saved = await verifyDb.Requirements.FindAsync(requirement.Id);
        Assert.NotNull(saved);
        Assert.Null(saved!.FeatureId);
        Assert.Null(saved.TaskItemId);
    }

    [Fact]
    public async Task Create_PersistsNormalizedRealFeatureLink_RoundTrips()
    {
        using var f = new SqliteTestFactory();
        var ids = SeedTwoProjects(f);
        using var db = f.CreateDbContext();

        // A genuine in-project id must survive normalization untouched and persist as the FK.
        var requirement = new Requirement
        {
            ProjectId = ids.Project1Id,
            Text = "Created with a real feature link",
            FeatureId = ApiEndpoints.NormalizeLink(ids.Feature1Id),
            TaskItemId = ApiEndpoints.NormalizeLink(ids.Task1Id)
        };
        db.Requirements.Add(requirement);
        await db.SaveChangesAsync();

        using var verifyDb = f.CreateDbContext();
        var saved = await verifyDb.Requirements.FindAsync(requirement.Id);
        Assert.NotNull(saved);
        Assert.Equal(ids.Feature1Id, saved!.FeatureId);
        Assert.Equal(ids.Task1Id, saved.TaskItemId);
    }

    [Fact]
    public async Task Persisting_RawZeroFeatureId_ViolatesForeignKey()
    {
        // Demonstrates the bug the normalization prevents: a RAW FeatureId=0 (no normalization) is
        // not a valid Feature id, so real SQLite rejects the insert. This is precisely the 500 the
        // old create path produced.
        using var f = new SqliteTestFactory();
        var ids = SeedTwoProjects(f);
        using var db = f.CreateDbContext();

        db.Requirements.Add(new Requirement
        {
            ProjectId = ids.Project1Id,
            Text = "Bad raw feature link",
            FeatureId = 0
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Persisting_RawZeroTaskItemId_ViolatesForeignKey()
    {
        using var f = new SqliteTestFactory();
        var ids = SeedTwoProjects(f);
        using var db = f.CreateDbContext();

        db.Requirements.Add(new Requirement
        {
            ProjectId = ids.Project1Id,
            Text = "Bad raw task link",
            TaskItemId = 0
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Update_NormalizedZeroClearsExistingLinks_NoFkViolation()
    {
        // The update path also routes through NormalizeLink. Start with real links, then "clear"
        // them with the 0 sentinel and confirm they persist as null (not a raw-0 FK violation).
        using var f = new SqliteTestFactory();
        var ids = SeedTwoProjects(f);

        using (var seed = f.CreateDbContext())
        {
            var req = await seed.Requirements.FindAsync(ids.Req1Id);
            req!.FeatureId = ids.Feature1Id;
            req.TaskItemId = ids.Task1Id;
            await seed.SaveChangesAsync();
        }

        using var db = f.CreateDbContext();
        var toUpdate = await db.Requirements.FindAsync(ids.Req1Id);
        // Same expression the PUT/PATCH handlers use after the fix.
        toUpdate!.FeatureId = ApiEndpoints.NormalizeLink(0);
        toUpdate.TaskItemId = ApiEndpoints.NormalizeLink(0);
        await db.SaveChangesAsync();

        using var verifyDb = f.CreateDbContext();
        var saved = await verifyDb.Requirements.FindAsync(ids.Req1Id);
        Assert.Null(saved!.FeatureId);
        Assert.Null(saved.TaskItemId);
    }
}
