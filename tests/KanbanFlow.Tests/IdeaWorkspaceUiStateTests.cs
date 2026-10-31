using System.Reflection;
using System.Text.Json;
using KanbanFlow.Components.Pages;
using KanbanFlow.Components.Workspaces;
using KanbanFlow.Models.IdeaWorkspaces;
using KanbanFlow.Services.IdeaWorkspaces;
using Microsoft.AspNetCore.Components;

namespace KanbanFlow.Tests;

public sealed class IdeaWorkspaceUiStateTests
{
    private static readonly TimeSpan CallbackTimeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RestoredCleanBuffers_UseCurrentSavedContentAndLayout_FromCacheOrStorage(bool fromCache)
    {
        await using var component = new IdeaWorkspace();
        var saved = CurrentContext();
        var previous = new WorkspaceBrowserDraft
        {
            SelectedComponentId = "card-a", SelectedPlacementId = "place-a",
            ContentDrafts = new() { ["card-a"] = Draft("card-a", 8, "Old saved title", "\r\n  Old saved text Ω  \r\n") },
            LayoutDrafts = new() { ["place-a"] = new() { PlacementId = "place-a", BaseRevision = 8, X = 10, Y = 20 } },
            Checkpoint = new() { Objective = CompleteText("Independent resume objective") }
        };

        var restored = Restore(component, saved, previous, fromCache);

        Assert.Equal(WorkspaceContentDraft.From(saved.Document.Components[0], saved.Revision), restored.ContentDrafts["card-a"]);
        Assert.Equal(WorkspaceLayoutDraft.From(saved.Document.Placements[0], saved.Revision), restored.LayoutDrafts["place-a"]);
        Assert.Equal("card-a", restored.SelectedComponentId);
        Assert.Equal("place-a", restored.SelectedPlacementId);
        Assert.Same(previous.Checkpoint, restored.Checkpoint);
        Assert.False(previous.ContentDrafts["card-a"].Dirty);
        Assert.Equal(8, previous.ContentDrafts["card-a"].BaseRevision);
        Assert.Equal(10, previous.LayoutDrafts["place-a"].X);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RestoredDirtyBuffers_PreserveCompleteTextLayoutAndPendingOperation_FromCacheOrStorage(bool fromCache)
    {
        await using var component = new IdeaWorkspace();
        var text = CompleteText("Retained authored draft");
        var content = Draft("card-a", 8, "  Unsaved title Ω  ", text) with { Dirty = true };
        var layout = new WorkspaceLayoutDraft { PlacementId = "place-a", BaseRevision = 8, X = 88, Y = 99, Width = 410, Dirty = true };
        var pending = new WorkspacePendingCommand(new("immutable-operation", 8, "update_component",
            JsonSerializer.SerializeToElement(new { componentId = "card-a", content = text }), "ui-test"), "content:card-a");
        var previous = new WorkspaceBrowserDraft
        {
            SelectedComponentId = "card-a", SelectedPlacementId = "place-a",
            ContentDrafts = new() { ["card-a"] = content }, LayoutDrafts = new() { ["place-a"] = layout },
            Discussion = new(text), Plan = new() { Title = text, BaseRevision = 8 },
            Checkpoint = new() { Objective = text, NextActions = text }, Pending = pending
        };

        var restored = Restore(component, CurrentContext(), previous, fromCache);

        Assert.Same(content, restored.ContentDrafts["card-a"]);
        Assert.Equal(text, restored.ContentDrafts["card-a"].Content);
        Assert.Equal(8, restored.ContentDrafts["card-a"].BaseRevision);
        Assert.Same(layout, restored.LayoutDrafts["place-a"]);
        Assert.Same(previous.Discussion, restored.Discussion);
        Assert.Same(previous.Plan, restored.Plan);
        Assert.Same(previous.Checkpoint, restored.Checkpoint);
        Assert.Same(pending, restored.Pending);
        Assert.NotNull(restored.Pending);
        Assert.Equal(text, restored.Pending.Command.Payload.GetProperty("content").GetString());
    }

    // The repository has no component renderer test dependency. Invoke the real restoration block;
    // workspace-ui-restoration.browser.mjs separately drives the actual rendered A/B/A and reload paths.
    private static WorkspaceBrowserDraft Restore(IdeaWorkspace component, IdeaWorkspaceContext saved, WorkspaceBrowserDraft previous, bool fromCache)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(IdeaWorkspace);
        FieldInfo Field(string name) => type.GetField(name, flags)
            ?? throw new Xunit.Sdk.XunitException($"IdeaWorkspace no longer has the restoration field {name}.");
        Field("context").SetValue(component, saved);
        if (fromCache)
            ((Dictionary<string, WorkspaceBrowserDraft>)Field("browserCache").GetValue(component)!)[saved.WorkspaceId] = previous;
        var method = type.GetMethod("RestoreDraft", flags)
            ?? throw new Xunit.Sdk.XunitException("IdeaWorkspace no longer has its RestoreDraft block.");
        method.Invoke(component, [new WorkspaceUiScope(saved.WorkspaceId, 3), new WorkspaceBrowserSession("browser-client", "tab-session", fromCache ? new() : previous)]);
        return Assert.IsType<WorkspaceBrowserDraft>(Field("draft").GetValue(component));
    }

    private static IdeaWorkspaceContext CurrentContext()
    {
        var source = new IdeaWorkspaceSource("idea", 11, "Current idea", "Source", null, null, null, null, null, null, default, default);
        return new("idea:11", "idea", 11, source.Title, 12, source, source, false, null, false,
            "/ideas/workspace/idea/11", null, null, default, 42, new()
            {
                Components = [new() { Id = "card-a", Title = "Current saved title Ω", Content = CompleteText("Current saved content") }],
                Placements = [new() { Id = "place-a", ComponentId = "card-a", X = 120, Y = 230, Width = 460, Height = 310, Order = 4, ZIndex = 3 }]
            });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RapidFieldEdits_PreserveBothLatestFields_WhileEitherEarlierCallbackStillAwaits(bool finishFirstEditFirst)
    {
        var original = Draft("card-a", 8, "Original title", "\r\n  Original Markdown Ω  \r\n");
        var input = new WorkspaceDraftInput<WorkspaceContentDraft>(original);
        const string identity = "idea:11/card-a";
        var authority = (WorkspaceRevision: 8L, DraftRevision: 8L);
        input.Receive(original, identity, authority);
        var first = new HeldCallback();
        var second = new HeldCallback();
        var running = new List<Task>();
        try
        {
            var titleEdit = input.Value with { Title = "  Revised exploration Ω  ", Dirty = true };
            var firstChange = input.ChangeAsync(titleEdit, first.Create(this));
            running.Add(firstChange);
            var firstSent = await first.Entered.Task.WaitAsync(CallbackTimeout);
            Assert.False(firstChange.IsCompleted);
            Assert.Equal(titleEdit.Title, firstSent.Title);
            Assert.Equal(original.Content, firstSent.Content);

            // The parent's old parameters can render while its first persistence callback is awaiting.
            input.Receive(original, identity, authority);
            Assert.Equal(titleEdit, input.Value);
            var completeText = CompleteText("Latest unsaved reasoning");
            var contentEdit = input.Value with { Content = completeText, Dirty = true };
            var secondChange = input.ChangeAsync(contentEdit, second.Create(this));
            running.Add(secondChange);
            var secondSent = await second.Entered.Task.WaitAsync(CallbackTimeout);
            Assert.False(secondChange.IsCompleted);
            Assert.Equal(titleEdit.Title, secondSent.Title);
            Assert.Equal(completeText, secondSent.Content);
            Assert.Equal(contentEdit, input.Value);

            input.Receive(firstSent, identity, authority);
            Assert.Equal(contentEdit, input.Value);
            if (finishFirstEditFirst)
            {
                first.Release.TrySetResult();
                await firstChange.WaitAsync(CallbackTimeout);
                Assert.False(secondChange.IsCompleted);
            }
            else
            {
                second.Release.TrySetResult();
                await secondChange.WaitAsync(CallbackTimeout);
                Assert.False(firstChange.IsCompleted);
            }

            input.Receive(original, identity, authority);
            Assert.Equal(contentEdit, input.Value);
            first.Release.TrySetResult();
            second.Release.TrySetResult();
            await Task.WhenAll(running).WaitAsync(CallbackTimeout);
            Assert.Equal(titleEdit.Title, input.Value.Title);
            Assert.Equal(completeText, input.Value.Content);

            var acknowledged = contentEdit with { Dirty = false };
            input.Receive(acknowledged, identity, authority);
            Assert.Equal(acknowledged, input.Value);
        }
        finally
        {
            first.Release.TrySetResult();
            second.Release.TrySetResult();
            await Task.WhenAll(running).WaitAsync(CallbackTimeout);
        }
    }

    [Fact]
    public async Task ExplicitReapply_UpdatesTheDraftRevisionDuringAnOlderCallback_AndSubsequentEditUsesThatRevision()
    {
        var original = Draft("card-a", 10, "Exploration", "\r\n  Before the local edit Ω  \r\n");
        var input = new WorkspaceDraftInput<WorkspaceContentDraft>(original);
        const string identity = "idea:11/card-a";
        input.Receive(original, identity, (WorkspaceRevision: 10L, DraftRevision: 10L));
        var pending = new HeldCallback();
        var completeText = CompleteText("Human draft survives explicit reapply");
        var local = original with { Title = "  Keep this approach Ω  ", Content = completeText, Dirty = true };
        var change = input.ChangeAsync(local, pending.Create(this));
        try
        {
            var sent = await pending.Entered.Task.WaitAsync(CallbackTimeout);
            Assert.Equal(local, sent);
            Assert.False(change.IsCompleted);

            // Refresh discovers revision 12 while the human draft still names revision 10.
            input.Receive(local, identity, (WorkspaceRevision: 12L, DraftRevision: 10L));
            Assert.Equal(10, input.Value.BaseRevision);
            Assert.Equal(completeText, input.Value.Content);
            var rebased = local with { BaseRevision = 12, OriginalContent = "\r\n  Collaborator's complete saved text Ω  \r\n" };
            input.Receive(rebased, identity, (WorkspaceRevision: 12L, DraftRevision: 12L));
            Assert.False(change.IsCompleted);
            Assert.Equal(12, input.Value.BaseRevision);
            Assert.Equal(local.Title, input.Value.Title);
            Assert.Equal(completeText, input.Value.Content);
            Assert.Equal(rebased.OriginalContent, input.Value.OriginalContent);

            pending.Release.TrySetResult();
            await change.WaitAsync(CallbackTimeout);
            Assert.Equal(rebased, input.Value);
            WorkspaceContentDraft? nextSent = null;
            var next = input.Value with { Title = "A later field edit after reapply Ω" };
            await input.ChangeAsync(next, EventCallback.Factory.Create<WorkspaceContentDraft>(this,
                (WorkspaceContentDraft value) => { nextSent = value; }));
            Assert.NotNull(nextSent);
            Assert.Equal(12, nextSent.BaseRevision);
            Assert.Equal(next.Title, nextSent.Title);
            Assert.Equal(completeText, nextSent.Content);
        }
        finally
        {
            pending.Release.TrySetResult();
            await change.WaitAsync(CallbackTimeout);
        }
    }

    [Fact]
    public async Task WorkspaceIdentityChange_IsolatesOldCallbacks_FromCurrentParametersAndUnsavedBuffer()
    {
        var originalA = Draft("card-a", 4, "Idea A", "\r\n  Original A Ω  \r\n");
        var originalB = Draft("card-b", 4, "Idea B", "\r\n  Original B 漢字  \r\n");
        var input = new WorkspaceDraftInput<WorkspaceContentDraft>(originalA);
        var authority = (WorkspaceRevision: 4L, DraftRevision: 4L);
        input.Receive(originalA, "idea:11/card-a", authority);
        var pendingA = new HeldCallback();
        var pendingB = new HeldCallback();
        var running = new List<Task>();
        try
        {
            var editedA = originalA with { Content = CompleteText("A's pending buffer"), Dirty = true };
            var changeA = input.ChangeAsync(editedA, pendingA.Create(this));
            running.Add(changeA);
            Assert.Equal(editedA, await pendingA.Entered.Task.WaitAsync(CallbackTimeout));

            input.Receive(originalB, "idea:12/card-b", authority);
            Assert.Equal(originalB, input.Value);
            var currentB = originalB with { Title = "Updated parameters for idea B Ω", Content = "\r\n  Current B source and spacing  \r\n" };
            input.Receive(currentB, "idea:12/card-b", authority);
            Assert.False(changeA.IsCompleted);
            Assert.Equal(currentB, input.Value);

            var editedB = input.Value with { Content = CompleteText("B's current independent buffer"), Dirty = true };
            var changeB = input.ChangeAsync(editedB, pendingB.Create(this));
            running.Add(changeB);
            Assert.Equal(editedB, await pendingB.Entered.Task.WaitAsync(CallbackTimeout));
            pendingA.Release.TrySetResult();
            await changeA.WaitAsync(CallbackTimeout);
            Assert.False(changeB.IsCompleted);
            Assert.Equal(editedB, input.Value);

            // Completing A must not release B's active local-edit protection.
            input.Receive(originalB, "idea:12/card-b", authority);
            Assert.Equal(editedB.Title, input.Value.Title);
            Assert.Equal(editedB.Content, input.Value.Content);
            Assert.Equal("card-b", input.Value.ComponentId);
            pendingB.Release.TrySetResult();
            await changeB.WaitAsync(CallbackTimeout);
            var acknowledgedB = editedB with { Dirty = false };
            input.Receive(acknowledgedB, "idea:12/card-b", authority);
            Assert.Equal(acknowledgedB, input.Value);
        }
        finally
        {
            pendingA.Release.TrySetResult();
            pendingB.Release.TrySetResult();
            await Task.WhenAll(running).WaitAsync(CallbackTimeout);
        }
    }

    private static WorkspaceContentDraft Draft(string id, long revision, string title, string content) => new()
    {
        Key = id, ComponentId = id, BaseRevision = revision, Type = "markdown", Title = title,
        Content = content, OriginalTitle = title, OriginalContent = content
    };

    private static string CompleteText(string label) =>
        $"\r\n  ## {label} Ω 漢字 🧭\r\n\r\n```text\r\n    preserve indentation and final spaces  \r\n```\r\n"
        + new string('文', 40_000) + "\r\nFINAL COMPLETE UI DRAFT REQUIREMENT Ω  \r\n";

    private sealed class HeldCallback
    {
        public TaskCompletionSource<WorkspaceContentDraft> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public EventCallback<WorkspaceContentDraft> Create(object receiver) => EventCallback.Factory.Create<WorkspaceContentDraft>(receiver,
            (WorkspaceContentDraft value) => { Entered.TrySetResult(value); return Release.Task; });
    }
}
