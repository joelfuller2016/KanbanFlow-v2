using KanbanFlow.Mcp;
using KanbanFlow.Models;

namespace KanbanFlow.Tests;

public sealed class ProjectContextMemoryTests
{
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
    }

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
    }
}
