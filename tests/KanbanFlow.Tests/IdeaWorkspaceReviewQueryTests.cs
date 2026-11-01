using KanbanFlow.Models;
using KanbanFlow.Services.IdeaWorkspaces;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Tests;

public sealed class IdeaWorkspaceReviewQueryTests
{
    [Theory]
    [InlineData("all")]
    [InlineData("ALL")]
    [InlineData(" AlL ")]
    public async Task CatalogAllKind_IsUnfiltered_AndSharesContinuationWithAnOmittedKind(string kind)
    {
        using var factory = new SqliteTestFactory();
        await using (var db = factory.CreateDbContext())
        {
            db.Ideas.AddRange(new Idea { Title = "Current idea" }, new Idea { Title = "Archived idea", Status = IdeaStatus.Archived });
            db.Projects.AddRange(new Project { Name = "First project" }, new Project { Name = "Second project" });
            await db.SaveChangesAsync();
        }
        var service = new IdeaWorkspaceService(factory);
        var unfiltered = await service.ListAsync();

        var first = await service.ListAsync(kind: kind, limit: 2);
        Assert.Equal(4, first.Total);
        Assert.NotNull(first.NextCursor);
        var second = await service.ListAsync(cursor: first.NextCursor);

        Assert.Equal(unfiltered.Items.Select(item => item.WorkspaceId),
            first.Items.Concat(second.Items).Select(item => item.WorkspaceId));
        Assert.Equal(4, second.Total);
        Assert.Null(second.NextCursor);
        var omittedFirst = await service.ListAsync(limit: 2);
        var explicitSecond = await service.ListAsync(kind: kind, cursor: omittedFirst.NextCursor);
        Assert.Equal(second.Items.Select(item => item.WorkspaceId), explicitSecond.Items.Select(item => item.WorkspaceId));
        await using var verify = factory.CreateDbContext();
        Assert.Empty(await verify.IdeaWorkspaceStates.ToListAsync());
        Assert.Empty(await verify.IdeaWorkspaceReceipts.ToListAsync());
        Assert.Empty(await verify.ActivityEvents.ToListAsync());
    }
}
