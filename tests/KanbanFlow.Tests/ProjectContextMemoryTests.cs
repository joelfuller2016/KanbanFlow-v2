using KanbanFlow.Data;
using KanbanFlow.Mcp;
using KanbanFlow.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace KanbanFlow.Tests;

public sealed class ProjectContextMemoryTests
{
    /// <summary>The native MCP schema makes continuation discoverable without exposing DI services.</summary>
    [Fact]
    public void Context_NativeToolSchemaExposesAnOptionalMemoryCursor()
    {
        using var factory = new SqliteTestFactory();
        using var services = new ServiceCollection()
            .AddSingleton<IDbContextFactory<AppDbContext>>(factory)
            .BuildServiceProvider();
        var tool = McpServerTool.Create(typeof(KanbanMcpTools).GetMethod(nameof(KanbanMcpTools.GetProjectContext))!,
            options: new McpServerToolCreateOptions { Services = services });

        var schema = tool.ProtocolTool.InputSchema;
        var properties = schema.GetProperty("properties");
        Assert.True(properties.TryGetProperty("memoryCursor", out _));
        Assert.False(properties.TryGetProperty("dbFactory", out _));
        Assert.False(properties.TryGetProperty("cancellationToken", out _));
        Assert.Equal(["projectId"], schema.GetProperty("required").EnumerateArray().Select(p => p.GetString()));
        Assert.Contains("MemoryNextCursor", tool.ProtocolTool.Description);
    }

    /// <summary>Only current versions in the selected project's namespace belong in context.</summary>
    [Fact]
    public async Task Context_ReturnsOnlyCurrentMemoryFromTheRequestedProject()
    {
        using var factory = new SqliteTestFactory();
        using var db = factory.CreateDbContext();
        var project = new Project { Name = "Project context" };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        var key = project.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var current = new MemoryNode { ScopeKey = key, Content = "Use the reviewed API", Type = MemoryType.Decision, Source = "decision-review" };
        db.MemoryNodes.AddRange(current,
            new MemoryNode { ScopeKey = key, Content = "Old decision", ValidTo = DateTime.UtcNow },
            new MemoryNode { ScopeKey = key, Content = "Superseded decision", SupersededById = 999 },
            new MemoryNode { ScopeKey = key, Content = "Personal preference", Scope = MemoryScope.Agent },
            new MemoryNode { ScopeKey = "another-project", Content = "Other project" });
        await db.SaveChangesAsync();

        var context = await KanbanMcpTools.GetProjectContext(factory, project.Id);

        var node = Assert.Single(context.Memory);
        Assert.Equal(current.Id, node.Id);
        Assert.Equal("Decision", node.Type);
        Assert.Equal("decision-review", node.Source);
        Assert.Equal($"MemoryNode:{current.Id}", node.Reference);
        Assert.Equal($"Project:{project.Id}", context.Reference);
        Assert.Equal("KanbanFlow", context.Authority);
        Assert.False(context.MemoryTruncated);
        Assert.Null(context.MemoryNextCursor);
    }

    /// <summary>A full page exposes a continuation that retrieves the omitted node.</summary>
    [Fact]
    public async Task Context_BoundsMemoryAndReportsWhenMoreMustBeRetrieved()
    {
        using var factory = new SqliteTestFactory();
        using var db = factory.CreateDbContext();
        var project = new Project { Name = "Large project" };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        for (var i = 0; i < 201; i++)
            db.MemoryNodes.Add(new MemoryNode { ScopeKey = project.Id.ToString(), Content = $"Fact {i}" });
        await db.SaveChangesAsync();

        var context = await KanbanMcpTools.GetProjectContext(factory, project.Id);

        Assert.Equal(200, context.Memory.Count);
        Assert.True(context.MemoryTruncated);
        Assert.Equal("Fact 200", context.Memory[0].Content);
        Assert.DoesNotContain(context.Memory, n => n.Content == "Fact 0");
        var next = await KanbanMcpTools.GetProjectContext(factory, project.Id, context.MemoryNextCursor);
        Assert.Equal("Fact 0", Assert.Single(next.Memory).Content);
        Assert.False(next.MemoryTruncated);
        Assert.Null(next.MemoryNextCursor);
    }

    /// <summary>Creation time takes precedence over insertion ID, including at a page boundary.</summary>
    [Fact]
    public async Task Context_PagesByCreationTimeThenIdWithoutRepeatingOrLosingNodes()
    {
        using var factory = new SqliteTestFactory();
        using var db = factory.CreateDbContext();
        var project = new Project { Name = "Chronological context" };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        var epoch = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var nodes = Enumerable.Range(0, 403).Select(i => new MemoryNode
        {
            ScopeKey = project.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Content = $"Fact {i}",
            CreatedAt = epoch.AddDays(-(i / 3))
        }).ToArray();
        db.MemoryNodes.AddRange(nodes);
        await db.SaveChangesAsync();

        var ids = new List<int>();
        string? cursor = null;
        do
        {
            var context = await KanbanMcpTools.GetProjectContext(factory, project.Id, cursor);
            Assert.InRange(context.Memory.Count, 1, 200);
            Assert.Equal(context.MemoryNextCursor is not null, context.MemoryTruncated);
            ids.AddRange(context.Memory.Select(n => n.Id));
            cursor = context.MemoryNextCursor;
        } while (cursor is not null);

        Assert.Equal(nodes.OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id).Select(n => n.Id), ids);
    }

    /// <summary>Continuation survives deletion of its anchor and insertion before the current page.</summary>
    [Fact]
    public async Task Context_ContinuationRetainsScopeAndCurrentVersionFiltersAfterConcurrentChanges()
    {
        using var factory = new SqliteTestFactory();
        using var db = factory.CreateDbContext();
        var project = new Project { Name = "Changing context" };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        var key = project.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var epoch = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var nodes = Enumerable.Range(0, 203).Select(i => new MemoryNode
        {
            ScopeKey = key, Content = $"Fact {i}", CreatedAt = epoch.AddSeconds(i)
        }).ToArray();
        db.MemoryNodes.AddRange(nodes);
        await db.SaveChangesAsync();
        var first = await KanbanMcpTools.GetProjectContext(factory, project.Id);
        db.MemoryNodes.Remove(nodes.Single(n => n.Id == first.Memory[^1].Id));
        nodes[1].ValidTo = epoch.AddDays(1);
        nodes[2].SupersededById = nodes[^1].Id;
        db.MemoryNodes.AddRange(
            new MemoryNode { ScopeKey = key, Content = "Newer insert", CreatedAt = epoch.AddDays(1) },
            new MemoryNode { ScopeKey = "other-project", Content = "Other project", CreatedAt = epoch },
            new MemoryNode { ScopeKey = key, Scope = MemoryScope.Agent, Content = "Agent memory", CreatedAt = epoch });
        await db.SaveChangesAsync();

        var next = await KanbanMcpTools.GetProjectContext(factory, project.Id, first.MemoryNextCursor);

        Assert.Equal(nodes[0].Id, Assert.Single(next.Memory).Id);
        Assert.False(next.MemoryTruncated);
        Assert.Null(next.MemoryNextCursor);
    }

    /// <summary>Malformed and cross-project cursors produce an actionable MCP error.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("1:-1:1")]
    [InlineData("1:3155378976000000000:1")]
    [InlineData("1:0:0")]
    [InlineData("999:0:1")]
    public async Task Context_RejectsInvalidCursor(string cursor)
    {
        using var factory = new SqliteTestFactory();
        using var db = factory.CreateDbContext();
        var project = new Project { Name = "Cursor validation" };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<McpException>(() => KanbanMcpTools.GetProjectContext(factory, project.Id, cursor));

        Assert.Contains("Invalid memoryCursor", error.Message);
    }
}
