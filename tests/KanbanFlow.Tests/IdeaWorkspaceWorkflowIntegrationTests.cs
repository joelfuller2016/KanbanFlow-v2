using System.Text.Json;
using KanbanFlow.Data;
using KanbanFlow.Models;
using KanbanFlow.Models.IdeaWorkspaces;
using KanbanFlow.Models.Workflows;
using KanbanFlow.Services.IdeaWorkspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace KanbanFlow.Tests;

public sealed class IdeaWorkspaceWorkflowIntegrationTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Actor = "Workspace capture author Ω";

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task GalleryCapture_PreservesExactOriginalBodyOrTitle_AndReceiptReplayHasNoCrossFamilyEffects(int bodyShape)
    {
        using var factory = new WorkflowSqliteTestFactory();
        var service = new IdeaWorkspaceService(factory);
        const string title = "  \tOriginal title Ω 文\r\n ";
        var body = bodyShape switch { 0 => null, 1 => "  \r\n\t ", _ => "  # Original Ω\r\n\r\n    indentation  \r\n" + new string('文', 30_000) + "\r\ncomplete final requirement  " };
        const string plan = "  Exact plan Ω\r\nretain final whitespace  ";
        var input = new IdeaWorkspaceCreate(WorkflowSqliteTestFactory.Key(), Actor, Title: title, Body: body, Plan: plan);

        var created = await service.CreateAsync(input);

        await using (var db = factory.CreateDbContext())
        {
            var idea = Assert.Single(await db.Ideas.ToListAsync());
            Assert.Equal(title.Trim(), idea.Title); Assert.Equal(body, idea.Body); Assert.Equal(plan, idea.Plan);
            var request = Assert.Single(await db.WorkRequests.Include(value => value.Messages).ToListAsync());
            Assert.Equal(idea.Id, request.IdeaId); Assert.Equal(1, request.Version); Assert.Null(request.ProjectId);
            var original = Assert.Single(request.Messages);
            Assert.Equal("original", original.Kind); Assert.Equal(Actor, original.Author);
            Assert.Equal("idea-workspace", original.Source);
            Assert.Equal(string.IsNullOrWhiteSpace(body) ? title : body, original.Text);
            Assert.Equal(created.IdeaId, request.IdeaId);
        }
        var beforeReplay = await Snapshot(factory);
        var replay = await service.CreateAsync(input);
        Assert.True(replay.Replayed); Assert.Equal(created with { Replayed = true }, replay);
        Assert.Equal(beforeReplay, await Snapshot(factory));
        var workflow = await factory.Service.GetForIdeaAsync(created.IdeaId!.Value);
        Assert.NotNull(workflow); Assert.Equal(string.IsNullOrWhiteSpace(body) ? title : body, Assert.Single(workflow.Messages).Text);
    }

    [Fact]
    public async Task GalleryCapture_FailingFinalReceipt_RollsBackIdeaWorkflowOriginalAuditAndWorkspace_AndSameOperationCanRetry()
    {
        var failure = new FailWorkspaceReceipt { Enabled = true };
        using var factory = new WorkflowSqliteTestFactory(failure);
        var service = new IdeaWorkspaceService(factory);
        var input = new IdeaWorkspaceCreate("rollback-capture", Actor, Title: "Exact title", Body: "  Complete original Ω\r\n ");
        var before = await Snapshot(factory);

        var error = await Assert.ThrowsAsync<IOException>(() => service.CreateAsync(input));

        Assert.Equal("Injected final workspace receipt failure", error.Message);
        Assert.Equal(before, await Snapshot(factory));
        failure.Enabled = false;
        var created = await service.CreateAsync(input);
        await using var db = factory.CreateDbContext();
        Assert.Single(await db.Ideas.ToListAsync()); Assert.Single(await db.WorkRequests.ToListAsync());
        Assert.Equal(input.Body, Assert.Single(await db.Set<RequestMessage>().ToListAsync()).Text);
        Assert.Single(await db.IdeaWorkspaceStates.ToListAsync()); Assert.Single(await db.IdeaWorkspaceReceipts.ToListAsync());
        Assert.False(created.Replayed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkspaceAssociation_UpdatesIdeaWorkspaceAndWorkflowVersionTogether_AndReplaysExactly(bool createProject)
    {
        using var factory = new WorkflowSqliteTestFactory();
        var service = new IdeaWorkspaceService(factory);
        var captured = await Capture(factory);
        var key = "idea:" + captured.IdeaId;
        var target = createProject ? (int?)null : await AddProject(factory, "Existing destination");
        var command = await Association(service, key, target);
        var original = Assert.Single(captured.Messages);

        var result = await service.ExecuteAsync(key, command);

        var current = await factory.LoadAsync(captured.Id);
        Assert.NotNull(current.ProjectId); Assert.Equal(captured.Version + 1, current.Version);
        if (target is not null) Assert.Equal(target, current.ProjectId);
        Assert.Equal(original.Text, Assert.Single(current.Messages).Text);
        Assert.Equal(original.Source, Assert.Single(current.Messages).Source);
        var context = await service.GetAsync(key);
        Assert.Equal(current.ProjectId, context.ProjectId); Assert.Equal(current.ProjectId, result.ProjectId);
        await using (var db = factory.CreateDbContext())
        {
            var idea = await db.Ideas.SingleAsync(value => value.Id == captured.IdeaId);
            Assert.Equal(IdeaStatus.Promoted, idea.Status); Assert.Equal(current.ProjectId, idea.PromotedProjectId);
            Assert.Equal(current.ProjectId, (await db.IdeaWorkspaceStates.SingleAsync(value => value.WorkspaceId == key)).LinkedProjectId);
        }
        var beforeReplay = await Snapshot(factory);
        var replay = await service.ExecuteAsync(key, command);
        Assert.True(replay.Replayed); Assert.Equal(result with { Replayed = true }, replay);
        Assert.Equal(beforeReplay, await Snapshot(factory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitGraphFreeRelink_AdvancesWorkflowVersionOnlyWhenProjectIdentityChanges(bool sameProject)
    {
        using var factory = new WorkflowSqliteTestFactory();
        var service = new IdeaWorkspaceService(factory);
        var captured = await Capture(factory);
        var key = "idea:" + captured.IdeaId;
        var first = await AddProject(factory, "First project");
        var second = sameProject ? first : await AddProject(factory, "Second project");
        await service.ExecuteAsync(key, await Association(service, key, first));
        var before = await factory.LoadAsync(captured.Id);

        await service.ExecuteAsync(key, await Association(service, key, second, first));

        var after = await factory.LoadAsync(captured.Id);
        Assert.Equal(second, after.ProjectId);
        Assert.Equal(before.Version + (sameProject ? 0 : 1), after.Version);
        Assert.Equal(second, (await service.GetAsync(key)).ProjectId);
        Assert.Equal(Assert.Single(before.Messages).Text, Assert.Single(after.Messages).Text);
    }

    [Theory]
    [InlineData("feature")]
    [InlineData("published-task")]
    [InlineData("target-feature-with-foreign-task")]
    public async Task ForeignWorkflowGraph_RejectsWorkspaceRelinkWithoutAnyPartialProjectVersionAuditOrReceipt(string graph)
    {
        using var factory = new WorkflowSqliteTestFactory();
        var service = new IdeaWorkspaceService(factory);
        var captured = graph == "feature" ? await Capture(factory) : await factory.PublishedAsync(WorkflowSqliteTestFactory.Draft("Published workflow task"));
        var target = await AddProject(factory, "Rejected destination");
        await using (var db = factory.CreateDbContext())
        {
            var request = await db.WorkRequests.SingleAsync(value => value.Id == captured.Id);
            if (graph == "feature")
            {
                var foreignProject = new Project { Name = "Foreign feature project" }; db.Projects.Add(foreignProject); await db.SaveChangesAsync();
                var feature = new Feature { ProjectId = foreignProject.Id, Name = "Existing workflow feature" }; db.Features.Add(feature); await db.SaveChangesAsync();
                request.ProjectId = foreignProject.Id; request.FeatureId = feature.Id;
            }
            else if (graph == "published-task") request.FeatureId = null; // Independently exercise mapped-task protection.
            else
            {
                var targetFeature = new Feature { ProjectId = target, Name = "Feature already in target" }; db.Features.Add(targetFeature); await db.SaveChangesAsync();
                request.FeatureId = targetFeature.Id; // A compatible feature cannot excuse a foreign mapped task.
            }
            await db.SaveChangesAsync();
        }
        var key = "idea:" + captured.IdeaId;
        var context = await service.GetAsync(key);
        var command = await Association(service, key, target, context.ProjectId);
        var before = await Snapshot(factory);

        var error = await Assert.ThrowsAsync<IdeaWorkspaceException>(() => service.ExecuteAsync(key, command));

        Assert.Equal(409, error.StatusCode); Assert.Equal("project_link_workflow_conflict", error.Code);
        Assert.Equal(before, await Snapshot(factory));
    }

    [Fact]
    public async Task ExistingPublishedWorkflow_CanKeepItsCurrentProjectWithoutChangingItsConcurrencyVersion()
    {
        using var factory = new WorkflowSqliteTestFactory();
        var captured = await factory.PublishedAsync(WorkflowSqliteTestFactory.Draft());
        var service = new IdeaWorkspaceService(factory); var key = "idea:" + captured.IdeaId;

        await service.ExecuteAsync(key, await Association(service, key, captured.ProjectId, captured.ProjectId));

        var after = await factory.LoadAsync(captured.Id);
        Assert.Equal(captured.ProjectId, after.ProjectId); Assert.Equal(captured.FeatureId, after.FeatureId);
        Assert.Equal(captured.Version, after.Version);
        Assert.Equal(Assert.Single(captured.WorkItems).TaskItemId, Assert.Single(after.WorkItems).TaskItemId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailingAssociationReceipt_RollsBackBothFamiliesAndNewProject_ThenSameEnvelopeRetriesOnce(bool createProject)
    {
        var failure = new FailWorkspaceReceipt();
        using var factory = new WorkflowSqliteTestFactory(failure);
        var service = new IdeaWorkspaceService(factory); var captured = await Capture(factory); var key = "idea:" + captured.IdeaId;
        var target = createProject ? (int?)null : await AddProject(factory, "Existing destination");
        var command = await Association(service, key, target);
        var before = await Snapshot(factory); failure.Enabled = true;

        await Assert.ThrowsAsync<IOException>(() => service.ExecuteAsync(key, command));

        Assert.Equal(before, await Snapshot(factory));
        failure.Enabled = false;
        var result = await service.ExecuteAsync(key, command);
        var after = await factory.LoadAsync(captured.Id);
        Assert.Equal(captured.Version + 1, after.Version); Assert.Equal(result.ProjectId, after.ProjectId);
        await using var db = factory.CreateDbContext();
        Assert.Single(await db.Projects.ToListAsync()); Assert.Single(await db.WorkRequests.ToListAsync());
        Assert.Single(await db.IdeaWorkspaceReceipts.ToListAsync());
    }

    private static async Task<WorkRequest> Capture(WorkflowSqliteTestFactory factory)
    {
        var receipt = await factory.Service.CaptureAsync(new("Workflow idea", "  Exact workflow original Ω\r\nretain final line  ", null, "integration-test"), Actor, WorkflowSqliteTestFactory.Key());
        return await factory.LoadAsync(receipt.RequestId);
    }
    private static async Task<int> AddProject(WorkflowSqliteTestFactory factory, string name)
    { await using var db = factory.CreateDbContext(); var project = new Project { Name = name }; db.Projects.Add(project); await db.SaveChangesAsync(); return project.Id; }
    private static async Task<IdeaWorkspaceCommand> Association(IdeaWorkspaceService service, string key, int? target, int? expectedCurrent = null)
        => new(WorkflowSqliteTestFactory.Key(), (await service.GetAsync(key)).Revision, target is null ? "create_project" : "link_project",
            JsonSerializer.SerializeToElement(target is null ? (object)new { name = "Workspace-created project" } : new { projectId = target, expectedCurrentProjectId = expectedCurrent }, Json), Actor);

    private static async Task<string> Snapshot(WorkflowSqliteTestFactory factory)
    {
        await using var db = factory.CreateDbContext();
        return JsonSerializer.Serialize(new
        {
            Ideas = await db.Ideas.AsNoTracking().OrderBy(value => value.Id).Select(value => new { value.Id, value.Title, value.Body, value.Plan, value.Status, value.PromotedProjectId, value.UpdatedAt }).ToArrayAsync(),
            Requests = await db.WorkRequests.AsNoTracking().OrderBy(value => value.Id).Select(value => new { value.Id, value.IdeaId, value.ProjectId, value.FeatureId, value.Version, value.UpdatedAt }).ToArrayAsync(),
            Messages = await db.Set<RequestMessage>().AsNoTracking().OrderBy(value => value.Id).Select(value => new { value.Id, value.RequestId, value.Kind, value.Text, value.Author, value.Source }).ToArrayAsync(),
            Projects = await db.Projects.AsNoTracking().OrderBy(value => value.Id).Select(value => new { value.Id, value.Name, value.Description, value.Goals, value.Readme }).ToArrayAsync(),
            States = await db.IdeaWorkspaceStates.AsNoTracking().OrderBy(value => value.WorkspaceId).ToArrayAsync(),
            Receipts = await db.IdeaWorkspaceReceipts.AsNoTracking().OrderBy(value => value.WorkspaceId).ThenBy(value => value.OperationId).ToArrayAsync(),
            WorkflowReceipts = await db.WorkflowReceipts.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(),
            Activity = await db.ActivityEvents.AsNoTracking().OrderBy(value => value.Id).Select(value => new { value.Id, value.EntityType, value.EntityId, value.EventType, value.ProjectId, value.BeforeJson, value.AfterJson, value.Actor }).ToArrayAsync(),
            Features = await db.Features.AsNoTracking().OrderBy(value => value.Id).Select(value => new { value.Id, value.ProjectId, value.Name }).ToArrayAsync(),
            Tasks = await db.Tasks.AsNoTracking().OrderBy(value => value.Id).Select(value => new { value.Id, value.FeatureId, value.Name }).ToArrayAsync(),
            WorkItems = await db.Set<PlannedTask>().AsNoTracking().OrderBy(value => value.Id).Select(value => new {
                value.Id, value.RequestId, value.PlanVersionId, value.DraftId, value.TaskItemId,
                value.SpecificationVersion, value.SpecificationHash, value.SpecificationJson }).ToArrayAsync()
        }, Json);
    }

    private sealed class FailWorkspaceReceipt : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && eventData.Context!.ChangeTracker.Entries<IdeaWorkspaceReceipt>().Any(value => value.State == EntityState.Added))
                throw new IOException("Injected final workspace receipt failure");
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
