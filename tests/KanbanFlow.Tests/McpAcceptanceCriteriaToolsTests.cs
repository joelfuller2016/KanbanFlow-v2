using KanbanFlow.Data;
using KanbanFlow.Mcp;
using KanbanFlow.Models;
using KanbanFlow.Services;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;

namespace KanbanFlow.Tests;

/// <summary>
/// Tests for the native MCP acceptance-criteria tools (#438).
/// </summary>
/// <remarks>
/// The headline test is <see cref="Agent_using_only_native_mcp_tools_can_reach_Done_without_an_override"/>.
/// Before #438 the native server had no acceptance-criteria tools at all, so an agent on
/// <c>POST /mcp</c> that hit the #428 Definition-of-Done gate had exactly one exit —
/// <c>overrideReason</c> — and the gate's escape hatch became its default path. That test walks
/// the whole loop through the tool surface only, and asserts the task reaches Done with
/// <b>no</b> <c>TaskDoneOverridden</c> event, which is the property that was impossible before.
/// </remarks>
public sealed class McpAcceptanceCriteriaToolsTests
{
    private static async Task<(int TaskId, int ProjectId)> SeedTaskAsync(IDbContextFactory<AppDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        var project = new Project { Name = "AC tools" };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        var feature = new Feature { ProjectId = project.Id, Name = "F" };
        db.Features.Add(feature);
        await db.SaveChangesAsync();

        var task = new TaskItem { FeatureId = feature.Id, Name = "T", Status = WorkStatus.InProgress };
        db.Tasks.Add(task);
        await db.SaveChangesAsync();
        return (task.Id, project.Id);
    }

    /// <summary>
    /// The whole point of #438: drive a task with unmet criteria to Done using ONLY native MCP
    /// tools, and never touch the override.
    /// </summary>
    [Fact]
    public async Task Agent_using_only_native_mcp_tools_can_reach_Done_without_an_override()
    {
        using var h = new SqliteTestFactory();
        var (taskId, projectId) = await SeedTaskAsync(h);
        var svc = new TaskService(h);

        // The agent adds its Definition of Done, then tries to finish.
        await KanbanMcpTools.CreateAcceptanceCriterion(h, taskId, "endpoint returns 201");
        await KanbanMcpTools.CreateAcceptanceCriterion(h, taskId, "invalid payload returns 422");

        // 1. The gate refuses, and its message must name the remedy — not only the override.
        var refusal = await Assert.ThrowsAsync<McpException>(
            () => KanbanMcpTools.UpdateTaskStatus(svc, taskId, "Done"));
        Assert.Contains("mark_acceptance_criterion_done", refusal.Message);
        Assert.Contains("list_acceptance_criteria", refusal.Message);

        // 2. The agent lists them — this is the call that did not exist before #438.
        var criteria = await KanbanMcpTools.ListAcceptanceCriteria(h, taskId);
        Assert.Equal(2, criteria.Count);
        Assert.All(criteria, c => Assert.False(c.Completed));

        // 3. It completes each one.
        foreach (var c in criteria)
            await KanbanMcpTools.MarkAcceptanceCriterionDone(h, c.Id);

        // 4. The move now succeeds.
        var updated = await KanbanMcpTools.UpdateTaskStatus(svc, taskId, "Done");
        Assert.Equal(nameof(WorkStatus.Done), updated.Status);

        // 5. And it got there honestly: no override was recorded.
        await using var db = await h.CreateDbContextAsync();
        Assert.DoesNotContain(
            await db.ActivityEvents.Where(e => e.ProjectId == projectId).ToListAsync(),
            e => e.EventType == "TaskDoneOverridden");
    }

    [Fact]
    public async Task Mark_done_reports_when_the_task_is_clear_to_move()
    {
        using var h = new SqliteTestFactory();
        var (taskId, _) = await SeedTaskAsync(h);

        var first = await KanbanMcpTools.CreateAcceptanceCriterion(h, taskId, "one");
        var second = await KanbanMcpTools.CreateAcceptanceCriterion(h, taskId, "two");

        var afterFirst = await KanbanMcpTools.MarkAcceptanceCriterionDone(h, first.Id);
        Assert.True(afterFirst.Completed);
        Assert.Contains("1 criterion/criteria still unmet", afterFirst.Message);

        var afterSecond = await KanbanMcpTools.MarkAcceptanceCriterionDone(h, second.Id);
        Assert.Contains("No unmet criteria remain", afterSecond.Message);
    }

    [Fact]
    public async Task Mark_done_can_reopen_a_criterion()
    {
        using var h = new SqliteTestFactory();
        var (taskId, _) = await SeedTaskAsync(h);
        var c = await KanbanMcpTools.CreateAcceptanceCriterion(h, taskId, "one");

        await KanbanMcpTools.MarkAcceptanceCriterionDone(h, c.Id);
        var reopened = await KanbanMcpTools.MarkAcceptanceCriterionDone(h, c.Id, completed: false);

        Assert.False(reopened.Completed);
        Assert.Contains("1 criterion/criteria still unmet", reopened.Message);
    }

    [Fact]
    public async Task Create_appends_after_the_last_criterion()
    {
        using var h = new SqliteTestFactory();
        var (taskId, _) = await SeedTaskAsync(h);

        var a = await KanbanMcpTools.CreateAcceptanceCriterion(h, taskId, "a");
        var b = await KanbanMcpTools.CreateAcceptanceCriterion(h, taskId, "b");
        var explicitRank = await KanbanMcpTools.CreateAcceptanceCriterion(h, taskId, "c", sortOrder: 99);

        Assert.Equal(0, a.SortOrder);
        Assert.Equal(1, b.SortOrder);
        Assert.Equal(99, explicitRank.SortOrder);

        // Listing is ordered by SortOrder, so the explicit 99 sorts last.
        var listed = await KanbanMcpTools.ListAcceptanceCriteria(h, taskId);
        Assert.Equal(new[] { "a", "b", "c" }, listed.Select(x => x.Text).ToArray());
    }

    [Fact]
    public async Task Update_preserves_omitted_fields_and_rejects_blank_text()
    {
        using var h = new SqliteTestFactory();
        var (taskId, _) = await SeedTaskAsync(h);
        var c = await KanbanMcpTools.CreateAcceptanceCriterion(h, taskId, "original");
        await KanbanMcpTools.MarkAcceptanceCriterionDone(h, c.Id);

        // Text-only update must NOT wipe Completed (the #203/#260 partial-update rule).
        var renamed = await KanbanMcpTools.UpdateAcceptanceCriterion(h, c.Id, text: "renamed");
        Assert.Equal("renamed", renamed.Text);
        Assert.True(renamed.Completed);

        var blank = await Assert.ThrowsAsync<McpException>(
            () => KanbanMcpTools.UpdateAcceptanceCriterion(h, c.Id, text: "   "));
        Assert.Contains("cannot be blank", blank.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_rejects_blank_text(string text)
    {
        using var h = new SqliteTestFactory();
        var (taskId, _) = await SeedTaskAsync(h);

        var ex = await Assert.ThrowsAsync<McpException>(
            () => KanbanMcpTools.CreateAcceptanceCriterion(h, taskId, text));
        Assert.Contains("cannot be blank", ex.Message);
    }

    [Fact]
    public async Task Create_rejects_text_over_the_limit()
    {
        using var h = new SqliteTestFactory();
        var (taskId, _) = await SeedTaskAsync(h);

        var ex = await Assert.ThrowsAsync<McpException>(
            () => KanbanMcpTools.CreateAcceptanceCriterion(h, taskId, new string('x', 1001)));
        Assert.Contains("1000 characters or fewer", ex.Message);
    }

    /// <summary>
    /// The native tools and the Node wrapper expose the SAME tool names, so they must take the
    /// SAME argument names — an agent that moves between transports must not have to rewrite its
    /// calls.
    /// </summary>
    /// <remarks>
    /// This is the defect class <c>McpCreateTaskSortOrderTests</c> was written to prevent
    /// ("the same logical call produced different data depending on which transport the agent
    /// happened to be using"). The native tools first shipped with <c>criterionId</c> while
    /// <c>scripts/mcp-server.mjs</c> has always used <c>acId</c>, which would have failed with a
    /// JSON-RPC -32602 naming an argument the agent had never heard of. Caught in review of
    /// PR #442; this test is the guard so it cannot come back.
    /// </remarks>
    [Theory]
    [InlineData(nameof(KanbanMcpTools.MarkAcceptanceCriterionDone))]
    [InlineData(nameof(KanbanMcpTools.UpdateAcceptanceCriterion))]
    public void Criterion_id_parameter_is_named_acId_to_match_the_node_wrapper(string methodName)
    {
        var method = typeof(KanbanMcpTools).GetMethod(methodName);
        Assert.NotNull(method);

        var names = method!.GetParameters().Select(p => p.Name).ToArray();
        Assert.Contains("acId", names);
        Assert.DoesNotContain("criterionId", names);
    }

    /// <summary>
    /// Every argument of every new tool, checked against the wrapper's schema — not just the id
    /// on two of the four.
    /// </summary>
    /// <remarks>
    /// The narrower test above pins one past mistake. This one states the whole contract, because
    /// a reviewer pointed out the obvious hole: that test was green while a real divergence
    /// (<c>completed</c>'s required-ness) still existed, and it covered two of four tools.
    /// The names here are transcribed from <c>scripts/mcp-server.mjs</c>'s inputSchema blocks.
    /// </remarks>
    [Theory]
    [InlineData(nameof(KanbanMcpTools.ListAcceptanceCriteria), "taskId")]
    [InlineData(nameof(KanbanMcpTools.CreateAcceptanceCriterion), "taskId,text,sortOrder")]
    [InlineData(nameof(KanbanMcpTools.MarkAcceptanceCriterionDone), "acId,completed")]
    [InlineData(nameof(KanbanMcpTools.UpdateAcceptanceCriterion), "acId,text,completed,sortOrder")]
    public void Every_tool_argument_matches_the_node_wrapper_schema(string methodName, string expectedCsv)
    {
        var method = typeof(KanbanMcpTools).GetMethod(methodName);
        Assert.NotNull(method);

        // Drop the DI-injected factory and the cancellation token — neither crosses the wire.
        var wireArgs = method!.GetParameters()
            .Where(p => p.ParameterType != typeof(CancellationToken)
                     && !p.ParameterType.Name.StartsWith("IDbContextFactory", StringComparison.Ordinal))
            .Select(p => p.Name)
            .ToArray();

        Assert.Equal(expectedCsv.Split(','), wireArgs);
    }

    /// <summary>
    /// Errors must name the tool that yields a valid id — the mcp-builder convention this file's
    /// header cites, and the difference between an agent that recovers and one that gives up.
    /// </summary>
    [Fact]
    public async Task Unknown_ids_are_actionable_errors_not_framework_faults()
    {
        using var h = new SqliteTestFactory();

        var missingTask = await Assert.ThrowsAsync<McpException>(
            () => KanbanMcpTools.ListAcceptanceCriteria(h, 9999));
        Assert.Contains("get_project_context", missingTask.Message);

        var missingCriterion = await Assert.ThrowsAsync<McpException>(
            () => KanbanMcpTools.MarkAcceptanceCriterionDone(h, 9999));
        Assert.Contains("list_acceptance_criteria", missingCriterion.Message);
    }
}
