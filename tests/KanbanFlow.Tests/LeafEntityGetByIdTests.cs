using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KanbanFlow.Data;
using KanbanFlow.Models;
using KanbanFlow.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KanbanFlow.Tests;

public sealed class LeafEntityGetByIdTests
{
    [Theory]
    [InlineData("subtasks")]
    [InlineData("acceptance-criteria")]
    [InlineData("requirements")]
    public async Task CreateLocationReadsTheSameLeafAsTheParentList(string resource)
    {
        using var factory = new SqliteTestFactory();
        var project = new Project { Name = "Parent project" };
        var feature = new Feature { Name = "Linked feature", Project = project };
        var task = new TaskItem { Name = "Linked task", Feature = feature };
        var parentSubtask = new Subtask { TaskItem = task, Text = "Parent subtask" };
        await using (var db = factory.CreateDbContext())
        {
            db.Add(parentSubtask);
            await db.SaveChangesAsync();
        }

        await using var app = await StartAsync(factory);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var collection = resource == "requirements"
            ? $"/api/projects/{project.Id}/{resource}"
            : $"/api/tasks/{task.Id}/{resource}";
        object payload = resource switch
        {
            "subtasks" => new { text = "Nested leaf", parentSubtaskId = parentSubtask.Id, completed = true },
            "acceptance-criteria" => new { text = "Leaf criterion", completed = true },
            _ => new
            {
                text = "Leaf requirement",
                description = "Details",
                priority = "P0",
                status = "InProgress",
                featureId = feature.Id,
                taskItemId = task.Id
            }
        };

        using var sibling = await client.PostAsJsonAsync(collection, new { text = "Sibling record" });
        Assert.Equal(HttpStatusCode.Created, sibling.StatusCode);
        using var create = await client.PostAsJsonAsync(collection, payload);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetInt32();
        var location = Assert.IsType<Uri>(create.Headers.Location);
        Assert.Equal($"/api/{resource}/{id}", location.OriginalString);

        await using var auditDb = factory.CreateDbContext();
        var auditCount = await auditDb.ActivityEvents.CountAsync();
        using var read = await client.GetAsync(location);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var item = await read.Content.ReadFromJsonAsync<JsonElement>();
        foreach (var field in created.EnumerateObject())
        {
            var value = item.GetProperty(field.Name);
            if (field.Name is "createdAt" or "updatedAt")
                Assert.Equal(field.Value.GetDateTime().Ticks, value.GetDateTime().Ticks);
            else
                Assert.True(JsonElement.DeepEquals(field.Value, value), $"Field {field.Name} differs from the create response.");
        }
        if (resource == "requirements")
        {
            Assert.Equal(feature.Name, item.GetProperty("featureName").GetString());
            Assert.Equal(task.Name, item.GetProperty("taskItemName").GetString());
        }

        var list = await client.GetFromJsonAsync<JsonElement>(collection);
        var listed = Assert.Single(list.EnumerateArray(), leaf => leaf.GetProperty("id").GetInt32() == id);
        Assert.Equal(listed.GetRawText(), item.GetRawText());
        Assert.Equal(auditCount, await auditDb.ActivityEvents.CountAsync());
    }

    [Theory]
    [InlineData("subtasks", "Subtask")]
    [InlineData("acceptance-criteria", "Acceptance criterion")]
    [InlineData("requirements", "Requirement")]
    public async Task MissingLeafReturnsTheEntitySpecificNotFoundMessage(string resource, string entity)
    {
        using var factory = new SqliteTestFactory();
        await using var app = await StartAsync(factory);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var response = await client.GetAsync($"/api/{resource}/999999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal($"{entity} 999999 not found.", body.GetProperty("message").GetString());
    }

    private static async Task<WebApplication> StartAsync(SqliteTestFactory factory)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<IDbContextFactory<AppDbContext>>(factory);
        builder.Services.AddScoped(_ => factory.CreateDbContext());
        builder.Services.AddSingleton(new ClaimTtlSetting(ClaimPolicy.DefaultTtl));
        builder.Services.AddScoped<ITaskService, TaskService>();
        builder.Services.AddScoped<ITaskDependencyService, TaskDependencyService>();
        builder.Services.AddScoped<IIdeaService, IdeaService>();
        builder.Services.AddScoped<IMemoryService, MemoryService>();
        builder.Services.AddScoped<IExternalWorkItemService, ExternalWorkItemService>();
        builder.Services.AddSingleton<IMemorySearch>(_ => throw new NotSupportedException());

        var app = builder.Build();
        try
        {
            app.MapApiEndpoints();
            await app.StartAsync();
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }
}
