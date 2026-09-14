using KanbanFlow.Mcp;
using KanbanFlow.Models;
using KanbanFlow.Models.Workflows;
using KanbanFlow.Services;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;

namespace KanbanFlow.Tests.Services.Workflows;

public sealed class IdeaEditVersionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EditFromEarlierRead_CannotOverwriteACompletedHumanEdit(bool native)
    {
        using var factory = new WorkflowSqliteTestFactory();
        var service = new IdeaService(factory);
        var created = await service.CreateAsync(new IdeaCreate("Original", "Original notes"), "author");
        var stale = (await service.GetByIdAsync(created.Id))!;
        var updated = await service.UpdateAsync(created.Id,
            new IdeaUpdate(Title: "Human's title", Body: "Human's notes", ExpectedWorkflowVersion: stale.WorkflowVersion), "human");
        var before = (await factory.Service.GetForIdeaAsync(created.Id))!;

        if (native)
        {
            var error = await Assert.ThrowsAsync<McpException>(() => KanbanMcpTools.UpdateIdea(service, created.Id,
                title: stale.Title, body: stale.Body, expectedWorkflowVersion: stale.WorkflowVersion));
            Assert.StartsWith("conflict:", error.Message);
        }
        else
        {
            var error = await Assert.ThrowsAsync<WorkflowException>(() => service.UpdateAsync(created.Id,
                new IdeaUpdate(Title: stale.Title, Body: stale.Body, ExpectedWorkflowVersion: stale.WorkflowVersion), "stale-client"));
            Assert.Equal("conflict", error.Code);
        }

        var after = (await service.GetByIdAsync(created.Id))!;
        Assert.Equal(updated.Title, after.Title);
        Assert.Equal(updated.Body, after.Body);
        Assert.Equal(updated.WorkflowVersion, after.WorkflowVersion);
        Assert.Equal(updated.UpdatedAt, after.UpdatedAt);
        var workflow = (await factory.Service.GetForIdeaAsync(created.Id))!;
        Assert.Equal(before.Version, workflow.Version);
        Assert.Equal(before.Messages.Select(m => (m.Id, m.Text)), workflow.Messages.Select(m => (m.Id, m.Text)));
        await using var db = factory.CreateDbContext();
        Assert.Single(await db.ActivityEvents.Where(e => e.EventType == "IdeaUpdated").ToListAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    public async Task MissingOrInvalidVersion_IsRejectedEvenForANoOp(int? expected)
    {
        using var factory = new WorkflowSqliteTestFactory();
        var service = new IdeaService(factory);
        var idea = await service.CreateAsync(new IdeaCreate("Original"), "author");
        await Assert.ThrowsAsync<ValidationException>(() => service.UpdateAsync(idea.Id,
            new IdeaUpdate(Title: idea.Title, ExpectedWorkflowVersion: expected), "client"));
        await using var db = factory.CreateDbContext();
        Assert.False(await db.ActivityEvents.AnyAsync(e => e.EventType == "IdeaUpdated"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadsExposeCurrentAndLegacyVersions_AndLegacyZeroCanBeAdoptedOnlyOnce(bool hasBody)
    {
        using var factory = new WorkflowSqliteTestFactory();
        var service = new IdeaService(factory);
        var created = await service.CreateAsync(new IdeaCreate("Captured"), "author");
        var legacy = new Idea { Title = "Legacy", Body = hasBody ? "Original legacy notes" : null };
        await using (var db = factory.CreateDbContext()) { db.Ideas.Add(legacy); await db.SaveChangesAsync(); }
        var listed = await service.ListAsync();
        Assert.Equal(created.WorkflowVersion, listed.Single(i => i.Id == created.Id).WorkflowVersion);
        Assert.Equal(0, listed.Single(i => i.Id == legacy.Id).WorkflowVersion);
        Assert.Equal(created.WorkflowVersion, (await KanbanMcpTools.GetIdea(service, created.Id)).WorkflowVersion);
        var updated = await service.UpdateAsync(legacy.Id, new IdeaUpdate(Title: "Revised title", Body: "Revised", ExpectedWorkflowVersion: 0), "human");
        Assert.True(updated.WorkflowVersion > 0);
        await Assert.ThrowsAsync<WorkflowException>(() => service.UpdateAsync(legacy.Id,
            new IdeaUpdate(Body: "Late overwrite", ExpectedWorkflowVersion: 0), "agent"));
        var workflow = (await factory.Service.GetForIdeaAsync(legacy.Id))!;
        Assert.Equal(hasBody ? "Original legacy notes" : "Legacy", Assert.Single(workflow.Messages, m => m.Kind == "original").Text);
    }

    [Fact]
    public async Task NewWorkflowDecision_InvalidatesAnOpenIdeaEdit()
    {
        using var factory = new WorkflowSqliteTestFactory();
        var service = new IdeaService(factory);
        var idea = await service.CreateAsync(new IdeaCreate("Original"), "author");
        var request = (await factory.Service.GetForIdeaAsync(idea.Id))!;
        await factory.Service.ArchiveAsync(request.Id, request.Version, true, "human", "archive");
        await Assert.ThrowsAsync<WorkflowException>(() => service.UpdateAsync(idea.Id,
            new IdeaUpdate(Title: "Late title", ExpectedWorkflowVersion: idea.WorkflowVersion), "agent"));
        Assert.Equal("Original", (await service.GetByIdAsync(idea.Id))!.Title);
    }
}
