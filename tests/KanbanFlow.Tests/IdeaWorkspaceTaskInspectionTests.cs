using System.Reflection;
using System.Text.Json;
using KanbanFlow.Components.Pages;
using KanbanFlow.Components.Workspaces;
using KanbanFlow.Data;
using KanbanFlow.Models;
using KanbanFlow.Services.IdeaWorkspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.JSInterop;

namespace KanbanFlow.Tests;

public sealed class IdeaWorkspaceTaskInspectionTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectingAnotherTask_ClearsPreviousDetailsBeforeTheRead_AndNeverShowsThemAfterFailure(bool failRead)
    {
        using var database = new SqliteTestFactory();
        var project = new Project { Name = "Inspection project" };
        var feature = new Feature { Name = "Current feature", Project = project };
        var second = new TaskItem { Name = "Task B", Description = "  Full task B Ω\r\n" + new string('文', 10_000), Feature = feature };
        await using (var db = database.CreateDbContext()) { db.Tasks.Add(second); await db.SaveChangesAsync(); }
        var service = new IdeaWorkspaceService(database);
        var context = await service.GetAsync($"project:{project.Id}");
        var held = new HeldFactory(database, failRead);
        await using var page = new IdeaWorkspace();
        Set(page, "DbFactory", held); Set(page, "context", context); Set(page, "workspaceId", context.WorkspaceId); Set(page, "generation", 3);
        var old = new IdeaWorkspaceTaskReference(999, "Task A", "Old details must disappear", "Done", "Low", null, feature.Id, project.Id, default);
        Set(page, "inspectedTask", old); Set(page, "inspectedTaskId", old.Id);
        var running = (Task)Invoke(page, "InspectPublishedTask", new WorkspaceUiScope(context.WorkspaceId, 3), second.Id)!;
        try
        {
            await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(Field<IdeaWorkspaceTaskReference?>(page, "inspectedTask"));
            Assert.Equal(second.Id, Field<int?>(page, "inspectedTaskId"));
            Assert.True(Field<bool>(page, "taskBusy"));
            held.Release.TrySetResult();
            await running.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(Field<bool>(page, "taskBusy"));
            if (failRead)
            {
                Assert.Null(Field<IdeaWorkspaceTaskReference?>(page, "inspectedTask"));
                Assert.Contains("controlled read failed", Field<string>(page, "taskError"));
            }
            else
            {
                var shown = Assert.IsType<IdeaWorkspaceTaskReference>(Field<IdeaWorkspaceTaskReference?>(page, "inspectedTask"));
                Assert.Equal(second.Id, shown.Id); Assert.Equal(second.Name, shown.Name); Assert.Equal(second.Description, shown.Description);
                Assert.Null(Field<string?>(page, "taskError"));
            }
        }
        finally { held.Release.TrySetResult(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulLocalRelink_ClearsPreviousProjectTaskAndRejectsItsLateRead_WhileRetainingCompleteDrafts(bool holdOldRead)
    {
        using var database = new SqliteTestFactory();
        var projectA = new Project { Name = "Original inspection project" };
        var projectB = new Project { Name = "New inspection project" };
        var featureA = new Feature { Name = "Original feature", Project = projectA };
        var featureB = new Feature { Name = "New feature", Project = projectB };
        var taskA = new TaskItem { Name = "Original project task", Description = CompleteText("Project A task"), Feature = featureA };
        var taskB = new TaskItem { Name = "New project task", Description = CompleteText("Project B task"), Feature = featureB };
        await using (var db = database.CreateDbContext()) { db.Tasks.AddRange(taskA, taskB); await db.SaveChangesAsync(); }
        var service = new IdeaWorkspaceService(database);
        var created = await service.CreateAsync(new("capture-relink", "test", Title: "Local task inspector relink"));
        await service.ExecuteAsync(created.WorkspaceId, new("initial-link", created.Revision, "link_project",
            JsonSerializer.SerializeToElement(new { projectId = projectA.Id }, Json), "test"));
        var context = await service.GetAsync(created.WorkspaceId);
        Assert.Equal(projectA.Id, context.ProjectId);
        var memory = new WorkspaceBrowserDraft
        {
            ContentDrafts = new() { ["$new"] = new() { Dirty = true, BaseRevision = context.Revision, Title = "  Unsaved card Ω  ", Content = CompleteText("Unsaved new card") } },
            LayoutDrafts = new() { ["retained-layout"] = new() { PlacementId = "retained-layout", Dirty = true, X = 740, Y = 650, Width = 810, Height = 620 } },
            Discussion = new(CompleteText("Unsent discussion")), Connector = new("source", "target", CompleteText("Unsent connection")),
            Plan = new() { Title = CompleteText("Unpublished plan"), BaseRevision = context.Revision,
                Items = [new() { Id = "retained-item", Title = "Full work item", Description = CompleteText("Task scope"), AcceptanceCriteria = "  Full criterion Ω\r\n" }] },
            Checkpoint = new() { Objective = CompleteText("Unsaved checkpoint"), CompletedChanges = "  Full completed change Ω\r\n", OpenQuestions = CompleteText("Open questions"), NextActions = "  Keep building\r\n" },
            Delivery = new() { AssociationKnown = true, AssociationProjectId = projectA.Id, TargetProjectId = projectB.Id, FeatureId = featureA.Id,
                SelectedItemIds = ["original-project-plan-item"], FeatureName = CompleteText("Proposed feature for new project"), ProjectName = CompleteText("Proposed project name") }
        };
        var runtime = new DraftRuntime();
        var browser = new WorkspaceBrowserInterop(runtime);
        await browser.InitializeAsync(context.WorkspaceId);
        var held = new HeldFactory(database, false);
        await using var page = new IdeaWorkspace();
        Set(page, "DbFactory", holdOldRead ? held : database); Set(page, "Workspaces", service);
        Set(page, "context", context); Set(page, "workspaceId", context.WorkspaceId); Set(page, "generation", 3); Set(page, "restoredGeneration", 3);
        Set(page, "clientId", "browser"); Set(page, "sessionId", "tab"); Set(page, "browser", browser); Set(page, "draft", memory);
        var scope = new WorkspaceUiScope(context.WorkspaceId, 3);
        var oldRead = (Task)Invoke(page, "InspectPublishedTask", scope, taskA.Id)!;
        try
        {
            if (holdOldRead)
            {
                await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(Field<bool>(page, "taskBusy"));
            }
            else
            {
                await oldRead.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(taskA.Description, Field<IdeaWorkspaceTaskReference>(page, "inspectedTask").Description);
            }
            Assert.Equal(taskA.Id, Field<int?>(page, "inspectedTaskId"));
            var sequence = Field<long>(page, "taskSequence");

            await (Task)Invoke(page, "RunCommand", scope, WorkspaceUiCommand.Create("link_project", new { projectId = projectB.Id, expectedCurrentProjectId = projectA.Id }, context.Revision))!;

            Assert.Null(Field<string?>(page, "error"));
            Assert.Equal(projectB.Id, Field<IdeaWorkspaceContext>(page, "context").ProjectId);
            Assert.Null(Field<IdeaWorkspaceTaskReference?>(page, "inspectedTask"));
            Assert.Null(Field<int?>(page, "inspectedTaskId"));
            Assert.Null(Field<string?>(page, "taskError"));
            Assert.False(Field<bool>(page, "taskBusy"));
            Assert.True(Field<long>(page, "taskSequence") > sequence);
            var expected = memory with { Delivery = memory.Delivery with { AssociationProjectId = projectB.Id, SelectedItemIds = null, FeatureId = null } };
            Assert.Equal(JsonSerializer.Serialize(expected, Json), JsonSerializer.Serialize(Field<WorkspaceBrowserDraft>(page, "draft"), Json));
            Assert.Equal(JsonSerializer.Serialize(expected, Json), runtime.Writes.Last());
            var sent = JsonSerializer.Deserialize<WorkspaceBrowserDraft>(runtime.Writes.First(), Json)!.Pending!;
            Assert.Equal("link_project", sent.Command.Action);
            Assert.Equal(context.Revision, sent.Command.ExpectedRevision);
            Assert.Equal(projectB.Id, sent.Command.Payload.GetProperty("projectId").GetInt32());
            Assert.Equal(projectA.Id, sent.Command.Payload.GetProperty("expectedCurrentProjectId").GetInt32());
            Assert.Equal(JsonSerializer.Serialize(memory.Delivery, Json), sent.DraftAtSend!.Value.GetRawText());
            Assert.Null(JsonSerializer.Deserialize<WorkspaceBrowserDraft>(runtime.Writes.Last(), Json)!.Pending);
            Assert.Equal(projectB.Id, (await service.GetAsync(context.WorkspaceId)).ProjectId);
            held.Release.TrySetResult();
            await oldRead.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Null(Field<IdeaWorkspaceTaskReference?>(page, "inspectedTask"));
            Assert.Null(Field<int?>(page, "inspectedTaskId"));
            Assert.Null(Field<string?>(page, "taskError"));
            Assert.False(Field<bool>(page, "taskBusy"));
            Assert.Equal(JsonSerializer.Serialize(expected, Json), JsonSerializer.Serialize(Field<WorkspaceBrowserDraft>(page, "draft"), Json));
            Set(page, "DbFactory", database);
            await (Task)Invoke(page, "InspectPublishedTask", scope, taskB.Id)!;
            var currentTask = Field<IdeaWorkspaceTaskReference>(page, "inspectedTask");
            Assert.Equal(taskB.Id, currentTask.Id); Assert.Equal(projectB.Id, currentTask.ProjectId); Assert.Equal(taskB.Description, currentTask.Description);
        }
        finally { held.Release.TrySetResult(); await oldRead.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    private static string CompleteText(string label) => "  # " + label + " Ω\r\n\r\n    preserve indentation  \r\n" + new string('文', 12_000) + "\r\ncomplete final requirement  ";

    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Flags)!.GetValue(target)!;
    private static object? Invoke(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Flags)!.Invoke(target, args);
    private static void Set(object target, string name, object value)
    { var property = target.GetType().GetProperty(name, Flags); if (property is not null) property.SetValue(target, value); else target.GetType().GetField(name, Flags)!.SetValue(target, value); }
    private sealed class HeldFactory(SqliteTestFactory inner, bool failRead) : IDbContextFactory<AppDbContext>
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AppDbContext CreateDbContext() => inner.CreateDbContext();
        public async Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken);
            if (failRead) throw new IOException("controlled read failed");
            return inner.CreateDbContext();
        }
    }

    private sealed class DraftRuntime : IJSRuntime, IJSObjectReference
    {
        public List<string> Writes { get; } = [];
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, default, args);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "import") return ValueTask.FromResult((T)(object)this);
            if (identifier == "initialize") return ValueTask.FromResult((T)(object)JsonSerializer.SerializeToElement(new WorkspaceBrowserSession("browser", "tab", null), Json));
            if (identifier == "saveDraft") Writes.Add(JsonSerializer.Serialize(args![1], Json));
            else if (identifier != "disposeWriter") throw new Xunit.Sdk.XunitException("Unexpected browser method: " + identifier);
            return ValueTask.FromResult(default(T)!);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
