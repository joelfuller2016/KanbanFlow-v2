using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using KanbanFlow.Data;
using KanbanFlow.Models;
using KanbanFlow.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace KanbanFlow.Tests;

public sealed class TaskDescriptionEndpointTests
{
    private const string Original = "Original card\r\n\r\n    notes 漢🙂  \r\n";
    private const string Proposed = "Proposed card\r\n\r\n    notes 漢🙂  \r\n";

    [Fact]
    public async Task Patch_DoesNotOverwriteAnEditCommittedBeforeItAcquiresTheWriter()
    {
        var interceptor = new BeforeTransaction();
        using var factory = new SqliteFileTestFactory(interceptor);
        var id = await SeedAsync(factory);
        const string edited = Original + "A board edit";
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(factory.ConnectionString).Options;
        interceptor.Run = async ct =>
        {
            await using var editor = new AppDbContext(options);
            await TaskService.UpdateInContextAsync(editor, id, new TaskUpdate(Description: edited), "editor", ct);
        };

        var updated = await new TaskService(factory).UpdateDescriptionIfCurrentAsync(id, Hash(Original), Proposed, "sync-test");

        Assert.False(updated);
        await using var db = factory.CreateDbContext();
        Assert.Equal(edited, (await db.Tasks.SingleAsync()).Description);
        var change = Assert.Single(await db.ActivityEvents.Where(e => e.EventType == "TaskUpdated").ToListAsync());
        Assert.Equal("editor", change.Actor);
    }

    [Fact]
    public async Task Patch_RequiresCurrentDescriptionAndAuditsExactText()
    {
        using var factory = new SqliteTestFactory();
        var id = await SeedAsync(factory);
        await using var host = await TaskDeleteActorEndpointTests.TaskHost.StartAsync(factory);

        using var stale = await PatchAsync(host.Client, id, Hash("An older description"));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using (var db = factory.CreateDbContext())
        {
            Assert.Equal(Original, (await db.Tasks.SingleAsync()).Description);
            Assert.Empty(await db.ActivityEvents.ToListAsync());
        }

        using var current = await PatchAsync(host.Client, id, Hash(Original));
        Assert.Equal(HttpStatusCode.NoContent, current.StatusCode);
        using var repeat = await PatchAsync(host.Client, id, Hash(Proposed));
        Assert.Equal(HttpStatusCode.NoContent, repeat.StatusCode);
        using var after = factory.CreateDbContext();
        var task = await after.Tasks.SingleAsync();
        Assert.Equal(Proposed, task.Description);
        Assert.Equal("Task name", task.Name);
        var change = Assert.Single(await after.ActivityEvents.Where(e => e.EventType == "TaskUpdated").ToListAsync());
        Assert.Equal("sync-test", change.Actor);
    }

    private static Task<HttpResponseMessage> PatchAsync(HttpClient client, int id, string expectedSha256)
        => client.PatchAsJsonAsync($"/api/tasks/{id}/description",
            new { expectedSha256, description = Proposed, actor = "sync-test" });

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static async Task<int> SeedAsync(IDbContextFactory<AppDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        var task = new TaskItem
        {
            Name = "Task name", Description = Original,
            Feature = new Feature { Name = "Feature", Project = new Project { Name = "Project" } }
        };
        db.Tasks.Add(task);
        await db.SaveChangesAsync();
        return task.Id;
    }

    private sealed class BeforeTransaction : DbTransactionInterceptor
    {
        public Func<CancellationToken, Task>? Run { get; set; }

        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection,
            TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            if (Run is { } run)
            {
                Run = null;
                await run(cancellationToken);
            }
            return result;
        }
    }
}
