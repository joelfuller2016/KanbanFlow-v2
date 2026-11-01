using System.Reflection;
using KanbanFlow.Components.Pages;
using KanbanFlow.Models;
using KanbanFlow.Services.IdeaWorkspaces;

namespace KanbanFlow.Tests;

public sealed class IdeaGalleryFilterCursorRecoveryTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task FailedFilterReload_KeepsTheDisplayedCursorPairedWithItsFilters_AndRetainsTheRetryInputs()
    {
        using var scenario = await Scenario.Create();
        var original = Field<IdeaWorkspacePage<IdeaWorkspaceSummary>>(scenario.Gallery, "page");
        var firstIds = Items(scenario.Gallery).Select(item => item.WorkspaceId).ToArray();
        Assert.Equal(50, firstIds.Length);
        Assert.NotNull(original.NextCursor);
        scenario.ChangeFilters("Changed", "project", "");
        scenario.Reads.FailNext = true;

        await Call(scenario.Gallery, "Reload");

        Assert.Same(original, Field<IdeaWorkspacePage<IdeaWorkspaceSummary>>(scenario.Gallery, "page"));
        Assert.Equal(firstIds, Items(scenario.Gallery).Select(item => item.WorkspaceId));
        Assert.Equal("Temporary gallery read failure.", Field<string?>(scenario.Gallery, "error"));
        AssertInputs(scenario.Gallery, "Changed", "project", "");

        await Call(scenario.Gallery, "More");

        Assert.True(scenario.Reads.CursorMismatch is null, scenario.Reads.CursorMismatch?.Code);
        Assert.Null(Field<string?>(scenario.Gallery, "error"));
        Assert.Equal(51, Items(scenario.Gallery).Count);
        Assert.All(Items(scenario.Gallery), item => Assert.Equal("idea", item.Kind));
        Assert.Equal(original.NextCursor, scenario.Reads.Calls[^1].Cursor);
        Assert.Equal(new Filters("idea", "New", "Original"), scenario.Reads.Calls[^1].Filters);
        AssertInputs(scenario.Gallery, "Changed", "project", "");

        await Call(scenario.Gallery, "Reload");
        Assert.Equal(50, Items(scenario.Gallery).Count);
        Assert.All(Items(scenario.Gallery), item => Assert.Equal("project", item.Kind));
        Assert.Null(scenario.Reads.Calls[^1].Cursor);
        Assert.Equal(new Filters("project", null, "Changed"), scenario.Reads.Calls[^1].Filters);
        await Call(scenario.Gallery, "More");
        Assert.Equal(52, Items(scenario.Gallery).Count);
        Assert.All(Items(scenario.Gallery), item => Assert.StartsWith("Changed", item.Title));
        Assert.Null(Field<IdeaWorkspacePage<IdeaWorkspaceSummary>>(scenario.Gallery, "page").NextCursor);
        Assert.Null(Field<string?>(scenario.Gallery, "error"));
    }

    [Fact]
    public async Task SuccessfulHeldReload_CommitsTheRequestedFilters_WhileLaterUnsubmittedInputsAndBusyGuardsSurvive()
    {
        using var scenario = await Scenario.Create();
        scenario.ChangeFilters("Changed", "project", "");
        scenario.Reads.HoldNext = true;
        var pending = Call(scenario.Gallery, "Reload");
        try
        {
            var request = await scenario.Reads.Entered.Task.WaitAsync(Deadline);
            Assert.Equal(new Filters("project", null, "Changed"), request.Filters);
            scenario.ChangeFilters("Unsubmitted input", "linked", "Archived");
            var count = scenario.Reads.Calls.Count;
            await Call(scenario.Gallery, "More");
            await Call(scenario.Gallery, "Reload");
            Assert.Equal(count, scenario.Reads.Calls.Count);
            scenario.Reads.Release.TrySetResult();
            await pending.WaitAsync(Deadline);

            await Call(scenario.Gallery, "More");

            Assert.True(scenario.Reads.CursorMismatch is null, scenario.Reads.CursorMismatch?.Code);
            Assert.Equal(52, Items(scenario.Gallery).Count);
            Assert.All(Items(scenario.Gallery), item => Assert.Equal("project", item.Kind));
            Assert.Equal(new Filters("project", null, "Changed"), scenario.Reads.Calls[^1].Filters);
            AssertInputs(scenario.Gallery, "Unsubmitted input", "linked", "Archived");
            Assert.Null(Field<string?>(scenario.Gallery, "error"));
        }
        finally { scenario.Reads.Release.TrySetResult(); await pending.WaitAsync(Deadline); }
    }

    [Fact]
    public async Task DisposalDuringReload_DoesNotCommitTheLatePageOrItsFilters()
    {
        using var scenario = await Scenario.Create();
        var original = Field<IdeaWorkspacePage<IdeaWorkspaceSummary>>(scenario.Gallery, "page");
        var originalIds = Items(scenario.Gallery).Select(item => item.WorkspaceId).ToArray();
        scenario.ChangeFilters("Changed", "project", "");
        scenario.Reads.HoldNext = true;
        var pending = Call(scenario.Gallery, "Reload");
        try
        {
            var request = await scenario.Reads.Entered.Task.WaitAsync(Deadline);
            scenario.Gallery.Dispose();
            Assert.True(request.Token.IsCancellationRequested);
            scenario.Reads.Release.TrySetResult();
            await pending.WaitAsync(Deadline);

            Assert.Same(original, Field<IdeaWorkspacePage<IdeaWorkspaceSummary>>(scenario.Gallery, "page"));
            Assert.Equal(originalIds, Items(scenario.Gallery).Select(item => item.WorkspaceId));
            Assert.Equal("Original", Field<string?>(scenario.Gallery, "activeQuery"));
            Assert.Equal("idea", Field<string?>(scenario.Gallery, "activeKind"));
            Assert.Equal("New", Field<string?>(scenario.Gallery, "activeStatus"));
            Assert.False(Field<bool>(scenario.Gallery, "busy"));
            Assert.Null(Field<string?>(scenario.Gallery, "error"));
        }
        finally { scenario.Reads.Release.TrySetResult(); await pending.WaitAsync(Deadline); }
    }

    private static void AssertInputs(IdeaGallery gallery, string query, string kind, string status)
    {
        Assert.Equal(query, Field<string?>(gallery, "query"));
        Assert.Equal(kind, Field<string?>(gallery, "kind"));
        Assert.Equal(status, Field<string?>(gallery, "status"));
    }

    private static List<IdeaWorkspaceSummary> Items(IdeaGallery gallery) => Field<List<IdeaWorkspaceSummary>>(gallery, "items");
    private static T Field<T>(IdeaGallery gallery, string name) => (T)typeof(IdeaGallery).GetField(name, Flags)!.GetValue(gallery)!;
    private static void Set(IdeaGallery gallery, string name, object? value) => typeof(IdeaGallery).GetField(name, Flags)!.SetValue(gallery, value);
    private static Task Call(IdeaGallery gallery, string method) => (Task)typeof(IdeaGallery).GetMethod(method, Flags)!.Invoke(gallery, null)!;

    private sealed class Scenario(SqliteTestFactory factory, IdeaGallery gallery, GalleryReads reads) : IDisposable
    {
        public IdeaGallery Gallery { get; } = gallery;
        public GalleryReads Reads { get; } = reads;

        public static async Task<Scenario> Create()
        {
            var factory = new SqliteTestFactory();
            try
            {
                await using var db = factory.CreateDbContext();
                db.Ideas.AddRange(Enumerable.Range(0, 51).Select(i => new Idea { Title = $"Original idea {i}", Status = IdeaStatus.New }));
                db.Projects.AddRange(Enumerable.Range(0, 52).Select(i => new Project { Name = $"Changed project {i}" }));
                await db.SaveChangesAsync();
                var service = DispatchProxy.Create<IIdeaWorkspaceService, GalleryReads>();
                var reads = (GalleryReads)(object)service;
                reads.Inner = new IdeaWorkspaceService(factory);
                var gallery = new IdeaGallery();
                typeof(IdeaGallery).GetProperty("Workspaces", Flags)!.SetValue(gallery, service);
                var scenario = new Scenario(factory, gallery, reads);
                scenario.ChangeFilters("Original", "idea", "New");
                await Call(gallery, "Reload");
                return scenario;
            }
            catch { factory.Dispose(); throw; }
        }

        public void ChangeFilters(string query, string kind, string status)
        {
            Set(Gallery, "query", query);
            Set(Gallery, "kind", kind);
            Set(Gallery, "status", status);
        }

        public void Dispose()
        {
            if (!Field<bool>(Gallery, "disposed")) Gallery.Dispose();
            factory.Dispose();
        }
    }

    public sealed record Filters(string? Kind, string? Status, string? Query);
    public sealed record Request(Filters Filters, string? Cursor, CancellationToken Token);

    public class GalleryReads : DispatchProxy
    {
        public IdeaWorkspaceService Inner { get; set; } = null!;
        public bool FailNext { get; set; }
        public bool HoldNext { get; set; }
        public List<Request> Calls { get; } = [];
        public IdeaWorkspaceException? CursorMismatch { get; private set; }
        public TaskCompletionSource<Request> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name != nameof(IIdeaWorkspaceService.ListAsync)) return method.Invoke(Inner, args);
            var request = new Request(new((string?)args![0], (string?)args[1], (string?)args[2]),
                (string?)args[3], (CancellationToken)args[5]!);
            Calls.Add(request);
            return Read(request, (int?)args[4]);
        }

        private async Task<IdeaWorkspacePage<IdeaWorkspaceSummary>> Read(Request request, int? limit)
        {
            if (FailNext) { FailNext = false; throw new IOException("Temporary gallery read failure."); }
            var hold = HoldNext; HoldNext = false;
            try
            {
                var loaded = await Inner.ListAsync(request.Filters.Kind, request.Filters.Status, request.Filters.Query,
                    request.Cursor, limit, request.Token);
                if (hold) { Entered.TrySetResult(request); await Release.Task; }
                return loaded;
            }
            catch (IdeaWorkspaceException ex) when (ex.Code == "cursor_mismatch") { CursorMismatch = ex; throw; }
        }
    }
}
