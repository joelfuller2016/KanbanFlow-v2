using KanbanFlow.Mcp;
using KanbanFlow.Models.Workflows;
using KanbanFlow.Services.Workflows;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using static KanbanFlow.Tests.WorkflowSqliteTestFactory;

namespace KanbanFlow.Tests.Services.Workflows;

public sealed class WorkflowReceiptHashTests
{
    [Fact]
    public async Task NativePlanRetry_IgnoresDictionaryOrderButPreservesArrayOrderAndValues()
    {
        using var f = new WorkflowSqliteTestFactory();
        var first = Draft("First");
        var second = Draft("Second");
        var request = await f.PublishedAsync(first, second);
        var context = (await f.Service.ContextAsync(request.Id))!;
        var tokens = context.Tasks.ToDictionary(t => t.TaskId, t => t.EditToken);
        var input = new WorkflowCommandInput { Operation = "save_plan", Actor = "client", Key = "same-plan",
            RequestId = request.Id, ExpectedVersion = request.Version, Tasks = [first, second], TaskBaselines = tokens };
        var receipt = await WorkspaceMcpTools.WorkflowCommand(f.Service, new WorkflowOptions(), input);
        await using var db = f.CreateDbContext();
        var events = await db.ActivityEvents.CountAsync();
        input.TaskBaselines = tokens.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Equal(receipt, await WorkspaceMcpTools.WorkflowCommand(f.Service, new WorkflowOptions(), input));
        Assert.Equal(events, await db.ActivityEvents.CountAsync());
        Assert.Equal(2, await db.PlanVersions.CountAsync());

        input.Tasks = [second, first];
        var arrayConflict = await Assert.ThrowsAsync<McpException>(() => WorkspaceMcpTools.WorkflowCommand(f.Service, new WorkflowOptions(), input));
        Assert.StartsWith("conflict:", arrayConflict.Message);
        input.Tasks = [first, second];
        input.TaskBaselines[tokens.Keys.First()] = "different token";
        var valueConflict = await Assert.ThrowsAsync<McpException>(() => WorkspaceMcpTools.WorkflowCommand(f.Service, new WorkflowOptions(), input));
        Assert.StartsWith("conflict:", valueConflict.Message);
        Assert.Equal(events, await db.ActivityEvents.CountAsync());
        Assert.Equal(2, await db.PlanVersions.CountAsync());
    }

    [Fact]
    public async Task PreCanonicalReceipt_StillAcceptsItsOriginalExactPayload()
    {
        using var f = new WorkflowSqliteTestFactory();
        var captured = await f.Service.CaptureAsync(new CaptureRequest("Legacy", "Original", null), "client", "capture");
        var brief = Brief();
        var receipt = await f.Service.SaveBriefAsync(captured.RequestId, captured.Version, brief, "client", "brief");
        await using var db = f.CreateDbContext();
        var row = await db.WorkflowReceipts.SingleAsync(r => r.Key == "brief");
        row.PayloadHash = WorkspaceService.Hash(new { id = captured.RequestId, expected = captured.Version, payload = brief });
        await db.SaveChangesAsync();
        Assert.Equal(receipt, await f.Service.SaveBriefAsync(captured.RequestId, captured.Version, brief, "client", "brief"));
        Assert.Single(await db.BriefRevisions.ToListAsync());
    }
}
