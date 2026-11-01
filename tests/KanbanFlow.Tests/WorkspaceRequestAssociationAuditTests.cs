using System.Text.Json;
using KanbanFlow.Data;
using KanbanFlow.Models;
using KanbanFlow.Models.IdeaWorkspaces;
using KanbanFlow.Models.Workflows;
using KanbanFlow.Services.IdeaWorkspaces;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Tests;

public sealed class WorkspaceRequestAssociationAuditTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Actor = "  Reviewed project association Ω  ";
    private const string AuditType = "WorkRequestProjectLinked";
    private static readonly string FullTitle = "  # Complete request title Ω\r\n" + new string('文', 390) + "\r\nFINAL TITLE  ";

    [Theory]
    [InlineData("link")]
    [InlineData("create")]
    [InlineData("relink")]
    public async Task ChangedAssociation_AuditsTheExactRequestRowIdentityVersionActorAndFullTitle(string action)
    {
        using var factory = new WorkflowSqliteTestFactory();
        var service = new IdeaWorkspaceService(factory);
        var request = await Capture(factory, service);
        var key = $"idea:{request.IdeaId}";
        int? previousProject = null;
        if (action == "relink")
        {
            previousProject = await Project(factory, "Prior project");
            await service.ExecuteAsync(key, await Association(service, key, previousProject, actor: "prior-link-author"));
        }
        var target = action == "create" ? (int?)null : await Project(factory, "Reviewed project");
        if (action == "link")
        {
            await using var db = factory.CreateDbContext();
            var feature = new Feature { Name = "Retained compatible feature", ProjectId = target!.Value };
            db.Features.Add(feature);
            await db.SaveChangesAsync();
            (await db.WorkRequests.SingleAsync()).FeatureId = feature.Id;
            await db.SaveChangesAsync();
        }
        var before = await factory.LoadAsync(request.Id);
        var command = await Association(service, key, target, previousProject);

        var result = await service.ExecuteAsync(key, command);

        var after = await factory.LoadAsync(request.Id);
        Assert.Equal(result.ProjectId, after.ProjectId);
        Assert.Equal(before.Version + 1, after.Version);
        await using var check = factory.CreateDbContext();
        var audit = Assert.Single(await check.ActivityEvents.Where(a => a.EventType == AuditType && a.Actor == Actor).ToListAsync());
        Assert.Equal("WorkRequest", audit.EntityType);
        Assert.Equal(request.Id.ToString("D"), audit.EntityKey);
        Assert.Equal(request.IdeaId, audit.EntityId);
        Assert.Equal(after.ProjectId, audit.ProjectId);
        Assert.Contains(command.Action, audit.Message);
        AssertRow(audit.BeforeJson!, before);
        AssertRow(audit.AfterJson!, after);
        Assert.Equal(after.UpdatedAt.Ticks, audit.CreatedAt.Ticks);
        Assert.True(after.UpdatedAt > before.UpdatedAt);
    }

    [Fact]
    public async Task SameProject_NoOpKeepsTheRequestRowAndCreatesNoRequestVersionAudit()
    {
        using var factory = new WorkflowSqliteTestFactory();
        var service = new IdeaWorkspaceService(factory);
        var request = await Capture(factory, service);
        var key = $"idea:{request.IdeaId}";
        var project = await Project(factory, "Current project");
        await service.ExecuteAsync(key, await Association(service, key, project));
        var before = await factory.LoadAsync(request.Id);
        var count = await AuditCount(factory);

        await service.ExecuteAsync(key, await Association(service, key, project, project));

        AssertRow(Row(before), await factory.LoadAsync(request.Id));
        Assert.Equal(count, await AuditCount(factory));
    }

    [Fact]
    public async Task ExactReceiptReplay_DoesNotRepeatRequestVersionAuditOrAnyOtherWrite()
    {
        using var factory = new WorkflowSqliteTestFactory();
        var service = new IdeaWorkspaceService(factory);
        var request = await Capture(factory, service);
        var key = $"idea:{request.IdeaId}";
        var project = await Project(factory, "Replay destination");
        var command = await Association(service, key, project);
        var result = await service.ExecuteAsync(key, command);
        var before = await Snapshot(factory);

        var replay = await service.ExecuteAsync(key, command);

        Assert.True(replay.Replayed);
        Assert.Equal(result with { Replayed = true }, replay);
        Assert.Equal(before, await Snapshot(factory));
        Assert.Equal(1, await AuditCount(factory));
    }

    [Fact]
    public async Task StaleWorkspaceRevision_RejectsWithoutRequestAuditOrPartialWrites()
    {
        using var factory = new WorkflowSqliteTestFactory();
        var service = new IdeaWorkspaceService(factory);
        var request = await Capture(factory, service);
        var key = $"idea:{request.IdeaId}";
        var project = await Project(factory, "Current destination");
        var command = await Association(service, key, project);
        await service.ExecuteAsync(key, command);
        var before = await Snapshot(factory);

        var error = await Assert.ThrowsAsync<IdeaWorkspaceException>(() => service.ExecuteAsync(key,
            command with { OperationId = WorkflowSqliteTestFactory.Key() }));

        Assert.Equal("revision_conflict", error.Code);
        Assert.Equal(409, error.StatusCode);
        Assert.Equal(before, await Snapshot(factory));
        Assert.Equal(1, await AuditCount(factory));
    }

    [Fact]
    public async Task ForeignPublishedWorkflow_RejectsRelinkWithoutAnyRequestAuditOrPartialWrite()
    {
        using var factory = new WorkflowSqliteTestFactory();
        var request = await factory.PublishedAsync(WorkflowSqliteTestFactory.Draft());
        var target = await Project(factory, "Foreign project");
        var service = new IdeaWorkspaceService(factory);
        var key = $"idea:{request.IdeaId}";
        var command = await Association(service, key, target, request.ProjectId);
        var before = await Snapshot(factory);

        var error = await Assert.ThrowsAsync<IdeaWorkspaceException>(() => service.ExecuteAsync(key, command));

        Assert.Equal("project_link_workflow_conflict", error.Code);
        Assert.Equal(409, error.StatusCode);
        Assert.Equal(before, await Snapshot(factory));
        Assert.Equal(0, await AuditCount(factory));
    }

    [Fact]
    public async Task UncapturedLegacyIdea_RetainsExplicitAdoptionAndDoesNotInventRequestAudit()
    {
        using var factory = new WorkflowSqliteTestFactory();
        int ideaId;
        await using (var db = factory.CreateDbContext())
        {
            var idea = new Idea { Title = "Legacy idea", Body = "  Original legacy text Ω  " };
            db.Ideas.Add(idea);
            await db.SaveChangesAsync();
            ideaId = idea.Id;
        }
        var project = await Project(factory, "Legacy destination");
        var service = new IdeaWorkspaceService(factory);
        var key = $"idea:{ideaId}";
        await service.ExecuteAsync(key, await Association(service, key, project));

        await using var check = factory.CreateDbContext();
        Assert.Empty(await check.WorkRequests.ToListAsync());
        Assert.Equal(0, await AuditCount(factory));
        Assert.Equal(project, (await check.Ideas.SingleAsync()).PromotedProjectId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestAuditFailure_RollsBackEveryTableIncludingCreatedProjectVersionAndReceipt(bool createProject)
    {
        using var factory = new WorkflowSqliteTestFactory();
        var service = new IdeaWorkspaceService(factory);
        var request = await Capture(factory, service);
        var target = createProject ? (int?)null : await Project(factory, "Existing destination");
        var key = $"idea:{request.IdeaId}";
        var command = await Association(service, key, target);
        await using (var db = factory.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER RejectRequestProjectAudit BEFORE INSERT ON ActivityEvents WHEN NEW.EventType = 'WorkRequestProjectLinked' BEGIN SELECT RAISE(ABORT, 'request project audit rejected'); END;");
        var before = await Snapshot(factory);

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => service.ExecuteAsync(key, command));

        Assert.Contains("request project audit rejected", error.ToString());
        Assert.Equal(before, await Snapshot(factory));
    }

    private static async Task<WorkRequest> Capture(WorkflowSqliteTestFactory factory, IdeaWorkspaceService service)
    {
        var created = await service.CreateAsync(new(WorkflowSqliteTestFactory.Key(), "Original submitter", "Original title",
            "\r\n  Complete original text Ω  \r\n", "  Complete plan  "));
        await using (var db = factory.CreateDbContext())
        {
            var row = await db.WorkRequests.SingleAsync();
            row.Title = FullTitle;
            row.Version = 19;
            row.Archived = true;
            row.CreatedAt = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            row.UpdatedAt = new DateTime(2025, 2, 3, 4, 5, 6, DateTimeKind.Utc);
            await db.SaveChangesAsync();
        }
        return (await factory.Service.GetForIdeaAsync(created.IdeaId!.Value))!;
    }

    private static async Task<int> Project(WorkflowSqliteTestFactory factory, string name)
    {
        await using var db = factory.CreateDbContext();
        var project = new Project { Name = name };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        return project.Id;
    }

    private static async Task<IdeaWorkspaceCommand> Association(IdeaWorkspaceService service, string key, int? target,
        int? previous = null, string actor = Actor) => new(WorkflowSqliteTestFactory.Key(), (await service.GetAsync(key)).Revision,
            target is null ? "create_project" : "link_project",
            target is null ? JsonSerializer.SerializeToElement(new { name = "New reviewed destination" }, Json)
                : JsonSerializer.SerializeToElement(new { projectId = target, expectedCurrentProjectId = previous }, Json), actor);

    private static async Task<int> AuditCount(WorkflowSqliteTestFactory factory)
    {
        await using var db = factory.CreateDbContext();
        return await db.ActivityEvents.CountAsync(a => a.EventType == AuditType);
    }

    private static string Row(WorkRequest row) => JsonSerializer.Serialize(new
    {
        row.Id,
        row.IdeaId,
        row.ProjectId,
        row.FeatureId,
        row.Title,
        row.Version,
        row.Archived,
        row.CreatedAt,
        row.UpdatedAt
    }, Json);

    private static void AssertRow(string json, WorkRequest row)
    {
        var snapshot = JsonDocument.Parse(json).RootElement;
        Assert.Equal(new[] { "archived", "createdAt", "featureId", "id", "ideaId", "projectId", "title", "updatedAt", "version" },
            snapshot.EnumerateObject().Select(p => p.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.Equal(row.Id, snapshot.GetProperty("id").GetGuid());
        Assert.Equal(row.IdeaId, snapshot.GetProperty("ideaId").GetInt32());
        Assert.Equal(row.ProjectId, snapshot.GetProperty("projectId").Deserialize<int?>());
        Assert.Equal(row.FeatureId, snapshot.GetProperty("featureId").Deserialize<int?>());
        Assert.Equal(row.Title, snapshot.GetProperty("title").GetString());
        Assert.Equal(row.Version, snapshot.GetProperty("version").GetInt32());
        Assert.Equal(row.Archived, snapshot.GetProperty("archived").GetBoolean());
        Assert.Equal(row.CreatedAt.Ticks, snapshot.GetProperty("createdAt").GetDateTime().Ticks);
        Assert.Equal(row.UpdatedAt.Ticks, snapshot.GetProperty("updatedAt").GetDateTime().Ticks);
    }

    private static async Task<string> Snapshot(WorkflowSqliteTestFactory factory)
    {
        await using var db = factory.CreateDbContext();
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
            command.CommandText = "SELECT * FROM \"" + table.Replace("\"", "\"\"") + "\"";
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<string>();
            while (await reader.ReadAsync())
            {
                var row = new object?[reader.FieldCount];
                for (var index = 0; index < row.Length; index++) row[index] = await reader.IsDBNullAsync(index) ? null : reader.GetValue(index);
                rows.Add(JsonSerializer.Serialize(row, Json));
            }
            snapshot.Add(table, rows.OrderBy(value => value, StringComparer.Ordinal).ToArray());
        }
        return JsonSerializer.Serialize(snapshot, Json);
    }
}
