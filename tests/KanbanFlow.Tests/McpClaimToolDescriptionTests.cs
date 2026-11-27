using System.ComponentModel;
using System.Reflection;
using KanbanFlow.Mcp;
using KanbanFlow.Models;
using KanbanFlow.Services;
using ModelContextProtocol;

namespace KanbanFlow.Tests;

public sealed class McpClaimToolDescriptionTests
{
    [Fact]
    public void ClaimToolDescriptionExplainsExpiryRenewalAndTakeover()
    {
        var method = typeof(KanbanMcpTools).GetMethod(nameof(KanbanMcpTools.ClaimTask));
        Assert.NotNull(method);

        var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description;
        Assert.NotNull(description);
        Assert.Contains("TTL", description, StringComparison.Ordinal);
        Assert.Contains("renew", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("takeover", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TaskClaimStolen", description, StringComparison.Ordinal);
    }

    [Fact]
    public void RankingToolAndActorParameterDistinguishFreshFromExpiredClaims()
    {
        var method = typeof(KanbanMcpTools).GetMethod(nameof(KanbanMcpTools.GetNextTasks));
        Assert.NotNull(method);

        var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description;
        Assert.NotNull(description);
        Assert.Contains("fresh claim", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TTL", description, StringComparison.Ordinal);
        Assert.Contains("available", description, StringComparison.OrdinalIgnoreCase);

        var actor = Assert.Single(method.GetParameters(), parameter => parameter.Name == "actor");
        var actorDescription = actor.GetCustomAttribute<DescriptionAttribute>()?.Description;
        Assert.NotNull(actorDescription);
        Assert.Contains("fresh claim", actorDescription, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FreshClaimConflictIdentifiesHolderAndExplainsWhenToRetry()
    {
        using var factory = new SqliteTestFactory();
        int taskId;
        await using (var db = await factory.CreateDbContextAsync())
        {
            var project = new Project { Name = "Claim guidance project" };
            var feature = new Feature { Name = "Claims", Project = project };
            var task = new TaskItem { Name = "Claimed task", Feature = feature, Status = WorkStatus.Todo };
            db.Add(task);
            await db.SaveChangesAsync();
            taskId = task.Id;
        }

        var now = new DateTime(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);
        var service = new TaskService(factory, TimeSpan.FromHours(1), clock: () => now);
        await service.ClaimAsync(taskId, "working-agent");

        var conflict = await Assert.ThrowsAsync<McpException>(
            () => KanbanMcpTools.ClaimTask(service, taskId, "waiting-agent"));

        Assert.Contains("working-agent", conflict.Message, StringComparison.Ordinal);
        Assert.Contains("fresh claim", conflict.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("retry claim_task", conflict.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("configured TTL", conflict.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("work a different task", conflict.Message, StringComparison.OrdinalIgnoreCase);
    }
}
