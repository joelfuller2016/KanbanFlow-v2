using KanbanFlow.Mcp;
using KanbanFlow.Models;
using KanbanFlow.Services;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;

namespace KanbanFlow.Tests;

/// <summary>Real-SQLite contract coverage for the native in-process MCP task tools.</summary>
public sealed class McpTaskToolsTests
{
    [Fact]
    public async Task GetProjectContext_OrdersTasksByFeatureThenCanonicalTaskRank()
    {
        using var factory = new SqliteTestFactory();
        int projectId;
        int[] expectedTaskIds;

        await using (var db = factory.CreateDbContext())
        {
            var project = new Project { Name = "MCP context" };
            var laterFeature = new Feature { Project = project, Name = "Later", SortOrder = 10 };
            var firstFeature = new Feature { Project = project, Name = "First", SortOrder = 0 };
            var secondFeature = new Feature { Project = project, Name = "Second", SortOrder = 0 };
            db.AddRange(project, laterFeature, firstFeature, secondFeature);
            await db.SaveChangesAsync();

            var laterTask = new TaskItem { Feature = laterFeature, Name = "Later task", SortOrder = 0 };
            var firstA = new TaskItem { Feature = firstFeature, Name = "First A", SortOrder = 0 };
            var firstB = new TaskItem { Feature = firstFeature, Name = "First B", SortOrder = 0 };
            var secondTask = new TaskItem { Feature = secondFeature, Name = "Second task", SortOrder = -1 };
            db.AddRange(laterTask, firstA, firstB, secondTask);
            await db.SaveChangesAsync();

            projectId = project.Id;
            expectedTaskIds = [firstA.Id, firstB.Id, secondTask.Id, laterTask.Id];
        }

        var context = await KanbanMcpTools.GetProjectContext(factory, projectId);

        Assert.Equal(expectedTaskIds, context.Tasks.Select(task => task.Id));
    }

    [Fact]
    public async Task CreateTask_FractionalSortOrder_RoundTripsThroughNativeMcpAndSqlite()
    {
        using var factory = new SqliteTestFactory();
        var featureId = await SeedFeature(factory);
        var service = new TaskService(factory);

        var result = await KanbanMcpTools.CreateTask(
            service, featureId, "Fractional", sortOrder: 0.25d, actor: "mcp-test");

        Assert.Equal(0.25d, result.SortOrder);
        await using var db = factory.CreateDbContext();
        var persisted = await db.Tasks.SingleAsync(task => task.Id == result.Id);
        Assert.Equal(0.25d, persisted.SortOrder);
    }

    [Fact]
    public async Task CreateTask_OmittedSortOrder_AppendsAsBefore()
    {
        using var factory = new SqliteTestFactory();
        var featureId = await SeedFeature(factory, existingSortOrder: 4.5d);
        var service = new TaskService(factory);

        var result = await KanbanMcpTools.CreateTask(service, featureId, "Appended");

        Assert.Equal(5.5d, result.SortOrder);
    }

    [Fact]
    public async Task CreateTask_NonFiniteSortOrder_TranslatesValidationToMcpErrorWithoutWrites()
    {
        using var factory = new SqliteTestFactory();
        var featureId = await SeedFeature(factory);
        var service = new TaskService(factory);

        var error = await Assert.ThrowsAsync<McpException>(() => KanbanMcpTools.CreateTask(
            service, featureId, "Invalid rank", sortOrder: double.NaN));

        Assert.Contains("finite number", error.Message, StringComparison.OrdinalIgnoreCase);
        await using var db = factory.CreateDbContext();
        Assert.Empty(await db.Tasks.ToListAsync());
        Assert.Empty(await db.TaskComments.ToListAsync());
        Assert.Empty(await db.ActivityEvents.ToListAsync());
    }

    private static async Task<int> SeedFeature(
        SqliteTestFactory factory,
        double? existingSortOrder = null)
    {
        await using var db = factory.CreateDbContext();
        var project = new Project { Name = "MCP create" };
        var feature = new Feature { Project = project, Name = "Feature" };
        db.AddRange(project, feature);

        if (existingSortOrder is not null)
        {
            db.Tasks.Add(new TaskItem
            {
                Feature = feature,
                Name = "Existing",
                SortOrder = existingSortOrder.Value
            });
        }

        await db.SaveChangesAsync();
        return feature.Id;
    }
}
