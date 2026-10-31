using System.Text.Json;
using System.Data.Common;
using KanbanFlow.Data;
using KanbanFlow.Models;
using KanbanFlow.Models.IdeaWorkspaces;
using KanbanFlow.Services.IdeaWorkspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace KanbanFlow.Tests;

public sealed class IdeaWorkspaceServiceTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Get_UnmodifiedIdea_ReturnsFullSourceAndRevisionZero_WithoutSavedStateOrAudit()
    {
        using var factory = new SqliteTestFactory();
        var body = "  # Research\r\n\r\n    Ω indented\r\n" + new string('x', 120_000) + "\r\nfinal-line  \r\n";
        const string plan = "\r\n  ## First milestone\r\n\r\n    keep indentation  \r\n";
        var id = await SeedIdea(factory, body: body, plan: plan);
        var service = new IdeaWorkspaceService(factory);

        var context = await service.GetAsync($"idea:{id}");

        Assert.Equal($"idea:{id}", context.WorkspaceId);
        Assert.Equal(0, context.Revision);
        Assert.False(context.SourceMissing);
        Assert.Equal(body, context.Source!.Body);
        Assert.Equal(plan, context.Source.Plan);
        Assert.Equal(body, context.OriginalSource.Body);
        Assert.Equal(plan, context.OriginalSource.Plan);
        Assert.Contains(context.Document.Components, c => c.Content == body);
        await using var db = factory.CreateDbContext();
        Assert.Empty(await db.Set<IdeaWorkspaceState>().ToListAsync());
        Assert.Empty(await db.Set<IdeaWorkspaceReceipt>().ToListAsync());
        Assert.Empty(await db.ActivityEvents.ToListAsync());
    }

    [Fact]
    public async Task Get_StandaloneProject_ReturnsCompleteDocumentsAndKanbanLink_WithoutCreatingAnIdea()
    {
        using var factory = new SqliteTestFactory();
        var projectId = await SeedProject(factory);
        var context = await new IdeaWorkspaceService(factory).GetAsync($"project:{projectId}");

        Assert.Equal("project", context.Kind);
        Assert.Equal(projectId, context.ProjectId);
        Assert.Equal("  project description\r\n", context.Source!.Body);
        Assert.Equal("\r\nproject goals  ", context.Source.Plan);
        Assert.Equal("# Original readme\r\n\r\n    retained\r\n", context.Source.Readme);
        Assert.Equal($"/project/{projectId}", context.KanbanUrl);
        await using var db = factory.CreateDbContext();
        Assert.Empty(await db.Ideas.ToListAsync());
        Assert.Empty(await db.Set<IdeaWorkspaceState>().ToListAsync());
    }

    [Theory]
    [InlineData("idea:0")]
    [InlineData("idea:01")]
    [InlineData("project:-1")]
    [InlineData("Idea:1")]
    [InlineData("idea:1:2")]
    [InlineData("other:1")]
    public async Task Get_InvalidIdentity_IsRejected(string workspaceId)
    {
        using var factory = new SqliteTestFactory();
        var error = await Assert.ThrowsAsync<IdeaWorkspaceException>(
            () => new IdeaWorkspaceService(factory).GetAsync(workspaceId));
        Assert.Equal(400, error.StatusCode);
        Assert.Equal("invalid_workspace", error.Code);
    }

    [Fact]
    public async Task Get_UnknownSourceWithoutSavedState_IsNotFound()
    {
        using var factory = new SqliteTestFactory();
        var error = await Assert.ThrowsAsync<IdeaWorkspaceException>(
            () => new IdeaWorkspaceService(factory).GetAsync("idea:4567"));
        Assert.Equal(404, error.StatusCode);
    }

    [Fact]
    public async Task CreateAndUpdateComponent_PreserveAuthoredWhitespace_AndWriteFullAttributedAudit()
    {
        using var factory = new SqliteTestFactory();
        var id = await SeedIdea(factory);
        var key = $"idea:{id}";
        var service = new IdeaWorkspaceService(factory);
        const string initial = "  ## Option\r\n\r\n    source text  \r\n";
        var updated = "\r\n  updated Ω\r\n" + new string('z', 80_000) + "\r\nlast requirement  \r\n";

        var first = await AddNote(service, key, 0, "note-a", "placement-a", initial);
        Assert.Equal(1, first.Revision);
        Assert.Equal("note-a", first.ComponentId);
        Assert.Equal("placement-a", first.PlacementId);
        await Change(service, key, 1, "update_component", new { componentId = "note-a", title = "Revised option", content = updated });
        var context = await service.GetAsync(key);

        var note = Assert.Single(context.Document.Components, c => c.Id == "note-a");
        Assert.Equal(updated, note.Content);
        Assert.Equal("Revised option", note.Title);
        Assert.Equal(2, context.Revision);
        await using var db = factory.CreateDbContext();
        var events = await db.ActivityEvents.OrderBy(a => a.Id).ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.All(events, item => Assert.Equal("test-client", item.Actor));
        Assert.All(events, item => Assert.Equal(id, item.EntityId));
        Assert.Contains(initial, JsonStrings(events[1].BeforeJson!));
        Assert.Contains(updated, JsonStrings(events[1].AfterJson!));
        Assert.Equal(2, await db.Set<IdeaWorkspaceReceipt>().CountAsync());
    }

    [Fact]
    public async Task RemovePlacement_PreservesComponentAndOtherAppearance_ThenComponentRemovalAuditsItsContent()
    {
        using var factory = new SqliteTestFactory();
        var key = $"idea:{await SeedIdea(factory)}";
        var service = new IdeaWorkspaceService(factory);
        await AddNote(service, key, 0, "note-a", "place-a", "Keep this reusable content.");
        await Change(service, key, 1, "add_placement", new { placement = new { id = "place-b", componentId = "note-a", x = 420, y = 30, width = 280, height = 180, zIndex = 1, order = 2 } });

        await Change(service, key, 2, "remove_placement", new { placementId = "place-a" });
        var afterPlacement = await service.GetAsync(key);
        Assert.Contains(afterPlacement.Document.Components, c => c.Id == "note-a" && c.Content == "Keep this reusable content.");
        Assert.DoesNotContain(afterPlacement.Document.Placements, p => p.Id == "place-a");
        Assert.Contains(afterPlacement.Document.Placements, p => p.Id == "place-b" && p.ComponentId == "note-a");

        await Change(service, key, 3, "remove_component", new { componentId = "note-a" });
        var afterComponent = await service.GetAsync(key);
        Assert.DoesNotContain(afterComponent.Document.Components, c => c.Id == "note-a");
        Assert.DoesNotContain(afterComponent.Document.Placements, p => p.ComponentId == "note-a");
        await using var db = factory.CreateDbContext();
        var last = await db.ActivityEvents.OrderBy(a => a.Id).LastAsync();
        Assert.Contains("Keep this reusable content.", JsonStrings(last.BeforeJson!));
    }

    [Fact]
    public async Task MoveComponents_ChangesPositionSizeStackingAndOutlineOrder_WithoutChangingContent()
    {
        using var factory = new SqliteTestFactory();
        var key = $"idea:{await SeedIdea(factory)}";
        var service = new IdeaWorkspaceService(factory);
        await AddNote(service, key, 0, "note-a", "place-a", "Unchanged note");

        await Change(service, key, 1, "move_components", new { placements = new[] { new { placementId = "place-a", x = -25.5, y = 342.25, width = 500.0, height = 260.0, zIndex = 4, order = 7 } } });
        var context = await service.GetAsync(key);
        var placement = Assert.Single(context.Document.Placements, p => p.Id == "place-a");
        Assert.Equal(-25.5, placement.X);
        Assert.Equal(342.25, placement.Y);
        Assert.Equal(500, placement.Width);
        Assert.Equal(260, placement.Height);
        Assert.Equal(4, placement.ZIndex);
        Assert.Equal(7, placement.Order);
        Assert.Equal("Unchanged note", Assert.Single(context.Document.Components, c => c.Id == "note-a").Content);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(-1, 100)]
    [InlineData(100, 0)]
    [InlineData(100, -1)]
    public async Task NonpositiveSize_IsRejected_WithoutMovingTheComponent(double width, double height)
    {
        using var factory = new SqliteTestFactory();
        var key = $"idea:{await SeedIdea(factory)}";
        var service = new IdeaWorkspaceService(factory);
        await AddNote(service, key, 0, "note-a", "place-a");

        var error = await Assert.ThrowsAsync<IdeaWorkspaceException>(() => Change(service, key, 1, "move_components", new { placements = new[] { new { placementId = "place-a", x = 999, width, height } } }));

        Assert.Equal(400, error.StatusCode);
        var context = await service.GetAsync(key);
        Assert.Equal(1, context.Revision);
        var placement = Assert.Single(context.Document.Placements, p => p.Id == "place-a");
        Assert.Equal(20, placement.X);
        Assert.Equal(280, placement.Width);
        Assert.Equal(180, placement.Height);
    }

    [Fact]
    public async Task MoveFrame_PreservesChildrenLocalCoordinates_AndRemovingAParentIsRejected()
    {
        using var factory = new SqliteTestFactory();
        var key = $"idea:{await SeedIdea(factory)}";
        var service = new IdeaWorkspaceService(factory);
        await AddComponent(service, key, 0, "frame-a", "frame-place", "frame");
        await AddNote(service, key, 1, "child", "child-place");
        await Change(service, key, 2, "move_components", new { placements = new[] { new { placementId = "child-place", parentId = "frame-place", x = 15, y = 25 } } });
        await Change(service, key, 3, "move_components", new { placements = new[] { new { placementId = "frame-place", x = 900, y = 650 } } });

        var context = await service.GetAsync(key);
        var child = Assert.Single(context.Document.Placements, p => p.Id == "child-place");
        Assert.Equal("frame-place", child.ParentId);
        Assert.Equal(15, child.X);
        Assert.Equal(25, child.Y);
        await Assert.ThrowsAsync<IdeaWorkspaceException>(() => Change(service, key, 4, "remove_placement", new { placementId = "frame-place" }));
        Assert.Equal(4, (await service.GetAsync(key)).Revision);
    }

    [Fact]
    public async Task InvalidMoveBatch_RollsBackEveryPlacement_AndWritesNoReceiptOrAudit()
    {
        using var factory = new SqliteTestFactory();
        var key = $"idea:{await SeedIdea(factory)}";
        var service = new IdeaWorkspaceService(factory);
        await AddNote(service, key, 0, "note-a", "place-a");
        var before = await service.GetAsync(key);
        var place = Assert.Single(before.Document.Placements, p => p.Id == "place-a");

        await Assert.ThrowsAsync<IdeaWorkspaceException>(() => Change(service, key, 1, "move_components", new
        {
            placements = new[]
            {
                new { placementId = "place-a", x = 900.0, width = 500.0 },
                new { placementId = "missing-placement", x = 10.0, width = 100.0 }
            }
        }));

        var after = await service.GetAsync(key);
        var actual = Assert.Single(after.Document.Placements, p => p.Id == "place-a");
        Assert.Equal(place.X, actual.X);
        Assert.Equal(place.Width, actual.Width);
        Assert.Equal(before.Revision, after.Revision);
        await using var db = factory.CreateDbContext();
        Assert.Single(await db.ActivityEvents.ToListAsync());
        Assert.Single(await db.Set<IdeaWorkspaceReceipt>().ToListAsync());
    }

    [Fact]
    public async Task ParentCycle_IsRejected_WithoutChangingPreviouslySavedFrames()
    {
        using var factory = new SqliteTestFactory();
        var key = $"idea:{await SeedIdea(factory)}";
        var service = new IdeaWorkspaceService(factory);
        await AddComponent(service, key, 0, "frame-a", "place-a", "frame");
        await AddComponent(service, key, 1, "frame-b", "place-b", "frame");
        await Change(service, key, 2, "move_components", new { placements = new[] { new { placementId = "place-b", parentId = "place-a" } } });

        await Assert.ThrowsAsync<IdeaWorkspaceException>(() => Change(service, key, 3, "move_components", new { placements = new[] { new { placementId = "place-a", parentId = "place-b" } } }));
        var context = await service.GetAsync(key);
        Assert.Equal(3, context.Revision);
        Assert.Null(Assert.Single(context.Document.Placements, p => p.Id == "place-a").ParentId);
        Assert.Equal("place-a", Assert.Single(context.Document.Placements, p => p.Id == "place-b").ParentId);
    }

    [Fact]
    public async Task ForeignWorkspacePlacement_CannotBeMoved_OrCauseAnEmptyWorkspaceSave()
    {
        using var factory = new SqliteTestFactory();
        var firstKey = $"idea:{await SeedIdea(factory, title: "First")}";
        var secondKey = $"idea:{await SeedIdea(factory, title: "Second")}";
        var service = new IdeaWorkspaceService(factory);
        await AddNote(service, firstKey, 0, "note-a", "first-place");

        await Assert.ThrowsAsync<IdeaWorkspaceException>(() => Change(service, secondKey, 0, "move_components", new { placements = new[] { new { placementId = "first-place", x = 99 } } }));
        Assert.Equal(1, (await service.GetAsync(firstKey)).Revision);
        Assert.Equal(0, (await service.GetAsync(secondKey)).Revision);
        await using var db = factory.CreateDbContext();
        Assert.Single(await db.Set<IdeaWorkspaceState>().ToListAsync());
        Assert.Single(await db.ActivityEvents.ToListAsync());
    }

    [Theory]
    [InlineData("script")]
    [InlineData("")]
    [InlineData("iframe")]
    public async Task UnsupportedComponentType_IsRejectedBeforeAnySave(string type)
    {
        using var factory = new SqliteTestFactory();
        var key = $"idea:{await SeedIdea(factory)}";
        var service = new IdeaWorkspaceService(factory);
        var error = await Assert.ThrowsAsync<IdeaWorkspaceException>(() => AddComponent(service, key, 0, "invalid", "place", type));
        Assert.Equal(400, error.StatusCode);
        await using var db = factory.CreateDbContext();
        Assert.Empty(await db.Set<IdeaWorkspaceState>().ToListAsync());
        Assert.Empty(await db.ActivityEvents.ToListAsync());
    }

    [Fact]
    public async Task DuplicateComponentId_IsRejected_WithoutReplacingItsOriginalContent()
    {
        using var factory = new SqliteTestFactory();
        var key = $"idea:{await SeedIdea(factory)}";
        var service = new IdeaWorkspaceService(factory);
        await AddNote(service, key, 0, "note-a", "place-a", "Original");
        await Assert.ThrowsAsync<IdeaWorkspaceException>(() => AddNote(service, key, 1, "note-a", "place-b", "Replacement"));
        var context = await service.GetAsync(key);
        Assert.Equal("Original", Assert.Single(context.Document.Components, c => c.Id == "note-a").Content);
        Assert.DoesNotContain(context.Document.Placements, p => p.Id == "place-b");
        Assert.Equal(1, context.Revision);
    }

    [Fact]
    public async Task MissingTaskReference_IsRejectedBeforeAnySave()
    {
        using var factory = new SqliteTestFactory();
        var key = $"idea:{await SeedIdea(factory)}";
        var service = new IdeaWorkspaceService(factory);
        await Assert.ThrowsAsync<IdeaWorkspaceException>(() => Change(service, key, 0, "create_component", new { component = new { id = "task-a", type = "task-reference", title = "Missing task", content = "", taskId = 123456 } }));
        Assert.Equal(0, (await service.GetAsync(key)).Revision);
        await using var db = factory.CreateDbContext();
        Assert.Empty(await db.Set<IdeaWorkspaceReceipt>().ToListAsync());
        Assert.Empty(await db.ActivityEvents.ToListAsync());
    }

    [Fact]
    public async Task StaleRevision_IsRejected_WithoutOverwritingTheNewerComponent()
    {
        using var factory = new SqliteTestFactory();
        var key = $"idea:{await SeedIdea(factory)}";
        var service = new IdeaWorkspaceService(factory);
        await AddNote(service, key, 0, "note-a", "place-a", "Newest saved content");

        var error = await Assert.ThrowsAsync<IdeaWorkspaceException>(() => Change(service, key, 0, "update_component", new { componentId = "note-a", content = "Stale overwrite" }));
        Assert.Equal(409, error.StatusCode);
        Assert.Equal("Newest saved content", Assert.Single((await service.GetAsync(key)).Document.Components, c => c.Id == "note-a").Content);
        await using var db = factory.CreateDbContext();
        Assert.Single(await db.ActivityEvents.ToListAsync());
    }

    [Fact]
    public async Task IdenticalOperationReplay_ReturnsOriginalIdentityAndRevision_WithoutDuplicateEffects()
    {
        using var factory = new SqliteTestFactory();
        var key = $"idea:{await SeedIdea(factory)}";
        var service = new IdeaWorkspaceService(factory);
        var command = Command(0, "create_component", new { component = new { type = "note", title = "Generated identity", content = "Exactly once" } }, operationId: "stable-operation");
        var first = await service.ExecuteAsync(key, command);
        await Change(service, key, 1, "add_comment", new { text = "An intervening save" });

        var retry = await service.ExecuteAsync(key, command);

        Assert.True(retry.Replayed);
        Assert.Equal(first.ComponentId, retry.ComponentId);
        Assert.Equal(first.PlacementId, retry.PlacementId);
        Assert.Equal(first.ActivityId, retry.ActivityId);
        Assert.Equal(1, retry.Revision);
        var context = await service.GetAsync(key);
        Assert.Equal(2, context.Revision);
        Assert.Single(context.Document.Components, c => c.Content == "Exactly once");
        await using var db = factory.CreateDbContext();
        Assert.Equal(2, await db.Set<IdeaWorkspaceReceipt>().CountAsync());
        Assert.Equal(2, await db.ActivityEvents.CountAsync());
    }

    [Fact]
    public async Task ReusedOperationWithDifferentPayload_Conflicts_AndRetainsOriginalReceipt()
    {
        using var factory = new SqliteTestFactory();
        var key = $"idea:{await SeedIdea(factory)}";
        var service = new IdeaWorkspaceService(factory);
        await service.ExecuteAsync(key, Command(0, "create_component", new { component = new { id = "note-a", type = "note", title = "Original", content = "First payload" } }, operationId: "same-id"));
        var error = await Assert.ThrowsAsync<IdeaWorkspaceException>(() => service.ExecuteAsync(key, Command(0, "create_component", new { component = new { id = "note-b", type = "note", title = "Other", content = "Second payload" } }, operationId: "same-id")));

        Assert.Equal(409, error.StatusCode);
        var context = await service.GetAsync(key);
        Assert.Contains(context.Document.Components, c => c.Id == "note-a" && c.Content == "First payload");
        Assert.DoesNotContain(context.Document.Components, c => c.Id == "note-b");
        await using var db = factory.CreateDbContext();
        Assert.Single(await db.Set<IdeaWorkspaceReceipt>().ToListAsync());
        Assert.Single(await db.ActivityEvents.ToListAsync());
    }

    [Fact]
    public async Task ReorderedPayloadProperties_ReplayTheSameReceipt_WithoutDuplicateEffects()
    {
        using var factory = new SqliteTestFactory();
        var key = $"idea:{await SeedIdea(factory)}";
        var service = new IdeaWorkspaceService(factory);
        using var originalPayload = JsonDocument.Parse("""
            {"component":{"id":"note-a","type":"note","title":"Same note","content":"Same content"},"placement":{"id":"place-a","x":20,"y":30,"width":280,"height":180}}
            """);
        using var reorderedPayload = JsonDocument.Parse("""
            {"placement":{"height":180,"width":280,"y":30,"x":20,"id":"place-a"},"component":{"content":"Same content","title":"Same note","type":"note","id":"note-a"}}
            """);
        var original = new IdeaWorkspaceCommand("canonical-operation", 0, "create_component", originalPayload.RootElement, "test-client");
        var reordered = original with { Payload = reorderedPayload.RootElement };

        var first = await service.ExecuteAsync(key, original);
        var replay = await service.ExecuteAsync(key, reordered);

        Assert.True(replay.Replayed);
        Assert.Equal(first.ComponentId, replay.ComponentId);
        Assert.Equal(first.PlacementId, replay.PlacementId);
        Assert.Equal(first.ActivityId, replay.ActivityId);
        Assert.Equal(first.Revision, replay.Revision);
        var context = await service.GetAsync(key);
        Assert.Equal(1, context.Revision);
        Assert.Single(context.Document.Components, c => c.Id == "note-a" && c.Content == "Same content");
        await using var db = factory.CreateDbContext();
        Assert.Single(await db.Set<IdeaWorkspaceReceipt>().ToListAsync());
        Assert.Single(await db.ActivityEvents.ToListAsync());
    }

    [Fact]
    public async Task UnknownTypedPayloadMember_IsRejected_InsteadOfSilentlyLosingIt()
    {
        using var factory = new SqliteTestFactory();
        var key = $"idea:{await SeedIdea(factory)}";
        var service = new IdeaWorkspaceService(factory);

        var error = await Assert.ThrowsAsync<IdeaWorkspaceException>(() => Change(service, key, 0, "create_component", new
        {
            component = new { id = "table", type = "table", title = "Comparison", content = "Supported content", unsupportedRows = new[] { "Authored data that must not disappear" } }
        }));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal("invalid_payload", error.Code);
        Assert.Equal(0, (await service.GetAsync(key)).Revision);
        await using var db = factory.CreateDbContext();
        Assert.Empty(await db.Set<IdeaWorkspaceState>().ToListAsync());
        Assert.Empty(await db.Set<IdeaWorkspaceReceipt>().ToListAsync());
        Assert.Empty(await db.ActivityEvents.ToListAsync());
    }

    [Theory]
    [InlineData("update_component", "{\"componentId\":\"note-a\",\"content\":\"Changed\",\"unsupportedRows\":[\"must not disappear\"]}")]
    [InlineData("move_components", "{\"placements\":[{\"placementId\":\"place-a\",\"x\":500,\"unsupportedRows\":[\"must not disappear\"]}]}")]
    public async Task UnknownManuallyParsedFields_AreRejectedWithoutChangingContentOrAudit(string action, string payload)
    {
        using var factory = new SqliteTestFactory();
        var key = $"idea:{await SeedIdea(factory)}";
        var service = new IdeaWorkspaceService(factory);
        await AddNote(service, key, 0, "note-a", "place-a", "Original content");
        var before = await service.GetAsync(key);
        using var parsed = JsonDocument.Parse(payload);
        var error = await Assert.ThrowsAsync<IdeaWorkspaceException>(() => service.ExecuteAsync(key,
            new IdeaWorkspaceCommand("unknown-manual", 1, action, parsed.RootElement, "test-client")));
        Assert.Equal(400, error.StatusCode);
        var after = await service.GetAsync(key);
        Assert.Equal(1, after.Revision);
        Assert.Equal("Original content", Assert.Single(after.Document.Components, c => c.Id == "note-a").Content);
        Assert.Equal(before.Document.Placements.Single(p => p.Id == "place-a").X,
            after.Document.Placements.Single(p => p.Id == "place-a").X);
        await using var db = factory.CreateDbContext();
        Assert.Equal(1, await db.ActivityEvents.CountAsync());
        Assert.Equal(1, await db.Set<IdeaWorkspaceReceipt>().CountAsync());
    }

    [Theory]
    [InlineData("", "test-client")]
    [InlineData("  ", "test-client")]
    [InlineData("operation", "")]
    [InlineData("operation", "  ")]
    public async Task MissingOperationOrActor_IsRejectedBeforeAnySave(string operationId, string actor)
    {
        using var factory = new SqliteTestFactory();
        var key = $"idea:{await SeedIdea(factory)}";
        var error = await Assert.ThrowsAsync<IdeaWorkspaceException>(() => new IdeaWorkspaceService(factory).ExecuteAsync(key,
            Command(0, "add_comment", new { text = "No successful write" }, operationId, actor)));
        Assert.Equal(400, error.StatusCode);
        await using var db = factory.CreateDbContext();
        Assert.Empty(await db.Set<IdeaWorkspaceState>().ToListAsync());
        Assert.Empty(await db.ActivityEvents.ToListAsync());
    }

    [Fact]
    public async Task AuditInsertFailure_RollsBackStateAndReceipt()
    {
        using var factory = new SqliteTestFactory();
        var key = $"idea:{await SeedIdea(factory)}";
        await using (var db = factory.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER FailWorkspaceAudit BEFORE INSERT ON ActivityEvents BEGIN SELECT RAISE(ABORT, 'audit write rejected'); END;");

        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => AddNote(new IdeaWorkspaceService(factory), key, 0, "note-a", "place-a"));
        var sqliteFailure = Assert.IsType<SqliteException>(failure.InnerException);
        Assert.Contains("audit write rejected", sqliteFailure.Message, StringComparison.Ordinal);

        await using var verify = factory.CreateDbContext();
        Assert.Empty(await verify.Set<IdeaWorkspaceState>().ToListAsync());
        Assert.Empty(await verify.Set<IdeaWorkspaceReceipt>().ToListAsync());
        Assert.Empty(await verify.ActivityEvents.ToListAsync());
    }

    [Fact]
    public async Task ReceiptInsertFailure_AfterStateAndAuditSave_RollsBackAllThreeEffects()
    {
        using var factory = new SqliteTestFactory();
        var key = $"idea:{await SeedIdea(factory)}";
        await using (var db = factory.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER FailWorkspaceReceipt BEFORE INSERT ON IdeaWorkspaceReceipts BEGIN SELECT RAISE(ABORT, 'receipt write rejected'); END;");

        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => AddNote(new IdeaWorkspaceService(factory), key, 0, "note-a", "place-a"));
        var sqliteFailure = Assert.IsType<SqliteException>(failure.InnerException);
        Assert.Contains("receipt write rejected", sqliteFailure.Message, StringComparison.Ordinal);

        await using var verify = factory.CreateDbContext();
        Assert.Empty(await verify.Set<IdeaWorkspaceState>().ToListAsync());
        Assert.Empty(await verify.Set<IdeaWorkspaceReceipt>().ToListAsync());
        Assert.Empty(await verify.ActivityEvents.ToListAsync());
    }

    [Fact]
    public async Task ConnectorAndThreadedComments_RetainTheirAnchorsFullTextAndAuthors()
    {
        using var factory = new SqliteTestFactory();
        var key = $"idea:{await SeedIdea(factory)}";
        var service = new IdeaWorkspaceService(factory);
        await AddNote(service, key, 0, "question", "question-place");
        await AddNote(service, key, 1, "evidence", "evidence-place");
        await Change(service, key, 2, "create_connector", new { connector = new { id = "edge", fromComponentId = "evidence", toComponentId = "question", label = "answers" } });
        await Change(service, key, 3, "add_comment", new { componentId = "question", text = "  first question\r\n\r\n    context  \r\n" });
        var parent = Assert.Single((await service.GetAsync(key)).Document.Comments);
        await service.ExecuteAsync(key, Command(4, "add_comment", new { componentId = "question", replyToId = parent.Id, text = "\r\n  full answer  \r\n" }, actor: "other-client"));

        var context = await service.GetAsync(key);
        var connector = Assert.Single(context.Document.Connectors);
        Assert.Equal("evidence", connector.FromComponentId);
        Assert.Equal("question", connector.ToComponentId);
        Assert.Equal("answers", connector.Label);
        Assert.Equal("  first question\r\n\r\n    context  \r\n", Assert.Single(context.Document.Comments, c => c.Id == parent.Id).Text);
        var reply = Assert.Single(context.Document.Comments, c => c.ReplyToId == parent.Id);
        Assert.Equal("question", reply.ComponentId);
        Assert.Equal("\r\n  full answer  \r\n", reply.Text);
        Assert.Equal("other-client", reply.Actor);

        var failure = await Assert.ThrowsAsync<IdeaWorkspaceException>(() => Change(service, key, 5, "add_comment", new { componentId = "evidence", replyToId = parent.Id, text = "Wrong thread" }));
        Assert.Equal(400, failure.StatusCode);
        Assert.Equal(5, (await service.GetAsync(key)).Revision);
        Assert.Equal(2, (await service.GetAsync(key)).Document.Comments.Count);
    }

    [Fact]
    public async Task RemovedComponentDiscussion_AcceptsRepliesOnItsHistoricalAnchor_AndPreservesAuthoredText()
    {
        using var factory = new SqliteTestFactory();
        var key = $"idea:{await SeedIdea(factory)}";
        var service = new IdeaWorkspaceService(factory);
        const string content = "\r\n  # Removed card Ω\r\n\r\n    retain its complete context  \r\n";
        const string question = "  Original question Ω\r\n\r\n    complete discussion context  \r\n";
        const string answer = "\r\n  Answer after removal Ω\r\n\r\n    preserve this reply  \r\n";
        await AddNote(service, key, 0, "historical-card", "historical-place", content);
        await Change(service, key, 1, "add_comment", new { componentId = "historical-card", text = question });
        var parent = Assert.Single((await service.GetAsync(key)).Document.Comments);
        await Change(service, key, 2, "remove_component", new { componentId = "historical-card" });

        await service.ExecuteAsync(key, Command(3, "add_comment",
            new { componentId = "historical-card", replyToId = parent.Id, text = answer }, actor: "reply-client"));

        var context = await service.GetAsync(key);
        Assert.Equal(4, context.Revision);
        Assert.DoesNotContain(context.Document.Components, item => item.Id == "historical-card");
        var retained = Assert.Single(context.Document.Comments, item => item.Id == parent.Id);
        Assert.Equal(question, retained.Text);
        Assert.Equal("test-client", retained.Actor);
        Assert.Equal("historical-card", retained.ComponentId);
        var reply = Assert.Single(context.Document.Comments, item => item.ReplyToId == parent.Id);
        Assert.Equal("historical-card", reply.ComponentId);
        Assert.Equal(answer, reply.Text);
        Assert.Equal("reply-client", reply.Actor);
        await using var db = factory.CreateDbContext();
        var removal = await db.ActivityEvents.SingleAsync(item => item.Message == "remove_component");
        Assert.Contains(content, JsonStrings(removal.BeforeJson!));
        var replyAudit = await db.ActivityEvents.OrderBy(item => item.Id).LastAsync();
        Assert.Contains(question, JsonStrings(replyAudit.AfterJson!));
        Assert.Contains(answer, JsonStrings(replyAudit.AfterJson!));
        Assert.Equal("reply-client", replyAudit.Actor);
        Assert.Equal(4, await db.IdeaWorkspaceReceipts.CountAsync());
    }

    [Theory]
    [InlineData("top-level", "component_not_found", 404)]
    [InlineData("missing-parent", "comment_not_found", 404)]
    [InlineData("wrong-anchor", "comment_anchor_mismatch", 400)]
    [InlineData("omitted-anchor", "comment_anchor_mismatch", 400)]
    public async Task RemovedComponentDiscussion_RejectsNewOrMismatchedThreads_WithoutAnySavedEffect(
        string attempt, string code, int statusCode)
    {
        using var factory = new SqliteTestFactory();
        var key = $"idea:{await SeedIdea(factory)}";
        var service = new IdeaWorkspaceService(factory);
        await AddNote(service, key, 0, "historical-card", "historical-place");
        await Change(service, key, 1, "add_comment", new { componentId = "historical-card", text = "Original thread" });
        var parent = Assert.Single((await service.GetAsync(key)).Document.Comments);
        await Change(service, key, 2, "remove_component", new { componentId = "historical-card" });
        var before = await service.GetAsync(key);
        var payload = new Dictionary<string, object?> { ["text"] = "Must not create a new thread" };
        if (attempt != "omitted-anchor") payload["componentId"] = attempt == "wrong-anchor" ? "other-card" : "historical-card";
        if (attempt != "top-level") payload["replyToId"] = attempt == "missing-parent" ? "missing-comment" : parent.Id;

        var error = await Assert.ThrowsAsync<IdeaWorkspaceException>(() => Change(service, key, 3, "add_comment", payload));

        Assert.Equal(code, error.Code);
        Assert.Equal(statusCode, error.StatusCode);
        var after = await service.GetAsync(key);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(JsonSerializer.Serialize(before.Document, Json), JsonSerializer.Serialize(after.Document, Json));
        await using var db = factory.CreateDbContext();
        Assert.Equal(3, await db.IdeaWorkspaceReceipts.CountAsync());
        Assert.Equal(3, await db.ActivityEvents.CountAsync());
    }

    [Fact]
    public async Task WorkspaceDiscussionReply_WithOmittedAnchor_RemainsInItsWorkspaceThread()
    {
        using var factory = new SqliteTestFactory();
        var key = $"idea:{await SeedIdea(factory)}";
        var service = new IdeaWorkspaceService(factory);
        await Change(service, key, 0, "add_comment", new { text = "Workspace-level parent" });
        var parent = Assert.Single((await service.GetAsync(key)).Document.Comments);

        await Change(service, key, 1, "add_comment", new { replyToId = parent.Id, text = "  Workspace-level reply Ω\r\n" });

        var reply = Assert.Single((await service.GetAsync(key)).Document.Comments, item => item.ReplyToId == parent.Id);
        Assert.Null(reply.ComponentId);
        Assert.Equal("  Workspace-level reply Ω\r\n", reply.Text);
    }

    [Fact]
    public async Task Checkpoints_AreIndependentAcrossClientsSessionsAndWorkspaces()
    {
        using var factory = new SqliteTestFactory();
        var firstKey = $"idea:{await SeedIdea(factory, title: "First")}";
        var secondKey = $"idea:{await SeedIdea(factory, title: "Second")}";
        var service = new IdeaWorkspaceService(factory);
        await Change(service, firstKey, 0, "save_checkpoint", Checkpoint("client-a", "session-1", "First objective"));
        await Change(service, firstKey, 1, "save_checkpoint", Checkpoint("client-b", "session-1", "Other client"));
        await Change(service, firstKey, 2, "save_checkpoint", Checkpoint("client-a", "session-2", "Other session"));
        await Change(service, secondKey, 0, "save_checkpoint", Checkpoint("client-a", "session-1", "Second workspace"));
        await Change(service, firstKey, 3, "save_checkpoint", Checkpoint("client-a", "session-1", "Revised first objective"));

        var first = await service.GetAsync(firstKey);
        Assert.Equal(3, first.Document.Checkpoints.Count);
        Assert.Equal("Revised first objective", Assert.Single(first.Document.Checkpoints, c => c.ClientId == "client-a" && c.SessionId == "session-1").Objective);
        Assert.Equal("Other client", Assert.Single(first.Document.Checkpoints, c => c.ClientId == "client-b").Objective);
        Assert.Equal("Other session", Assert.Single(first.Document.Checkpoints, c => c.SessionId == "session-2").Objective);
        var second = await service.GetAsync(secondKey);
        Assert.Equal("Second workspace", Assert.Single(second.Document.Checkpoints).Objective);
        Assert.Equal(4, first.Revision);
        Assert.Equal(1, second.Revision);
    }

    [Fact]
    public async Task SavedWorkspace_SurvivesSourceDeletion_WithOriginalContentAndSavedComponents()
    {
        using var factory = new SqliteTestFactory();
        var id = await SeedIdea(factory, body: "  original idea\r\n", plan: "\r\noriginal plan  ");
        var key = $"idea:{id}";
        var service = new IdeaWorkspaceService(factory);
        await AddNote(service, key, 0, "retained", "retained-place", "Saved separately");
        await using (var db = factory.CreateDbContext())
        {
            db.Ideas.Remove(await db.Ideas.SingleAsync(i => i.Id == id));
            await db.SaveChangesAsync();
        }

        var reopened = await service.GetAsync(key);
        Assert.True(reopened.SourceMissing);
        Assert.Null(reopened.Source);
        Assert.Equal("  original idea\r\n", reopened.OriginalSource.Body);
        Assert.Equal("\r\noriginal plan  ", reopened.OriginalSource.Plan);
        Assert.Contains(reopened.Document.Components, c => c.Id == "retained" && c.Content == "Saved separately");
        Assert.Equal(1, reopened.Revision);
    }

    [Fact]
    public async Task LaterLegacySourceEdit_IsVisible_WhileOriginalSnapshotAndSavedCanvasArePreserved()
    {
        using var factory = new SqliteTestFactory();
        var id = await SeedIdea(factory, title: "Original title", body: "  original body\r\n", plan: "\r\noriginal plan  ");
        var key = $"idea:{id}";
        var service = new IdeaWorkspaceService(factory);
        await AddNote(service, key, 0, "saved-note", "saved-place", "Keep the canvas separate");
        await using (var db = factory.CreateDbContext())
        {
            var legacy = await db.Ideas.SingleAsync(i => i.Id == id);
            legacy.Title = "Edited legacy title";
            legacy.Body = "  newer legacy body\r\n";
            legacy.Plan = "\r\nnewer legacy plan  ";
            await db.SaveChangesAsync();
        }

        await Change(service, key, 1, "add_comment", new { text = "A later workspace save" });
        var context = await service.GetAsync(key);
        Assert.Equal("Edited legacy title", context.Source!.Title);
        Assert.Equal("  newer legacy body\r\n", context.Source.Body);
        Assert.Equal("\r\nnewer legacy plan  ", context.Source.Plan);
        Assert.Equal("Original title", context.OriginalSource.Title);
        Assert.Equal("  original body\r\n", context.OriginalSource.Body);
        Assert.Equal("\r\noriginal plan  ", context.OriginalSource.Plan);
        Assert.Contains(context.Document.Components, c => c.Id == "source-brief" && c.Content == "  original body\r\n");
        Assert.Contains(context.Document.Components, c => c.Id == "saved-note" && c.Content == "Keep the canvas separate");
        Assert.Equal(2, context.Revision);
    }

    [Fact]
    public async Task ProposedPlan_IsSeparateFromAcceptedRevision_AndPreservesCompleteItemContent()
    {
        using var factory = new SqliteTestFactory();
        var key = $"idea:{await SeedIdea(factory)}";
        var service = new IdeaWorkspaceService(factory);
        await Change(service, key, 0, "propose_plan", new { title = "First proposal", items = new[] { new { id = "item-a", title = "First deliverable", description = "  full detail\r\n", acceptanceCriteria = new[] { "Original criterion" }, dependsOn = Array.Empty<string>() } } });
        var draft = Assert.Single((await service.GetAsync(key)).Document.PlanRevisions);
        Assert.Equal("Draft", draft.Status);
        await Change(service, key, 1, "accept_plan", new { planRevisionId = draft.Id });
        await Change(service, key, 2, "propose_plan", new { title = "Later proposal", items = new[] { new { id = "item-b", title = "Additional scope", description = "Later detail", acceptanceCriteria = new[] { "Later criterion" }, dependsOn = Array.Empty<string>() } } });

        var context = await service.GetAsync(key);
        Assert.Equal(draft.Id, context.Document.AcceptedPlanRevisionId);
        var accepted = Assert.Single(context.Document.PlanRevisions, p => p.Id == draft.Id);
        Assert.Equal("Accepted", accepted.Status);
        Assert.Equal("  full detail\r\n", Assert.Single(accepted.Items).Description);
        Assert.Equal("Original criterion", Assert.Single(Assert.Single(accepted.Items).AcceptanceCriteria));
        Assert.Contains(context.Document.PlanRevisions, p => p.Title == "Later proposal" && p.Status == "Draft");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidPlanDependencies_AreRejectedWithoutSavingADraft(bool cycle)
    {
        using var factory = new SqliteTestFactory();
        var key = $"idea:{await SeedIdea(factory)}";
        var service = new IdeaWorkspaceService(factory);
        var error = await Assert.ThrowsAsync<IdeaWorkspaceException>(() => Change(service, key, 0, "propose_plan", new
        {
            title = "Invalid dependencies",
            items = new[]
            {
                new { id = "a", title = "A", description = "A", acceptanceCriteria = Array.Empty<string>(), dependsOn = new[] { cycle ? "b" : "missing" } },
                new { id = "b", title = "B", description = "B", acceptanceCriteria = Array.Empty<string>(), dependsOn = cycle ? new[] { "a" } : Array.Empty<string>() }
            }
        }));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal(cycle ? "dependency_cycle" : "missing_dependency", error.Code);
        Assert.Empty((await service.GetAsync(key)).Document.PlanRevisions);
        await using var db = factory.CreateDbContext();
        Assert.Empty(await db.Set<IdeaWorkspaceState>().ToListAsync());
        Assert.Empty(await db.ActivityEvents.ToListAsync());
    }

    [Fact]
    public async Task ProposedPlan_ModerateDependencyDepth_PreservesEveryAuthoredItem()
    {
        using var factory = new SqliteTestFactory();
        var key = $"idea:{await SeedIdea(factory)}";
        var service = new IdeaWorkspaceService(factory);
        const int depth = 128;
        var items = Enumerable.Range(0, depth)
            .Select(index => PlanItem($"item-{index}", index + 1 < depth ? [$"item-{index + 1}"] : []))
            .ToArray();

        await Change(service, key, 0, "propose_plan", new { title = "Complete dependency chain", items });

        var saved = Assert.Single((await service.GetAsync(key)).Document.PlanRevisions);
        Assert.Equal(depth, saved.Items.Count);
        Assert.Equal(JsonSerializer.Serialize(items, Json), JsonSerializer.Serialize(saved.Items, Json));
    }

    [Fact]
    public async Task ProposedPlan_SharedDependencyGraph_PreservesEveryAuthoredBranch()
    {
        using var factory = new SqliteTestFactory();
        var key = $"idea:{await SeedIdea(factory)}";
        var service = new IdeaWorkspaceService(factory);
        var items = new[]
        {
            PlanItem("first-root", "left", "right"),
            PlanItem("second-root", "right", "shared-a"),
            PlanItem("left", "shared-a", "shared-b"),
            PlanItem("right", "shared-a", "shared-b"),
            PlanItem("shared-a", "tail"),
            PlanItem("shared-b", "tail"),
            PlanItem("tail"),
            PlanItem("independent")
        };

        await Change(service, key, 0, "propose_plan", new { title = "Shared prerequisites", items });

        var saved = Assert.Single((await service.GetAsync(key)).Document.PlanRevisions);
        Assert.Equal(JsonSerializer.Serialize(items, Json), JsonSerializer.Serialize(saved.Items, Json));
    }

    [Theory]
    [InlineData("cycle", "dependency_cycle")]
    [InlineData("missing", "missing_dependency")]
    [InlineData("null", "missing_dependency")]
    public async Task ProposedPlan_InvalidSharedDependencyBranch_RejectsTheWholeDraft(string invalid, string code)
    {
        using var factory = new SqliteTestFactory();
        var key = $"idea:{await SeedIdea(factory)}";
        var service = new IdeaWorkspaceService(factory);
        var items = new[]
        {
            PlanItem("complete"),
            PlanItem("root", "left", "right"),
            PlanItem("left", "shared"),
            PlanItem("right", "shared", invalid == "cycle" ? "root" : invalid == "null" ? null! : "missing"),
            PlanItem("shared", "complete")
        };

        var error = await Assert.ThrowsAsync<IdeaWorkspaceException>(() => Change(service, key, 0,
            "propose_plan", new { title = "Invalid branch after a shared prerequisite", items }));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal(code, error.Code);
        Assert.Empty((await service.GetAsync(key)).Document.PlanRevisions);
        await using var db = factory.CreateDbContext();
        Assert.Empty(await db.IdeaWorkspaceStates.ToListAsync());
        Assert.Empty(await db.IdeaWorkspaceReceipts.ToListAsync());
        Assert.Empty(await db.ActivityEvents.ToListAsync());
    }

    [Fact]
    public async Task LinkedPlanAcceptance_WithoutCanonicalProjectRevision_LeavesTheDraftIntact()
    {
        using var factory = new SqliteTestFactory();
        var projectId = await SeedProject(factory);
        var ideaId = await SeedIdea(factory);
        await using (var db = factory.CreateDbContext())
        {
            var idea = await db.Ideas.SingleAsync(i => i.Id == ideaId);
            idea.Status = IdeaStatus.Promoted;
            idea.PromotedProjectId = projectId;
            await db.SaveChangesAsync();
        }
        var key = $"idea:{ideaId}";
        var service = new IdeaWorkspaceService(factory);
        await Change(service, key, 0, "propose_plan", new { title = "Proposal", items = new[] { new { id = "item", title = "Item", description = "Details", acceptanceCriteria = Array.Empty<string>(), dependsOn = Array.Empty<string>() } } });
        var draft = Assert.Single((await service.GetAsync(key)).Document.PlanRevisions);

        var error = await Assert.ThrowsAsync<IdeaWorkspaceException>(() => Change(service, key, 1, "accept_plan", new { planRevisionId = draft.Id }));
        Assert.Equal(409, error.StatusCode);
        var context = await service.GetAsync(key);
        Assert.Equal(1, context.Revision);
        Assert.Null(context.Document.AcceptedPlanRevisionId);
        Assert.Equal("Draft", Assert.Single(context.Document.PlanRevisions).Status);
    }

    [Fact]
    public async Task FileDatabase_ReopenedByFreshFactory_RetainsAcknowledgedContentCheckpointAndReceipt()
    {
        using var database = new WorkspaceFileDatabase();
        string key;
        IdeaWorkspaceCommand original;
        using (var first = database.CreateFactory())
        {
            var id = await SeedIdea(first, body: "  durable original\r\n");
            key = $"idea:{id}";
            var service = new IdeaWorkspaceService(first);
            original = Command(0, "create_component", new { component = new { id = "durable", type = "note", title = "Durable", content = "  persisted\r\n\r\nfinal text  " } }, operationId: "restart-operation");
            await service.ExecuteAsync(key, original);
            await Change(service, key, 1, "save_checkpoint", Checkpoint("external-client", "session", "Continue after restart"));
        }

        using var reopened = database.CreateFactory();
        var secondService = new IdeaWorkspaceService(reopened);
        var context = await secondService.GetAsync(key);
        Assert.Equal(2, context.Revision);
        Assert.Equal("  persisted\r\n\r\nfinal text  ", Assert.Single(context.Document.Components, c => c.Id == "durable").Content);
        Assert.Equal("Continue after restart", Assert.Single(context.Document.Checkpoints).Objective);
        Assert.Equal("  durable original\r\n", context.OriginalSource.Body);
        var replay = await secondService.ExecuteAsync(key, original);
        Assert.True(replay.Replayed);
        Assert.Equal(1, replay.Revision);
    }

    [Fact]
    public async Task ConcurrentIndependentConnections_AllowOneWriter_AndRejectTheStaleWriter()
    {
        using var database = new WorkspaceFileDatabase();
        using var firstFactory = database.CreateFactory();
        var key = $"idea:{await SeedIdea(firstFactory)}";
        using var secondFactory = database.CreateFactory();
        var firstService = new IdeaWorkspaceService(firstFactory);
        var secondService = new IdeaWorkspaceService(secondFactory);

        using var start = new Barrier(2);
        var attempts = await Task.WhenAll(
            Task.Run(async () => { Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(10))); return await Attempt(firstService, key, "first"); }),
            Task.Run(async () => { Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(10))); return await Attempt(secondService, key, "second"); }));

        Assert.Single(attempts, error => error is null);
        var conflict = Assert.Single(attempts, error => error is not null);
        var typed = Assert.IsType<IdeaWorkspaceException>(conflict);
        Assert.Equal(409, typed.StatusCode);
        var context = await firstService.GetAsync(key);
        Assert.Equal(1, context.Revision);
        Assert.Single(context.Document.Components, c => c.Id is "first" or "second");
        await using var db = firstFactory.CreateDbContext();
        Assert.Single(await db.ActivityEvents.ToListAsync());
        Assert.Single(await db.Set<IdeaWorkspaceReceipt>().ToListAsync());
    }

    [Fact]
    public async Task ContextRead_UsesOneSnapshot_WhenAnotherConnectionCommitsBetweenItsQueries()
    {
        using var database = new WorkspaceFileDatabase(enableWal: true);
        using var writer = database.CreateFactory();
        var id = await SeedIdea(writer, body: "Source before concurrent save");
        var key = $"idea:{id}";
        await AddNote(new IdeaWorkspaceService(writer), key, 0, "note-a", "place-a");
        var interceptor = new AfterWorkspaceStateRead(async () =>
        {
            await using var write = writer.CreateDbContext();
            await using var transaction = await write.Database.BeginTransactionAsync();
            await write.Database.ExecuteSqlInterpolatedAsync($"UPDATE Ideas SET Body = {"Source after concurrent save"} WHERE Id = {id}");
            await write.Database.ExecuteSqlInterpolatedAsync($"UPDATE IdeaWorkspaceStates SET Revision = 2 WHERE WorkspaceId = {key}");
            await transaction.CommitAsync();
        });
        using var readerFactory = database.CreateFactory(interceptor);

        var context = await new IdeaWorkspaceService(readerFactory).GetAsync(key);

        Assert.True(interceptor.Fired);
        Assert.Equal(1, context.Revision);
        Assert.Equal("Source before concurrent save", context.Source!.Body);
        var afterCommit = await new IdeaWorkspaceService(writer).GetAsync(key);
        Assert.Equal(2, afterCommit.Revision);
        Assert.Equal("Source after concurrent save", afterCommit.Source!.Body);
    }

    private static async Task<Exception?> Attempt(IdeaWorkspaceService service, string key, string id)
    {
        try
        {
            await AddNote(service, key, 0, id, id + "-place");
            return null;
        }
        catch (Exception error)
        {
            return error;
        }
    }

    private static object Checkpoint(string client, string session, string objective) => new
    {
        clientId = client,
        sessionId = session,
        objective,
        focusedComponentIds = Array.Empty<string>(),
        completedChanges = new[] { "Captured context" },
        openQuestions = new[] { "Next question" },
        nextActions = new[] { "Next action" },
        lastAcknowledgedActivityId = 0
    };

    private static Task<IdeaWorkspaceMutationResult> AddNote(IdeaWorkspaceService service, string key, long revision, string id, string placement, string content = "A note") =>
        AddComponent(service, key, revision, id, placement, "note", content);

    private static IdeaWorkspacePlanItem PlanItem(string id, params string[] dependencies) => new()
    {
        Id = id, Title = $"  Deliver {id} Ω  ", Description = $"\r\n  # Complete {id} description Ω\r\n\r\n    preserve indentation  \r\n",
        AcceptanceCriteria = [$"  Full {id} criterion Ω  \r\n"], DependsOn = [.. dependencies]
    };

    private static Task<IdeaWorkspaceMutationResult> AddComponent(IdeaWorkspaceService service, string key, long revision, string id, string placement, string type, string content = "") =>
        Change(service, key, revision, "create_component", new
        {
            component = new { id, type, title = id, content },
            placement = new { id = placement, x = 20, y = 30, width = 280, height = 180, zIndex = 0, order = 0 }
        });

    private static Task<IdeaWorkspaceMutationResult> Change(IdeaWorkspaceService service, string key, long revision, string action, object payload) =>
        service.ExecuteAsync(key, Command(revision, action, payload));

    private static IdeaWorkspaceCommand Command(long revision, string action, object payload, string? operationId = null, string actor = "test-client") =>
        new(operationId ?? Guid.NewGuid().ToString("N"), revision, action, JsonSerializer.SerializeToElement(payload, Json), actor);

    private static async Task<int> SeedIdea(IDbContextFactory<AppDbContext> factory, string title = "Idea", string? body = null, string? plan = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        var idea = new Idea { Title = title, Body = body, Plan = plan };
        db.Ideas.Add(idea);
        await db.SaveChangesAsync();
        return idea.Id;
    }

    private static async Task<int> SeedProject(IDbContextFactory<AppDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        var project = new Project { Name = "Project", Description = "  project description\r\n", Goals = "\r\nproject goals  ", Readme = "# Original readme\r\n\r\n    retained\r\n" };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        return project.Id;
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

    private sealed class WorkspaceFileDatabase : IDisposable
    {
        private readonly string path;

        public WorkspaceFileDatabase(bool enableWal = false)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KanbanFlow.slnx")))
                directory = directory.Parent;
            if (directory is null)
                throw new InvalidOperationException("The test output must remain beneath the repository.");
            var scratch = Path.Combine(directory.FullName, "_sandbox", "579", "tests");
            Directory.CreateDirectory(scratch);
            path = Path.Combine(scratch, Guid.NewGuid().ToString("N") + ".db");
            using var factory = CreateFactory();
            using var db = factory.CreateDbContext();
            db.Database.EnsureCreated();
            if (enableWal)
            {
                db.Database.OpenConnection();
                using var command = db.Database.GetDbConnection().CreateCommand();
                command.CommandText = "PRAGMA journal_mode=WAL";
                Assert.Equal("wal", command.ExecuteScalar());
            }
        }

        public FileFactory CreateFactory(DbCommandInterceptor? interceptor = null) => new(path, interceptor);

        public void Dispose()
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var ownedFile = path + suffix;
                if (File.Exists(ownedFile)) File.Delete(ownedFile);
            }
        }
    }

    private sealed class FileFactory(string path, DbCommandInterceptor? interceptor = null) : IDbContextFactory<AppDbContext>, IDisposable
    {
        private readonly DbContextOptions<AppDbContext> options = Options(path, interceptor);

        private static DbContextOptions<AppDbContext> Options(string path, DbCommandInterceptor? interceptor)
        {
            var builder = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 15 }.ToString());
            if (interceptor is not null) builder.AddInterceptors(interceptor);
            return builder.Options;
        }

        public AppDbContext CreateDbContext() => new(options);
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
        public void Dispose() { }
    }

    private sealed class AfterWorkspaceStateRead(Func<Task> commitWriter) : DbCommandInterceptor
    {
        private int fired;
        public bool Fired => Volatile.Read(ref fired) != 0;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"IdeaWorkspaceStates\"", StringComparison.Ordinal) &&
                Interlocked.CompareExchange(ref fired, 1, 0) == 0)
                await commitWriter();
            return result;
        }
    }
}
