using KanbanFlow;
using KanbanFlow.Models;

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
}
