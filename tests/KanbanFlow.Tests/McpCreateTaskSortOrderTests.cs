using KanbanFlow.Mcp;
using KanbanFlow.Models;
using KanbanFlow.Services;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;

namespace KanbanFlow.Tests;

/// <summary>
/// Contract parity between the NATIVE MCP <c>create_task</c> tool and the stdio bridge
/// (<c>scripts/mcp-server.mjs</c>), which forwards <c>sortOrder</c> to
/// <c>POST /api/features/{id}/tasks</c> (#307, PR #325 review).
///
/// <para>The stdio schema advertises an optional finite fractional <c>sortOrder</c>. Before this
/// change the native tool had no such parameter, silently dropped any rank the caller intended, and
/// never reported the persisted value back — so the same logical call produced different data
/// depending on which transport the agent happened to be using.</para>
///
/// <para>These run the real <see cref="TaskService"/> against real SQLite; nothing is mocked.</para>
/// </summary>
public sealed class McpCreateTaskSortOrderTests
{
    private static int SeedFeature(SqliteTestFactory factory)
    {
        using var db = factory.CreateDbContext();
        var project = new Project { Name = "MCP parity project" };
        db.Projects.Add(project);
        db.SaveChanges();

        var feature = new Feature { ProjectId = project.Id, Name = "MCP parity feature" };
        db.Features.Add(feature);
        db.SaveChanges();
        return feature.Id;
    }

    private static async Task<double> PersistedRank(SqliteTestFactory factory, int taskId)
    {
        await using var db = factory.CreateDbContext();
        return await db.Tasks.AsNoTracking().Where(t => t.Id == taskId).Select(t => t.SortOrder).SingleAsync();
    }

    [Fact]
    public async Task CreateTask_WithFractionalSortOrder_PersistsAndReturnsThatRank()
    {
        using var factory = new SqliteTestFactory();
        var featureId = SeedFeature(factory);
        var service = new TaskService(factory);

        var result = await KanbanMcpTools.CreateTask(
            service, featureId, "fractional rank", sortOrder: 0.25);

        Assert.Equal(0.25d, result.SortOrder);
        Assert.Equal(0.25d, await PersistedRank(factory, result.Id));
    }

    [Fact]
    public async Task CreateTask_WithoutSortOrder_AppendsAfterTheLastTaskAndReportsIt()
    {
        using var factory = new SqliteTestFactory();
        var featureId = SeedFeature(factory);
        var service = new TaskService(factory);

        var first = await KanbanMcpTools.CreateTask(service, featureId, "first");
        var second = await KanbanMcpTools.CreateTask(service, featureId, "second");

        // Omitting the rank must keep the pre-existing append behaviour, not write 0 or null.
        Assert.Equal(0d, first.SortOrder);
        Assert.Equal(1d, second.SortOrder);
        Assert.Equal(1d, await PersistedRank(factory, second.Id));
    }

    [Fact]
    public async Task CreateTask_WithExplicitSortOrder_IsReportedByGetTask()
    {
        using var factory = new SqliteTestFactory();
        var featureId = SeedFeature(factory);
        var service = new TaskService(factory);

        var created = await KanbanMcpTools.CreateTask(
            service, featureId, "round trip", sortOrder: 2.5);

        // Round-trip through the persisted row: the rank the tool reported is the rank stored.
        Assert.Equal(2.5d, created.SortOrder);
        Assert.Equal(2.5d, await PersistedRank(factory, created.Id));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public async Task CreateTask_WithNonFiniteSortOrder_IsRejected(double rank)
    {
        using var factory = new SqliteTestFactory();
        var featureId = SeedFeature(factory);
        var service = new TaskService(factory);

        var ex = await Assert.ThrowsAsync<McpException>(() =>
            KanbanMcpTools.CreateTask(service, featureId, "bad rank", sortOrder: rank));
        Assert.Contains("finite", ex.Message, StringComparison.OrdinalIgnoreCase);

        await using var db = factory.CreateDbContext();
        Assert.Equal(0, await db.Tasks.CountAsync());
    }
}
