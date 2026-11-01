using System.Text.Json;
using KanbanFlow.Data;
using KanbanFlow.Models;
using KanbanFlow.Models.IdeaWorkspaces;
using KanbanFlow.Services.IdeaWorkspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace KanbanFlow.Tests;

public sealed class IdeaWorkspaceQueryTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Capabilities_DescribeTypedComponentsActionsAndExplicitPagingLimits()
    {
        using var factory = new SqliteTestFactory();
        var capabilities = new IdeaWorkspaceService(factory).GetCapabilities();

        Assert.Equal(1, capabilities.ContractVersion);
        Assert.Equal(50, capabilities.DefaultPageSize);
        Assert.Equal(500, capabilities.MaximumPageSize);
        Assert.Equal(600, capabilities.SnapshotLifetimeSeconds);
        Assert.Contains("frame", capabilities.ComponentTypes);
        Assert.Contains("table", capabilities.ComponentTypes);
        Assert.Contains("task-reference", capabilities.ComponentTypes);
        Assert.Contains("create_component", capabilities.Actions);
        Assert.Contains("move_components", capabilities.Actions);
        Assert.Contains("save_checkpoint", capabilities.Actions);
    }

    [Fact]
    public async Task EmptyCatalog_ReturnsAnExplicitEmptyPage_WithoutWritingRecords()
    {
        using var factory = new SqliteTestFactory();
        var page = await new IdeaWorkspaceService(factory).ListAsync();

        Assert.Empty(page.Items);
        Assert.Equal(0, page.Total);
        Assert.Equal(50, page.Limit);
        Assert.Null(page.NextCursor);
        await AssertNoWorkspaceWrites(factory);
    }

    [Fact]
    public async Task MixedCatalogBeyondOneHundred_ReturnsEveryIdeaAndProjectExactlyOnce_WithoutFakeSources()
    {
        using var factory = new SqliteTestFactory();
        var expected = await SeedCatalog(factory, 137, 9);
        var service = new IdeaWorkspaceService(factory);
        var actual = new List<string>();
        string? cursor = null;
        var pages = 0;

        do
        {
            var page = await service.ListAsync(cursor: cursor, limit: cursor is null ? 17 : null);
            Assert.Equal(expected.Count, page.Total);
            Assert.Equal(17, page.Limit);
            Assert.InRange(page.Items.Count, 1, 17);
            actual.AddRange(page.Items.Select(item => item.WorkspaceId));
            cursor = page.NextCursor;
            Assert.True(++pages < 20, "Catalog continuation did not terminate.");
        } while (cursor is not null);

        Assert.Equal(expected.OrderBy(value => value, StringComparer.Ordinal), actual.OrderBy(value => value, StringComparer.Ordinal));
        Assert.Equal(actual.Count, actual.Distinct(StringComparer.Ordinal).Count());
        await using var db = factory.CreateDbContext();
        Assert.Equal(137, await db.Ideas.CountAsync());
        Assert.Equal(9, await db.Projects.CountAsync());
        Assert.True(await db.Ideas.AnyAsync(idea => idea.Status == IdeaStatus.Archived));
        Assert.True(await db.Ideas.AnyAsync(idea => idea.Status == IdeaStatus.Rejected));
        await AssertNoWorkspaceWrites(factory);
    }

    [Fact]
    public async Task KindAndStatusFilters_IncludeLinkedIdeasAndArchivedRecords_WithAccurateTotals()
    {
        using var factory = new SqliteTestFactory();
        await SeedCatalog(factory, 12, 3);
        int linkedIdea;
        await using (var db = factory.CreateDbContext())
        {
            var idea = await db.Ideas.OrderBy(row => row.Id).FirstAsync();
            idea.PromotedProjectId = await db.Projects.MinAsync(project => project.Id);
            idea.Status = IdeaStatus.Promoted;
            linkedIdea = idea.Id;
            await db.SaveChangesAsync();
        }
        var service = new IdeaWorkspaceService(factory);

        var ideas = await service.ListAsync(kind: "idea");
        var projects = await service.ListAsync(kind: "project");
        var linked = await service.ListAsync(kind: "linked");
        var archived = await service.ListAsync(status: "archived");
        var rejected = await service.ListAsync(kind: "idea", status: "Rejected");

        Assert.Equal(12, ideas.Total);
        Assert.All(ideas.Items, item => Assert.StartsWith("idea:", item.WorkspaceId));
        Assert.Equal(3, projects.Total);
        Assert.All(projects.Items, item => Assert.StartsWith("project:", item.WorkspaceId));
        Assert.Equal($"idea:{linkedIdea}", Assert.Single(linked.Items).WorkspaceId);
        Assert.Equal(1, linked.Total);
        Assert.Equal(3, archived.Total);
        Assert.All(archived.Items, item => Assert.StartsWith("idea:", item.WorkspaceId));
        Assert.Equal(3, rejected.Total);
        Assert.All(rejected.Items, item => Assert.StartsWith("idea:", item.WorkspaceId));
        await AssertNoWorkspaceWrites(factory);
    }

    [Fact]
    public async Task Search_FindsPlanOnlyProjectReadmeComponentAndDraftContent_WithoutTruncatingSources()
    {
        using var factory = new SqliteTestFactory();
        var longPlan = new string('p', 110_000) + "\r\n  plan-only-needle  \r\n";
        var ideaId = await SeedIdea(factory, "Idea", plan: longPlan);
        int projectId;
        await using (var db = factory.CreateDbContext())
        {
            var project = new Project { Name = "Project", Readme = "\r\n    readme-only-needle  \r\n" };
            db.Projects.Add(project);
            await db.SaveChangesAsync();
            projectId = project.Id;
        }
        var service = new IdeaWorkspaceService(factory);
        var ideaKey = $"idea:{ideaId}";
        await Write(service, ideaKey, 0, "create_component", new { component = new { id = "comparison", type = "table", title = "Comparison", content = "  component-only-needle\r\n" } });
        await Write(service, ideaKey, 1, "propose_plan", new { title = "Proposal", items = new[] { new { id = "item", title = "Scope", description = "\r\n draft-only-needle  ", acceptanceCriteria = new[] { "One criterion" }, dependsOn = Array.Empty<string>() } } });

        foreach (var term in new[] { "PLAN-ONLY-NEEDLE", "component-only-needle", "draft-only-needle" })
        {
            var page = await service.ListAsync(q: term);
            Assert.Equal(1, page.Total);
            Assert.Equal(ideaKey, Assert.Single(page.Items).WorkspaceId);
        }
        var readme = await service.ListAsync(q: "readme-only-needle");
        Assert.Equal($"project:{projectId}", Assert.Single(readme.Items).WorkspaceId);
        Assert.Equal(0, (await service.ListAsync(q: "not-present-anywhere")).Total);
        Assert.Equal(longPlan, (await service.GetAsync(ideaKey)).Source!.Plan);
        Assert.Equal(longPlan, (await service.GetAsync(ideaKey)).OriginalSource.Plan);
    }

    [Fact]
    public async Task CatalogContinuation_UsesItsOriginalMembership_WhenAnotherClientInsertsASource()
    {
        using var factory = new SqliteTestFactory();
        var originalKeys = await SeedCatalog(factory, 120, 5);
        var service = new IdeaWorkspaceService(factory);
        var first = await service.ListAsync(limit: 19);
        Assert.NotNull(first.NextCursor);
        var insertedId = await SeedIdea(factory, "000 inserted after snapshot");
        var insertedKey = $"idea:{insertedId}";
        var actual = first.Items.Select(item => item.WorkspaceId).ToList();
        var cursor = first.NextCursor;

        while (cursor is not null)
        {
            var next = await service.ListAsync(cursor: cursor);
            Assert.Equal(originalKeys.Count, next.Total);
            actual.AddRange(next.Items.Select(item => item.WorkspaceId));
            cursor = next.NextCursor;
            Assert.True(actual.Count <= originalKeys.Count, "A snapshot continuation included extra rows or repeated a page.");
        }

        Assert.Equal(originalKeys.OrderBy(value => value, StringComparer.Ordinal), actual.OrderBy(value => value, StringComparer.Ordinal));
        Assert.DoesNotContain(insertedKey, actual);
        var refreshed = await service.ListAsync(limit: 500);
        Assert.Equal(originalKeys.Count + 1, refreshed.Total);
        Assert.Contains(refreshed.Items, item => item.WorkspaceId == insertedKey);
        await AssertNoWorkspaceWrites(factory);
    }

    [Fact]
    public async Task CatalogCursor_RejectsChangedFiltersPageSizeAndMalformedValues()
    {
        using var factory = new SqliteTestFactory();
        await SeedCatalog(factory, 8, 2);
        var service = new IdeaWorkspaceService(factory);
        var first = await service.ListAsync(kind: "idea", limit: 3);
        Assert.NotNull(first.NextCursor);

        foreach (var request in new Func<Task>[]
        {
            async () => await service.ListAsync(kind: "project", cursor: first.NextCursor),
            async () => await service.ListAsync(kind: "idea", status: "Archived", cursor: first.NextCursor),
            async () => await service.ListAsync(kind: "idea", q: "changed", cursor: first.NextCursor),
            async () => await service.ListAsync(kind: "idea", cursor: first.NextCursor, limit: 4),
            async () => await service.ListAsync(cursor: "not-a-cursor")
        })
        {
            var error = await Assert.ThrowsAsync<IdeaWorkspaceException>(request);
            Assert.Equal(400, error.StatusCode);
        }
        await AssertNoWorkspaceWrites(factory);
    }

    [Fact]
    public async Task CatalogCursor_ReportsExpiryExplicitly_UsingTheSnapshotClock()
    {
        using var factory = new SqliteTestFactory();
        await SeedCatalog(factory, 5, 0);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
        var service = new IdeaWorkspaceService(factory, clock);
        var first = await service.ListAsync(limit: 2);
        Assert.NotNull(first.NextCursor);
        clock.Advance(TimeSpan.FromMinutes(11));

        var error = await Assert.ThrowsAsync<IdeaWorkspaceException>(() => service.ListAsync(cursor: first.NextCursor));

        Assert.Equal(409, error.StatusCode);
        Assert.Contains("expir", error.Message, StringComparison.OrdinalIgnoreCase);
        var restarted = await service.ListAsync(limit: 2);
        Assert.Equal(5, restarted.Total);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(501)]
    public async Task Catalog_InvalidPageLimitsFailExplicitly(int limit)
    {
        using var factory = new SqliteTestFactory();
        var error = await Assert.ThrowsAsync<IdeaWorkspaceException>(() => new IdeaWorkspaceService(factory).ListAsync(limit: limit));
        Assert.Equal(400, error.StatusCode);
        await AssertNoWorkspaceWrites(factory);
    }

    [Theory]
    [InlineData("unsupported-kind", null)]
    [InlineData(null, "unsupported-status")]
    public async Task Catalog_UnknownFiltersFailExplicitly(string? kind, string? status)
    {
        using var factory = new SqliteTestFactory();
        var error = await Assert.ThrowsAsync<IdeaWorkspaceException>(() => new IdeaWorkspaceService(factory).ListAsync(kind: kind, status: status));
        Assert.Equal(400, error.StatusCode);
    }

    [Fact]
    public async Task Catalog_IncludesSavedWorkspaceAfterItsSourceIsRemoved_WithOriginalTitle()
    {
        using var factory = new SqliteTestFactory();
        var id = await SeedIdea(factory, "Retained original title", body: "  saved original body\r\n");
        var key = $"idea:{id}";
        var service = new IdeaWorkspaceService(factory);
        await Write(service, key, 0, "add_comment", new { text = "A saved workspace" });
        await using (var db = factory.CreateDbContext())
        {
            db.Ideas.Remove(await db.Ideas.SingleAsync(idea => idea.Id == id));
            await db.SaveChangesAsync();
        }

        var page = await service.ListAsync();
        var item = Assert.Single(page.Items);
        Assert.Equal(key, item.WorkspaceId);
        Assert.Equal("Retained original title", item.Title);
        Assert.True(item.SourceMissing);
        Assert.Equal(1, page.Total);
        Assert.True((await service.GetAsync(key)).SourceMissing);
        Assert.Equal("  saved original body\r\n", (await service.GetAsync(key)).OriginalSource.Body);
    }

    [Fact]
    public async Task ComponentPages_ReturnBoundedExplicitSummaries_WhileDetailRetainsFullLongMarkdown()
    {
        using var factory = new SqliteTestFactory();
        var key = $"idea:{await SeedIdea(factory, "Components", body: "Original brief")}";
        var service = new IdeaWorkspaceService(factory);
        var markdown = "\r\n  # Full comparison\r\n\r\n    indentation\r\n" + new string('m', 125_000) + "\r\nlast requirement  \r\n";
        await Write(service, key, 0, "create_component", new { component = new { id = "long-note", type = "markdown", title = "Long reference", content = markdown }, placement = new { id = "long-place", x = 20, y = 30, width = 280, height = 180 } });
        for (var index = 0; index < 3; index++)
            await Write(service, key, index + 1, "create_component", new { component = new { id = $"note-{index}", type = "note", title = $"Note {index}", content = "Other content" } });
        await Write(service, key, 4, "add_comment", new { componentId = "long-note", text = "  discuss the final requirement\r\n" });
        var context = await service.GetAsync(key);
        var expected = context.Document.Components.Select(component => component.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var actual = new List<string>();
        string? cursor = null;
        do
        {
            var page = await service.ListComponentsAsync(key, cursor: cursor, limit: cursor is null ? 3 : null);
            Assert.Equal(expected.Length, page.Total);
            Assert.Equal(5, page.SnapshotRevision);
            Assert.InRange(page.Items.Count, 1, 3);
            Assert.All(page.Items, component => Assert.False(component.ContentIncluded));
            var longSummary = page.Items.SingleOrDefault(component => component.Id == "long-note");
            if (longSummary is not null)
            {
                Assert.Equal(markdown.Length, longSummary.ContentLength);
                Assert.Equal(1, longSummary.PlacementCount);
            }
            actual.AddRange(page.Items.Select(component => component.Id));
            cursor = page.NextCursor;
            Assert.True(actual.Count <= expected.Length);
        } while (cursor is not null);

        Assert.Equal(expected, actual.OrderBy(id => id, StringComparer.Ordinal));
        var detail = await service.GetComponentAsync(key, "long-note");
        Assert.Equal(5, detail.Revision);
        Assert.Equal(markdown, detail.Component.Content);
        Assert.Equal("long-place", Assert.Single(detail.Placements).Id);
        Assert.Equal("  discuss the final requirement\r\n", Assert.Single(detail.Comments).Text);
        Assert.False(detail.TaskMissing);
        Assert.Null(detail.Task);
        await using var db = factory.CreateDbContext();
        Assert.Equal(5, await db.ActivityEvents.CountAsync());
        Assert.Equal(5, await db.Set<IdeaWorkspaceReceipt>().CountAsync());
    }

    [Fact]
    public async Task ComponentCursor_PreservesSnapshotMembershipAndRevision_AfterAnotherComponentIsAdded()
    {
        using var factory = new SqliteTestFactory();
        var key = $"idea:{await SeedIdea(factory, "Components")}";
        var service = new IdeaWorkspaceService(factory);
        var original = await service.GetAsync(key);
        var first = await service.ListComponentsAsync(key, limit: 2);
        Assert.NotNull(first.NextCursor);
        await Write(service, key, 0, "create_component", new { component = new { id = "later-note", type = "note", title = "Added later", content = "New content" } });
        var ids = first.Items.Select(item => item.Id).ToList();
        var cursor = first.NextCursor;

        while (cursor is not null)
        {
            var page = await service.ListComponentsAsync(key, cursor: cursor);
            Assert.Equal(0, page.SnapshotRevision);
            Assert.Equal(original.Document.Components.Count, page.Total);
            ids.AddRange(page.Items.Select(item => item.Id));
            cursor = page.NextCursor;
            Assert.True(ids.Count <= original.Document.Components.Count);
        }

        Assert.Equal(original.Document.Components.Select(item => item.Id).OrderBy(id => id, StringComparer.Ordinal), ids.OrderBy(id => id, StringComparer.Ordinal));
        Assert.DoesNotContain("later-note", ids);
        var fresh = await service.ListComponentsAsync(key);
        Assert.Equal(1, fresh.SnapshotRevision);
        Assert.Contains(fresh.Items, item => item.Id == "later-note");
    }

    [Fact]
    public async Task ComponentCursor_RejectsOtherWorkspaceAndCatalogScopes_AndMissingDetailIsExplicit()
    {
        using var factory = new SqliteTestFactory();
        var firstKey = $"idea:{await SeedIdea(factory, "First")}";
        var secondKey = $"idea:{await SeedIdea(factory, "Second")}";
        var service = new IdeaWorkspaceService(factory);
        var components = await service.ListComponentsAsync(firstKey, limit: 2);
        var catalog = await service.ListAsync(limit: 1);
        Assert.NotNull(components.NextCursor);
        Assert.NotNull(catalog.NextCursor);

        foreach (var request in new Func<Task>[]
        {
            async () => await service.ListComponentsAsync(secondKey, cursor: components.NextCursor),
            async () => await service.ListComponentsAsync(firstKey, cursor: catalog.NextCursor),
            async () => await service.ListComponentsAsync(firstKey, cursor: components.NextCursor, limit: 3)
        })
        {
            var error = await Assert.ThrowsAsync<IdeaWorkspaceException>(request);
            Assert.Equal(400, error.StatusCode);
        }
        var missing = await Assert.ThrowsAsync<IdeaWorkspaceException>(() => service.GetComponentAsync(firstKey, "not-a-component"));
        Assert.Equal(404, missing.StatusCode);
        await AssertNoWorkspaceWrites(factory);
    }

    [Fact]
    public async Task TaskReferenceDetail_ReadsCurrentDomainStatusAndFullDescription_ThenMarksARemovedTask()
    {
        using var factory = new SqliteTestFactory();
        int projectId;
        int taskId;
        var description = "  original task\r\n" + new string('d', 105_000) + "\r\nlast task requirement  ";
        await using (var db = factory.CreateDbContext())
        {
            var project = new Project { Name = "Delivery" };
            var feature = new Feature { Name = "Feature", Project = project };
            var task = new TaskItem { Name = "Original task", Description = description, Status = WorkStatus.Todo, Feature = feature };
            db.Tasks.Add(task);
            await db.SaveChangesAsync();
            projectId = project.Id;
            taskId = task.Id;
        }
        var key = $"project:{projectId}";
        var service = new IdeaWorkspaceService(factory);
        await Write(service, key, 0, "create_component", new { component = new { id = "task-reference", type = "task-reference", title = "Linked work", content = "Reference annotation", taskId } });
        var initial = await service.GetComponentAsync(key, "task-reference");
        Assert.False(initial.TaskMissing);
        Assert.Equal("Todo", initial.Task!.Status);
        Assert.Equal(description, initial.Task.Description);
        Assert.Equal(taskId, initial.Task.Id);
        await using (var db = factory.CreateDbContext())
        {
            var task = await db.Tasks.SingleAsync(row => row.Id == taskId);
            task.Name = "Current task title";
            task.Description = "  current task details\r\n";
            task.Status = WorkStatus.Done;
            task.Assignee = "Current owner";
            await db.SaveChangesAsync();
        }
        var updated = await service.GetComponentAsync(key, "task-reference");
        Assert.Equal("Done", updated.Task!.Status);
        Assert.Equal("Current task title", updated.Task.Name);
        Assert.Equal("  current task details\r\n", updated.Task.Description);
        Assert.Equal("Current owner", updated.Task.Assignee);
        Assert.Equal("Reference annotation", updated.Component.Content);
        await using (var db = factory.CreateDbContext())
        {
            db.Tasks.Remove(await db.Tasks.SingleAsync(row => row.Id == taskId));
            await db.SaveChangesAsync();
        }

        var removed = await service.GetComponentAsync(key, "task-reference");
        Assert.True(removed.TaskMissing);
        Assert.Null(removed.Task);
        Assert.Equal(taskId, removed.Component.TaskId);
        Assert.Equal("Reference annotation", removed.Component.Content);
        Assert.Equal(1, removed.Revision);
    }

    [Fact]
    public async Task ChangesBeyondFiveHundred_UseAFixedWatermark_ThenResumeAfterTheAcknowledgedPosition()
    {
        using var factory = new SqliteTestFactory();
        var id = await SeedIdea(factory, "History");
        var foreignId = await SeedIdea(factory, "Other history");
        var key = $"idea:{id}";
        var service = new IdeaWorkspaceService(factory);
        await Write(service, key, 0, "add_comment", new { text = "Initial durable history" });
        var finalAudit = JsonSerializer.Serialize(new { content = "\r\n  complete history detail\r\n" + new string('h', 112_000) + "\r\nfinal history line  " }, Json);
        await using (var db = factory.CreateDbContext())
        {
            for (var index = 0; index < 600; index++)
                db.ActivityEvents.Add(new ActivityEvent { EntityType = "IdeaWorkspace", EntityId = id, EventType = "IdeaWorkspaceChanged", Actor = "history-fixture", Message = $"Event {index}", BeforeJson = "{}", AfterJson = index == 599 ? finalAudit : "{}" });
            db.ActivityEvents.Add(new ActivityEvent { EntityType = "IdeaWorkspace", EntityId = foreignId, EventType = "IdeaWorkspaceChanged", Actor = "history-fixture", Message = "Foreign history", AfterJson = "{}" });
            await db.SaveChangesAsync();
        }
        List<int> expected;
        await using (var db = factory.CreateDbContext())
            expected = await db.ActivityEvents.Where(activity => activity.EntityType == "IdeaWorkspace" && activity.EntityId == id).OrderBy(activity => activity.Id).Select(activity => activity.Id).ToListAsync();

        var first = await service.GetChangesAsync(key, limit: 500);
        Assert.Equal(601, first.Total);
        Assert.Equal(500, first.Items.Count);
        Assert.Equal(expected[^1], first.Watermark);
        Assert.NotNull(first.NextCursor);
        await Write(service, key, 1, "add_comment", new { text = "Saved after the first history page" });
        var second = await service.GetChangesAsync(key, cursor: first.NextCursor);

        Assert.Equal(601, second.Total);
        Assert.Equal(101, second.Items.Count);
        Assert.Null(second.NextCursor);
        Assert.Equal(first.Watermark, second.Watermark);
        Assert.Equal(expected, first.Items.Concat(second.Items).Select(activity => activity.Id).ToList());
        Assert.Equal(finalAudit, second.Items[^1].AfterJson);
        Assert.DoesNotContain(first.Items.Concat(second.Items), activity => activity.Message == "Foreign history");
        var resumed = await service.GetChangesAsync(key, afterActivityId: first.Watermark, limit: 500);
        Assert.Equal(first.Watermark, resumed.AfterActivityId);
        Assert.Equal(1, resumed.Total);
        Assert.Equal("add_comment", Assert.Single(resumed.Items).Message);
        Assert.True(resumed.Items[0].Id > first.Watermark);
    }

    [Fact]
    public async Task ChangeCursor_RejectsDifferentWorkspaceAfterPositionOrPageSize()
    {
        using var factory = new SqliteTestFactory();
        var key = $"idea:{await SeedIdea(factory, "First")}";
        var otherKey = $"idea:{await SeedIdea(factory, "Other")}";
        var service = new IdeaWorkspaceService(factory);
        await Write(service, key, 0, "add_comment", new { text = "One" });
        await Write(service, key, 1, "add_comment", new { text = "Two" });
        var first = await service.GetChangesAsync(key, afterActivityId: 0, limit: 1);
        Assert.NotNull(first.NextCursor);

        foreach (var request in new Func<Task>[]
        {
            async () => await service.GetChangesAsync(otherKey, cursor: first.NextCursor),
            async () => await service.GetChangesAsync(key, cursor: first.NextCursor, afterActivityId: 1),
            async () => await service.GetChangesAsync(key, cursor: first.NextCursor, limit: 2),
            async () => await service.GetChangesAsync(key, afterActivityId: -1)
        })
        {
            var error = await Assert.ThrowsAsync<IdeaWorkspaceException>(request);
            Assert.Equal(400, error.StatusCode);
        }
    }

    private static async Task<List<string>> SeedCatalog(SqliteTestFactory factory, int ideaCount, int projectCount)
    {
        await using var db = factory.CreateDbContext();
        var statuses = new[] { IdeaStatus.New, IdeaStatus.Triaging, IdeaStatus.Archived, IdeaStatus.Rejected };
        for (var index = 0; index < ideaCount; index++)
            db.Ideas.Add(new Idea { Title = $"Idea {index:D4}", Status = statuses[index % statuses.Length] });
        for (var index = 0; index < projectCount; index++)
            db.Projects.Add(new Project { Name = $"Project {index:D4}" });
        await db.SaveChangesAsync();
        return (await db.Ideas.Select(idea => "idea:" + idea.Id).ToListAsync())
            .Concat(await db.Projects.Select(project => "project:" + project.Id).ToListAsync()).ToList();
    }

    private static async Task<int> SeedIdea(SqliteTestFactory factory, string title, string? body = null, string? plan = null)
    {
        await using var db = factory.CreateDbContext();
        var idea = new Idea { Title = title, Body = body, Plan = plan };
        db.Ideas.Add(idea);
        await db.SaveChangesAsync();
        return idea.Id;
    }

    private static Task<IdeaWorkspaceMutationResult> Write(IdeaWorkspaceService service, string key, long revision, string action, object payload) =>
        service.ExecuteAsync(key, new IdeaWorkspaceCommand(Guid.NewGuid().ToString("N"), revision, action, JsonSerializer.SerializeToElement(payload, Json), "query-tests"));

    private static async Task AssertNoWorkspaceWrites(SqliteTestFactory factory)
    {
        await using var db = factory.CreateDbContext();
        Assert.Empty(await db.Set<IdeaWorkspaceState>().ToListAsync());
        Assert.Empty(await db.Set<IdeaWorkspaceReceipt>().ToListAsync());
        Assert.Empty(await db.ActivityEvents.ToListAsync());
    }
}
