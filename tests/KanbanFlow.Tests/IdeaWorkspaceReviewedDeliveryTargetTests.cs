using System.Reflection;
using System.Text.Json;
using KanbanFlow.Components.Pages;
using KanbanFlow.Components.Workspaces;
using KanbanFlow.Data;
using KanbanFlow.Models;
using KanbanFlow.Services;
using KanbanFlow.Services.IdeaWorkspaces;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

namespace KanbanFlow.Tests;

public sealed class IdeaWorkspaceReviewedDeliveryTargetTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultPreview_PublishesOnlyToItsReviewedFeature_OrRejectsItsRemoval(bool removeReviewedFeature)
    {
        using var factory = new SqliteTestFactory();
        var service = new IdeaWorkspaceService(factory);
        var project = new Project { Name = "Reviewed delivery destination" };
        var first = new Feature { Project = project, Name = "Reviewed feature A" };
        var second = new Feature { Project = project, Name = "Historical feature B" };
        await using (var db = factory.CreateDbContext()) { db.Features.AddRange(first, second); await db.SaveChangesAsync(); }
        var key = $"project:{project.Id}";
        await AcceptPlan(service, key, "Review destination", "First published task", "Second published task", "Unpublished task");
        var context = await service.GetAsync(key);
        var firstItem = context.AcceptedPlan!.Items.Single(item => item.Title == "First published task");
        var secondItem = context.AcceptedPlan.Items.Single(item => item.Title == "Second published task");
        await Change(service, key, "publish_plan", new { expectedProjectPlanRevision = context.ProjectPlanRevision, itemIds = new[] { firstItem.Id }, featureId = first.Id });
        context = await service.GetAsync(key);
        await Change(service, key, "publish_plan", new { expectedProjectPlanRevision = context.ProjectPlanRevision, itemIds = new[] { secondItem.Id }, featureId = second.Id });
        context = await service.GetAsync(key);
        var unpublished = context.AcceptedPlan!.Items.Single(item => item.Title == "Unpublished task");
        var (page, calls, _) = await Page(service, context, new WorkspaceDeliveryDraft { AssociationKnown = true,
            AssociationProjectId = project.Id, SelectedItemIds = [unpublished.Id], ProjectName = "Keep authored project name" });
        await using var ownedPage = page;
        var scope = new WorkspaceUiScope(key, 7);
        var request = new IdeaWorkspacePublicationPreviewRequest(context.Revision, context.ProjectPlanRevision!.Value, [unpublished.Id]);
        await Call(page, "PreviewDelivery", scope, request);
        var reviewedPreview = Field<IdeaWorkspacePublicationPreview>(page, "publicationPreview");
        Assert.Equal(first.Id, reviewedPreview.FeatureId);
        Assert.Null(request.FeatureId);
        Assert.Null(request.FeatureName);
        var firstTask = context.Document.Publications.Single(item => item.FeatureId == first.Id).TaskId;
        await using (var db = factory.CreateDbContext())
        {
            (await db.Tasks.SingleAsync(task => task.Id == firstTask)).FeatureId = second.Id;
            await db.SaveChangesAsync();
            if (removeReviewedFeature)
            {
                db.Features.Remove(await db.Features.SingleAsync(feature => feature.Id == first.Id));
                await db.SaveChangesAsync();
            }
            Assert.Equal(2, await db.Tasks.CountAsync());
            Assert.All(await db.Tasks.ToArrayAsync(), task => Assert.Equal(second.Id, task.FeatureId));
        }
        var unchangedRevision = await service.GetAsync(key);
        Assert.Equal(context.Revision, unchangedRevision.Revision);
        Assert.Equal(context.ProjectPlanRevision, unchangedRevision.ProjectPlanRevision);
        int auditsBefore;
        int receiptsBefore;
        await using (var db = factory.CreateDbContext())
        {
            auditsBefore = await db.ActivityEvents.CountAsync();
            receiptsBefore = await db.Set<KanbanFlow.Models.IdeaWorkspaces.IdeaWorkspaceReceipt>().CountAsync();
        }

        await Call(page, "PublishReviewed", scope, reviewedPreview);

        await using var verify = factory.CreateDbContext();
        if (removeReviewedFeature)
        {
            Assert.Equal(2, await verify.Tasks.CountAsync());
            Assert.False(await verify.Tasks.AnyAsync(task => task.Name == "Unpublished task"));
            Assert.Contains("publication_target_missing", Field<string>(page, "error"));
            Assert.Equal(auditsBefore, await verify.ActivityEvents.CountAsync());
            Assert.Equal(receiptsBefore, await verify.Set<KanbanFlow.Models.IdeaWorkspaces.IdeaWorkspaceReceipt>().CountAsync());
            Assert.Equal(context.Revision, (await service.GetAsync(key)).Revision);
        }
        else
        {
            Assert.Equal(first.Id, (await verify.Tasks.SingleAsync(task => task.Name == "Unpublished task")).FeatureId);
            Assert.Null(Field<string?>(page, "error"));
        }
        var sent = Assert.Single(calls.Commands);
        Assert.Equal(JsonValueKind.Number, sent.Payload.GetProperty("featureId").ValueKind);
        Assert.Equal(first.Id, sent.Payload.GetProperty("featureId").GetInt32());
        Assert.Equal(JsonValueKind.Null, sent.Payload.GetProperty("featureName").ValueKind);
        Assert.Equal("Keep authored project name", Field<WorkspaceBrowserDraft>(page, "draft").Delivery.ProjectName);
        Assert.Equal(second.Id, (await verify.Tasks.SingleAsync(task => task.Id == firstTask)).FeatureId);
        Assert.Equal(first.Id, (await service.GetAsync(key)).Document.Publications.Single(item => item.TaskId == firstTask).FeatureId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  Exact authored feature Ω  ")]
    public async Task NewFeaturePreview_FreezesResolvedDefault_AndPreservesAuthoredNameAndExactPendingReplay(string? authoredName)
    {
        using var factory = new SqliteTestFactory();
        var service = new IdeaWorkspaceService(factory);
        var project = new Project { Name = "New reviewed destination" };
        await using (var db = factory.CreateDbContext()) { db.Projects.Add(project); await db.SaveChangesAsync(); }
        var key = $"project:{project.Id}";
        await AcceptPlan(service, key, "  Reviewed plan feature Ω  ", "Only new task");
        var context = await service.GetAsync(key);
        var selected = context.AcceptedPlan!.Items.Select(item => item.Id).ToList();
        var draft = new WorkspaceDeliveryDraft { AssociationKnown = true, AssociationProjectId = project.Id,
            SelectedItemIds = [.. selected], FeatureName = authoredName ?? "", ProjectName = "  Authored project Ω  " };
        var (page, calls, runtime) = await Page(service, context, draft);
        await using var ownedPage = page;
        var scope = new WorkspaceUiScope(key, 7);
        var request = new IdeaWorkspacePublicationPreviewRequest(context.Revision, context.ProjectPlanRevision!.Value, selected, FeatureName: authoredName);
        await Call(page, "PreviewDelivery", scope, request);
        var preview = Field<IdeaWorkspacePublicationPreview>(page, "publicationPreview");
        Assert.Null(preview.FeatureId);
        Assert.Equal(authoredName, request.FeatureName);
        selected.Clear(); // The reviewed selection must not follow a caller's later mutable list.
        calls.LoseNextResponse = true;

        await Call(page, "PublishReviewed", scope, preview);

        var pending = Field<WorkspaceBrowserDraft>(page, "draft").Pending!;
        Assert.NotNull(pending);
        Assert.Equal(authoredName ?? preview.FeatureName, pending.Command.Payload.GetProperty("featureName").GetString());
        Assert.Equal(preview.Items.Select(item => item.CanonicalItemId), pending.Command.Payload.GetProperty("itemIds").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(authoredName ?? "", pending.DraftAtSend!.Value.GetProperty("featureName").GetString());
        Assert.Equal(draft.ProjectName, pending.DraftAtSend.Value.GetProperty("projectName").GetString());
        Assert.Equal(pending.Command.OperationId, JsonSerializer.Deserialize<WorkspaceBrowserDraft>(runtime.Raw!, Json)!.Pending!.Command.OperationId);
        await Call(page, "RetryPending", scope);
        Assert.Equal(2, calls.Commands.Count);
        Assert.Equal(calls.Commands[0].OperationId, calls.Commands[1].OperationId);
        Assert.Equal(calls.Commands[0].Payload.GetRawText(), calls.Commands[1].Payload.GetRawText());
        Assert.True(calls.LastResult!.Replayed);
        Assert.Null(Field<WorkspaceBrowserDraft>(page, "draft").Pending);
        Assert.Equal(draft.ProjectName, Field<WorkspaceBrowserDraft>(page, "draft").Delivery.ProjectName);
        await using var verify = factory.CreateDbContext();
        Assert.Equal(preview.FeatureName, (await verify.Features.SingleAsync()).Name);
        Assert.Equal(1, await verify.Tasks.CountAsync());
        Assert.Equal(1, await verify.ActivityEvents.CountAsync(item => item.EventType == "WorkspacePlanTaskPublished"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObsoletePreview_CannotBindAPublicationTargetAfterDraftOrSourceChanges(bool sourceChanged)
    {
        using var factory = new SqliteTestFactory();
        var service = new IdeaWorkspaceService(factory);
        var project = new Project { Name = "Guarded preview" };
        await using (var db = factory.CreateDbContext()) { db.Projects.Add(project); await db.SaveChangesAsync(); }
        var key = $"project:{project.Id}";
        await AcceptPlan(service, key, "Preview guard", "First task");
        var context = await service.GetAsync(key);
        var initial = new WorkspaceDeliveryDraft { AssociationKnown = true, AssociationProjectId = project.Id, FeatureName = "Original draft" };
        var (page, calls, _) = await Page(service, context, initial);
        await using var ownedPage = page;
        calls.HoldPreview = true;
        var scope = new WorkspaceUiScope(key, 7);
        var operation = Call(page, "PreviewDelivery", scope, new IdeaWorkspacePublicationPreviewRequest(context.Revision, context.ProjectPlanRevision!.Value));
        try
        {
            await calls.PreviewEntered.Task.WaitAsync(Deadline);
            if (sourceChanged)
            {
                Set(page, "generation", 8);
                Invoke(page, "ClearDeliveryView");
            }
            else await Call(page, "DeliveryChanged", scope, initial with { FeatureName = "New authored draft" });
            calls.PreviewRelease.TrySetResult();
            await operation.WaitAsync(Deadline);
            Assert.Null(Field<IdeaWorkspacePublicationPreview?>(page, "publicationPreview"));
            Assert.Null(Field<IdeaWorkspacePublicationPreviewRequest?>(page, "publicationRequest"));
            Assert.False(Field<bool>(page, "previewBusy"));
            Assert.Equal(sourceChanged ? initial.FeatureName : "New authored draft", Field<WorkspaceBrowserDraft>(page, "draft").Delivery.FeatureName);
            Assert.Empty(calls.Commands);
        }
        finally { calls.PreviewRelease.TrySetResult(); await operation.WaitAsync(Deadline); }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RenderedPublication_RejectsAReplacedPreview_ThenPublishesTheFreshReviewedChoice(bool changeSelection, bool changeDestination)
    {
        using var factory = new SqliteTestFactory();
        var service = new IdeaWorkspaceService(factory);
        var complete = "\r\n  Complete source and draft Ω  " + new string('Ω', 24_000) + "\r\n  Retain this ending.  \r\n";
        var project = new Project { Name = "Rendered publication review", Description = complete, Goals = complete, Readme = complete };
        var firstFeature = new Feature { Project = project, Name = "Reviewed feature A" };
        var secondFeature = new Feature { Project = project, Name = "Unreviewed feature B" };
        await using (var db = factory.CreateDbContext()) { db.Features.AddRange(firstFeature, secondFeature); await db.SaveChangesAsync(); }
        var key = $"project:{project.Id}";
        await AcceptPlan(service, key, "Rendered plan", "First reviewed task", "Second unreviewed task");
        var context = await service.GetAsync(key);
        var firstItem = context.AcceptedPlan!.Items.Single(item => item.Title == "First reviewed task");
        var secondItem = context.AcceptedPlan.Items.Single(item => item.Title == "Second unreviewed task");
        var firstDraft = new WorkspaceDeliveryDraft { AssociationKnown = true, AssociationProjectId = project.Id,
            SelectedItemIds = [firstItem.Id], FeatureId = firstFeature.Id, ProjectName = complete };
        var (page, calls, runtime) = await Page(service, context, firstDraft);
        Set(page, "Kind", "project"); Set(page, "SourceId", project.Id); Set(page, "panel", "delivery"); Set(page, "view", "outline");
        Set(page, "DbFactory", factory);
        await using var services = new ServiceCollection().AddSingleton<IDbContextFactory<AppDbContext>>(factory)
            .AddSingleton<IJSRuntime>(runtime).AddSingleton<IMarkdownRenderer, MarkdownRenderer>().BuildServiceProvider();
        await using var renderer = new DeliveryCaptureRenderer(services, page);
        var scope = new WorkspaceUiScope(key, 7);
        await Call(page, "PreviewDelivery", scope, new IdeaWorkspacePublicationPreviewRequest(context.Revision,
            context.ProjectPlanRevision!.Value, [firstItem.Id], firstFeature.Id));
        var firstPreview = Field<IdeaWorkspacePublicationPreview>(page, "publicationPreview");
        await renderer.Render();
        var oldButton = await renderer.Button("delivery-publish");

        var selectedId = changeSelection ? secondItem.Id : firstItem.Id;
        var featureId = changeDestination ? secondFeature.Id : firstFeature.Id;
        await Call(page, "DeliveryChanged", scope, firstDraft with { SelectedItemIds = [selectedId], FeatureId = featureId });
        await Call(page, "PreviewDelivery", scope, new IdeaWorkspacePublicationPreviewRequest(context.Revision,
            context.ProjectPlanRevision.Value, [selectedId], featureId));
        var secondPreview = Field<IdeaWorkspacePublicationPreview>(page, "publicationPreview");
        Assert.NotSame(firstPreview, secondPreview);
        Assert.Equal(firstPreview.Revision, secondPreview.Revision);
        Assert.Equal(firstPreview.ProjectPlanRevision, secondPreview.ProjectPlanRevision);
        await renderer.Render();
        var freshButton = await renderer.Button("delivery-publish");
        var before = await PublicationDatabaseSnapshot(factory);

        await renderer.Click(oldButton);

        var after = await PublicationDatabaseSnapshot(factory);
        await using (var verify = factory.CreateDbContext())
            Assert.True(before == after, $"The old rendered preview must write no table, audit or receipt; tasks after the stale click: {await verify.Tasks.CountAsync()}.");
        Assert.Empty(calls.Commands);
        Assert.Same(secondPreview, Field<IdeaWorkspacePublicationPreview>(page, "publicationPreview"));
        Assert.Contains("preview", Field<string>(page, "deliveryError"), StringComparison.OrdinalIgnoreCase);
        var retained = Field<WorkspaceBrowserDraft>(page, "draft").Delivery;
        Assert.Equal(new[] { selectedId }, retained.SelectedItemIds);
        Assert.Equal(featureId, retained.FeatureId);
        Assert.Equal(complete, retained.ProjectName);

        await renderer.Click(freshButton);

        var sent = Assert.Single(calls.Commands);
        Assert.Equal(selectedId, Assert.Single(sent.Payload.GetProperty("itemIds").EnumerateArray()).GetString());
        Assert.Equal(featureId, sent.Payload.GetProperty("featureId").GetInt32());
        await using (var verify = factory.CreateDbContext())
        {
            var task = Assert.Single(await verify.Tasks.ToArrayAsync());
            Assert.Equal(changeSelection ? secondItem.Title : firstItem.Title, task.Name);
            Assert.Equal(featureId, task.FeatureId);
            Assert.Equal(changeSelection ? secondItem.Description : firstItem.Description, task.Description);
            var preserved = await verify.Projects.SingleAsync(item => item.Id == project.Id);
            Assert.Equal(complete, preserved.Description); Assert.Equal(complete, preserved.Goals); Assert.Equal(complete, preserved.Readme);
        }
        Assert.Null(Field<string?>(page, "error"));
    }

    private static async Task<string> PublicationDatabaseSnapshot(SqliteTestFactory factory)
    {
        await using var db = factory.CreateDbContext();
        var connection = db.Database.GetDbConnection();
        using var names = connection.CreateCommand();
        names.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        var tables = new List<string>();
        await using (var reader = await names.ExecuteReaderAsync()) while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        var snapshot = new SortedDictionary<string, string[]>();
        foreach (var table in tables)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM \"" + table.Replace("\"", "\"\"") + "\"";
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<string>();
            while (await reader.ReadAsync())
            {
                var values = new object?[reader.FieldCount];
                for (var index = 0; index < values.Length; index++) values[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);
                rows.Add(JsonSerializer.Serialize(values, Json));
            }
            snapshot[table] = rows.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        }
        return JsonSerializer.Serialize(snapshot, Json);
    }

#pragma warning disable BL0006 // Test renderer captures the actual parent and child Razor callbacks.
    private sealed class DeliveryCaptureRenderer(IServiceProvider services, IdeaWorkspace page) : Renderer(services, NullLoggerFactory.Instance)
    {
        private int? componentId;
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        protected override void HandleException(Exception exception) => throw new InvalidOperationException("Delivery render failed.", exception);
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
        public Task Render() => Dispatcher.InvokeAsync(async () =>
        {
            componentId ??= AssignRootComponentId(page);
            await RenderRootComponentAsync(componentId.Value, ParameterView.Empty);
        });
        public Task<object> Button(string testId) => Dispatcher.InvokeAsync(() => FindButton(componentId!.Value, testId)
            ?? throw new InvalidOperationException("Rendered delivery button missing: " + testId));
        private object? FindButton(int id, string testId)
        {
            var frames = GetCurrentRenderTreeFrames(id);
            for (var index = 0; index < frames.Count; index++)
            {
                var frame = frames.Array[index];
                if (frame.FrameType == RenderTreeFrameType.Component && FindButton(frame.ComponentId, testId) is { } nested) return nested;
                if (frame.FrameType != RenderTreeFrameType.Element || frame.ElementName != "button") continue;
                object? click = null;
                var matches = false;
                var disabled = false;
                for (var attributeIndex = index + 1; attributeIndex < frames.Count && frames.Array[attributeIndex].FrameType == RenderTreeFrameType.Attribute; attributeIndex++)
                {
                    var attribute = frames.Array[attributeIndex];
                    if (attribute.AttributeName == "data-testid") matches = Equals(attribute.AttributeValue, testId);
                    if (attribute.AttributeName == "onclick") click = attribute.AttributeValue;
                    if (attribute.AttributeName == "disabled") disabled = Equals(attribute.AttributeValue, true);
                }
                if (matches) return disabled ? throw new InvalidOperationException("Rendered delivery button is disabled.") : click;
            }
            return null;
        }
        public Task Click(object callback) => Dispatcher.InvokeAsync(() => callback switch
        {
            EventCallback<MouseEventArgs> click => click.InvokeAsync(new()),
            EventCallback click => click.InvokeAsync(new MouseEventArgs()),
            Func<Task> click => click(),
            _ => throw new InvalidOperationException("Unexpected rendered delivery callback: " + callback.GetType())
        });
    }
#pragma warning restore BL0006

    private static async Task AcceptPlan(IdeaWorkspaceService service, string key, string title, params string[] titles)
    {
        await Change(service, key, "propose_plan", new { title, expectedProjectPlanRevision = (await service.GetAsync(key)).ProjectPlanRevision,
            items = titles.Select((name, index) => new { id = "step-" + index, title = name,
                description = "\r\n  Complete authored scope Ω  \r\n", acceptanceCriteria = new[] { "  Verified completely Ω  " }, dependsOn = Array.Empty<string>() }).ToArray() });
        var proposed = await service.GetAsync(key);
        await Change(service, key, "accept_plan", new { planRevisionId = proposed.Document.PlanRevisions.Last().Id, expectedProjectPlanRevision = proposed.ProjectPlanRevision });
    }

    private static async Task Change(IdeaWorkspaceService service, string key, string action, object payload)
        => await service.ExecuteAsync(key, new(Guid.NewGuid().ToString("N"), (await service.GetAsync(key)).Revision,
            action, JsonSerializer.SerializeToElement(payload, Json), "delivery-target-test"));

    private static async Task<(IdeaWorkspace Page, CapturedService Calls, DraftRuntime Runtime)> Page(
        IdeaWorkspaceService service, IdeaWorkspaceContext context, WorkspaceDeliveryDraft draft)
    {
        var proxy = DispatchProxy.Create<IIdeaWorkspaceService, CapturedService>();
        var calls = (CapturedService)(object)proxy;
        calls.Inner = service;
        var runtime = new DraftRuntime();
        var browser = new WorkspaceBrowserInterop(runtime);
        await browser.InitializeAsync(context.WorkspaceId);
        var page = new IdeaWorkspace();
        Set(page, "Workspaces", proxy); Set(page, "context", context); Set(page, "workspaceId", context.WorkspaceId);
        Set(page, "generation", 7); Set(page, "restoredGeneration", 7); Set(page, "clientId", "delivery-test");
        Set(page, "sessionId", "tab"); Set(page, "browser", browser); Set(page, "draft", new WorkspaceBrowserDraft { Delivery = draft });
        return (page, calls, runtime);
    }

    public class CapturedService : DispatchProxy
    {
        public IIdeaWorkspaceService Inner { get; set; } = default!;
        public List<IdeaWorkspaceCommand> Commands { get; } = [];
        public IdeaWorkspaceMutationResult? LastResult { get; private set; }
        public bool LoseNextResponse { get; set; }
        public bool HoldPreview { get; set; }
        public TaskCompletionSource PreviewEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PreviewRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IIdeaWorkspaceService.ExecuteAsync))
            {
                Commands.Add((IdeaWorkspaceCommand)args![1]!);
                return Capture((Task<IdeaWorkspaceMutationResult>)method.Invoke(Inner, args)!);
            }
            if (method.Name == nameof(IIdeaWorkspaceService.PreviewPublicationAsync))
                return Preview((Task<IdeaWorkspacePublicationPreview>)method.Invoke(Inner, args)!);
            return method.Invoke(Inner, args);
        }
        private async Task<IdeaWorkspaceMutationResult> Capture(Task<IdeaWorkspaceMutationResult> operation)
        {
            var result = await operation;
            LastResult = result;
            if (LoseNextResponse) { LoseNextResponse = false; throw new IOException("Response lost after the real commit."); }
            return result;
        }
        private async Task<IdeaWorkspacePublicationPreview> Preview(Task<IdeaWorkspacePublicationPreview> operation)
        {
            var result = await operation;
            if (HoldPreview) { PreviewEntered.TrySetResult(); await PreviewRelease.Task; }
            return result;
        }
    }

    private sealed class DraftRuntime : IJSRuntime, IJSObjectReference
    {
        public string? Raw { get; private set; }
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, default, args);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "import") return ValueTask.FromResult((T)(object)this);
            if (identifier == "initialize") return ValueTask.FromResult((T)(object)JsonSerializer.SerializeToElement(new WorkspaceBrowserSession("delivery-test", "tab", null), Json));
            if (identifier == "saveDraft") Raw = JsonSerializer.Serialize(args![1], Json);
            else if (identifier != "disposeWriter") throw new Xunit.Sdk.XunitException("Unexpected browser method: " + identifier);
            return ValueTask.FromResult(default(T)!);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static Task Call(object target, string name, params object?[] args) => (Task)Invoke(target, name, args)!;
    private static object? Invoke(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Flags)!.Invoke(target, args);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Flags)!.GetValue(target)!;
    private static void Set(object target, string name, object value)
    {
        var property = target.GetType().GetProperty(name, Flags);
        if (property is not null) property.SetValue(target, value); else target.GetType().GetField(name, Flags)!.SetValue(target, value);
    }
}
