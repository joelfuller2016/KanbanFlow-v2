using KanbanFlow.Mcp;
using KanbanFlow.Models.Workflows;
using KanbanFlow.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ModelContextProtocol;

namespace KanbanFlow.Tests.Services.Workflows;

public sealed class IdeaMcpConflictTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdeaTools_SurfaceWorkflowConflictAndRollBackFailedMutation(bool promote)
    {
        var interceptor = new WorkflowConflict();
        using var factory = new WorkflowSqliteTestFactory(interceptor);
        var service = new IdeaService(factory);
        var idea = await service.CreateAsync(new IdeaCreate("Original", "Original body"), "human");
        interceptor.Enabled = true;

        var error = await Assert.ThrowsAsync<McpException>(async () =>
        {
            if (promote) await KanbanMcpTools.PromoteIdea(service, idea.Id);
            else await KanbanMcpTools.UpdateIdea(service, idea.Id, title: "Changed title");
        });

        Assert.StartsWith("conflict: This idea was modified by another client", error.Message);
        await using var db = factory.CreateDbContext();
        Assert.Equal("Original", (await db.Ideas.SingleAsync()).Title);
        Assert.Null((await db.Ideas.SingleAsync()).PromotedProjectId);
        Assert.Empty(await db.Projects.ToListAsync());
        Assert.False(await db.ActivityEvents.AnyAsync(e => e.EventType == "IdeaUpdated" || e.EventType == "IdeaPromoted"));
    }

    private sealed class WorkflowConflict : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && eventData.Context!.ChangeTracker.Entries<WorkRequest>().Any(e => e.State == EntityState.Modified))
                throw new DbUpdateConcurrencyException("Injected workflow concurrency conflict");
            return ValueTask.FromResult(result);
        }
    }
}
