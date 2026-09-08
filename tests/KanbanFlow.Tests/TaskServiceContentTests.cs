using System.Text.Json;
using KanbanFlow.Models;
using KanbanFlow.Services;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Tests;

public sealed class TaskServiceContentTests
{
    private static readonly DateTime T0 = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Create_PreservesLargeDescriptionAndAuditsCompleteContent()
    {
        using var factory = new SqliteTestFactory();
        var fixture = await SeedAsync(factory, "Existing description");
        var service = new TaskService(factory);
        var body = LargeMarkdown("created body");
        Assert.True(body.Length > 100_000);

        var created = await service.CreateAsync(new TaskCreate(fixture.FeatureId, "  Complete task  ",
            Description: body, Status: WorkStatus.Todo, Priority: TaskPriority.Critical,
            Assignee: "  Reviewer  ", SortOrder: 18.5), "  Codex  ");

        Assert.Equal(body, created.Description);
        await using var persisted = factory.CreateDbContext();
        var saved = await persisted.Tasks.SingleAsync(t => t.Id == created.Id);
        Assert.Equal(body, saved.Description);
        Assert.Equal("Complete task", saved.Name);
        Assert.Equal("Reviewer", saved.Assignee);
        Assert.Equal(WorkStatus.Todo, saved.Status);
        Assert.Equal(TaskPriority.Critical, saved.Priority);
        Assert.Equal(18.5, saved.SortOrder);
        var activity = Assert.Single(await persisted.ActivityEvents.ToListAsync());
        Assert.Equal("TaskCreated", activity.EventType);
        Assert.Equal("Task", activity.EntityType);
        Assert.Equal(created.Id, activity.EntityId);
        Assert.Equal(fixture.ProjectId, activity.ProjectId);
        Assert.Equal("Codex", activity.Actor);
        Assert.Null(activity.BeforeJson);
        using var after = JsonDocument.Parse(activity.AfterJson!);
        Assert.Equal(body, after.RootElement.GetProperty("description").GetString());
        Assert.Equal("Complete task", after.RootElement.GetProperty("name").GetString());
        Assert.Equal("Reviewer", after.RootElement.GetProperty("assignee").GetString());
        var createdNote = Assert.Single(await persisted.TaskComments.Where(c => c.TaskItemId == created.Id).ToListAsync());
        Assert.Equal(CommentType.Created, createdNote.Type);
        Assert.Equal("Task created", createdNote.Text);
        Assert.Equal("Codex", createdNote.Author);
        var untouched = await persisted.Tasks.SingleAsync(t => t.Id == fixture.TaskId);
        AssertUnrelatedFields(untouched, fixture);
        Assert.Equal("Existing description", untouched.Description);
        Assert.Equal(T0, untouched.UpdatedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n\u2003")]
    public async Task Create_BlankDescription_RemainsNull(string? description)
    {
        using var factory = new SqliteTestFactory();
        var fixture = await SeedAsync(factory, "Existing description");

        var created = await new TaskService(factory).CreateAsync(
            new TaskCreate(fixture.FeatureId, "New task", Description: description), "Codex");

        Assert.Null(created.Description);
        await using var persisted = factory.CreateDbContext();
        Assert.Null((await persisted.Tasks.SingleAsync(t => t.Id == created.Id)).Description);
        var activity = Assert.Single(await persisted.ActivityEvents.ToListAsync());
        Assert.Equal("TaskCreated", activity.EventType);
        using var after = JsonDocument.Parse(activity.AfterJson!);
        Assert.Equal(JsonValueKind.Null, after.RootElement.GetProperty("description").ValueKind);
    }

    [Fact]
    public async Task Update_PreservesLargeDescriptionWithAccurateAuditAndIdempotentRepeat()
    {
        using var factory = new SqliteTestFactory();
        var beforeBody = LargeMarkdown("previous body");
        var afterBody = LargeMarkdown("revised body");
        var fixture = await SeedAsync(factory, beforeBody);
        var service = new TaskService(factory);

        var updated = await service.UpdateAsync(fixture.TaskId, new TaskUpdate(Description: afterBody), "  Codex  ");

        Assert.Equal(afterBody, updated.Description);
        DateTime savedAt;
        await using (var persisted = factory.CreateDbContext())
        {
            var saved = await persisted.Tasks.SingleAsync(t => t.Id == fixture.TaskId);
            Assert.Equal(afterBody, saved.Description);
            AssertUnrelatedFields(saved, fixture);
            Assert.NotEqual(T0, saved.UpdatedAt);
            savedAt = saved.UpdatedAt;
            var activity = Assert.Single(await persisted.ActivityEvents.ToListAsync());
            AssertDescriptionChange(activity, fixture, beforeBody, afterBody);
            using var before = JsonDocument.Parse(activity.BeforeJson!);
            using var after = JsonDocument.Parse(activity.AfterJson!);
            AssertUnrelatedSnapshot(before.RootElement, fixture);
            AssertUnrelatedSnapshot(after.RootElement, fixture);
            var comments = await persisted.TaskComments.OrderBy(c => c.Id).ToListAsync();
            Assert.Equal(2, comments.Count);
            Assert.Equal("Original discussion", comments[0].Text);
            Assert.Equal("Updated: Description", comments[1].Text);
            Assert.Equal("Codex", comments[1].Author);
        }

        await service.UpdateAsync(fixture.TaskId, new TaskUpdate(Description: afterBody), "Codex");

        await using var repeated = factory.CreateDbContext();
        var repeatedTask = await repeated.Tasks.SingleAsync(t => t.Id == fixture.TaskId);
        Assert.Equal(afterBody, repeatedTask.Description);
        Assert.Equal(savedAt, repeatedTask.UpdatedAt);
        AssertUnrelatedFields(repeatedTask, fixture);
        Assert.Single(await repeated.ActivityEvents.ToListAsync());
        Assert.Equal(2, await repeated.TaskComments.CountAsync());
    }

    [Theory]
    [InlineData("    Same content")]
    [InlineData("Same content  \r\n\t")]
    public async Task Update_WhitespaceOnlyDifferenceInNonblankDescription_IsSavedAndAudited(string edited)
    {
        using var factory = new SqliteTestFactory();
        var fixture = await SeedAsync(factory, "Same content");

        await new TaskService(factory).UpdateAsync(fixture.TaskId, new TaskUpdate(Description: edited), "Codex");

        await using var persisted = factory.CreateDbContext();
        var saved = await persisted.Tasks.SingleAsync(t => t.Id == fixture.TaskId);
        Assert.Equal(edited, saved.Description);
        AssertUnrelatedFields(saved, fixture);
        Assert.NotEqual(T0, saved.UpdatedAt);
        AssertDescriptionChange(Assert.Single(await persisted.ActivityEvents.ToListAsync()), fixture, "Same content", edited);
        Assert.Equal(2, await persisted.TaskComments.CountAsync());
    }

    [Fact]
    public async Task Update_NullDescription_PreservesContentWhenAnotherFieldChanges()
    {
        using var factory = new SqliteTestFactory();
        var body = LargeMarkdown("retained body");
        var fixture = await SeedAsync(factory, body);

        await new TaskService(factory).UpdateAsync(fixture.TaskId,
            new TaskUpdate(Name: "  Renamed task  ", Description: null), "Codex");

        await using var persisted = factory.CreateDbContext();
        var saved = await persisted.Tasks.SingleAsync(t => t.Id == fixture.TaskId);
        Assert.Equal(body, saved.Description);
        AssertUnrelatedFields(saved, fixture, name: "Renamed task");
        var activity = Assert.Single(await persisted.ActivityEvents.ToListAsync());
        AssertDescriptionChange(activity, fixture, body, body);
        using var before = JsonDocument.Parse(activity.BeforeJson!);
        using var after = JsonDocument.Parse(activity.AfterJson!);
        Assert.Equal("Existing task", before.RootElement.GetProperty("name").GetString());
        Assert.Equal("Renamed task", after.RootElement.GetProperty("name").GetString());
        Assert.Equal("Updated: Name", (await persisted.TaskComments.OrderBy(c => c.Id).LastAsync()).Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t\r\n\u2003")]
    public async Task Update_BlankDescription_ClearsContentAndAuditsCompleteOldValue(string blank)
    {
        using var factory = new SqliteTestFactory();
        var beforeBody = LargeMarkdown("cleared body");
        var fixture = await SeedAsync(factory, beforeBody);
        var service = new TaskService(factory);

        var updated = await service.UpdateAsync(fixture.TaskId, new TaskUpdate(Description: blank), "Codex");

        Assert.Null(updated.Description);
        DateTime savedAt;
        await using (var persisted = factory.CreateDbContext())
        {
            var saved = await persisted.Tasks.SingleAsync(t => t.Id == fixture.TaskId);
            Assert.Null(saved.Description);
            AssertUnrelatedFields(saved, fixture);
            savedAt = saved.UpdatedAt;
            AssertDescriptionChange(Assert.Single(await persisted.ActivityEvents.ToListAsync()), fixture, beforeBody, null);
        }

        await service.UpdateAsync(fixture.TaskId, new TaskUpdate(Description: blank), "Codex");

        await using var repeated = factory.CreateDbContext();
        var repeatedTask = await repeated.Tasks.SingleAsync(t => t.Id == fixture.TaskId);
        Assert.Null(repeatedTask.Description);
        Assert.Equal(savedAt, repeatedTask.UpdatedAt);
        Assert.Single(await repeated.ActivityEvents.ToListAsync());
        Assert.Equal(2, await repeated.TaskComments.CountAsync());
    }

    [Fact]
    public async Task AddComment_PreservesLargeMarkdownAndAuditsCompleteTextWithoutChangingTask()
    {
        using var factory = new SqliteTestFactory();
        var fixture = await SeedAsync(factory, "Existing description");
        var body = LargeMarkdown("decision comment");
        Assert.True(body.Length > 100_000);

        var added = await new TaskService(factory).AddCommentAsync(fixture.TaskId, body,
            author: "  Joel  ", type: CommentType.Decision, actor: "  Codex  ");

        Assert.Equal(body, added.Text);
        Assert.Equal("Joel", added.Author);
        await using var persisted = factory.CreateDbContext();
        var comments = await persisted.TaskComments.OrderBy(c => c.Id).ToListAsync();
        Assert.Equal(2, comments.Count);
        Assert.Equal("Original discussion", comments[0].Text);
        Assert.Equal(added.Id, comments[1].Id);
        Assert.Equal(fixture.TaskId, comments[1].TaskItemId);
        Assert.Equal(body, comments[1].Text);
        Assert.Equal("Joel", comments[1].Author);
        Assert.Equal(CommentType.Decision, comments[1].Type);
        var activity = Assert.Single(await persisted.ActivityEvents.ToListAsync());
        Assert.Equal("TaskCommentAdded", activity.EventType);
        Assert.Equal("Task", activity.EntityType);
        Assert.Equal(fixture.ProjectId, activity.ProjectId);
        Assert.Equal(fixture.TaskId, activity.EntityId);
        Assert.Equal("Codex", activity.Actor);
        Assert.Null(activity.BeforeJson);
        using var after = JsonDocument.Parse(activity.AfterJson!);
        Assert.Equal(body, after.RootElement.GetProperty("text").GetString());
        Assert.Equal("Joel", after.RootElement.GetProperty("author").GetString());
        var task = await persisted.Tasks.SingleAsync(t => t.Id == fixture.TaskId);
        AssertUnrelatedFields(task, fixture);
        Assert.Equal("Existing description", task.Description);
        Assert.Equal(T0, task.UpdatedAt);
    }

    private static void AssertDescriptionChange(ActivityEvent activity, Fixture fixture, string? beforeBody, string? afterBody)
    {
        Assert.Equal("TaskUpdated", activity.EventType);
        Assert.Equal("Task", activity.EntityType);
        Assert.Equal(fixture.TaskId, activity.EntityId);
        Assert.Equal(fixture.ProjectId, activity.ProjectId);
        Assert.Equal("Codex", activity.Actor);
        using var before = JsonDocument.Parse(activity.BeforeJson!);
        using var after = JsonDocument.Parse(activity.AfterJson!);
        Assert.Equal(beforeBody, before.RootElement.GetProperty("description").GetString());
        Assert.Equal(afterBody, after.RootElement.GetProperty("description").GetString());
    }

    private static void AssertUnrelatedFields(TaskItem task, Fixture fixture, string name = "Existing task")
    {
        Assert.Equal(name, task.Name);
        Assert.Equal(fixture.TaskId, task.Id);
        Assert.Equal(fixture.FeatureId, task.FeatureId);
        Assert.Equal(WorkStatus.Review, task.Status);
        Assert.Equal(TaskPriority.High, task.Priority);
        Assert.Equal("Joel", task.Assignee);
        Assert.Equal("agent-7", task.ClaimedBy);
        Assert.Equal(T0, task.ClaimedAt);
        Assert.Equal(7.5, task.SortOrder);
        Assert.Equal(T0, task.CreatedAt);
        Assert.Null(task.CompletedAt);
    }

    private static void AssertUnrelatedSnapshot(JsonElement snapshot, Fixture fixture)
    {
        Assert.Equal("Existing task", snapshot.GetProperty("name").GetString());
        Assert.Equal(fixture.FeatureId, snapshot.GetProperty("featureId").GetInt32());
        Assert.Equal("Review", snapshot.GetProperty("status").GetString());
        Assert.Equal("High", snapshot.GetProperty("priority").GetString());
        Assert.Equal("Joel", snapshot.GetProperty("assignee").GetString());
        Assert.Equal("agent-7", snapshot.GetProperty("claimedBy").GetString());
        Assert.Equal(T0, snapshot.GetProperty("claimedAt").GetDateTime());
        Assert.Equal(7.5, snapshot.GetProperty("sortOrder").GetDouble());
        Assert.Equal(JsonValueKind.Null, snapshot.GetProperty("completedAt").ValueKind);
    }

    private static string LargeMarkdown(string marker) =>
        "\t \r\n    " + marker + " begins with indented code\r\n\r\n" +
        string.Concat(Enumerable.Range(0, 1024).Select(i =>
            $"{i:D4} | {marker} | 中文 🧪 café e\u0301 | **bold** [reference](https://example.test/path) | \"quoted\" C:\\repo\\file  \r\n")) +
        "\r\n```json\r\n{\"retain\": \"all source text\"}\r\n```\r\n\r\n" + marker + " final tail — keep its hard break  \r\n\t ";

    private static async Task<Fixture> SeedAsync(SqliteTestFactory factory, string? description)
    {
        await using var db = factory.CreateDbContext();
        var project = new Project { Name = "Content project", CreatedAt = T0, UpdatedAt = T0 };
        var feature = new Feature { Project = project, Name = "Content feature", CreatedAt = T0, UpdatedAt = T0 };
        var task = new TaskItem
        {
            Feature = feature,
            Name = "Existing task",
            Description = description,
            Status = WorkStatus.Review,
            Priority = TaskPriority.High,
            Assignee = "Joel",
            ClaimedBy = "agent-7",
            ClaimedAt = T0,
            SortOrder = 7.5,
            CreatedAt = T0,
            UpdatedAt = T0
        };
        task.Comments.Add(new TaskComment { Text = "Original discussion", Author = "Joel", Type = CommentType.Note, CreatedAt = T0 });
        db.Tasks.Add(task);
        await db.SaveChangesAsync();
        return new(project.Id, feature.Id, task.Id);
    }

    private sealed record Fixture(int ProjectId, int FeatureId, int TaskId);
}
