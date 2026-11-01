using System.Net.Http.Json;
using System.Text.Json;
using KanbanFlow.Data;
using KanbanFlow.Mcp;
using KanbanFlow.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KanbanFlow.Tests;

public sealed class ProjectContextMemoryHttpTests
{
    /// <summary>A native HTTP client discovers the cursor and retrieves complete authored memory on both pages.</summary>
    [Fact]
    public async Task Context_NativeHttpClientRetrievesCompleteCurrentMemoryAcrossPages()
    {
        using var factory = new SqliteTestFactory();
        await using var db = factory.CreateDbContext();
        var authored = "# Reviewed decision\n\n" + string.Concat(Enumerable.Repeat("  Keep Markdown whitespace — 日本語.  \n", 3000));
        var project = new Project { Name = "Native context", Readme = authored };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        var key = project.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var epoch = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var nodes = Enumerable.Range(0, 202).Select(i => new MemoryNode
        {
            ScopeKey = key,
            Content = i is 0 or 201 ? authored : $"Fact {i}",
            Type = MemoryType.Decision,
            Source = "reviewed-source",
            ValidFrom = epoch,
            CreatedAt = epoch.AddSeconds(i)
        }).ToArray();
        db.MemoryNodes.AddRange(nodes);
        db.MemoryNodes.AddRange(
            new MemoryNode { ScopeKey = key, Content = "Retired", ValidTo = epoch },
            new MemoryNode { ScopeKey = key, Content = "Superseded", SupersededById = 999 },
            new MemoryNode { ScopeKey = key, Scope = MemoryScope.Session, Content = "Session memory" },
            new MemoryNode { ScopeKey = "other-project", Content = "Other project" });
        await db.SaveChangesAsync();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IDbContextFactory<AppDbContext>>(factory);
        builder.Services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithToolsFromAssembly(typeof(KanbanMcpTools).Assembly);
        await using var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
            client.DefaultRequestHeaders.Add("MCP-Protocol-Version", "2025-06-18");
            var listed = await RpcAsync(client, 1, "tools/list", new { });
            var tool = listed.GetProperty("tools").EnumerateArray()
                .Single(t => t.GetProperty("name").GetString() == "get_project_context");
            Assert.True(tool.GetProperty("inputSchema").GetProperty("properties").TryGetProperty("memoryCursor", out _));

            var ids = new List<int>();
            string? cursor = null;
            var pages = 0;
            do
            {
                var call = await RpcAsync(client, 2 + pages, "tools/call", new
                {
                    name = "get_project_context",
                    arguments = new { projectId = project.Id, memoryCursor = cursor }
                });
                Assert.False(call.TryGetProperty("isError", out var isError) && isError.GetBoolean(), call.ToString());
                using var content = JsonDocument.Parse(call.GetProperty("content")[0].GetProperty("text").GetString()!);
                var context = content.RootElement;
                Assert.Equal("KanbanFlow", context.GetProperty("authority").GetString());
                Assert.Equal($"Project:{project.Id}", context.GetProperty("reference").GetString());
                Assert.Equal(authored, context.GetProperty("readme").GetString());
                var memory = context.GetProperty("memory");
                Assert.Equal(pages == 0 ? 200 : 2, memory.GetArrayLength());
                foreach (var node in memory.EnumerateArray())
                {
                    var id = node.GetProperty("id").GetInt32();
                    var expected = nodes.Single(n => n.Id == id);
                    Assert.Equal(expected.Content, node.GetProperty("content").GetString());
                    Assert.Equal($"MemoryNode:{id}", node.GetProperty("reference").GetString());
                    Assert.Equal("Decision", node.GetProperty("type").GetString());
                    Assert.Equal(expected.Source, node.GetProperty("source").GetString());
                    Assert.Equal(expected.ValidFrom, node.GetProperty("validFrom").GetDateTime());
                    ids.Add(id);
                }
                cursor = context.TryGetProperty("memoryNextCursor", out var continuation) ? continuation.GetString() : null;
                Assert.Equal(cursor is not null, context.GetProperty("memoryTruncated").GetBoolean());
                pages++;
                Assert.InRange(pages, 1, 2);
            } while (cursor is not null);
            Assert.Equal(2, pages);
            Assert.Equal(nodes.Reverse().Select(n => n.Id), ids);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static async Task<JsonElement> RpcAsync(HttpClient client, int id, string method, object parameters)
    {
        using var response = await client.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id, method, @params = parameters });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        var json = response.Content.Headers.ContentType?.MediaType == "text/event-stream"
            ? body.Split('\n').Single(line => line.StartsWith("data: ", StringComparison.Ordinal))[6..]
            : body;
        using var message = JsonDocument.Parse(json);
        Assert.False(message.RootElement.TryGetProperty("error", out _), json);
        return message.RootElement.GetProperty("result").Clone();
    }
}
