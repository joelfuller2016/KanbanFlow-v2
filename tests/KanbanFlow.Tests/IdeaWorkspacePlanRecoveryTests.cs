using System.Reflection;
using System.Text.Json;
using KanbanFlow.Components.Workspaces;
using KanbanFlow.Models;
using KanbanFlow.Models.IdeaWorkspaces;
using KanbanFlow.Services.IdeaWorkspaces;
using Microsoft.AspNetCore.Components;

namespace KanbanFlow.Tests;

public sealed class IdeaWorkspacePlanRecoveryTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(7)]
    [InlineData(9)]
    [InlineData(null)]
    public async Task Reapply_RetainsEveryAuthoredFieldAndDependency_AndOnlyCurrentProjectProvenance(int? projectId)
    {
        var first = Item("first") with { Provenance = new(7, "idea:1", "original-first", "first") };
        var second = Item("second") with { DependsOn = ["first"], Provenance = projectId is { } id ? new(id, "idea:2", "original-second", "second") : null };
        var original = new WorkspacePlanDraft { Title = "  Authored plan Ω  ", BaseRevision = 3, BaseProjectPlanRevision = 2, Items = [first, second] };
        var before = JsonSerializer.Serialize(original, Json);
        var panel = Panel(Context(projectId), original);
        WorkspaceUiCommand? command = null;
        Set(panel, "OnCommand", EventCallback.Factory.Create<WorkspaceUiCommand>(this, value => command = value));

        await Call(panel, "Reapply");
        await Call(panel, "Propose");

        var reapplied = Current(panel);
        Assert.Equal(12, reapplied.BaseRevision);
        Assert.Equal(Context(projectId).ProjectPlanRevision, reapplied.BaseProjectPlanRevision);
        Assert.Equal(original.Title, reapplied.Title);
        Assert.Equal(first with { Provenance = projectId == 7 ? first.Provenance : null }, reapplied.Items[0]);
        Assert.Equal(second, reapplied.Items[1]);
        Assert.Equal(["first"], reapplied.Items[1].DependsOn);
        Assert.Equal(first.OriginalAcceptanceCriteria, reapplied.Items[0].OriginalAcceptanceCriteria);
        Assert.Equal(before, JsonSerializer.Serialize(original, Json));
        Assert.Equal(projectId == 7 ? JsonValueKind.Object : JsonValueKind.Null, command!.Payload.GetProperty("items")[0].GetProperty("provenance").ValueKind);
        Assert.Equal(first.Description, command.Payload.GetProperty("items")[0].GetProperty("description").GetString());
        Assert.Equal(first.OriginalAcceptanceCriteria, command.Payload.GetProperty("items")[0].GetProperty("acceptanceCriteria").EnumerateArray().Select(value => value.GetString()!));
    }

    [Fact]
    public async Task ReapplyAfterRelinking_AcceptsTheRetainedCompletePlan_ThroughTheRealService()
    {
        using var factory = new SqliteTestFactory();
        var service = new IdeaWorkspaceService(factory);
        var a = new Project { Name = "Project A" }; var b = new Project { Name = "Project B" };
        await using (var db = factory.CreateDbContext()) { db.Projects.AddRange(a, b); await db.SaveChangesAsync(); }
        var created = await service.CreateAsync(new("capture", "test", Title: "Retained idea"));
        var key = created.WorkspaceId;
        await Change(service, key, "link_project", new { projectId = a.Id });
        await Change(service, key, "propose_plan", new { title = "Original A plan", expectedProjectPlanRevision = (await service.GetAsync(key)).ProjectPlanRevision,
            items = new[] { new { id = "first", title = "First scope", description = "Original text", acceptanceCriteria = new[] { "  criterion\r\ncontinuation  ", "" }, dependsOn = Array.Empty<string>() } } });
        var proposed = await service.GetAsync(key);
        await Change(service, key, "accept_plan", new { planRevisionId = proposed.Document.PlanRevisions.Last().Id, expectedProjectPlanRevision = proposed.ProjectPlanRevision });
        var source = await service.GetAsync(key);
        var canonical = source.AcceptedPlan!.Items.Single();
        var authored = new WorkspacePlanDraft { Title = "  Keep this expansion Ω  ", BaseRevision = source.Revision, BaseProjectPlanRevision = source.ProjectPlanRevision,
            Items = [new() { Id = canonical.Id, Title = canonical.Title, Description = CompleteText("unsaved expansion"),
                AcceptanceCriteria = string.Join('\n', canonical.AcceptanceCriteria), OriginalAcceptanceCriteria = [.. canonical.AcceptanceCriteria],
                DependsOn = [.. canonical.DependsOn], Provenance = canonical.Provenance }] };
        await Change(service, key, "link_project", new { projectId = b.Id, expectedCurrentProjectId = a.Id });
        var target = await service.GetAsync(key);
        var panel = Panel(target, authored);
        WorkspaceUiCommand? request = null;
        Set(panel, "OnCommand", EventCallback.Factory.Create<WorkspaceUiCommand>(this, value => request = value));

        await Call(panel, "Reapply");
        await Call(panel, "Propose");
        await service.ExecuteAsync(key, new("reapplied-proposal", request!.ExpectedRevision!.Value, request.Action, request.Payload, "human"));

        var saved = (await service.GetAsync(key)).Document.PlanRevisions.Last();
        Assert.Equal(authored.Title, saved.Title);
        Assert.Equal(authored.Items[0].Description, Assert.Single(saved.Items).Description);
        Assert.Equal(canonical.AcceptanceCriteria, saved.Items[0].AcceptanceCriteria);
        Assert.Null(Current(panel).Items[0].Provenance);
        Assert.Equal(a.Id, authored.Items[0].Provenance!.ProjectId);
        Assert.Equal("Original text", (await service.GetAsync($"project:{a.Id}")).AcceptedPlan!.Items.Single().Description);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitCopyOrReapply_DuringPendingItemPersistence_ReplacesChildAuthority_WithoutRevivingOlderFields(bool reapply)
    {
        var original = Item("same") with { Provenance = new(7, "idea:1", "source", "same") };
        var panel = Panel(Context(7), new() { Title = "Draft", BaseRevision = 3, BaseProjectPlanRevision = 2, Items = [original] });
        var child = Editor(panel, original);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        Set(child, "OnChanged", EventCallback.Factory.Create<WorkspacePlanItemDraft>(this, async value => {
            await Call(panel, "ItemChanged", value);
            if (++calls == 1) { entered.TrySetResult(); await release.Task; }
        }));
        var local = original with { Description = CompleteText("local edit awaiting persistence") };
        var changing = Call(child, "Change", local);
        try
        {
            await entered.Task.WaitAsync(Deadline);
            Set(child, "Item", original); Invoke(child, "OnParametersSet");
            Assert.Equal(local.Description, ChildCurrent(child).Description);
            if (reapply)
            {
                Set(panel, "Context", Context(9)); Set(panel, "Draft", Current(panel)); Invoke(panel, "OnParametersSet");
                await Call(panel, "Reapply");
            }
            else
            {
                var replacement = new IdeaWorkspacePlanRevision { Title = "Copied plan", Items = [new() { Id = "same", Title = "Copied item", Description = CompleteText("confirmed copy"), AcceptanceCriteria = ["  copied\r\ncriterion  ", ""] }] };
                await Call(panel, "CopyPlan", replacement);
                await Call(panel, "ConfirmCopy");
            }
            ReceiveChild(panel, child);
            var authoritative = Current(panel).Items.Single();
            Assert.Equal(authoritative, ChildCurrent(child));
            Assert.Null(ChildCurrent(child).Provenance);
            await Call(child, "Change", ChildCurrent(child) with { Title = "A later field edit" });
            Assert.Equal(authoritative.Description, Current(panel).Items.Single().Description);
            Assert.Equal(authoritative.OriginalAcceptanceCriteria, Current(panel).Items.Single().OriginalAcceptanceCriteria);
            Assert.Null(Current(panel).Items.Single().Provenance);
            release.TrySetResult(); await changing.WaitAsync(Deadline);
            Assert.Equal(authoritative.Description, Current(panel).Items.Single().Description);
        }
        finally { release.TrySetResult(); await changing.WaitAsync(Deadline); }
    }

    [Fact]
    public async Task WorkspaceChange_WithReusedItemId_OverridesAnOlderPendingEditorBuffer()
    {
        var original = Item("same"); var child = new WorkspacePlanItemEditor();
        Set(child, "WorkspaceId", "idea:1"); Set(child, "Item", original); Invoke(child, "OnParametersSet");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Set(child, "OnChanged", EventCallback.Factory.Create<WorkspacePlanItemDraft>(this, async value => { entered.TrySetResult(); await release.Task; }));
        var changing = Call(child, "Change", original with { Description = CompleteText("older workspace edit") });
        try
        {
            await entered.Task.WaitAsync(Deadline);
            var replacement = original with { Description = CompleteText("other workspace") };
            Set(child, "WorkspaceId", "idea:2"); Set(child, "Item", replacement); Invoke(child, "OnParametersSet");
            Assert.Same(replacement, ChildCurrent(child));
            release.TrySetResult(); await changing.WaitAsync(Deadline);
            Assert.Same(replacement, ChildCurrent(child));
        }
        finally { release.TrySetResult(); await changing.WaitAsync(Deadline); }
    }

    [Fact]
    public async Task DelayedItemEdit_AfterDependencyRemoval_CannotPutTheRemovedDependencyBack()
    {
        var first = Item("first") with { DependsOn = ["removed", "kept"] };
        var panel = Panel(Context(7), new() { Title = "Authored plan", BaseRevision = 12, BaseProjectPlanRevision = 7, Items = [first, Item("removed"), Item("kept")] });
        var child = Editor(panel, first);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        Set(child, "OnChanged", EventCallback.Factory.Create<WorkspacePlanItemDraft>(this, async value => { await Call(panel, "ItemChanged", value); if (++calls == 1) { entered.TrySetResult(); await release.Task; } }));
        var changing = Call(child, "Change", first with { Description = CompleteText("latest retained reasoning") });
        try
        {
            await entered.Task.WaitAsync(Deadline);
            await Call(panel, "RemoveItem", "removed");
            ReceiveChild(panel, child);
            Assert.Contains("removed", ChildCurrent(child).DependsOn);
            await Call(child, "Change", ChildCurrent(child) with { Title = "Latest item title" });
            var saved = Current(panel).Items.Single(value => value.Id == "first");
            Assert.Equal(["kept"], saved.DependsOn);
            Assert.Equal(CompleteText("latest retained reasoning"), saved.Description);
            Assert.Equal(first.OriginalAcceptanceCriteria, saved.OriginalAcceptanceCriteria);
            release.TrySetResult(); await changing.WaitAsync(Deadline);
            Assert.Equal(["kept"], Current(panel).Items.Single(value => value.Id == "first").DependsOn);
        }
        finally { release.TrySetResult(); await changing.WaitAsync(Deadline); }
    }

    private WorkspacePlanPanel Panel(IdeaWorkspaceContext context, WorkspacePlanDraft draft)
    {
        var panel = new WorkspacePlanPanel(); Set(panel, "Context", context); Set(panel, "Draft", draft);
        Set(panel, "OnDraftChanged", EventCallback.Factory.Create<WorkspacePlanDraft>(this, value => Set(panel, "Draft", value)));
        Invoke(panel, "OnParametersSet"); return panel;
    }
    private static WorkspacePlanItemEditor Editor(WorkspacePlanPanel panel, WorkspacePlanItemDraft item)
    {
        var child = new WorkspacePlanItemEditor(); Set(child, "Item", item); ReceiveChild(panel, child); return child;
    }
    private static void ReceiveChild(WorkspacePlanPanel panel, WorkspacePlanItemEditor child)
    {
        var id = ChildCurrentOrParameter(child).Id;
        Set(child, "Item", Current(panel).Items.Single(value => value.Id == id)); Set(child, "OtherItems", Current(panel).Items);
        Set(child, "WorkspaceId", ((IdeaWorkspaceContext)panel.GetType().GetProperty("Context")!.GetValue(panel)!).WorkspaceId);
        Set(child, "ResetVersion", panel.GetType().GetField("itemResetVersion", Flags)!.GetValue(panel)!); Invoke(child, "OnParametersSet");
    }
    private static WorkspacePlanItemDraft ChildCurrentOrParameter(WorkspacePlanItemEditor child) => (WorkspacePlanItemDraft)child.GetType().GetProperty("Item")!.GetValue(child)!;
    private static WorkspacePlanDraft Current(WorkspacePlanPanel panel) => (WorkspacePlanDraft)panel.GetType().GetProperty("editing", Flags)!.GetValue(panel)!;
    private static WorkspacePlanItemDraft ChildCurrent(WorkspacePlanItemEditor child) => (WorkspacePlanItemDraft)child.GetType().GetProperty("editingItem", Flags)!.GetValue(child)!;
    private static WorkspacePlanItemDraft Item(string id) => new() { Id = id, Title = "  Complete title " + id, Description = CompleteText(id),
        AcceptanceCriteria = "  criterion\r\ncontinuation  \n", OriginalAcceptanceCriteria = ["  criterion\r\ncontinuation  ", ""] };
    private static IdeaWorkspaceContext Context(int? projectId)
    {
        var source = new IdeaWorkspaceSource("idea", 1, "Idea", "Source", null, null, null, null, null, projectId, default, default);
        return new("idea:1", "idea", 1, source.Title, 12, source, source, false, projectId, false, "/ideas/workspace/idea/1", null, null, default, 0, new())
        { ProjectPlanRevision = projectId is null ? null : 7 };
    }
    private static async Task Change(IdeaWorkspaceService service, string key, string action, object payload)
        => await service.ExecuteAsync(key, new(Guid.NewGuid().ToString("N"), (await service.GetAsync(key)).Revision, action, JsonSerializer.SerializeToElement(payload, Json), "test"));
    private static Task Call(object target, string name, params object?[] args) => (Task)Invoke(target, name, args)!;
    private static object? Invoke(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Flags)!.Invoke(target, args);
    private static void Set(object target, string name, object value) => target.GetType().GetProperty(name, Flags)!.SetValue(target, value);
    private static string CompleteText(string label) => "\r\n  ## " + label + " Ω 漢字 🧭  \r\n" + new string('文', 20_000) + "\r\nFINAL COMPLETE REQUIREMENT  \r\n";
}
