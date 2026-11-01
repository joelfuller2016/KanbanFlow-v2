using System.Text.Json;
using KanbanFlow.Data;
using KanbanFlow.Models;
using KanbanFlow.Models.IdeaWorkspaces;
using KanbanFlow.Services.IdeaWorkspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Tests;

public sealed class IdeaWorkspaceWriteTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Capabilities_DiscloseEverySupportedComponentAndPlacementProperty_WithIdentityAndParentRules()
    {
        using var factory = new SqliteTestFactory();
        var capabilities = new IdeaWorkspaceService(factory).GetCapabilities();

        Assert.Equal(capabilities.ComponentTypes.OrderBy(value => value, StringComparer.Ordinal), capabilities.ComponentProperties.Keys.OrderBy(value => value, StringComparer.Ordinal));
        var note = capabilities.ComponentProperties["note"];
        Assert.Contains("title", note.RequiredFields);
        Assert.Contains("content", note.CreateFields);
        Assert.Contains("content", note.EditableFields);
        Assert.DoesNotContain("unsupportedRows", note.CreateFields);
        Assert.Contains("createdBy", note.ServerOwnedFields);
        Assert.Contains("createdAt", note.ServerOwnedFields);
        var task = capabilities.ComponentProperties["task-reference"];
        Assert.Contains("taskId", task.RequiredFields);
        Assert.Contains("taskId", task.CreateFields);
        Assert.Contains("taskId", task.EditableFields);
        Assert.DoesNotContain("taskId", note.EditableFields);
        Assert.NotNull(capabilities.PlacementProperties);
        var placement = capabilities.PlacementProperties;
        foreach (var field in new[] { "id", "componentId", "parentId", "x", "y", "width", "height", "zIndex", "order" })
            Assert.Contains(field, placement.CreateFields);
        foreach (var field in new[] { "parentId", "x", "y", "width", "height", "zIndex", "order" })
            Assert.Contains(field, placement.EditableFields);
        Assert.DoesNotContain("id", placement.EditableFields);
        Assert.DoesNotContain("componentId", placement.EditableFields);
        Assert.Contains("placement", placement.Constraints["parentId"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("relative", placement.Constraints["x/y"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Capabilities_DiscloseActionPayloadAndEnvelopeFields_ForCreationLayoutAndResume()
    {
        using var factory = new SqliteTestFactory();
        var capabilities = new IdeaWorkspaceService(factory).GetCapabilities();

        Assert.Equal(capabilities.Actions.OrderBy(value => value, StringComparer.Ordinal), capabilities.ActionPayloads.Keys.OrderBy(value => value, StringComparer.Ordinal));
        foreach (var (action, descriptor) in capabilities.ActionPayloads)
        {
            Assert.All(descriptor.RequiredFields, field => Assert.Contains(field, descriptor.Fields));
            if (action == "preview_publication")
            {
                Assert.Empty(descriptor.EnvelopeFields);
                Assert.Contains("expectedRevision", descriptor.RequiredFields);
                Assert.Contains("expectedProjectPlanRevision", descriptor.RequiredFields);
                Assert.DoesNotContain("operationId", descriptor.Fields);
                Assert.DoesNotContain("actor", descriptor.Fields);
            }
            else
            {
                Assert.Contains("operationId", descriptor.EnvelopeFields);
                Assert.Contains("actor", descriptor.EnvelopeFields);
            }
        }
        var create = capabilities.ActionPayloads["create_workspace"];
        foreach (var field in new[] { "title", "body", "plan", "tags", "priority", "projectId" })
            Assert.Contains(field, create.Fields);
        Assert.DoesNotContain("expectedRevision", create.EnvelopeFields);
        var component = capabilities.ActionPayloads["create_component"];
        Assert.Contains("component", component.RequiredFields);
        Assert.Contains("placement", component.Fields);
        Assert.Contains("expectedRevision", component.EnvelopeFields);
        var layout = capabilities.ActionPayloads["move_components"];
        Assert.Contains("placements", layout.RequiredFields);
        Assert.Contains("atomic", layout.Rules, StringComparison.OrdinalIgnoreCase);
        var checkpoint = capabilities.ActionPayloads["save_checkpoint"];
        foreach (var field in new[] { "clientId", "sessionId", "objective", "focusedComponentIds", "nextActions", "lastAcknowledgedActivityId" })
            Assert.Contains(field, checkpoint.Fields);
        Assert.Contains("clientId", checkpoint.RequiredFields);
        Assert.Contains("sessionId", checkpoint.RequiredFields);
    }

    [Theory]
    [InlineData("operationId")]
    [InlineData("actor")]
    public void CreateEnvelope_RequiresOperationAndActorMetadata(string missing)
    {
        var fields = new Dictionary<string, object?> { ["operationId"] = "capture", ["actor"] = "client", ["title"] = "Idea" };
        fields.Remove(missing);

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<IdeaWorkspaceCreate>(JsonSerializer.Serialize(fields, Json), Json));
    }

    [Theory]
    [InlineData("operationId")]
    [InlineData("expectedRevision")]
    [InlineData("action")]
    [InlineData("payload")]
    [InlineData("actor")]
    public void CommandEnvelope_RequiresEveryConcurrencyAndOperationField(string missing)
    {
        var fields = new Dictionary<string, object?>
        {
            ["operationId"] = "operation",
            ["expectedRevision"] = 0,
            ["action"] = "add_comment",
            ["payload"] = new { text = "Comment" },
            ["actor"] = "client"
        };
        fields.Remove(missing);

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<IdeaWorkspaceCommand>(JsonSerializer.Serialize(fields, Json), Json));
    }

    [Theory]
    [InlineData("create")]
    [InlineData("command")]
    public void Envelopes_RejectUnknownMetadata_InsteadOfSilentlyDroppingIt(string kind)
    {
        var fields = new Dictionary<string, object?> { ["operationId"] = "operation", ["actor"] = "client", ["unrecognized"] = "Must not disappear" };
        if (kind == "create")
        {
            fields["title"] = "Idea";
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<IdeaWorkspaceCreate>(JsonSerializer.Serialize(fields, Json), Json));
        }
        else
        {
            fields["expectedRevision"] = 0;
            fields["action"] = "add_comment";
            fields["payload"] = new { text = "Comment" };
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<IdeaWorkspaceCommand>(JsonSerializer.Serialize(fields, Json), Json));
        }
    }

    [Fact]
    public void ExplicitRevisionZero_IsAccepted_AndExistingProjectCaptureDoesNotRequireANewTitle()
    {
        var command = JsonSerializer.Deserialize<IdeaWorkspaceCommand>("""
            {"operationId":"operation","expectedRevision":0,"action":"add_comment","payload":{"text":"Comment"},"actor":"client"}
            """, Json);
        var create = JsonSerializer.Deserialize<IdeaWorkspaceCreate>("""
            {"operationId":"capture","actor":"client","projectId":7}
            """, Json);

        Assert.NotNull(command);
        Assert.Equal(0, command.ExpectedRevision);
        Assert.NotNull(create);
        Assert.Null(create.Title);
        Assert.Equal(7, create.ProjectId);
        Assert.Null(create.Body);
        Assert.Null(create.Plan);
    }

    [Fact]
    public async Task CreateIdea_PreservesCompleteAuthoredTextAndMetadata_InSourceSnapshotAndAudit()
    {
        using var factory = new SqliteTestFactory();
        var service = new IdeaWorkspaceService(factory);
        var body = "\r\n  # Captured idea\r\n\r\n    Ω indented source\r\n" + new string('b', 120_000) + "\r\nlast body requirement  \r\n";
        var plan = "  ## Initial plan\r\n\r\n    preserve all spacing  \r\n" + new string('p', 105_000) + "\r\nfinal plan line  ";

        var result = await service.CreateAsync(new IdeaWorkspaceCreate("capture-full", "capturing-client", Title: "Full idea", Body: body, Plan: plan, Tags: "alpha,beta", Priority: "High"));

        Assert.True(result.CreatedIdea);
        Assert.True(result.CreatedWorkspace);
        Assert.False(result.Replayed);
        Assert.NotNull(result.IdeaId);
        Assert.Null(result.ProjectId);
        Assert.Equal($"idea:{result.IdeaId}", result.WorkspaceId);
        Assert.Equal(1, result.Revision);
        var context = await service.GetAsync(result.WorkspaceId);
        Assert.Equal(body, context.Source!.Body);
        Assert.Equal(plan, context.Source.Plan);
        Assert.Equal(body, context.OriginalSource.Body);
        Assert.Equal(plan, context.OriginalSource.Plan);
        Assert.Equal("alpha,beta", context.Source.Tags);
        Assert.Equal("High", context.Source.Priority);
        Assert.Contains(context.Document.Components, component => component.Id == "source-brief" && component.Content == body);
        Assert.Contains(context.Document.Components, component => component.Id == "source-plan" && component.Content == plan);
        await using var db = factory.CreateDbContext();
        var source = Assert.Single(await db.Ideas.ToListAsync());
        Assert.Equal(body, source.Body);
        Assert.Equal(plan, source.Plan);
        Assert.Equal(TaskPriority.High, source.Priority);
        Assert.Single(await db.Set<IdeaWorkspaceState>().ToListAsync());
        Assert.Single(await db.Set<IdeaWorkspaceReceipt>().ToListAsync());
        var audit = await db.ActivityEvents.OrderBy(item => item.Id).ToListAsync();
        Assert.Equal(2, audit.Count);
        Assert.All(audit, item => Assert.Equal("capturing-client", item.Actor));
        Assert.Contains(audit, item => item.EntityType == "Idea" && item.EventType == "IdeaCreated" && item.EntityId == source.Id);
        var workspaceAudit = Assert.Single(audit, item => item.Id == result.ActivityId);
        Assert.Equal("IdeaWorkspace", workspaceAudit.EntityType);
        Assert.Contains(body, JsonStrings(workspaceAudit.AfterJson!));
        Assert.Contains(plan, JsonStrings(workspaceAudit.AfterJson!));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("  \r\n    ", "\r\n  ")]
    public async Task CreateIdea_DistinguishesNullEmptyAndWhitespaceBodyAndPlan(string? body, string? plan)
    {
        using var factory = new SqliteTestFactory();
        var service = new IdeaWorkspaceService(factory);

        var result = await service.CreateAsync(new IdeaWorkspaceCreate("text-shape", "capturing-client", Title: "Idea", Body: body, Plan: plan));

        var context = await service.GetAsync(result.WorkspaceId);
        Assert.Equal(body, context.Source!.Body);
        Assert.Equal(plan, context.Source.Plan);
        Assert.Equal(body, context.OriginalSource.Body);
        Assert.Equal(plan, context.OriginalSource.Plan);
        await using var db = factory.CreateDbContext();
        var source = Assert.Single(await db.Ideas.ToListAsync());
        Assert.Equal(body, source.Body);
        Assert.Equal(plan, source.Plan);
    }

    [Fact]
    public async Task CreateReplay_ReturnsOriginalIdeaWorkspaceAndRevision_WithoutDuplicatingSavedEffects()
    {
        using var factory = new SqliteTestFactory();
        var service = new IdeaWorkspaceService(factory);
        var request = new IdeaWorkspaceCreate("create-once", "client-a", Title: "One idea", Body: "  exact body\r\n", Plan: "\r\nplan  ");
        var first = await service.CreateAsync(request);
        await service.ExecuteAsync(first.WorkspaceId, Change("intervening-save", first.Revision, "add_comment", new { text = "Saved after creation" }, "client-a"));
        int beforeEvents;
        int beforeReceipts;
        await using (var db = factory.CreateDbContext())
        {
            beforeEvents = await db.ActivityEvents.CountAsync();
            beforeReceipts = await db.Set<IdeaWorkspaceReceipt>().CountAsync();
        }

        var replay = await service.CreateAsync(request);

        Assert.True(replay.Replayed);
        Assert.Equal(first.IdeaId, replay.IdeaId);
        Assert.Equal(first.WorkspaceId, replay.WorkspaceId);
        Assert.Equal(first.ActivityId, replay.ActivityId);
        Assert.Equal(first.Revision, replay.Revision);
        Assert.Equal(2, (await service.GetAsync(first.WorkspaceId)).Revision);
        await using var verify = factory.CreateDbContext();
        Assert.Single(await verify.Ideas.ToListAsync());
        Assert.Single(await verify.Set<IdeaWorkspaceState>().ToListAsync());
        Assert.Equal(beforeEvents, await verify.ActivityEvents.CountAsync());
        Assert.Equal(beforeReceipts, await verify.Set<IdeaWorkspaceReceipt>().CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReusedCreateOperation_WithChangedPayloadOrActor_ConflictsAndPreservesOriginal(bool changeActor)
    {
        using var factory = new SqliteTestFactory();
        var service = new IdeaWorkspaceService(factory);
        var original = new IdeaWorkspaceCreate("fixed-create-id", "client-a", Title: "Original idea", Body: "Original text");
        var first = await service.CreateAsync(original);
        var changed = changeActor ? original with { Actor = "client-b" } : original with { Body = "Changed text" };

        var error = await Assert.ThrowsAsync<IdeaWorkspaceException>(() => service.CreateAsync(changed));

        Assert.Equal(409, error.StatusCode);
        Assert.Equal("Original text", (await service.GetAsync(first.WorkspaceId)).OriginalSource.Body);
        await using var db = factory.CreateDbContext();
        Assert.Single(await db.Ideas.ToListAsync());
        Assert.Single(await db.Set<IdeaWorkspaceState>().ToListAsync());
        Assert.Single(await db.Set<IdeaWorkspaceReceipt>().ToListAsync());
        Assert.Equal(2, await db.ActivityEvents.CountAsync());
    }

    [Fact]
    public async Task CreateAndWorkspaceMutation_CanUseTheSameOperationId_InTheirDistinctReceiptScopes()
    {
        using var factory = new SqliteTestFactory();
        var service = new IdeaWorkspaceService(factory);
        var create = new IdeaWorkspaceCreate("shared-operation", "client", Title: "Idea");
        var created = await service.CreateAsync(create);

        var changed = await service.ExecuteAsync(created.WorkspaceId, Change("shared-operation", created.Revision, "save_checkpoint", new
        {
            clientId = "client",
            sessionId = "session",
            objective = "Continue here",
            focusedComponentIds = Array.Empty<string>(),
            completedChanges = Array.Empty<string>(),
            openQuestions = Array.Empty<string>(),
            nextActions = new[] { "Next action" },
            lastAcknowledgedActivityId = created.ActivityId
        }, "client"));

        Assert.Equal(2, changed.Revision);
        Assert.Equal("Continue here", Assert.Single((await service.GetAsync(created.WorkspaceId)).Document.Checkpoints).Objective);
        var replay = await service.CreateAsync(create);
        Assert.True(replay.Replayed);
        Assert.Equal(created.WorkspaceId, replay.WorkspaceId);
        Assert.Equal(1, replay.Revision);
        await using var db = factory.CreateDbContext();
        var receipts = await db.Set<IdeaWorkspaceReceipt>().OrderBy(item => item.WorkspaceId).ToListAsync();
        Assert.Equal(2, receipts.Count);
        Assert.All(receipts, receipt => Assert.Equal("shared-operation", receipt.OperationId));
        Assert.Equal(2, receipts.Select(receipt => receipt.WorkspaceId).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task OpenExistingProject_CapturesItsCompleteDocuments_WithoutCreatingAnIdeaOrEditingTheProject()
    {
        using var factory = new SqliteTestFactory();
        var readme = "\r\n  # Project readme\r\n\r\n    indented\r\n" + new string('r', 115_000) + "\r\nlast readme line  ";
        var projectId = await SeedProject(factory, readme);
        var service = new IdeaWorkspaceService(factory);

        var result = await service.CreateAsync(new IdeaWorkspaceCreate("open-project", "client", ProjectId: projectId));

        Assert.False(result.CreatedIdea);
        Assert.True(result.CreatedWorkspace);
        Assert.Null(result.IdeaId);
        Assert.Equal(projectId, result.ProjectId);
        Assert.Equal($"project:{projectId}", result.WorkspaceId);
        Assert.Equal(1, result.Revision);
        var context = await service.GetAsync(result.WorkspaceId);
        Assert.Equal("  original project description\r\n", context.OriginalSource.Body);
        Assert.Equal("\r\noriginal project goals  ", context.OriginalSource.Plan);
        Assert.Equal(readme, context.OriginalSource.Readme);
        Assert.Equal($"/project/{projectId}", context.KanbanUrl);
        await using var db = factory.CreateDbContext();
        Assert.Empty(await db.Ideas.ToListAsync());
        var project = Assert.Single(await db.Projects.ToListAsync());
        Assert.Equal("Original project", project.Name);
        Assert.Equal("  original project description\r\n", project.Description);
        Assert.Equal("\r\noriginal project goals  ", project.Goals);
        Assert.Equal(readme, project.Readme);
        Assert.Single(await db.Set<IdeaWorkspaceState>().ToListAsync());
        Assert.Single(await db.Set<IdeaWorkspaceReceipt>().ToListAsync());
    }

    [Fact]
    public async Task ReopenAlreadySavedProject_PreservesRevisionCanvasOriginalSnapshotAndUpdatedLegacyDocuments()
    {
        using var factory = new SqliteTestFactory();
        var projectId = await SeedProject(factory, "  original readme\r\n");
        var service = new IdeaWorkspaceService(factory);
        var first = await service.CreateAsync(new IdeaWorkspaceCreate("first-open", "client", ProjectId: projectId));
        await service.ExecuteAsync(first.WorkspaceId, Change("add-note", first.Revision, "create_component", new { component = new { id = "saved-note", type = "note", title = "Saved", content = "Keep this note" } }, "client"));
        await using (var db = factory.CreateDbContext())
        {
            var project = await db.Projects.SingleAsync(item => item.Id == projectId);
            project.Description = "  updated legacy description\r\n";
            project.Readme = "\r\nupdated legacy readme  ";
            await db.SaveChangesAsync();
        }
        var before = await service.GetAsync(first.WorkspaceId);
        int beforeEvents;
        await using (var db = factory.CreateDbContext()) beforeEvents = await db.ActivityEvents.CountAsync();

        var reopened = await service.CreateAsync(new IdeaWorkspaceCreate("second-open", "other-client", ProjectId: projectId));

        Assert.False(reopened.CreatedWorkspace);
        Assert.False(reopened.CreatedIdea);
        Assert.Equal(first.WorkspaceId, reopened.WorkspaceId);
        Assert.Equal(before.Revision, reopened.Revision);
        var after = await service.GetAsync(first.WorkspaceId);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Equal("  original project description\r\n", after.OriginalSource.Body);
        Assert.Equal("  original readme\r\n", after.OriginalSource.Readme);
        Assert.Equal("  updated legacy description\r\n", after.Source!.Body);
        Assert.Equal("\r\nupdated legacy readme  ", after.Source.Readme);
        Assert.Contains(after.Document.Components, component => component.Id == "saved-note" && component.Content == "Keep this note");
        await using var verify = factory.CreateDbContext();
        Assert.Empty(await verify.Ideas.ToListAsync());
        Assert.Single(await verify.Set<IdeaWorkspaceState>().ToListAsync());
        Assert.Equal(beforeEvents, await verify.ActivityEvents.CountAsync());
    }

    [Fact]
    public async Task ExistingProjectCaptureReplay_AfterSourceDeletion_ReturnsTheOriginalIdentityWithoutRecreation()
    {
        using var factory = new SqliteTestFactory();
        var projectId = await SeedProject(factory, "  retained original readme\r\n");
        var service = new IdeaWorkspaceService(factory);
        var request = new IdeaWorkspaceCreate("capture-before-removal", "client", ProjectId: projectId);
        var original = await service.CreateAsync(request);
        await using (var db = factory.CreateDbContext())
        {
            db.Projects.Remove(await db.Projects.SingleAsync(project => project.Id == projectId));
            await db.SaveChangesAsync();
        }

        var replay = await service.CreateAsync(request);

        Assert.True(replay.Replayed);
        Assert.Equal(original.WorkspaceId, replay.WorkspaceId);
        Assert.Equal(original.ProjectId, replay.ProjectId);
        Assert.Equal(original.ActivityId, replay.ActivityId);
        var context = await service.GetAsync(original.WorkspaceId);
        Assert.True(context.SourceMissing);
        Assert.True(context.ProjectMissing);
        Assert.Equal("  retained original readme\r\n", context.OriginalSource.Readme);
        var newOperation = await Assert.ThrowsAsync<IdeaWorkspaceException>(() => service.CreateAsync(request with { OperationId = "new-open-after-removal" }));
        Assert.Equal(404, newOperation.StatusCode);
        await using var verify = factory.CreateDbContext();
        Assert.Empty(await verify.Projects.ToListAsync());
        Assert.Empty(await verify.Ideas.ToListAsync());
        Assert.Single(await verify.Set<IdeaWorkspaceState>().ToListAsync());
        Assert.Single(await verify.Set<IdeaWorkspaceReceipt>().ToListAsync());
    }

    [Theory]
    [InlineData("Title")]
    [InlineData("Body")]
    [InlineData("Plan")]
    [InlineData("Tags")]
    [InlineData("Priority")]
    public async Task ExistingProjectSelection_RejectsAnyNewSourceField_WithoutChangingEitherSource(string field)
    {
        using var factory = new SqliteTestFactory();
        var projectId = await SeedProject(factory, "Original readme");
        var request = new IdeaWorkspaceCreate("ambiguous", "client", ProjectId: projectId);
        request = field switch
        {
            "Title" => request with { Title = "" },
            "Body" => request with { Body = "" },
            "Plan" => request with { Plan = "" },
            "Tags" => request with { Tags = "" },
            "Priority" => request with { Priority = "" },
            _ => throw new InvalidOperationException("Unknown test input")
        };

        var error = await Assert.ThrowsAsync<IdeaWorkspaceException>(() => new IdeaWorkspaceService(factory).CreateAsync(request));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal("source_selection_conflict", error.Code);
        await AssertNoCreatedWorkspace(factory, expectedProjects: 1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task NewIdea_RequiresANonblankTitle(string? title)
    {
        using var factory = new SqliteTestFactory();
        var error = await Assert.ThrowsAsync<IdeaWorkspaceException>(() => new IdeaWorkspaceService(factory).CreateAsync(new IdeaWorkspaceCreate("no-title", "client", Title: title)));
        Assert.Equal(400, error.StatusCode);
        await AssertNoCreatedWorkspace(factory);
    }

    [Theory]
    [InlineData("", "client")]
    [InlineData("  ", "client")]
    [InlineData("operation", "")]
    [InlineData("operation", "  ")]
    public async Task Create_RequiresNonblankOperationAndActor_BeforeAnySavedEffect(string operationId, string actor)
    {
        using var factory = new SqliteTestFactory();
        var error = await Assert.ThrowsAsync<IdeaWorkspaceException>(() => new IdeaWorkspaceService(factory).CreateAsync(new IdeaWorkspaceCreate(operationId, actor, Title: "Idea")));
        Assert.Equal(400, error.StatusCode);
        await AssertNoCreatedWorkspace(factory);
    }

    [Fact]
    public async Task TitleAtTheMaximum_IsAccepted_ButOneCharacterLongerCannotCreateAnotherSource()
    {
        using var factory = new SqliteTestFactory();
        var service = new IdeaWorkspaceService(factory);
        var valid = await service.CreateAsync(new IdeaWorkspaceCreate("maximum-title", "client", Title: new string('t', 500)));
        var error = await Assert.ThrowsAsync<IdeaWorkspaceException>(() => service.CreateAsync(new IdeaWorkspaceCreate("too-long-title", "client", Title: new string('t', 501))));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal(500, (await service.GetAsync(valid.WorkspaceId)).Source!.Title.Length);
        await using var db = factory.CreateDbContext();
        Assert.Single(await db.Ideas.ToListAsync());
        Assert.Single(await db.Set<IdeaWorkspaceState>().ToListAsync());
        Assert.Single(await db.Set<IdeaWorkspaceReceipt>().ToListAsync());
    }

    [Theory]
    [InlineData(0, 400)]
    [InlineData(-1, 400)]
    [InlineData(123456, 404)]
    public async Task MissingOrInvalidProject_DoesNotCreateAFallbackIdea(int projectId, int statusCode)
    {
        using var factory = new SqliteTestFactory();
        var error = await Assert.ThrowsAsync<IdeaWorkspaceException>(() => new IdeaWorkspaceService(factory).CreateAsync(new IdeaWorkspaceCreate("bad-project", "client", ProjectId: projectId)));
        Assert.Equal(statusCode, error.StatusCode);
        await AssertNoCreatedWorkspace(factory);
    }

    [Theory]
    [InlineData("Ideas")]
    [InlineData("IdeaWorkspaceStates")]
    [InlineData("ActivityEvents")]
    [InlineData("IdeaWorkspaceReceipts")]
    public async Task CreateInsertFailure_RollsBackSourceSnapshotAuditAndReceipt(string table)
    {
        using var factory = new SqliteTestFactory();
        // SQL identifiers cannot be parameters; each fault target uses a fixed SQL statement.
        var trigger = table switch
        {
            "Ideas" => "CREATE TRIGGER FailWorkspaceCreation BEFORE INSERT ON Ideas BEGIN SELECT RAISE(ABORT, 'workspace creation rejected'); END;",
            "IdeaWorkspaceStates" => "CREATE TRIGGER FailWorkspaceCreation BEFORE INSERT ON IdeaWorkspaceStates BEGIN SELECT RAISE(ABORT, 'workspace creation rejected'); END;",
            "ActivityEvents" => "CREATE TRIGGER FailWorkspaceCreation BEFORE INSERT ON ActivityEvents BEGIN SELECT RAISE(ABORT, 'workspace creation rejected'); END;",
            "IdeaWorkspaceReceipts" => "CREATE TRIGGER FailWorkspaceCreation BEFORE INSERT ON IdeaWorkspaceReceipts BEGIN SELECT RAISE(ABORT, 'workspace creation rejected'); END;",
            _ => throw new ArgumentOutOfRangeException(nameof(table), table, "Unsupported fault target.")
        };
        await using (var db = factory.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync(trigger);

        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => new IdeaWorkspaceService(factory).CreateAsync(new IdeaWorkspaceCreate("failed-create", "client", Title: "Must roll back", Body: "Full body", Plan: "Full plan")));

        var sqlite = Assert.IsType<SqliteException>(failure.InnerException);
        Assert.Contains("workspace creation rejected", sqlite.Message, StringComparison.Ordinal);
        await AssertNoCreatedWorkspace(factory);
    }

    [Fact]
    public async Task FailedProjectSnapshotCapture_LeavesTheExistingProjectDocumentsIntact()
    {
        using var factory = new SqliteTestFactory();
        var projectId = await SeedProject(factory, "  complete existing readme\r\n");
        await using (var db = factory.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER FailProjectCapture BEFORE INSERT ON IdeaWorkspaceReceipts BEGIN SELECT RAISE(ABORT, 'project capture rejected'); END;");

        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => new IdeaWorkspaceService(factory).CreateAsync(new IdeaWorkspaceCreate("failed-open", "client", ProjectId: projectId)));
        Assert.Contains("project capture rejected", Assert.IsType<SqliteException>(failure.InnerException).Message, StringComparison.Ordinal);

        await AssertNoCreatedWorkspace(factory, expectedProjects: 1);
        await using var verify = factory.CreateDbContext();
        var project = Assert.Single(await verify.Projects.ToListAsync());
        Assert.Equal("  original project description\r\n", project.Description);
        Assert.Equal("\r\noriginal project goals  ", project.Goals);
        Assert.Equal("  complete existing readme\r\n", project.Readme);
    }

    private static IdeaWorkspaceCommand Change(string operationId, long revision, string action, object payload, string actor) =>
        new(operationId, revision, action, JsonSerializer.SerializeToElement(payload, Json), actor);

    private static async Task<int> SeedProject(SqliteTestFactory factory, string readme)
    {
        await using var db = factory.CreateDbContext();
        var project = new Project { Name = "Original project", Description = "  original project description\r\n", Goals = "\r\noriginal project goals  ", Readme = readme };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        return project.Id;
    }

    private static async Task AssertNoCreatedWorkspace(SqliteTestFactory factory, int expectedProjects = 0)
    {
        await using var db = factory.CreateDbContext();
        Assert.Empty(await db.Ideas.ToListAsync());
        Assert.Equal(expectedProjects, await db.Projects.CountAsync());
        Assert.Empty(await db.Set<IdeaWorkspaceState>().ToListAsync());
        Assert.Empty(await db.Set<IdeaWorkspaceReceipt>().ToListAsync());
        Assert.Empty(await db.ActivityEvents.ToListAsync());
    }

    private static IEnumerable<string> JsonStrings(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Strings(document.RootElement).ToArray();

        static IEnumerable<string> Strings(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.String => [value.GetString()!],
            JsonValueKind.Array => value.EnumerateArray().SelectMany(Strings),
            JsonValueKind.Object => value.EnumerateObject().SelectMany(property => Strings(property.Value)),
            _ => []
        };
    }
}
