using KanbanFlow.Components.Workspaces;
using Microsoft.AspNetCore.Components;

namespace KanbanFlow.Tests;

public sealed class IdeaWorkspaceUiStateTests
{
    private static readonly TimeSpan CallbackTimeout = TimeSpan.FromSeconds(10);

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
