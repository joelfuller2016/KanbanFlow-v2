using System.Text.Json;
using KanbanFlow.Components.Workspaces;
using KanbanFlow.Models;
using KanbanFlow.Models.IdeaWorkspaces;
using KanbanFlow.Services;
using KanbanFlow.Services.IdeaWorkspaces;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace KanbanFlow.Tests;

public sealed class WorkspacePlanPanelRenderedAcceptanceTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("workspace")]
    [InlineData("project")]
    public async Task NormalAccept_RenderedCallbackKeepsWorkspaceRevisionAndProposalProjectBaseline(string advancement)
    {
        await using var scenario = await Scenario.Create(review: false);
        var rendered = scenario.Context;
        var callback = await scenario.Renderer.Button("plan-accept-" + scenario.ProposalId);
        if (advancement == "workspace") await scenario.AdvanceWorkspace();
        else await scenario.AdvanceProject("Unreviewed shared change");
        await scenario.Refresh();
        var before = await scenario.Snapshot();

        await scenario.Renderer.Click(callback);

        await AssertRejectedWithoutWrites(scenario, before,
            advancement == "workspace" ? "revision_conflict" : "project_plan_revision_conflict");
        var sent = Assert.Single(scenario.Sent);
        Assert.Equal(rendered.Revision, sent.ExpectedRevision);
        Assert.Equal(scenario.ProposalBaseline, sent.Payload.GetProperty("expectedProjectPlanRevision").GetInt64());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReviewedAccept_RenderedCallbackCannotOverwriteARefreshedSharedPlan(bool advanceWorkspaceToo)
    {
        await using var scenario = await Scenario.Create(review: true);
        var rendered = scenario.Context;
        Assert.NotEqual(scenario.ProposalBaseline, rendered.ProjectPlanRevision);
        var callback = await scenario.Renderer.Button("plan-rebase-accept-" + scenario.ProposalId);
        await scenario.AdvanceProject("Unreviewed shared change");
        if (advanceWorkspaceToo) await scenario.AdvanceWorkspace();
        await scenario.Refresh();
        Assert.NotEqual(rendered.ProjectPlanRevision, scenario.Context.ProjectPlanRevision);
        var before = await scenario.Snapshot();

        await scenario.Renderer.Click(callback);

        await AssertRejectedWithoutWrites(scenario, before,
            advanceWorkspaceToo ? "revision_conflict" : "project_plan_revision_conflict");
        var sent = Assert.Single(scenario.Sent);
        Assert.Equal(rendered.Revision, sent.ExpectedRevision);
        Assert.Equal(rendered.ProjectPlanRevision, sent.Payload.GetProperty("expectedProjectPlanRevision").GetInt64());
        Assert.Equal(Text("Unreviewed shared change"), Assert.Single((await scenario.Project()).AcceptedPlan!.Items).Description);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshRenderedAcceptance_UsesTheReviewedTokens_AndAcceptsTheCompleteProposal(bool review)
    {
        await using var scenario = await Scenario.Create(review);
        var rendered = scenario.Context;
        var callback = await scenario.Renderer.Button((review ? "plan-rebase-accept-" : "plan-accept-") + scenario.ProposalId);

        await scenario.Renderer.Click(callback);

        Assert.Null(scenario.Error);
        var sent = Assert.Single(scenario.Sent);
        Assert.Equal(rendered.Revision, sent.ExpectedRevision);
        Assert.Equal(review ? rendered.ProjectPlanRevision : scenario.ProposalBaseline,
            sent.Payload.GetProperty("expectedProjectPlanRevision").GetInt64());
        var source = await scenario.Service.GetAsync(scenario.Key);
        Assert.Equal("Accepted", source.Document.PlanRevisions.Single(plan => plan.Id == scenario.ProposalId).Status);
        Assert.Equal(Text("Source proposal"), Assert.Single((await scenario.Project()).AcceptedPlan!.Items).Description);
        Assert.True(source.Revision > rendered.Revision);
        Assert.True(source.ProjectPlanRevision > rendered.ProjectPlanRevision);
    }

    private static async Task AssertRejectedWithoutWrites(Scenario scenario, string before, string code)
    {
        var after = await scenario.Snapshot();
        Assert.True(scenario.Error is not null,
            $"The old rendered accept unexpectedly succeeded; SQLite changed: {before != after}.");
        Assert.Equal(code, scenario.Error.Code);
        Assert.Equal(409, scenario.Error.StatusCode);
        Assert.Equal(before, after);
    }

    private static string Text(string label) => $"\r\n  ## {label} Ω\r\n" + new string('文', 2000) + "\r\nCOMPLETE REQUIREMENT  \r\n";

    // Test-only renderer APIs capture and invoke the actual Razor event callbacks.
#pragma warning disable BL0006
    private sealed class Scenario : IAsyncDisposable
    {
        private readonly SqliteTestFactory factory = new();
        private readonly ServiceProvider services;
        public IdeaWorkspaceService Service { get; }
        public CaptureRenderer Renderer { get; }
        public string Key { get; private set; } = "";
        public string ProjectKey { get; private set; } = "";
        public string ProposalId { get; private set; } = "";
        public long? ProposalBaseline { get; private set; }
        public IdeaWorkspaceContext Context { get; private set; } = null!;
        public List<WorkspaceUiCommand> Sent { get; } = [];
        public IdeaWorkspaceException? Error { get; private set; }

        private Scenario()
        {
            Service = new(factory);
            services = new ServiceCollection().AddSingleton<IMarkdownRenderer, MarkdownRenderer>().BuildServiceProvider();
            Renderer = new(services);
        }

        public static async Task<Scenario> Create(bool review)
        {
            var scenario = new Scenario();
            try
            {
                await using var db = scenario.factory.CreateDbContext();
                var project = new Project { Name = "Shared render review" };
                db.Projects.Add(project);
                await db.SaveChangesAsync();
                scenario.ProjectKey = $"project:{project.Id}";
                await scenario.AdvanceProject("Initial shared change");
                scenario.Key = (await scenario.Service.CreateAsync(new("capture", "render-test", Title: "Rendered proposal"))).WorkspaceId;
                await scenario.Change(scenario.Key, "link_project", new { projectId = project.Id });
                var canonical = Assert.Single((await scenario.Project()).AcceptedPlan!.Items);
                await scenario.Propose(scenario.Key, Copy(canonical, "Source proposal"));
                var proposed = (await scenario.Service.GetAsync(scenario.Key)).Document.PlanRevisions.Last();
                scenario.ProposalId = proposed.Id;
                scenario.ProposalBaseline = proposed.ExpectedProjectPlanRevision;
                if (review) await scenario.AdvanceProject("Displayed reviewed shared change");
                await scenario.Refresh();
                return scenario;
            }
            catch { await scenario.DisposeAsync(); throw; }
        }

        public Task<IdeaWorkspaceContext> Project() => Service.GetAsync(ProjectKey);
        public Task AdvanceWorkspace() => Change(Key, "add_comment", new { text = "A workspace update after the button was rendered." });

        public async Task AdvanceProject(string description)
        {
            var existing = (await Project()).AcceptedPlan?.Items.SingleOrDefault();
            var item = existing is null
                ? new IdeaWorkspacePlanItem { Id = "shared-step", Title = "Shared step", Description = Text(description), AcceptanceCriteria = ["  Full criterion Ω  "] }
                : Copy(existing, description);
            await Propose(ProjectKey, item);
            var context = await Project();
            await Change(ProjectKey, "accept_plan", new { planRevisionId = context.Document.PlanRevisions.Last().Id,
                expectedProjectPlanRevision = context.ProjectPlanRevision });
        }

        private static IdeaWorkspacePlanItem Copy(IdeaWorkspacePlanItem item, string description) => new()
        {
            Id = item.Id, Title = item.Title, Description = Text(description),
            AcceptanceCriteria = [.. item.AcceptanceCriteria], DependsOn = [.. item.DependsOn], Provenance = item.Provenance
        };

        private async Task Propose(string key, IdeaWorkspacePlanItem item) =>
            await Change(key, "propose_plan", new { title = "Complete proposal", items = new[] { item },
                expectedProjectPlanRevision = (await Service.GetAsync(key)).ProjectPlanRevision });

        private async Task Change(string key, string action, object payload) =>
            await Service.ExecuteAsync(key, new(Guid.NewGuid().ToString("N"), (await Service.GetAsync(key)).Revision,
                action, JsonSerializer.SerializeToElement(payload, Json), "render-test"));

        public async Task Refresh()
        {
            Context = await Service.GetAsync(Key);
            await Renderer.Render(Context, EventCallback.Factory.Create<WorkspaceUiCommand>(this, Send));
        }

        private async Task Send(WorkspaceUiCommand command)
        {
            Sent.Add(command);
            try
            {
                await Service.ExecuteAsync(Key, new(Guid.NewGuid().ToString("N"), command.ExpectedRevision!.Value,
                    command.Action, command.Payload, "render-reviewer"));
            }
            catch (IdeaWorkspaceException ex) { Error = ex; }
        }

        public async Task<string> Snapshot()
        {
            await using var db = factory.CreateDbContext();
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
            await Renderer.DisposeAsync();
            await services.DisposeAsync();
            factory.Dispose();
        }
    }

    private sealed class CaptureRenderer(IServiceProvider services) : Renderer(services, NullLoggerFactory.Instance)
    {
        private readonly WorkspacePlanPanel panel = new();
        private int? componentId;
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        protected override void HandleException(Exception exception) => throw new InvalidOperationException("Plan render failed.", exception);
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;

        public Task Render(IdeaWorkspaceContext context, EventCallback<WorkspaceUiCommand> command) => Dispatcher.InvokeAsync(async () =>
        {
            componentId ??= AssignRootComponentId(panel);
            await RenderRootComponentAsync(componentId.Value, ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(WorkspacePlanPanel.Context)] = context,
                [nameof(WorkspacePlanPanel.Draft)] = new WorkspacePlanDraft(),
                [nameof(WorkspacePlanPanel.OnCommand)] = command
            }));
        });

        public Task<object> Button(string testId) => Dispatcher.InvokeAsync(() =>
        {
            var frames = GetCurrentRenderTreeFrames(componentId!.Value);
            for (var i = 0; i < frames.Count; i++)
            {
                if (frames.Array[i].FrameType != RenderTreeFrameType.Element || frames.Array[i].ElementName != "button") continue;
                object? click = null;
                var matches = false;
                for (var j = i + 1; j < frames.Count && frames.Array[j].FrameType == RenderTreeFrameType.Attribute; j++)
                {
                    var attribute = frames.Array[j];
                    if (attribute.AttributeName == "data-testid") matches = Equals(attribute.AttributeValue, testId);
                    if (attribute.AttributeName == "onclick") click = attribute.AttributeValue;
                }
                if (matches) return click ?? throw new InvalidOperationException("The rendered button has no callback.");
            }
            throw new InvalidOperationException("Rendered button missing: " + testId);
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
}
