using System.Reflection;
using System.Text.Json;
using KanbanFlow.Components.Pages;
using KanbanFlow.Components.Workspaces;
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

public sealed class WorkspacePublicationPreviewRenderedTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData("target")]
    [InlineData("content")]
    public async Task OldRenderedPublish_RejectsReplacementPreviewAtTheSameRevisions_WithoutAnyWrites(string replacement)
    {
        await using var scenario = await Scenario.Create();
        var first = await scenario.Preview(replacement: null);
        await scenario.Render();
        var oldButton = await scenario.Renderer.PublishButton();
        var second = await scenario.Preview(replacement);
        Assert.Equal(first.Revision, second.Revision);
        Assert.Equal(first.ProjectPlanRevision, second.ProjectPlanRevision);
        if (replacement == "target") Assert.NotEqual(first.FeatureId, second.FeatureId);
        else Assert.NotEqual(first.Items.Single().Description, second.Items.Single().Description);
        await scenario.Render();
        var before = await scenario.Snapshot();

        await scenario.Renderer.Click(oldButton.Callback);

        Assert.True(before == await scenario.Snapshot(), "The old rendered button published its replacement preview into SQLite.");
        Assert.Empty(scenario.Calls.Commands);
        Assert.Contains("preview", scenario.DeliveryError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("review", scenario.DeliveryError, StringComparison.OrdinalIgnoreCase);
        Assert.Same(second, scenario.CurrentPreview);
    }

    [Fact]
    public async Task PreviewInFlight_DisablesTheButton_AndRejectsAnAlreadyRenderedPublishWithoutAnyWrites()
    {
        await using var scenario = await Scenario.Create();
        await scenario.Preview(replacement: null);
        await scenario.Render();
        var oldButton = await scenario.Renderer.PublishButton();
        scenario.Calls.HoldPreview = true;
        var operation = scenario.RepeatPreview();
        try
        {
            await scenario.Calls.PreviewEntered.Task.WaitAsync(Deadline);
            Assert.True(scenario.PreviewBusy);
            await scenario.Render();
            var busyButton = await scenario.Renderer.PublishButton();
            var before = await scenario.Snapshot();

            await scenario.Renderer.Click(oldButton.Callback);

            Assert.True(before == await scenario.Snapshot(), "An old button published while a replacement preview was loading.");
            Assert.Empty(scenario.Calls.Commands);
            Assert.True(busyButton.Disabled, "The publish button remained enabled while a preview was loading.");
            Assert.Contains("preview", scenario.DeliveryError, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            scenario.Calls.PreviewRelease.TrySetResult();
            await operation.WaitAsync(Deadline);
        }
    }

    [Theory]
    [InlineData("target")]
    [InlineData("content")]
    public async Task FreshRenderedPublish_PublishesOnlyTheDisplayedItemAndFeature_WithCompleteAuthoredContent(string replacement)
    {
        await using var scenario = await Scenario.Create();
        await scenario.Preview(replacement: null);
        await scenario.Render();
        var preview = await scenario.Preview(replacement);
        await scenario.Render();
        var currentButton = await scenario.Renderer.PublishButton();
        Assert.False(currentButton.Disabled);

        await scenario.Renderer.Click(currentButton.Callback);

        Assert.Null(scenario.DeliveryError);
        var command = Assert.Single(scenario.Calls.Commands);
        Assert.Equal("publish_plan", command.Action);
        Assert.Equal(preview.FeatureId, command.Payload.GetProperty("featureId").GetInt32());
        Assert.Equal(preview.Items.Select(item => item.CanonicalItemId),
            command.Payload.GetProperty("itemIds").EnumerateArray().Select(item => item.GetString()));
        await using var db = scenario.Factory.CreateDbContext();
        var task = Assert.Single(await db.Tasks.Include(item => item.AcceptanceCriteria).ToArrayAsync());
        var expected = Assert.Single(preview.Items);
        Assert.Equal(preview.FeatureId, task.FeatureId);
        Assert.Equal(expected.Title, task.Name);
        Assert.Equal(expected.Description, task.Description);
        Assert.Equal(expected.AcceptanceCriteria, task.AcceptanceCriteria.OrderBy(item => item.SortOrder).Select(item => item.Text));
        var saved = await scenario.Service.GetAsync(scenario.Context.WorkspaceId);
        Assert.Equal(expected.CanonicalItemId, Assert.Single(saved.Document.Publications).CanonicalItemId);
        Assert.Equal(task.Id, saved.AcceptedPlan!.Items.Single(item => item.Id == expected.CanonicalItemId).PublishedTaskId);
        Assert.All(saved.AcceptedPlan.Items.Where(item => item.Id != expected.CanonicalItemId), item => Assert.Null(item.PublishedTaskId));
    }

    private static Task Call(object target, string name, params object?[] args)
        => (Task)target.GetType().GetMethod(name, Flags)!.Invoke(target, args)!;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Flags)!.GetValue(target)!;
    private static void Set(object target, string name, object value)
    {
        var property = target.GetType().GetProperty(name, Flags);
        if (property is not null) property.SetValue(target, value); else target.GetType().GetField(name, Flags)!.SetValue(target, value);
    }

    // The renderer captures the real Razor callback, then invokes it after newer parameters arrive.
#pragma warning disable BL0006
    private sealed class Scenario : IAsyncDisposable
    {
        public SqliteTestFactory Factory { get; } = new();
        public IdeaWorkspaceService Service { get; }
        public IdeaWorkspace Page { get; } = new();
        public IdeaWorkspaceReviewedDeliveryTargetTests.CapturedService Calls { get; }
        public IdeaWorkspaceContext Context { get; private set; } = default!;
        public CaptureRenderer Renderer { get; }
        public IdeaWorkspacePublicationPreview CurrentPreview => Field<IdeaWorkspacePublicationPreview>(Page, "publicationPreview");
        public bool PreviewBusy => Field<bool>(Page, "previewBusy");
        public string? DeliveryError => Field<string?>(Page, "deliveryError");
        private readonly ServiceProvider services;
        private readonly Feature firstFeature = new() { Name = "Reviewed feature A Ω" };
        private readonly Feature secondFeature = new() { Name = "Replacement feature B 文" };
        private WorkspaceUiScope Scope => new(Context.WorkspaceId, 7);

        private Scenario()
        {
            Service = new(Factory);
            var proxy = DispatchProxy.Create<IIdeaWorkspaceService, IdeaWorkspaceReviewedDeliveryTargetTests.CapturedService>();
            Calls = (IdeaWorkspaceReviewedDeliveryTargetTests.CapturedService)(object)proxy;
            Calls.Inner = Service;
            Set(Page, "Workspaces", proxy);
            services = new ServiceCollection().AddSingleton<IMarkdownRenderer, MarkdownRenderer>().BuildServiceProvider();
            Renderer = new(services, Factory);
        }

        public static async Task<Scenario> Create()
        {
            var scenario = new Scenario();
            try
            {
                var project = new Project { Name = "Rendered publication review" };
                scenario.firstFeature.Project = scenario.secondFeature.Project = project;
                await using (var db = scenario.Factory.CreateDbContext())
                {
                    db.Features.AddRange(scenario.firstFeature, scenario.secondFeature);
                    await db.SaveChangesAsync();
                }
                var key = $"project:{project.Id}";
                await scenario.Change(key, "propose_plan", new
                {
                    title = "Review exact publication",
                    expectedProjectPlanRevision = 0,
                    items = new[] { "A", "B" }.Select(label => new
                    {
                        id = "item-" + label,
                        title = "Task " + label,
                        description = $"\r\n  ## Complete {label} Ω 文\r\n" + new string('文', 2048) + $"\r\nFINAL {label}  \r\n",
                        acceptanceCriteria = new[] { $"  Complete criterion {label} Ω  " },
                        dependsOn = Array.Empty<string>()
                    }).ToArray()
                });
                scenario.Context = await scenario.Service.GetAsync(key);
                await scenario.Change(key, "accept_plan", new
                {
                    planRevisionId = scenario.Context.Document.PlanRevisions.Last().Id,
                    expectedProjectPlanRevision = scenario.Context.ProjectPlanRevision
                });
                scenario.Context = await scenario.Service.GetAsync(key);
                Set(scenario.Page, "context", scenario.Context); Set(scenario.Page, "workspaceId", key);
                Set(scenario.Page, "generation", 7); Set(scenario.Page, "restoredGeneration", 7);
                Set(scenario.Page, "clientId", "rendered-publication-test"); Set(scenario.Page, "sessionId", "tab");
                var browser = new WorkspaceBrowserInterop(new DraftRuntime());
                await browser.InitializeAsync(key);
                Set(scenario.Page, "browser", browser);
                return scenario;
            }
            catch { await scenario.DisposeAsync(); throw; }
        }

        private async Task Change(string key, string action, object payload)
            => await Service.ExecuteAsync(key, new(Guid.NewGuid().ToString("N"), (await Service.GetAsync(key)).Revision,
                action, JsonSerializer.SerializeToElement(payload, Json), "rendered-publication-test"));

        public async Task<IdeaWorkspacePublicationPreview> Preview(string? replacement)
        {
            var item = Context.AcceptedPlan!.Items.Single(item => item.Title == (replacement == "content" ? "Task B" : "Task A"));
            var feature = replacement == "target" ? secondFeature : firstFeature;
            var draft = new WorkspaceDeliveryDraft
            {
                AssociationKnown = true,
                AssociationProjectId = Context.ProjectId,
                FeatureId = feature.Id,
                SelectedItemIds = [item.Id]
            };
            await Call(Page, "DeliveryChanged", Scope, draft);
            await Call(Page, "PreviewDelivery", Scope, new IdeaWorkspacePublicationPreviewRequest(Context.Revision,
                Context.ProjectPlanRevision!.Value, [item.Id], feature.Id));
            return CurrentPreview;
        }

        public Task RepeatPreview() => Call(Page, "PreviewDelivery", Scope,
            new IdeaWorkspacePublicationPreviewRequest(Context.Revision, Context.ProjectPlanRevision!.Value,
                CurrentPreview.Items.Select(item => item.CanonicalItemId).ToArray(), CurrentPreview.FeatureId));

        public Task Render()
        {
            var callback = EventCallback.Factory.Create<IdeaWorkspacePublicationPreview>(this,
                preview => Call(Page, "PublishReviewed", Scope, preview));
            return Renderer.Render(Context, Field<WorkspaceBrowserDraft>(Page, "draft").Delivery, CurrentPreview, PreviewBusy, callback);
        }

        public async Task<string> Snapshot()
        {
            await using var db = Factory.CreateDbContext();
            var connection = db.Database.GetDbConnection();
            using var names = connection.CreateCommand();
            names.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            var tables = new List<string>();
            await using (var reader = await names.ExecuteReaderAsync())
                while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
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
                    for (var i = 0; i < values.Length; i++) values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    rows.Add(JsonSerializer.Serialize(values, Json));
                }
                snapshot[table] = rows.OrderBy(value => value, StringComparer.Ordinal).ToArray();
            }
            return JsonSerializer.Serialize(snapshot, Json);
        }

        public async ValueTask DisposeAsync()
        {
            Calls.PreviewRelease.TrySetResult();
            await Renderer.DisposeAsync();
            await Page.DisposeAsync();
            await services.DisposeAsync();
            Factory.Dispose();
        }
    }

    private sealed record RenderedButton(object Callback, bool Disabled);
    private sealed class CaptureRenderer : Renderer
    {
        private readonly WorkspaceDeliveryPanel panel = new();
        private int? componentId;
        public CaptureRenderer(IServiceProvider services, SqliteTestFactory factory) : base(services, NullLoggerFactory.Instance)
            => Set(panel, "DbFactory", factory);
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        protected override void HandleException(Exception exception) => throw new InvalidOperationException("Publication render failed.", exception);
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;

        public Task Render(IdeaWorkspaceContext context, WorkspaceDeliveryDraft draft, IdeaWorkspacePublicationPreview preview,
            bool previewBusy, object publish) => Dispatcher.InvokeAsync(async () =>
        {
            componentId ??= AssignRootComponentId(panel);
            await RenderRootComponentAsync(componentId.Value, ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(WorkspaceDeliveryPanel.Context)] = context,
                [nameof(WorkspaceDeliveryPanel.Draft)] = draft,
                [nameof(WorkspaceDeliveryPanel.Preview)] = preview,
                [nameof(WorkspaceDeliveryPanel.PreviewBusy)] = previewBusy,
                [nameof(WorkspaceDeliveryPanel.OnPublish)] = publish
            }));
        });

        public Task<RenderedButton> PublishButton() => Dispatcher.InvokeAsync(() =>
        {
            var frames = GetCurrentRenderTreeFrames(componentId!.Value);
            for (var i = 0; i < frames.Count; i++)
            {
                if (frames.Array[i].FrameType != RenderTreeFrameType.Element || frames.Array[i].ElementName != "button") continue;
                object? click = null;
                var matches = false;
                var disabled = false;
                for (var j = i + 1; j < frames.Count && frames.Array[j].FrameType == RenderTreeFrameType.Attribute; j++)
                {
                    var attribute = frames.Array[j];
                    if (attribute.AttributeName == "data-testid") matches = Equals(attribute.AttributeValue, "delivery-publish");
                    if (attribute.AttributeName == "onclick") click = attribute.AttributeValue;
                    if (attribute.AttributeName == "disabled") disabled = Equals(attribute.AttributeValue, true);
                }
                if (matches) return new RenderedButton(click ?? throw new InvalidOperationException("Publish has no callback."), disabled);
            }
            throw new InvalidOperationException("The rendered publish button is missing.");
        });

        public Task Click(object callback) => Dispatcher.InvokeAsync(() => callback switch
        {
            EventCallback<MouseEventArgs> click => click.InvokeAsync(new()),
            EventCallback click => click.InvokeAsync(new MouseEventArgs()),
            Func<Task> click => click(),
            _ => throw new InvalidOperationException("Unexpected rendered callback: " + callback.GetType())
        });
    }
#pragma warning restore BL0006

    private sealed class DraftRuntime : IJSRuntime, IJSObjectReference
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, default, args);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "import") return ValueTask.FromResult((T)(object)this);
            if (identifier == "initialize") return ValueTask.FromResult((T)(object)JsonSerializer.SerializeToElement(new WorkspaceBrowserSession("rendered-publication-test", "tab", null), Json));
            if (identifier is not ("saveDraft" or "disposeWriter")) throw new Xunit.Sdk.XunitException("Unexpected browser method: " + identifier);
            return ValueTask.FromResult(default(T)!);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
