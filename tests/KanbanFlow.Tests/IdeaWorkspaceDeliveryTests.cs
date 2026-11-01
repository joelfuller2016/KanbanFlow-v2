using System.Text.Json;
using System.Text.Json.Nodes;
using KanbanFlow.Data;
using KanbanFlow.Models;
using KanbanFlow.Models.IdeaWorkspaces;
using KanbanFlow.Services;
using KanbanFlow.Services.IdeaWorkspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Tests;

public sealed class IdeaWorkspaceDeliveryTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Actor = "delivery-client";
    private const string Description = "\r\n  ## Complete project description Ω\r\n\r\n    preserve indentation  \r\n";
    private const string Goals = "  ## Complete project goals\r\n\r\n- Final goal Ω  \r\n";
    private const string Body = "\r\n  # Original idea Ω\r\n\r\n    complete authored brief  \r\n";
    private const string Plan = "  ## Original idea plan\r\n\r\n- Preserve the last requirement  \r\n";

    [Fact]
    public async Task LinkProject_CapturesBothCompleteSources_WithoutReplacingLegacyDocuments()
    {
        using var factory = new SqliteTestFactory();
        var readme = "\r\n  # Project readme Ω\r\n" + new string('文', 110_000) + "\r\nFINAL README REQUIREMENT  \r\n";
        var projectId = await SeedProject(factory, readme: readme);
        var ideaId = await SeedIdea(factory);
        var service = new IdeaWorkspaceService(factory);
        var key = $"idea:{ideaId}";

        var result = await Change(service, key, "link_project", new { projectId });

        Assert.Equal(projectId, result.ProjectId);
        var context = await service.GetAsync(key);
        Assert.Equal(key, context.WorkspaceId);
        Assert.Equal(projectId, context.ProjectId);
        Assert.Equal($"project:{projectId}", context.AcceptedPlanWorkspaceId);
        Assert.Equal($"/project/{projectId}", context.KanbanUrl);
        Assert.Equal(Body, context.Source!.Body);
        Assert.Equal(Plan, context.Source.Plan);
        Assert.Equal(Body, context.OriginalSource.Body);
        Assert.Equal(Description, context.OriginalProjectSource!.Body);
        Assert.Equal(Goals, context.OriginalProjectSource.Plan);
        Assert.Equal(readme, context.OriginalProjectSource.Readme);
        var target = await service.GetAsync($"project:{projectId}");
        Assert.Equal(readme, target.OriginalSource.Readme);
        await using var db = factory.CreateDbContext();
        var idea = Assert.Single(await db.Ideas.ToListAsync());
        Assert.Equal(projectId, idea.PromotedProjectId);
        Assert.Equal(IdeaStatus.Promoted, idea.Status);
        Assert.Equal(Body, idea.Body);
        Assert.Equal(Plan, idea.Plan);
        var project = Assert.Single(await db.Projects.ToListAsync());
        Assert.Equal(Description, project.Description);
        Assert.Equal(Goals, project.Goals);
        Assert.Equal(readme, project.Readme);
        Assert.Equal(2, await db.IdeaWorkspaceStates.CountAsync());
        Assert.Contains(await db.ActivityEvents.ToListAsync(), item => item.Actor == Actor && item.EntityType == "Idea");
    }

    [Theory]
    [InlineData("add_comment")]
    [InlineData("create_component")]
    [InlineData("propose_plan")]
    public async Task LegacyLinkedIdea_FirstWorkspaceEdit_CapturesTheUntouchedProjectBeforeAcknowledging(string action)
    {
        using var factory = new SqliteTestFactory();
        var projectId = await SeedProject(factory);
        var ideaId = await SeedIdea(factory, projectId: projectId);
        var service = new IdeaWorkspaceService(factory);
        object payload = action switch
        {
            "add_comment" => new { text = "\r\n  First legacy workspace edit Ω  \r\n" },
            "create_component" => new { component = new { type = "note", title = "First edit", content = Body } },
            _ => new { title = "First legacy proposal", items = new[] { Item("step-1", "First step") } }
        };

        await Change(service, $"idea:{ideaId}", action, payload);

        var source = await service.GetAsync($"idea:{ideaId}");
        var target = await service.GetAsync($"project:{projectId}");
        Assert.Equal(Body, source.OriginalSource.Body);
        Assert.Equal(Description, source.OriginalProjectSource!.Body);
        Assert.Equal(Goals, target.OriginalSource.Plan);
        Assert.Equal("\r\n  # Saved project readme Ω  \r\n", target.OriginalSource.Readme);
        await using var db = factory.CreateDbContext();
        Assert.Equal(2, await db.IdeaWorkspaceStates.CountAsync());
        Assert.Equal(Description, (await db.Projects.SingleAsync()).Description);
    }

    [Fact]
    public async Task LegacyPromotionAfterWorkspaceCreation_RetainsTheObservedProjectLinkAndSnapshotAfterDeletion()
    {
        using var factory = new SqliteTestFactory();
        var ideaId = await SeedIdea(factory);
        var key = $"idea:{ideaId}";
        var fullBrief = "\r\n  # Legacy promoted brief Ω\r\n\r\n    keep all source context  \r\n" + new string('文', 105_000) + "\r\nFINAL PROMOTED REQUIREMENT  \r\n";
        await using (var db = factory.CreateDbContext())
        {
            (await db.Ideas.SingleAsync()).Body = fullBrief;
            await db.SaveChangesAsync();
        }
        var service = new IdeaWorkspaceService(factory);
        await Change(service, key, "add_comment", new { text = "Workspace existed before legacy promotion" });
        await using (var db = factory.CreateDbContext()) Assert.Null((await db.IdeaWorkspaceStates.SingleAsync()).LinkedProjectId);
        var promoted = await new IdeaService(factory).PromoteAsync(ideaId, "legacy-client");
        string readme;
        await using (var db = factory.CreateDbContext()) readme = (await db.Projects.SingleAsync(item => item.Id == promoted.ProjectId)).Readme!;
        Assert.True(readme.Length > 100_000);

        await Change(service, key, "create_component", new { component = new { id = "after-promotion", type = "note", title = "First edit after promotion", content = "Retain the observed target Ω" } });

        await using (var db = factory.CreateDbContext())
            Assert.Equal(promoted.ProjectId, (await db.IdeaWorkspaceStates.SingleAsync(item => item.WorkspaceId == key)).LinkedProjectId);
        var linked = await service.GetAsync(key);
        Assert.Equal(readme, linked.OriginalProjectSource!.Readme);
        Assert.Equal(fullBrief, linked.OriginalSource.Body);
        await new TaskService(factory).DeleteProjectAsync(promoted.ProjectId, "human-owner");
        await using (var db = factory.CreateDbContext()) Assert.Null((await db.Ideas.SingleAsync()).PromotedProjectId);
        var missing = await service.GetAsync(key);
        Assert.Equal(promoted.ProjectId, missing.ProjectId);
        Assert.True(missing.ProjectMissing);
        Assert.Equal(readme, missing.OriginalProjectSource!.Readme);
        Assert.Equal(fullBrief, missing.OriginalSource.Body);
        Assert.Contains(missing.Document.Components, item => item.Id == "after-promotion");
        var recoveryTarget = await SeedProject(factory, "Explicit legacy-link recovery");
        var beforeRelink = await Snapshot(factory);
        var conflict = await Assert.ThrowsAsync<IdeaWorkspaceException>(() => Change(service, key, "link_project", new { projectId = recoveryTarget }));
        Assert.Equal(409, conflict.StatusCode);
        Assert.Equal("project_link_conflict", conflict.Code);
        Assert.Equal(beforeRelink, await Snapshot(factory));
        await Change(service, key, "link_project", new { projectId = recoveryTarget, expectedCurrentProjectId = promoted.ProjectId });
        Assert.Equal(recoveryTarget, (await service.GetAsync(key)).ProjectId);
        Assert.Equal(readme, (await service.GetAsync($"project:{promoted.ProjectId}")).OriginalSource.Readme);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Association_CapturesNewObservedProjectDocuments_WithoutReplacingOriginalOrAppendingOnReadsAndReplay(bool failReceiptFirst)
    {
        using var factory = new SqliteTestFactory();
        var oldDescription = "\r\n  # OLD description Ω\r\n" + new string('旧', 50_000) + "\r\nOLD DESCRIPTION END  \r\n";
        var oldGoals = "  ## OLD goals Ω\r\n" + new string('目', 55_000) + "\r\nOLD GOALS END  ";
        var oldReadme = "\r\n  # OLD readme Ω\r\n" + new string('古', 65_000) + "\r\nOLD README END  \r\n";
        var newDescription = "\r\n  # NEW description Ω\r\n" + new string('新', 70_000) + "\r\nNEW DESCRIPTION END  \r\n";
        var newGoals = "  ## NEW goals Ω\r\n" + new string('標', 75_000) + "\r\nNEW GOALS END  ";
        var newReadme = "\r\n  # NEW readme Ω\r\n" + new string('文', 85_000) + "\r\nNEW README END  \r\n";
        var projectId = await SeedProject(factory, readme: oldReadme);
        await using (var db = factory.CreateDbContext())
        {
            var project = await db.Projects.SingleAsync();
            project.Description = oldDescription;
            project.Goals = oldGoals;
            await db.SaveChangesAsync();
        }
        var service = new IdeaWorkspaceService(factory);
        var target = $"project:{projectId}";
        await Change(service, target, "add_comment", new { text = "Original project documents observed by the module" });
        await Accept(service, target, Item("step-1", "Read-only history preview"));
        await using (var db = factory.CreateDbContext())
        {
            var project = await db.Projects.SingleAsync();
            project.Description = newDescription;
            project.Goals = newGoals;
            project.Readme = newReadme;
            project.UpdatedAt = project.UpdatedAt.AddMinutes(1);
            await db.SaveChangesAsync();
        }
        var key = $"idea:{await SeedIdea(factory, "New association after legacy source edit")}";
        var request = Command(0, "link_project", new { projectId }, "capture-current-history");
        if (failReceiptFirst)
        {
            await using (var db = factory.CreateDbContext())
                await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER RejectHistoryReceipt BEFORE INSERT ON IdeaWorkspaceReceipts WHEN NEW.OperationId = 'capture-current-history' BEGIN SELECT RAISE(ABORT, 'history receipt rejected'); END;");
            var beforeFailure = await Snapshot(factory);
            var failure = await Assert.ThrowsAsync<DbUpdateException>(() => service.ExecuteAsync(key, request));
            Assert.Contains("history receipt rejected", Assert.IsType<SqliteException>(failure.InnerException).Message, StringComparison.Ordinal);
            Assert.Equal(beforeFailure, await Snapshot(factory));
            await using var recovery = factory.CreateDbContext();
            await recovery.Database.ExecuteSqlRawAsync("DROP TRIGGER RejectHistoryReceipt;");
        }

        await service.ExecuteAsync(key, request);

        var context = await service.GetAsync(key);
        Assert.Equal(oldDescription, context.OriginalProjectSource!.Body);
        Assert.Equal(oldGoals, context.OriginalProjectSource.Plan);
        Assert.Equal(oldReadme, context.OriginalProjectSource.Readme);
        var observed = Assert.Single(context.ProjectSourceSnapshots, item => item.Source.Readme == newReadme);
        Assert.Equal(newDescription, observed.Source.Body);
        Assert.Equal(newGoals, observed.Source.Plan);
        Assert.Equal(Actor, observed.Actor);
        Assert.False(string.IsNullOrWhiteSpace(observed.Id));
        await using (var db = factory.CreateDbContext())
        {
            var activity = await db.ActivityEvents.Where(item => item.EntityType == "ProjectWorkspace" && item.EntityId == projectId && item.Message == "link_project").OrderByDescending(item => item.Id).FirstAsync();
            using var after = JsonDocument.Parse(activity.AfterJson!);
            var captured = after.RootElement.GetProperty("document").GetProperty("sourceSnapshots").EnumerateArray()
                .Single(item => item.GetProperty("id").GetString() == observed.Id).GetProperty("source");
            Assert.Equal(newDescription, captured.GetProperty("body").GetString());
            Assert.Equal(newGoals, captured.GetProperty("plan").GetString());
            Assert.Equal(newReadme, captured.GetProperty("readme").GetString());
        }
        var count = (await service.GetAsync(target)).Document.SourceSnapshots.Count;
        await Change(service, target, "add_comment", new { text = "Unchanged source observed again" });
        Assert.Equal(count, (await service.GetAsync(target)).Document.SourceSnapshots.Count);
        var beforeReads = await Snapshot(factory);
        await service.GetAsync(key);
        await service.GetAsync(target);
        await Preview(service, key);
        Assert.True((await service.ExecuteAsync(key, request)).Replayed);
        Assert.Equal(beforeReads, await Snapshot(factory));

        await new TaskService(factory).DeleteProjectAsync(projectId, "human-owner");

        var missing = await service.GetAsync(key);
        Assert.True(missing.ProjectMissing);
        Assert.Equal(oldDescription, missing.OriginalProjectSource!.Body);
        Assert.Equal(oldGoals, missing.OriginalProjectSource.Plan);
        Assert.Equal(oldReadme, missing.OriginalProjectSource.Readme);
        var retained = Assert.Single(missing.ProjectSourceSnapshots, item => item.Id == observed.Id);
        Assert.Equal(newDescription, retained.Source.Body);
        Assert.Equal(newGoals, retained.Source.Plan);
        Assert.Equal(newReadme, retained.Source.Readme);
        Assert.Equal(newReadme, Assert.Single((await service.GetAsync(target)).Document.SourceSnapshots, item => item.Id == observed.Id).Source.Readme);
    }

    [Fact]
    public async Task ObservedIdeaSourceEdit_SurvivesSourceDeletion_WithOriginalAndLatestObservedTextSeparate()
    {
        using var factory = new SqliteTestFactory();
        var ideaId = await SeedIdea(factory);
        var key = $"idea:{ideaId}";
        var service = new IdeaWorkspaceService(factory);
        await Change(service, key, "add_comment", new { text = "First source observation" });
        var observedBody = "\r\n  # Later observed idea Ω\r\n" + new string('観', 100_000) + "\r\nOBSERVED BODY END  \r\n";
        var observedPlan = "  ## Later observed plan Ω\r\n" + new string('計', 80_000) + "\r\nOBSERVED PLAN END  ";
        await using (var db = factory.CreateDbContext())
        {
            var idea = await db.Ideas.SingleAsync();
            idea.Body = observedBody;
            idea.Plan = observedPlan;
            idea.UpdatedAt = idea.UpdatedAt.AddMinutes(1);
            await db.SaveChangesAsync();
        }

        await Change(service, key, "add_comment", new { text = "Module write observes the later source" });

        var context = await service.GetAsync(key);
        Assert.Equal(Body, context.OriginalSource.Body);
        Assert.Equal(Plan, context.OriginalSource.Plan);
        var observed = Assert.Single(context.Document.SourceSnapshots, item => item.Source.Body == observedBody);
        Assert.Equal(observedPlan, observed.Source.Plan);
        var count = context.Document.SourceSnapshots.Count;
        await Change(service, key, "add_comment", new { text = "The same source must not produce duplicate history" });
        Assert.Equal(count, (await service.GetAsync(key)).Document.SourceSnapshots.Count);
        var unobserved = "\r\n  External text never observed by a module write Ω  \r\n";
        await using (var db = factory.CreateDbContext())
        {
            (await db.Ideas.SingleAsync()).Body = unobserved;
            await db.SaveChangesAsync();
        }
        var beforeRead = await Snapshot(factory);
        var current = await service.GetAsync(key);
        Assert.Equal(unobserved, current.Source!.Body);
        Assert.Equal(count, current.Document.SourceSnapshots.Count);
        Assert.Equal(beforeRead, await Snapshot(factory));

        await new IdeaService(factory).DeleteAsync(ideaId, "human-owner");

        var missing = await service.GetAsync(key);
        Assert.True(missing.SourceMissing);
        Assert.Equal(Body, missing.OriginalSource.Body);
        Assert.Equal(Plan, missing.OriginalSource.Plan);
        var retained = Assert.Single(missing.Document.SourceSnapshots, item => item.Id == observed.Id);
        Assert.Equal(observedBody, retained.Source.Body);
        Assert.Equal(observedPlan, retained.Source.Plan);
        Assert.DoesNotContain(missing.Document.SourceSnapshots, item => item.Source.Body == unobserved);
    }

    [Theory]
    [InlineData("step-1", true)]
    [InlineData("step-2", false)]
    public async Task PrimaryProjectAcceptance_DistinguishesResolvedIdentityCollisionFromANewLocalOrigin(string newLocalId, bool collides)
    {
        using var factory = new SqliteTestFactory();
        var projectId = await SeedProject(factory);
        var key = $"project:{projectId}";
        var service = new IdeaWorkspaceService(factory);
        var originalText = "\r\n  Original unpublished specification Ω  \r\n";
        await Accept(service, key, Item("step-1", "Original unpublished task", originalText));
        var canonical = Assert.Single((await service.GetAsync(key)).AcceptedPlan!.Items);
        Assert.NotEqual("step-1", canonical.Id);
        var copied = Copy(canonical);
        var newText = "\r\n  Distinct authored addition Ω\r\n\r\n    must not replace the original  \r\n" + new string('新', 40_000) + "\r\nFINAL DISTINCT REQUIREMENT  \r\n";
        var added = Item(newLocalId, "Distinct new proposal item", newText);
        Assert.NotEqual(copied.Id, added.Id);
        var proposed = await Propose(service, key, copied, added);
        Assert.Equal(2, proposed.Items.Count);
        Assert.Equal(originalText, proposed.Items.Single(item => item.Id == copied.Id).Description);
        Assert.Equal(newText, proposed.Items.Single(item => item.Id == added.Id).Description);
        var beforeAccept = await Snapshot(factory);
        var context = await service.GetAsync(key);
        var request = Command(context.Revision, "accept_plan", new { planRevisionId = proposed.Id, expectedProjectPlanRevision = context.ProjectPlanRevision });

        if (collides)
        {
            var error = await Assert.ThrowsAsync<IdeaWorkspaceException>(() => service.ExecuteAsync(key, request));
            Assert.Equal(409, error.StatusCode);
            Assert.Equal("duplicate_canonical_identity", error.Code);
            Assert.Equal(beforeAccept, await Snapshot(factory));
            var retained = await service.GetAsync(key);
            Assert.Equal(canonical.Id, Assert.Single(retained.AcceptedPlan!.Items).Id);
            Assert.Equal(originalText, Assert.Single(retained.AcceptedPlan.Items).Description);
            Assert.Equal(newText, retained.Document.PlanRevisions.Single(item => item.Id == proposed.Id).Items.Single(item => item.Id == added.Id).Description);
        }
        else
        {
            await service.ExecuteAsync(key, request);
            var accepted = (await service.GetAsync(key)).AcceptedPlan!;
            Assert.Equal(2, accepted.Items.Count);
            Assert.Equal(2, accepted.Items.Select(item => item.Provenance!.CanonicalItemId).Distinct().Count());
            Assert.Contains(accepted.Items, item => item.Description == originalText);
            Assert.Contains(accepted.Items, item => item.Description == newText);
        }
        await using var verify = factory.CreateDbContext();
        Assert.Empty(await verify.Tasks.ToListAsync());
        Assert.Empty(await verify.Features.ToListAsync());
    }

    [Fact]
    public async Task CreateProject_ReplaysOneIdentity_AndPreservesFullIdeaDocumentsAndPriorCanvas()
    {
        using var factory = new SqliteTestFactory();
        var ideaId = await SeedIdea(factory);
        var service = new IdeaWorkspaceService(factory);
        var key = $"idea:{ideaId}";
        await Change(service, key, "create_component", new { component = new { id = "retained-card", type = "markdown", title = "Exploration", content = Body } });
        var request = Command((await service.GetAsync(key)).Revision, "create_project", new { name = "Delivery project Ω" }, "create-project-once");

        var first = await service.ExecuteAsync(key, request);
        var beforeReplay = await Snapshot(factory);
        var replay = await service.ExecuteAsync(key, request);

        Assert.True(replay.Replayed);
        Assert.NotNull(first.ProjectId);
        Assert.Equal(first.ProjectId, replay.ProjectId);
        Assert.Equal(first.Revision, replay.Revision);
        Assert.Equal(beforeReplay, await Snapshot(factory));
        var context = await service.GetAsync(key);
        Assert.Equal(Body, context.OriginalSource.Body);
        Assert.Equal(Plan, context.OriginalSource.Plan);
        Assert.Contains(context.Document.Components, item => item.Id == "retained-card" && item.Content == Body);
        await using var db = factory.CreateDbContext();
        var project = Assert.Single(await db.Projects.ToListAsync());
        Assert.Equal("Delivery project Ω", project.Name);
        Assert.Equal(Body, project.Description);
        Assert.Equal(Plan, project.Goals);
        Assert.Contains(Body, project.Readme!, StringComparison.Ordinal);
        Assert.Contains(Plan, project.Readme!, StringComparison.Ordinal);
        Assert.Equal(first.ProjectId, (await db.Ideas.SingleAsync()).PromotedProjectId);
    }

    [Fact]
    public async Task ExplicitRelink_RequiresTheCurrentTarget_AndRetainsOldProjectHistory()
    {
        using var factory = new SqliteTestFactory();
        var firstProject = await SeedProject(factory, "Original target");
        var nextProject = await SeedProject(factory, "New target");
        var service = new IdeaWorkspaceService(factory);
        var key = $"idea:{await SeedIdea(factory)}";
        await Change(service, key, "link_project", new { projectId = firstProject });
        await Accept(service, key, Item("step-1", "Original delivery"));
        var oldMapping = Assert.Single((await Publish(service, key)).Publication!.Items);
        var oldTask = await TaskSnapshot(factory, oldMapping.TaskId);
        var oldCanonical = await service.GetAsync($"project:{firstProject}");
        var before = await Snapshot(factory);

        await Assert.ThrowsAsync<IdeaWorkspaceException>(() => Change(service, key, "link_project", new { projectId = nextProject }));
        Assert.Equal(before, await Snapshot(factory));
        await Assert.ThrowsAsync<IdeaWorkspaceException>(() => Change(service, key, "link_project", new { projectId = nextProject, expectedCurrentProjectId = firstProject + 1000 }));
        Assert.Equal(before, await Snapshot(factory));
        await Change(service, key, "link_project", new { projectId = nextProject, expectedCurrentProjectId = firstProject });

        var current = await service.GetAsync(key);
        Assert.Equal(nextProject, current.ProjectId);
        Assert.Equal(Body, current.OriginalSource.Body);
        Assert.NotEmpty(current.Document.PlanRevisions);
        Assert.Equal(oldCanonical.Document.AcceptedPlanRevisionId, (await service.GetAsync($"project:{firstProject}")).Document.AcceptedPlanRevisionId);
        Assert.Equal("New target", current.OriginalProjectSource!.Title);
        await Accept(service, key, Item("step-1", "Delivery for the explicitly selected new project"));
        var newMapping = Assert.Single((await Publish(service, key)).Publication!.Items);
        Assert.NotEqual(oldMapping.TaskId, newMapping.TaskId);
        Assert.Equal(oldTask, await TaskSnapshot(factory, oldMapping.TaskId));
        Assert.Equal(oldMapping.TaskId, Assert.Single((await service.GetAsync($"project:{firstProject}")).Document.Publications).TaskId);
        Assert.Equal(newMapping.TaskId, Assert.Single((await service.GetAsync($"project:{nextProject}")).Document.Publications).TaskId);
        await using var db = factory.CreateDbContext();
        Assert.Equal(nextProject, (await db.Ideas.SingleAsync()).PromotedProjectId);
        Assert.Equal(2, await db.Projects.CountAsync());
        Assert.Equal(2, await db.Tasks.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RelinkProject_WithForeignOrMissingTaskReferences_ConflictsUntilExplicitConversion(bool missingTasks)
    {
        using var factory = new SqliteTestFactory();
        var (service, key, originalProject) = await Linked(factory);
        var nextProject = await SeedProject(factory, "Explicit new delivery target");
        await Accept(service, key, Item("a", "First existing task"), Item("b", "Second existing task"));
        var publication = (await Publish(service, key)).Publication!;
        var cards = publication.Items.Select((item, index) => new IdeaWorkspaceComponent
        {
            Id = $"existing-task-card-{index}", Type = "task-reference", TaskId = item.TaskId,
            Title = $"  Complete authored card {index} Ω  ",
            Content = $"\r\n  # Full card {index} context Ω\r\n\r\n    preserve all narrative  \r\n"
        }).ToArray();
        foreach (var card in cards)
            await Change(service, key, "create_component", new { component = card });
        var oldHistory = JsonSerializer.Serialize((await service.GetAsync($"project:{originalProject}")).Document, Json);
        if (missingTasks)
        {
            await using var db = factory.CreateDbContext();
            db.Tasks.RemoveRange(await db.Tasks.ToListAsync());
            await db.SaveChangesAsync();
        }
        var original = await service.GetAsync(key);
        var before = await Snapshot(factory);

        var error = await Assert.ThrowsAsync<IdeaWorkspaceException>(() => Change(service, key, "link_project",
            new { projectId = nextProject, expectedCurrentProjectId = originalProject }, "blocked-reference-relink"));

        Assert.Equal(409, error.StatusCode);
        Assert.Equal("project_link_task_conflict", error.Code);
        foreach (var card in cards) Assert.Contains(card.Id, error.Message, StringComparison.Ordinal);
        Assert.Equal(before, await Snapshot(factory));
        var unchanged = await service.GetAsync(key);
        Assert.Equal(originalProject, unchanged.ProjectId);
        Assert.Equal(original.Revision, unchanged.Revision);
        Assert.Equal(JsonSerializer.Serialize(original.Document, Json), JsonSerializer.Serialize(unchanged.Document, Json));

        foreach (var card in cards)
            await Change(service, key, "update_component", new { componentId = card.Id, type = "note", taskId = (int?)null });
        await Change(service, key, "link_project", new { projectId = nextProject, expectedCurrentProjectId = originalProject });

        var linked = await service.GetAsync(key);
        Assert.Equal(nextProject, linked.ProjectId);
        Assert.Equal(Body, linked.OriginalSource.Body);
        Assert.Equal(Plan, linked.OriginalSource.Plan);
        foreach (var card in cards)
        {
            var retained = Assert.Single(linked.Document.Components, item => item.Id == card.Id);
            Assert.Equal("note", retained.Type);
            Assert.Null(retained.TaskId);
            Assert.Equal(card.Title, retained.Title);
            Assert.Equal(card.Content, retained.Content);
        }
        Assert.Equal(JsonSerializer.Serialize(original.Document.PlanRevisions, Json), JsonSerializer.Serialize(linked.Document.PlanRevisions, Json));
        Assert.Equal(oldHistory, JsonSerializer.Serialize((await service.GetAsync($"project:{originalProject}")).Document, Json));
        await using var verify = factory.CreateDbContext();
        var idea = await verify.Ideas.SingleAsync();
        Assert.Equal(nextProject, idea.PromotedProjectId);
        Assert.Equal(Body, idea.Body);
        Assert.Equal(Plan, idea.Plan);
        Assert.False(await verify.IdeaWorkspaceReceipts.AnyAsync(item => item.OperationId == "blocked-reference-relink"));
    }

    [Fact]
    public async Task LinkProject_ToTheCurrentTarget_PreservesValidTaskReferenceCards()
    {
        using var factory = new SqliteTestFactory();
        var (service, key, projectId) = await Linked(factory);
        await Accept(service, key, Item("a", "Task still belongs to this project"));
        var taskId = Assert.Single((await Publish(service, key)).Publication!.Items).TaskId;
        await Change(service, key, "create_component", new
        {
            component = new { id = "current-project-task", type = "task-reference", title = "  Valid task Ω  ", content = "\r\n  Retain this authored card  \r\n", taskId }
        });
        var before = await service.GetAsync(key);
        var card = Assert.Single(before.Document.Components, item => item.Id == "current-project-task");
        var task = await TaskSnapshot(factory, taskId);

        var result = await Change(service, key, "link_project", new { projectId });

        Assert.Equal(projectId, result.ProjectId);
        Assert.Equal(before.Revision + 1, result.Revision);
        var after = await service.GetAsync(key);
        Assert.Equal(projectId, after.ProjectId);
        Assert.Equal(JsonSerializer.Serialize(card, Json), JsonSerializer.Serialize(Assert.Single(after.Document.Components, item => item.Id == card.Id), Json));
        Assert.Equal(task, await TaskSnapshot(factory, taskId));
        Assert.Equal(taskId, Assert.Single((await service.GetAsync($"project:{projectId}")).Document.Publications).TaskId);
    }

    [Fact]
    public async Task TwoIdeasUsingTheSameLocalItemId_ExpandOneCanonicalPlanWithDistinctProvenance()
    {
        using var factory = new SqliteTestFactory();
        var projectId = await SeedProject(factory);
        var service = new IdeaWorkspaceService(factory);
        var a = $"idea:{await SeedIdea(factory, "Idea A")}";
        var b = $"idea:{await SeedIdea(factory, "Idea B")}";
        await Change(service, a, "link_project", new { projectId });
        await Change(service, b, "link_project", new { projectId });

        await Accept(service, a, Item("step-1", "A first step"));
        await Accept(service, b, Item("step-1", "B first step"));

        var project = await service.GetAsync($"project:{projectId}");
        var accepted = Assert.IsType<IdeaWorkspacePlanRevision>(project.AcceptedPlan);
        Assert.Equal(2, accepted.Items.Count);
        var first = Assert.Single(accepted.Items, item => item.Title == "A first step");
        var second = Assert.Single(accepted.Items, item => item.Title == "B first step");
        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual("step-1", first.Id);
        Assert.Equal(new IdeaWorkspacePlanItemProvenance(projectId, a, "step-1", first.Id), first.Provenance);
        Assert.Equal(new IdeaWorkspacePlanItemProvenance(projectId, b, "step-1", second.Id), second.Provenance);
        Assert.Equal(accepted.Id, (await service.GetAsync(a)).AcceptedPlan!.Id);
        Assert.Equal(accepted.Id, (await service.GetAsync(b)).AcceptedPlan!.Id);
        Assert.Contains(project.Document.PlanRevisions, item => item.Status == "Superseded" && item.Items.Any(part => part.Title == "A first step"));
        await using var db = factory.CreateDbContext();
        Assert.Empty(await db.Tasks.ToListAsync());
    }

    [Fact]
    public async Task StaleCanonicalAcceptance_ConflictsEvenWhenTheOtherIdeasLocalRevisionIsCurrent()
    {
        using var factory = new SqliteTestFactory();
        var projectId = await SeedProject(factory);
        var service = new IdeaWorkspaceService(factory);
        var a = $"idea:{await SeedIdea(factory, "Idea A")}";
        var b = $"idea:{await SeedIdea(factory, "Idea B")}";
        await Change(service, a, "link_project", new { projectId });
        await Change(service, b, "link_project", new { projectId });
        var draftA = await Propose(service, a, Item("a", "A proposal"));
        var draftB = await Propose(service, b, Item("b", "B proposal"));
        var oldB = await service.GetAsync(b);
        await Change(service, a, "accept_plan", new { planRevisionId = draftA.Id, expectedProjectPlanRevision = oldB.ProjectPlanRevision });
        var before = await Snapshot(factory);

        var error = await Assert.ThrowsAsync<IdeaWorkspaceException>(() => service.ExecuteAsync(b,
            Command(oldB.Revision, "accept_plan", new { planRevisionId = draftB.Id, expectedProjectPlanRevision = oldB.ProjectPlanRevision })));

        Assert.Equal(409, error.StatusCode);
        Assert.Equal("project_plan_revision_conflict", error.Code);
        Assert.Equal(before, await Snapshot(factory));
        Assert.Equal(oldB.Revision, (await service.GetAsync(b)).Revision);
        Assert.Contains((await service.GetAsync(b)).Document.PlanRevisions, item => item.Id == draftB.Id && item.Status == "Draft");
        await Change(service, b, "accept_plan", new { planRevisionId = draftB.Id, expectedProjectPlanRevision = (await service.GetAsync(b)).ProjectPlanRevision });
        Assert.Equal(2, (await service.GetAsync(b)).AcceptedPlan!.Items.Count);
    }

    [Fact]
    public async Task PreviewIsReadOnly_AndSubsetPublicationRequiresItsUnpublishedPredecessor()
    {
        using var factory = new SqliteTestFactory();
        var (service, key, projectId) = await Linked(factory);
        await Accept(service, key, Item("a", "Predecessor"), Item("b", "Dependent", dependencies: ["a"]), Item("c", "Independent"));
        var accepted = (await service.GetAsync(key)).AcceptedPlan!;
        var a = accepted.Items.Single(item => item.Title == "Predecessor");
        var b = accepted.Items.Single(item => item.Title == "Dependent");
        var before = await Snapshot(factory);

        var error = await Assert.ThrowsAsync<IdeaWorkspaceException>(() => Preview(service, key, [b.Id]));
        Assert.Equal("missing_publication_dependency", error.Code);
        Assert.Equal(before, await Snapshot(factory));
        var preview = await Preview(service, key, [a.Id]);
        Assert.Equal(1, preview.NewTaskCount);
        Assert.Equal(a.Id, Assert.Single(preview.Items).CanonicalItemId);
        Assert.Equal(before, await Snapshot(factory));
        var first = (await Publish(service, key, [a.Id])).Publication!;
        var secondPreview = await Preview(service, key, [b.Id]);
        Assert.Equal(1, secondPreview.NewTaskCount);
        Assert.Contains(a.Id, Assert.Single(secondPreview.Items).DependsOn);
        var second = (await Publish(service, key, [b.Id])).Publication!;
        await using var db = factory.CreateDbContext();
        var dependency = Assert.Single(await db.TaskDependencies.ToListAsync());
        Assert.Equal(projectId, dependency.ProjectId);
        Assert.Equal(Assert.Single(second.Items).TaskId, dependency.TaskItemId);
        Assert.Equal(Assert.Single(first.Items).TaskId, dependency.DependsOnTaskItemId);
        Assert.Equal(2, await db.Tasks.CountAsync());
    }

    [Fact]
    public async Task CopiedCanonicalExpansion_CreatesOnlyTheNewItem_AndPreservesStableTaskMappings()
    {
        using var factory = new SqliteTestFactory();
        var (service, a, projectId) = await Linked(factory);
        var b = $"idea:{await SeedIdea(factory, "Idea B")}";
        await Change(service, b, "link_project", new { projectId });
        await Accept(service, a, Item("step-1", "Original work"));
        var first = (await Publish(service, a)).Publication!;
        var originalMapping = Assert.Single(first.Items);
        var accepted = (await service.GetAsync(a)).AcceptedPlan!;

        await Accept(service, b, Copy(Assert.Single(accepted.Items)), Item("step-1", "New contribution"));
        var expanded = await Publish(service, b);

        Assert.Equal(1, expanded.Publication!.NewTaskCount);
        Assert.Equal(1, expanded.Publication.ExistingTaskCount);
        Assert.Equal(originalMapping.TaskId, expanded.Publication.Items.Single(item => item.SourceWorkspaceId == a).TaskId);
        Assert.NotEqual(originalMapping.TaskId, expanded.Publication.Items.Single(item => item.SourceWorkspaceId == b).TaskId);
        Assert.All(expanded.Publication.Items, item => Assert.Equal(originalMapping.FeatureId, item.FeatureId));
        var canonical = await service.GetAsync($"project:{projectId}");
        Assert.Equal(2, canonical.Document.Publications.Count);
        Assert.Equal(2, canonical.Document.Publications.Select(item => item.TaskId).Distinct().Count());
        await using var db = factory.CreateDbContext();
        Assert.Equal(2, await db.Tasks.CountAsync());
        Assert.Equal(2, await db.TaskComments.CountAsync(item => item.Type == CommentType.Created));
        Assert.Equal(2, await db.ActivityEvents.CountAsync(item => item.EventType == "TaskCreated"));
    }

    [Fact]
    public async Task Publication_PreservesFullDescriptionsCriteriaAndDependencyDirection_AndReplaysItsReceipt()
    {
        using var factory = new SqliteTestFactory();
        var (service, key, projectId) = await Linked(factory);
        var fullDescription = "\r\n  ## Complete delivery specification Ω\r\n\r\n    keep indentation  \r\n" + new string('文', 105_000) + "\r\nFINAL TASK REQUIREMENT  \r\n";
        var criteria = new[] { "  Complete criterion Ω  ", "\r\n    Retain this multiline acceptance condition  \r\n" };
        await Accept(service, key, Item("a", "First work item", fullDescription, criteria), Item("b", "Second work item", dependencies: ["a"]));
        var current = await service.GetAsync(key);
        var request = Command(current.Revision, "publish_plan", new { expectedProjectPlanRevision = current.ProjectPlanRevision }, "publish-complete-once");

        var first = await service.ExecuteAsync(key, request);
        var afterFirst = await Snapshot(factory);
        var replay = await service.ExecuteAsync(key, request);

        Assert.True(replay.Replayed);
        Assert.Equal(first.Revision, replay.Revision);
        Assert.Equal(first.ProjectPlanRevision, replay.ProjectPlanRevision);
        Assert.Equal(afterFirst, await Snapshot(factory));
        Assert.Equal(2, first.Publication!.NewTaskCount);
        await using (var db = factory.CreateDbContext())
        {
            var tasks = await db.Tasks.Include(item => item.Feature).OrderBy(item => item.Id).ToListAsync();
            Assert.Equal(2, tasks.Count);
            var task = Assert.Single(tasks, item => item.Name == "First work item");
            Assert.Equal(fullDescription, task.Description);
            Assert.Equal(projectId, task.Feature.ProjectId);
            Assert.Equal(WorkStatus.Backlog, task.Status);
            Assert.True(double.IsFinite(task.SortOrder));
            Assert.Equal(criteria, await db.AcceptanceCriteria.Where(item => item.TaskItemId == task.Id).OrderBy(item => item.SortOrder).Select(item => item.Text).ToArrayAsync());
            var created = Assert.Single(await db.ActivityEvents.Where(item => item.EventType == "TaskCreated" && item.EntityId == task.Id).ToListAsync());
            Assert.Equal(Actor, created.Actor);
            using var audit = JsonDocument.Parse(created.AfterJson!);
            Assert.Equal(fullDescription, audit.RootElement.GetProperty("description").GetString());
            Assert.Equal(2, await db.TaskComments.CountAsync(item => item.Type == CommentType.Created && item.Author == Actor));
            var edge = Assert.Single(await db.TaskDependencies.ToListAsync());
            Assert.Equal(tasks.Single(item => item.Name == "Second work item").Id, edge.TaskItemId);
            Assert.Equal(task.Id, edge.DependsOnTaskItemId);
        }
        var repeat = await Publish(service, key);
        Assert.Equal(0, repeat.Publication!.NewTaskCount);
        Assert.Equal(2, repeat.Publication.ExistingTaskCount);
        await using var verify = factory.CreateDbContext();
        Assert.Equal(2, await verify.Tasks.CountAsync());
        Assert.Equal(2, await verify.TaskComments.CountAsync());
        Assert.Single(await verify.TaskDependencies.ToListAsync());
        Assert.Single(await verify.Features.ToListAsync());
    }

    [Fact]
    public async Task ExpandingAnAcceptedPlan_DoesNotOverwriteHumanCompletedTaskOrCriteria()
    {
        using var factory = new SqliteTestFactory();
        var (service, key, _) = await Linked(factory);
        await Accept(service, key, Item("a", "Original published specification", criteria: ["  A human-completed criterion Ω  "]));
        var published = Assert.Single((await Publish(service, key)).Publication!.Items);
        await using (var db = factory.CreateDbContext())
        {
            var task = await db.Tasks.SingleAsync(item => item.Id == published.TaskId);
            task.Name = "  Human revised title Ω  ";
            task.Description = "\r\n    Human expanded delivery context Ω  \r\n";
            task.Status = WorkStatus.Done;
            task.Priority = TaskPriority.Critical;
            task.Assignee = "human-owner";
            task.SortOrder = 1234.5;
            task.CompletedAt = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
            task.UpdatedAt = task.CompletedAt.Value;
            foreach (var criterion in await db.AcceptanceCriteria.Where(item => item.TaskItemId == task.Id).ToListAsync())
            {
                criterion.Completed = true;
                criterion.UpdatedAt = task.CompletedAt.Value;
            }
            db.TaskComments.Add(new TaskComment { TaskItemId = task.Id, Author = "human-owner", Text = "\r\n  Reviewed and completed Ω  \r\n", Type = CommentType.Verification });
            await db.SaveChangesAsync();
        }
        var beforeTask = await TaskSnapshot(factory, published.TaskId);
        var priorItem = Assert.Single((await service.GetAsync(key)).AcceptedPlan!.Items);
        await Accept(service, key, Copy(priorItem), Item("b", "New work added after completion"));

        var preview = await Preview(service, key);
        Assert.Equal(WorkStatus.Done.ToString(), preview.Items.Single(item => item.CanonicalItemId == priorItem.Id).ExistingTask!.Status);
        var result = await Publish(service, key);

        Assert.Equal(1, result.Publication!.NewTaskCount);
        Assert.Equal(1, result.Publication.ExistingTaskCount);
        Assert.Equal(beforeTask, await TaskSnapshot(factory, published.TaskId));
        await using var verify = factory.CreateDbContext();
        Assert.Equal(2, await verify.Tasks.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StalePublicationToken_RejectsPreviewAndApply_WithoutAnySavedEffect(bool preview)
    {
        using var factory = new SqliteTestFactory();
        var (service, key, projectId) = await Linked(factory);
        await Accept(service, key, Item("a", "Shared work"));
        var stale = await service.GetAsync(key);
        await Change(service, $"project:{projectId}", "add_comment", new { text = "Project canvas changed after the client's plan read" });
        var before = await Snapshot(factory);

        var error = preview
            ? await Assert.ThrowsAsync<IdeaWorkspaceException>(() => service.PreviewPublicationAsync(key, new(stale.Revision, stale.ProjectPlanRevision!.Value)))
            : await Assert.ThrowsAsync<IdeaWorkspaceException>(() => service.ExecuteAsync(key, Command(stale.Revision, "publish_plan", new { expectedProjectPlanRevision = stale.ProjectPlanRevision })));

        Assert.Equal(409, error.StatusCode);
        Assert.Equal("project_plan_revision_conflict", error.Code);
        Assert.Equal(before, await Snapshot(factory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publication_AfterTaskMovesWithinProject_UsesLiveTaskAndRetainsOriginalMapping(bool deleteOriginalFeature)
    {
        using var factory = new SqliteTestFactory();
        var (service, key, projectId) = await Linked(factory);
        await Accept(service, key, Item("a", "Original published specification"));
        var published = Assert.Single((await Publish(service, key)).Publication!.Items);
        var liveFeatureId = await MovePublishedTask(factory, projectId, published, deleteOriginalFeature);
        var taskBefore = await TaskSnapshot(factory, published.TaskId);
        var mappingBefore = JsonSerializer.Serialize(Assert.Single((await service.GetAsync($"project:{projectId}")).Document.Publications), Json);
        var before = await Snapshot(factory);

        var preview = await Preview(service, key);

        Assert.Equal(0, preview.NewTaskCount);
        Assert.Equal(1, preview.ExistingTaskCount);
        Assert.Equal(deleteOriginalFeature ? (int?)null : published.FeatureId, preview.FeatureId);
        var existing = Assert.Single(preview.Items).ExistingTask!;
        Assert.Equal(published.TaskId, existing.Id);
        Assert.Equal(liveFeatureId, existing.FeatureId);
        Assert.Equal(projectId, existing.ProjectId);
        Assert.Equal("  Human moved task Ω  ", existing.Name);
        Assert.Equal("\r\n    Human retained task detail Ω  \r\n", existing.Description);
        Assert.Equal(before, await Snapshot(factory));

        var result = await Publish(service, key);

        Assert.Equal(0, result.Publication!.NewTaskCount);
        Assert.Equal(1, result.Publication.ExistingTaskCount);
        var retained = Assert.Single(result.Publication.Items);
        Assert.Equal(published.TaskId, retained.TaskId);
        Assert.Equal(published.FeatureId, retained.FeatureId);
        Assert.False(retained.Created);
        Assert.Equal(taskBefore, await TaskSnapshot(factory, published.TaskId));
        Assert.Equal(mappingBefore, JsonSerializer.Serialize(Assert.Single((await service.GetAsync($"project:{projectId}")).Document.Publications), Json));
        await using var db = factory.CreateDbContext();
        Assert.Equal(deleteOriginalFeature ? 1 : 2, await db.Features.CountAsync());
        Assert.Single(await db.Tasks.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewPublication_AfterHistoricalFeatureRemoval_RequiresAnExplicitTarget(bool createNewFeature)
    {
        using var factory = new SqliteTestFactory();
        var (service, key, projectId) = await Linked(factory);
        await Accept(service, key, Item("a", "Original published item"));
        var published = Assert.Single((await Publish(service, key)).Publication!.Items);
        var originalItem = Assert.Single((await service.GetAsync(key)).AcceptedPlan!.Items);
        var liveFeatureId = await MovePublishedTask(factory, projectId, published, deleteOriginalFeature: true);
        await Accept(service, key, Copy(originalItem), Item("b", "Explicitly targeted new work"));
        var current = await service.GetAsync(key);
        var newItem = current.AcceptedPlan!.Items.Single(item => item.Provenance!.SourceItemId == "b");
        var taskBefore = await TaskSnapshot(factory, published.TaskId);
        var before = await Snapshot(factory);

        var previewError = await Assert.ThrowsAsync<IdeaWorkspaceException>(() => Preview(service, key, [newItem.Id]));
        var publishError = await Assert.ThrowsAsync<IdeaWorkspaceException>(() => Publish(service, key, [newItem.Id]));

        Assert.Equal("publication_target_missing", previewError.Code);
        Assert.Equal(409, previewError.StatusCode);
        Assert.Equal("publication_target_missing", publishError.Code);
        Assert.Equal(409, publishError.StatusCode);
        var existingOnly = await Preview(service, key, [originalItem.Id]);
        Assert.Equal(0, existingOnly.NewTaskCount);
        Assert.Equal(1, existingOnly.ExistingTaskCount);
        Assert.Equal(liveFeatureId, Assert.Single(existingOnly.Items).ExistingTask!.FeatureId);
        Assert.Equal(before, await Snapshot(factory));
        var request = new IdeaWorkspacePublicationPreviewRequest(current.Revision, current.ProjectPlanRevision!.Value,
            [newItem.Id], createNewFeature ? null : liveFeatureId, createNewFeature ? "Explicit new target" : null);

        var explicitPreview = await service.PreviewPublicationAsync(key, request);
        var result = await service.ExecuteAsync(key, Command(current.Revision, "publish_plan", new
        {
            expectedProjectPlanRevision = current.ProjectPlanRevision, itemIds = new[] { newItem.Id },
            featureId = request.FeatureId, featureName = request.FeatureName
        }));

        Assert.Equal(1, explicitPreview.NewTaskCount);
        Assert.Equal(1, result.Publication!.NewTaskCount);
        Assert.Equal(0, result.Publication.ExistingTaskCount);
        var created = Assert.Single(result.Publication.Items);
        Assert.True(created.Created);
        Assert.NotEqual(published.TaskId, created.TaskId);
        Assert.Equal(taskBefore, await TaskSnapshot(factory, published.TaskId));
        await using var db = factory.CreateDbContext();
        var feature = await db.Features.SingleAsync(item => item.Id == created.FeatureId);
        Assert.Equal(projectId, feature.ProjectId);
        Assert.Equal(createNewFeature ? "Explicit new target" : "Human selected sibling feature", feature.Name);
        Assert.Equal(createNewFeature ? 2 : 1, await db.Features.CountAsync());
        Assert.Equal(2, await db.Tasks.CountAsync());
        var retained = (await service.GetAsync($"project:{projectId}")).Document.Publications.Single(item => item.TaskId == published.TaskId);
        Assert.Equal(published.FeatureId, retained.FeatureId);
    }

    [Theory]
    [InlineData("task")]
    [InlineData("feature")]
    [InlineData("project")]
    [InlineData("foreign_task")]
    public async Task MissingOrForeignPublishedTargets_AreReportedWithoutRecreatingWork(string missing)
    {
        using var factory = new SqliteTestFactory();
        var (service, key, projectId) = await Linked(factory);
        await Accept(service, key, Item("a", "Retained published identity"));
        var mapping = Assert.Single((await Publish(service, key)).Publication!.Items);
        await using (var db = factory.CreateDbContext())
        {
            switch (missing)
            {
                case "task": db.Tasks.Remove(await db.Tasks.SingleAsync(item => item.Id == mapping.TaskId)); break;
                case "feature": db.Features.Remove(await db.Features.SingleAsync(item => item.Id == mapping.FeatureId)); break;
                case "project": db.Projects.Remove(await db.Projects.SingleAsync(item => item.Id == projectId)); break;
                default:
                    var other = new Project { Name = "Foreign project" };
                    var feature = new Feature { Name = "Foreign feature", Project = other };
                    db.Features.Add(feature);
                    await db.SaveChangesAsync();
                    (await db.Tasks.SingleAsync(item => item.Id == mapping.TaskId)).FeatureId = feature.Id;
                    break;
            }
            await db.SaveChangesAsync();
        }
        var before = await Snapshot(factory);

        var error = await Assert.ThrowsAsync<IdeaWorkspaceException>(() => Publish(service, key));

        Assert.Equal(missing == "project" ? "project_not_found" : "publication_target_missing", error.Code);
        Assert.Equal(before, await Snapshot(factory));
        var canonical = await service.GetAsync($"project:{projectId}");
        Assert.Equal(mapping.TaskId, Assert.Single(canonical.Document.Publications).TaskId);
        Assert.Equal(Description, canonical.OriginalSource.Body);
        if (missing == "project")
        {
            Assert.True((await service.GetAsync(key)).ProjectMissing);
            var recoveredProject = await SeedProject(factory, "Explicit recovery target");
            await Change(service, key, "link_project", new { projectId = recoveredProject, expectedCurrentProjectId = projectId });
            var recovered = await service.GetAsync(key);
            Assert.Equal(recoveredProject, recovered.ProjectId);
            Assert.False(recovered.ProjectMissing);
            Assert.Equal(Body, recovered.OriginalSource.Body);
            Assert.True((await service.GetAsync($"project:{projectId}")).SourceMissing);
            Assert.Equal(mapping.TaskId, Assert.Single((await service.GetAsync($"project:{projectId}")).Document.Publications).TaskId);
        }
    }

    [Fact]
    public async Task StandaloneProject_CanAcceptAndPublishItsPlan_WithoutCreatingAnIdea()
    {
        using var factory = new SqliteTestFactory();
        var projectId = await SeedProject(factory);
        var service = new IdeaWorkspaceService(factory);
        var key = $"project:{projectId}";
        await Accept(service, key, Item("step-1", "Standalone project work"));

        var result = await Publish(service, key);

        Assert.Equal(projectId, result.Publication!.ProjectId);
        Assert.Equal(1, result.Publication.NewTaskCount);
        await using var db = factory.CreateDbContext();
        Assert.Empty(await db.Ideas.ToListAsync());
        var task = await db.Tasks.Include(item => item.Feature).SingleAsync();
        Assert.Equal(projectId, task.Feature.ProjectId);
    }

    [Fact]
    public async Task LinkReplay_PreservesAnExistingTargetSnapshotAndCanvas_AfterLaterLegacyEdits()
    {
        using var factory = new SqliteTestFactory();
        var projectId = await SeedProject(factory);
        var service = new IdeaWorkspaceService(factory);
        var target = $"project:{projectId}";
        await Change(service, target, "create_component", new { component = new { id = "project-note", type = "note", title = "Saved project context", content = Body } });
        var originalTarget = await service.GetAsync(target);
        await using (var db = factory.CreateDbContext())
        {
            var project = await db.Projects.SingleAsync();
            project.Description = "\r\n  Later legacy description Ω  \r\n";
            project.Readme = "\r\n  Later legacy readme Ω  \r\n";
            await db.SaveChangesAsync();
        }
        var key = $"idea:{await SeedIdea(factory)}";
        var request = Command(0, "link_project", new { projectId }, "link-project-once");

        var first = await service.ExecuteAsync(key, request);
        var saved = await Snapshot(factory);
        var replay = await service.ExecuteAsync(key, request);

        Assert.True(replay.Replayed);
        Assert.Equal(first.ProjectId, replay.ProjectId);
        Assert.Equal(saved, await Snapshot(factory));
        var current = await service.GetAsync(key);
        Assert.Equal(originalTarget.OriginalSource, current.OriginalProjectSource);
        Assert.Equal("\r\n  Later legacy description Ω  \r\n", current.ProjectSource!.Body);
        Assert.Equal("\r\n  Later legacy readme Ω  \r\n", current.ProjectSource.Readme);
        var retained = await service.GetAsync(target);
        Assert.Equal(originalTarget.Revision + 1, retained.Revision);
        var observed = Assert.Single(retained.Document.SourceSnapshots, item => item.Source.Readme == "\r\n  Later legacy readme Ω  \r\n");
        Assert.Equal("\r\n  Later legacy description Ω  \r\n", observed.Source.Body);
        Assert.Equal(Goals, observed.Source.Plan);
        Assert.Contains(retained.Document.Components, item => item.Id == "project-note" && item.Content == Body);
    }

    [Theory]
    [InlineData("provenance")]
    [InlineData("task")]
    [InlineData("feature")]
    public async Task ForgedPlanItemAuthority_IsRejectedBeforeSavingAProposal(string forged)
    {
        using var factory = new SqliteTestFactory();
        var (service, key, projectId) = await Linked(factory);
        var item = Item("step-1", "A caller must not choose publication identity");
        if (forged == "provenance") item.Provenance = new(projectId, "idea:999999", "step-1", "forged-canonical-item");
        if (forged == "task") item.PublishedTaskId = 123456;
        if (forged == "feature") item.PublishedFeatureId = 123456;
        var before = await Snapshot(factory);

        var error = await Assert.ThrowsAsync<IdeaWorkspaceException>(() => Propose(service, key, item));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal("forged_provenance", error.Code);
        Assert.Equal(before, await Snapshot(factory));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("version")]
    [InlineData("status")]
    [InlineData("createdBy")]
    [InlineData("createdAt")]
    [InlineData("acceptedBy")]
    [InlineData("acceptedAt")]
    public async Task ProposedPlan_ServerOwnedMetadata_IsRejectedBeforeAnySavedEffect(string field)
    {
        using var factory = new SqliteTestFactory();
        var (service, key, _) = await Linked(factory);
        var fields = new Dictionary<string, object?>
        {
            ["title"] = "Complete authored proposal Ω",
            ["items"] = new[] { Item("a", "Complete authored plan item") },
            ["expectedProjectPlanRevision"] = (await service.GetAsync(key)).ProjectPlanRevision,
            [field] = field switch
            {
                "version" => 999,
                "createdAt" or "acceptedAt" => new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc),
                _ => "Caller-authored authority must not be silently discarded"
            }
        };
        var before = await Snapshot(factory);

        var error = await Assert.ThrowsAsync<IdeaWorkspaceException>(() => Change(service, key, "propose_plan", fields));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal("unknown_payload_field", error.Code);
        Assert.Contains(field, error.Message, StringComparison.Ordinal);
        Assert.Equal(before, await Snapshot(factory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedPublishedSpecification_RejectsPreviewAndApply_WithoutOverwritingTheRealTask(bool preview)
    {
        using var factory = new SqliteTestFactory();
        var (service, key, _) = await Linked(factory);
        await Accept(service, key, Item("a", "Published specification"));
        var mapping = Assert.Single((await Publish(service, key)).Publication!.Items);
        var beforeTask = await TaskSnapshot(factory, mapping.TaskId);
        var changed = Copy(Assert.Single((await service.GetAsync(key)).AcceptedPlan!.Items));
        changed.Description += "\r\n  Changed after publication Ω  \r\n";
        await Accept(service, key, changed);
        var before = await Snapshot(factory);

        var error = preview
            ? await Assert.ThrowsAsync<IdeaWorkspaceException>(() => Preview(service, key))
            : await Assert.ThrowsAsync<IdeaWorkspaceException>(() => Publish(service, key));

        Assert.Equal(409, error.StatusCode);
        Assert.Equal("published_specification_changed", error.Code);
        Assert.Equal(before, await Snapshot(factory));
        Assert.Equal(beforeTask, await TaskSnapshot(factory, mapping.TaskId));
    }

    [Theory]
    [InlineData("empty", false)]
    [InlineData("empty", true)]
    [InlineData("duplicate", false)]
    [InlineData("duplicate", true)]
    [InlineData("unknown", false)]
    [InlineData("unknown", true)]
    public async Task InvalidPublicationSelections_FailExplicitly_WithoutFallingBackToAllItems(string selection, bool preview)
    {
        using var factory = new SqliteTestFactory();
        var (service, key, _) = await Linked(factory);
        await Accept(service, key, Item("a", "Selection must be explicit"));
        var itemId = Assert.Single((await service.GetAsync(key)).AcceptedPlan!.Items).Id;
        string[] ids = selection switch { "empty" => [], "duplicate" => [itemId, itemId], _ => ["missing-canonical-item"] };
        var before = await Snapshot(factory);

        var error = preview
            ? await Assert.ThrowsAsync<IdeaWorkspaceException>(() => Preview(service, key, ids))
            : await Assert.ThrowsAsync<IdeaWorkspaceException>(() => Publish(service, key, ids));

        Assert.Equal(selection == "unknown" ? 404 : 400, error.StatusCode);
        Assert.Equal(selection == "unknown" ? "plan_item_not_found" : "invalid_selection", error.Code);
        Assert.Equal(before, await Snapshot(factory));
    }

    [Fact]
    public async Task LegacyAcceptedPlan_ReadAndPreviewDeriveProvenanceWithoutWrites_AndPublicationRetainsOriginalIds()
    {
        using var factory = new SqliteTestFactory();
        var projectId = await SeedProject(factory);
        var key = $"project:{projectId}";
        var service = new IdeaWorkspaceService(factory);
        var initial = await service.GetAsync(key);
        var legacy = new IdeaWorkspacePlanRevision
        {
            Id = "legacy-accepted-plan", Version = 3, Title = "Legacy accepted work", Status = "Accepted",
            CreatedBy = "legacy-owner", CreatedAt = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc),
            Items = [Item("legacy-step-1", "Legacy first task"), Item("legacy-step-2", "Legacy dependent task", dependencies: ["legacy-step-1"])]
        };
        initial.Document.PlanRevisions.Add(legacy);
        initial.Document.AcceptedPlanRevisionId = legacy.Id;
        var legacyDocument = JsonNode.Parse(JsonSerializer.Serialize(initial.Document, Json))!.AsObject();
        legacyDocument.Remove("publications");
        foreach (var revision in legacyDocument["planRevisions"]!.AsArray())
            foreach (var item in revision!["items"]!.AsArray()) item!.AsObject().Remove("provenance");
        var legacyJson = legacyDocument.ToJsonString(Json);
        Assert.DoesNotContain("\"provenance\"", legacyJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"publications\"", legacyJson, StringComparison.Ordinal);
        var oldAudit = JsonSerializer.Serialize(new { document = legacyDocument, preserved = "  Legacy audit remains exact Ω  " }, Json);
        await using (var db = factory.CreateDbContext())
        {
            db.IdeaWorkspaceStates.Add(new()
            {
                WorkspaceId = key, Kind = "project", SourceId = projectId, LinkedProjectId = projectId, Revision = 7,
                OriginalSourceJson = JsonSerializer.Serialize(initial.Source, Json), DocumentJson = legacyJson,
                CreatedAt = legacy.CreatedAt, UpdatedAt = legacy.CreatedAt
            });
            db.ActivityEvents.Add(new() { ProjectId = projectId, EntityType = "ProjectWorkspace", EntityId = projectId,
                Actor = "legacy-owner", EventType = "IdeaWorkspaceChanged", Message = "legacy accepted plan", AfterJson = oldAudit, CreatedAt = legacy.CreatedAt });
            await db.SaveChangesAsync();
        }
        var before = await Snapshot(factory);

        var context = await service.GetAsync(key);
        var preview = await Preview(service, key);

        Assert.Equal(new[] { "legacy-step-1", "legacy-step-2" }, context.AcceptedPlan!.Items.Select(item => item.Id));
        Assert.All(context.AcceptedPlan.Items, item => Assert.Equal(new IdeaWorkspacePlanItemProvenance(projectId, key, item.Id, item.Id), item.Provenance));
        Assert.Equal(new[] { "legacy-step-1", "legacy-step-2" }, preview.Items.Select(item => item.CanonicalItemId));
        Assert.Equal(before, await Snapshot(factory));
        var request = Command(context.Revision, "publish_plan", new { expectedProjectPlanRevision = context.ProjectPlanRevision }, "legacy-publish-once");
        var published = await service.ExecuteAsync(key, request);
        var after = await Snapshot(factory);
        var replay = await service.ExecuteAsync(key, request);
        Assert.True(replay.Replayed);
        Assert.Equal(after, await Snapshot(factory));
        Assert.Equal(2, published.Publication!.NewTaskCount);
        var canonical = await service.GetAsync(key);
        Assert.Equal(new[] { "legacy-step-1", "legacy-step-2" }, canonical.AcceptedPlan!.Items.Select(item => item.Id));
        Assert.All(canonical.Document.PlanRevisions.Single(item => item.Id == legacy.Id).Items, item => Assert.NotNull(item.Provenance));
        await using var verify = factory.CreateDbContext();
        Assert.Equal(oldAudit, (await verify.ActivityEvents.SingleAsync(item => item.Message == "legacy accepted plan")).AfterJson);
        Assert.Equal(2, await verify.Tasks.CountAsync());
        Assert.Single(await verify.TaskDependencies.ToListAsync());
    }

    [Theory]
    [InlineData("link_project")]
    [InlineData("create_project")]
    public async Task AssociationLateReceiptFailure_RollsBackLegacyLinksSourcesTargetSnapshotsAndAudit(string action)
    {
        using var factory = new SqliteTestFactory();
        var projectId = await SeedProject(factory);
        var key = $"idea:{await SeedIdea(factory)}";
        var service = new IdeaWorkspaceService(factory);
        await using (var db = factory.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER RejectAssociationReceipt BEFORE INSERT ON IdeaWorkspaceReceipts WHEN NEW.OperationId = 'fail-association' BEGIN SELECT RAISE(ABORT, 'association receipt rejected'); END;");
        var before = await Snapshot(factory);
        object payload = action == "link_project" ? new { projectId } : new { name = "Must not remain as an orphan project" };

        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => Change(service, key, action, payload, "fail-association"));

        Assert.Contains("association receipt rejected", Assert.IsType<SqliteException>(failure.InnerException).Message, StringComparison.Ordinal);
        Assert.Equal(before, await Snapshot(factory));
    }

    [Theory]
    [InlineData("second_task")]
    [InlineData("criterion")]
    [InlineData("dependency")]
    [InlineData("canonical_state")]
    [InlineData("workspace_audit")]
    [InlineData("receipt")]
    public async Task LatePublicationFailure_RollsBackEveryTaskCriterionDependencyMappingWorkspaceAndAudit(string fault)
    {
        using var factory = new SqliteTestFactory();
        var (service, key, _) = await Linked(factory);
        await Accept(service, key, Item("a", "First work item"), Item("b", "Second work item", criteria: ["Second criterion Ω"], dependencies: ["a"]));
        var trigger = fault switch
        {
            "second_task" => "CREATE TRIGGER RejectDelivery BEFORE INSERT ON Tasks WHEN NEW.Name = 'Second work item' BEGIN SELECT RAISE(ABORT, 'delivery rejected'); END;",
            "criterion" => "CREATE TRIGGER RejectDelivery BEFORE INSERT ON AcceptanceCriteria WHEN NEW.Text = 'Second criterion Ω' BEGIN SELECT RAISE(ABORT, 'delivery rejected'); END;",
            "dependency" => "CREATE TRIGGER RejectDelivery BEFORE INSERT ON TaskDependencies BEGIN SELECT RAISE(ABORT, 'delivery rejected'); END;",
            "canonical_state" => "CREATE TRIGGER RejectDelivery BEFORE UPDATE ON IdeaWorkspaceStates WHEN OLD.Kind = 'project' BEGIN SELECT RAISE(ABORT, 'delivery rejected'); END;",
            "workspace_audit" => "CREATE TRIGGER RejectDelivery BEFORE INSERT ON ActivityEvents WHEN NEW.Message = 'publish_plan' BEGIN SELECT RAISE(ABORT, 'delivery rejected'); END;",
            _ => "CREATE TRIGGER RejectDelivery BEFORE INSERT ON IdeaWorkspaceReceipts WHEN NEW.OperationId = 'fail-publication' BEGIN SELECT RAISE(ABORT, 'delivery rejected'); END;"
        };
        await using (var db = factory.CreateDbContext()) await db.Database.ExecuteSqlRawAsync(trigger);
        var before = await Snapshot(factory);

        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => Publish(service, key, operationId: "fail-publication"));

        Assert.Contains("delivery rejected", Assert.IsType<SqliteException>(failure.InnerException).Message, StringComparison.Ordinal);
        Assert.Equal(before, await Snapshot(factory));
    }

    [Fact]
    public async Task FileRestart_RetainsCanonicalProvenanceMappingsAndTheOriginalPublicationReceipt()
    {
        using var database = new DeliveryFileDatabase();
        string key;
        int projectId;
        IdeaWorkspaceCommand request;
        IdeaWorkspaceMutationResult first;
        string afterPublish;
        using (var initial = database.CreateFactory())
        {
            var linked = await Linked(initial);
            key = linked.Key;
            projectId = linked.ProjectId;
            await Accept(linked.Service, key, Item("step-1", "Durable delivery"));
            var current = await linked.Service.GetAsync(key);
            request = Command(current.Revision, "publish_plan", new { expectedProjectPlanRevision = current.ProjectPlanRevision }, "restart-publication");
            first = await linked.Service.ExecuteAsync(key, request);
            afterPublish = await Snapshot(initial);
        }

        using var reopened = database.CreateFactory();
        var service = new IdeaWorkspaceService(reopened);
        var context = await service.GetAsync(key);
        var canonical = await service.GetAsync($"project:{projectId}");
        Assert.Equal(key, Assert.Single(context.AcceptedPlan!.Items).Provenance!.SourceWorkspaceId);
        Assert.Equal(Assert.Single(first.Publication!.Items).TaskId, Assert.Single(canonical.Document.Publications).TaskId);
        var replay = await service.ExecuteAsync(key, request);

        Assert.True(replay.Replayed);
        Assert.Equal(first.Revision, replay.Revision);
        Assert.Equal(first.ProjectPlanRevision, replay.ProjectPlanRevision);
        Assert.Equal(afterPublish, await Snapshot(reopened));
    }

    [Fact]
    public async Task IndependentWriters_SharingOneCanonicalRevision_AllowOnlyOnePublication()
    {
        using var database = new DeliveryFileDatabase();
        using var firstFactory = database.CreateFactory();
        var (firstService, a, projectId) = await Linked(firstFactory);
        var b = $"idea:{await SeedIdea(firstFactory, "Second contributing idea")}";
        await Change(firstService, b, "link_project", new { projectId });
        await Accept(firstService, a, Item("step-1", "First contribution"));
        await Accept(firstService, b, Item("step-1", "Second contribution"));
        using var secondFactory = database.CreateFactory();
        var secondService = new IdeaWorkspaceService(secondFactory);
        var contextA = await firstService.GetAsync(a);
        var contextB = await secondService.GetAsync(b);
        Assert.Equal(contextA.ProjectPlanRevision, contextB.ProjectPlanRevision);
        using var start = new Barrier(2);

        var outcomes = await Task.WhenAll(
            Task.Run(async () => { Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(10))); return await AttemptPublication(firstService, a, contextA, "writer-a"); }),
            Task.Run(async () => { Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(10))); return await AttemptPublication(secondService, b, contextB, "writer-b"); }));

        var success = Assert.Single(outcomes, item => item.Result is not null);
        var conflict = Assert.Single(outcomes, item => item.Error is not null).Error!;
        Assert.Equal("project_plan_revision_conflict", conflict.Code);
        Assert.Equal(409, conflict.StatusCode);
        Assert.Equal(2, success.Result!.Publication!.NewTaskCount);
        await using var db = firstFactory.CreateDbContext();
        Assert.Equal(2, await db.Tasks.CountAsync());
        Assert.Equal(2, await db.TaskComments.CountAsync(item => item.Type == CommentType.Created));
        Assert.Equal(1, await db.IdeaWorkspaceReceipts.CountAsync(item => item.OperationId == "writer-a" || item.OperationId == "writer-b"));
        Assert.Equal(2, (await firstService.GetAsync($"project:{projectId}")).Document.Publications.Count);
    }

    [Theory]
    [InlineData("expectedRevision")]
    [InlineData("expectedProjectPlanRevision")]
    public void PublicationPreviewEnvelope_RequiresBothRevisionTokens(string missing)
    {
        var fields = new Dictionary<string, object?> { ["expectedRevision"] = 1, ["expectedProjectPlanRevision"] = 2 };
        fields.Remove(missing);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<IdeaWorkspacePublicationPreviewRequest>(JsonSerializer.Serialize(fields, Json), Json));
    }

    [Fact]
    public void PublicationPreviewEnvelope_RejectsUnknownRequestedFields()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<IdeaWorkspacePublicationPreviewRequest>(
            """{"expectedRevision":1,"expectedProjectPlanRevision":2,"unsupportedTasks":["Must not disappear"]}""", Json));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(999999)]
    public async Task InvalidOrMissingAssociationTarget_DoesNotCreateAFallbackProject(int projectId)
    {
        using var factory = new SqliteTestFactory();
        var key = $"idea:{await SeedIdea(factory)}";
        var service = new IdeaWorkspaceService(factory);
        var before = await Snapshot(factory);

        var error = await Assert.ThrowsAsync<IdeaWorkspaceException>(() => Change(service, key, "link_project", new { projectId }));

        Assert.Equal(projectId > 0 ? 404 : 400, error.StatusCode);
        Assert.Equal(before, await Snapshot(factory));
    }

    [Theory]
    [InlineData("accept_plan")]
    [InlineData("publish_plan")]
    public async Task SharedPlanWrites_RequireAnExplicitCanonicalRevision_BeforeAnySavedEffect(string action)
    {
        using var factory = new SqliteTestFactory();
        var (service, key, _) = await Linked(factory);
        var draft = await Propose(service, key, Item("a", "Explicit revision required"));
        if (action == "publish_plan")
            await Change(service, key, "accept_plan", new { planRevisionId = draft.Id, expectedProjectPlanRevision = (await service.GetAsync(key)).ProjectPlanRevision });
        var payload = action == "accept_plan" ? new Dictionary<string, object?> { ["planRevisionId"] = draft.Id } : new Dictionary<string, object?>();
        var before = await Snapshot(factory);

        var error = await Assert.ThrowsAsync<IdeaWorkspaceException>(() => Change(service, key, action, payload));

        Assert.Equal(action == "accept_plan" ? 409 : 400, error.StatusCode);
        Assert.Equal(action == "accept_plan" ? "shared_plan_required" : "invalid_project_plan_revision", error.Code);
        Assert.Equal(before, await Snapshot(factory));
    }

    [Theory]
    [InlineData("link_project", "projectId")]
    [InlineData("create_project", "name")]
    [InlineData("publish_plan", "expectedProjectPlanRevision")]
    public async Task DeliveryCommands_RejectUnknownPayloadFields_WithoutAnySavedEffect(string action, string field)
    {
        using var factory = new SqliteTestFactory();
        var (service, key, projectId) = await Linked(factory);
        var fields = new Dictionary<string, object?> { [field] = field == "name" ? "New name" : projectId, ["unsupportedReadme"] = "Must not silently disappear" };
        var before = await Snapshot(factory);

        var error = await Assert.ThrowsAsync<IdeaWorkspaceException>(() => Change(service, key, action, fields));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal(before, await Snapshot(factory));
    }

    private static async Task<int> SeedIdea(IDbContextFactory<AppDbContext> factory, string title = "Delivery idea", int? projectId = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        var idea = new Idea { Title = title, Body = Body, Plan = Plan, PromotedProjectId = projectId, Status = projectId is null ? IdeaStatus.New : IdeaStatus.Promoted };
        db.Ideas.Add(idea);
        await db.SaveChangesAsync();
        return idea.Id;
    }

    private static async Task<int> SeedProject(IDbContextFactory<AppDbContext> factory, string name = "Delivery target", string readme = "\r\n  # Saved project readme Ω  \r\n")
    {
        await using var db = await factory.CreateDbContextAsync();
        var project = new Project { Name = name, Description = Description, Goals = Goals, Readme = readme };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        return project.Id;
    }

    private static async Task<(IdeaWorkspaceService Service, string Key, int ProjectId)> Linked(IDbContextFactory<AppDbContext> factory)
    {
        var projectId = await SeedProject(factory);
        var key = $"idea:{await SeedIdea(factory)}";
        var service = new IdeaWorkspaceService(factory);
        await Change(service, key, "link_project", new { projectId });
        return (service, key, projectId);
    }

    private static IdeaWorkspacePlanItem Item(string id, string title, string? description = null, string[]? criteria = null, string[]? dependencies = null) => new()
    {
        Id = id, Title = title, Description = description ?? $"\r\n  Complete description of {title} Ω  \r\n",
        AcceptanceCriteria = [.. (criteria ?? ["  Complete criterion Ω  "])], DependsOn = [.. (dependencies ?? [])]
    };

    private static IdeaWorkspacePlanItem Copy(IdeaWorkspacePlanItem item) => new()
    {
        Id = item.Id, Title = item.Title, Description = item.Description, AcceptanceCriteria = [.. item.AcceptanceCriteria],
        DependsOn = [.. item.DependsOn], Provenance = item.Provenance
    };

    private static async Task<int> MovePublishedTask(IDbContextFactory<AppDbContext> factory, int projectId,
        IdeaWorkspacePublishedItem published, bool deleteOriginalFeature)
    {
        await using var db = await factory.CreateDbContextAsync();
        var sibling = new Feature { ProjectId = projectId, Name = "Human selected sibling feature" };
        db.Features.Add(sibling);
        await db.SaveChangesAsync();
        var task = await db.Tasks.SingleAsync(item => item.Id == published.TaskId);
        task.FeatureId = sibling.Id;
        task.Name = "  Human moved task Ω  ";
        task.Description = "\r\n    Human retained task detail Ω  \r\n";
        task.Status = WorkStatus.Done;
        await db.SaveChangesAsync();
        if (deleteOriginalFeature)
        {
            db.Features.Remove(await db.Features.SingleAsync(item => item.Id == published.FeatureId));
            await db.SaveChangesAsync();
        }
        return sibling.Id;
    }

    private static async Task<IdeaWorkspacePlanRevision> Propose(IdeaWorkspaceService service, string key, params IdeaWorkspacePlanItem[] items)
    {
        await Change(service, key, "propose_plan", new { title = "Delivery proposal", items, expectedProjectPlanRevision = (await service.GetAsync(key)).ProjectPlanRevision });
        return (await service.GetAsync(key)).Document.PlanRevisions.Last();
    }

    private static async Task<IdeaWorkspaceMutationResult> Accept(IdeaWorkspaceService service, string key, params IdeaWorkspacePlanItem[] items)
    {
        var draft = await Propose(service, key, items);
        return await Change(service, key, "accept_plan", new { planRevisionId = draft.Id, expectedProjectPlanRevision = (await service.GetAsync(key)).ProjectPlanRevision });
    }

    private static async Task<IdeaWorkspacePublicationPreview> Preview(IdeaWorkspaceService service, string key, string[]? itemIds = null, int? featureId = null)
    {
        var context = await service.GetAsync(key);
        return await service.PreviewPublicationAsync(key, new(context.Revision, context.ProjectPlanRevision!.Value, itemIds, featureId));
    }

    private static async Task<IdeaWorkspaceMutationResult> Publish(IdeaWorkspaceService service, string key, string[]? itemIds = null, int? featureId = null, string? operationId = null)
    {
        var context = await service.GetAsync(key);
        return await service.ExecuteAsync(key, Command(context.Revision, "publish_plan", new { expectedProjectPlanRevision = context.ProjectPlanRevision, itemIds, featureId }, operationId));
    }

    private static async Task<IdeaWorkspaceMutationResult> Change(IdeaWorkspaceService service, string key, string action, object payload, string? operationId = null) =>
        await service.ExecuteAsync(key, Command((await service.GetAsync(key)).Revision, action, payload, operationId));

    private static IdeaWorkspaceCommand Command(long revision, string action, object payload, string? operationId = null) =>
        new(operationId ?? Guid.NewGuid().ToString("N"), revision, action, JsonSerializer.SerializeToElement(payload, Json), Actor);

    private static async Task<(IdeaWorkspaceMutationResult? Result, IdeaWorkspaceException? Error)> AttemptPublication(
        IdeaWorkspaceService service, string key, IdeaWorkspaceContext context, string operationId)
    {
        try
        {
            return (await service.ExecuteAsync(key, Command(context.Revision, "publish_plan", new { expectedProjectPlanRevision = context.ProjectPlanRevision }, operationId)), null);
        }
        catch (IdeaWorkspaceException error) { return (null, error); }
    }

    private static async Task<string> TaskSnapshot(IDbContextFactory<AppDbContext> factory, int taskId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var task = await db.Tasks.AsNoTracking().SingleAsync(item => item.Id == taskId);
        var criteria = await db.AcceptanceCriteria.AsNoTracking().Where(item => item.TaskItemId == taskId).OrderBy(item => item.Id)
            .Select(item => new { item.Id, item.Text, item.Completed, item.SortOrder, item.CreatedAt, item.UpdatedAt }).ToArrayAsync();
        var comments = await db.TaskComments.AsNoTracking().Where(item => item.TaskItemId == taskId).OrderBy(item => item.Id)
            .Select(item => new { item.Id, item.Text, item.Author, item.Type, item.CreatedAt }).ToArrayAsync();
        return JsonSerializer.Serialize(new { task.Id, task.FeatureId, task.Name, task.Description, task.Status, task.Priority,
            task.Assignee, task.ClaimedBy, task.ClaimedAt, task.SortOrder, task.CreatedAt, task.UpdatedAt, task.CompletedAt, criteria, comments }, Json);
    }

    // Read actual SQLite scalar rows to detect any unintended partial write, including audit and receipts.
    private static async Task<string> Snapshot(IDbContextFactory<AppDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.OpenConnectionAsync();
        var connection = db.Database.GetDbConnection();
        var tables = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        }
        var snapshot = new SortedDictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            await using var command = connection.CreateCommand();
            // Identifiers come from sqlite_master, and quoting retains every literal identifier character.
            command.CommandText = "SELECT * FROM \"" + table.Replace("\"", "\"\"") + "\"";
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<string>();
            while (await reader.ReadAsync())
            {
                var row = new object?[reader.FieldCount];
                for (var i = 0; i < row.Length; i++) row[i] = await reader.IsDBNullAsync(i) ? null : reader.GetValue(i);
                rows.Add(JsonSerializer.Serialize(row, Json));
            }
            snapshot.Add(table, rows.OrderBy(value => value, StringComparer.Ordinal).ToArray());
        }
        return JsonSerializer.Serialize(snapshot, Json);
    }

    private sealed class DeliveryFileDatabase : IDisposable
    {
        private readonly string path;

        public DeliveryFileDatabase()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KanbanFlow.slnx"))) directory = directory.Parent;
            if (directory is null) throw new InvalidOperationException("Test artifacts must remain inside the repository.");
            var scratch = Path.Combine(directory.FullName, "_sandbox", "579", "tests");
            Directory.CreateDirectory(scratch);
            path = Path.Combine(scratch, $"delivery-{Guid.NewGuid():N}.db");
            using var factory = CreateFactory();
            using var db = factory.CreateDbContext();
            db.Database.EnsureCreated();
        }

        public DeliveryFileFactory CreateFactory() => new(path);

        public void Dispose()
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }

    private sealed class DeliveryFileFactory(string path) : IDbContextFactory<AppDbContext>, IDisposable
    {
        private readonly DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 15 }.ToString()).Options;

        public AppDbContext CreateDbContext() => new(options);
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
        public void Dispose() { }
    }
}
