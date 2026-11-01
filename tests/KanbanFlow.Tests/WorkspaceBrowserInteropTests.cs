using System.Text.Json;
using KanbanFlow.Components.Workspaces;
using Microsoft.JSInterop;

namespace KanbanFlow.Tests;

public sealed class WorkspaceBrowserInteropTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FaultedImport_DisposalIsSafe_AndExplicitNewWrapperCanRetry(bool canceled)
    {
        var runtime = new BrowserRuntime { ImportError = canceled ? new OperationCanceledException("import canceled") : new JSException("module import failed") };
        var failed = new WorkspaceBrowserInterop(runtime);
        if (canceled) await Assert.ThrowsAsync<OperationCanceledException>(() => failed.InitializeAsync("idea:11").AsTask());
        else await Assert.ThrowsAsync<JSException>(() => failed.InitializeAsync("idea:11").AsTask());

        await failed.DisposeAsync();
        await failed.DisposeAsync();
        Assert.Equal(0, runtime.Module.DisposeCalls);

        runtime.ImportError = null;
        await using var retry = new WorkspaceBrowserInterop(runtime);
        var session = await retry.InitializeAsync("idea:11");
        Assert.Equal("browser-client", session.ClientId);
        Assert.Equal("tab-session", session.SessionId);
        Assert.Equal(2, runtime.ImportCalls);
    }

    [Fact]
    public async Task DisconnectedImport_DisposalIsSafe_AndExplicitNewWrapperCanRetry()
    {
        var runtime = new BrowserRuntime { ImportError = new JSDisconnectedException("circuit disconnected during import") };
        var failed = new WorkspaceBrowserInterop(runtime);
        await Assert.ThrowsAsync<JSDisconnectedException>(() => failed.InitializeAsync("idea:11").AsTask());

        await failed.DisposeAsync();
        await failed.DisposeAsync();
        Assert.Equal(0, runtime.Module.WriterCalls);
        Assert.Equal(0, runtime.Module.DisposeCalls);

        runtime.ImportError = null;
        var retry = new WorkspaceBrowserInterop(runtime);
        var session = await retry.InitializeAsync("idea:11");
        Assert.Equal("tab-session", session.SessionId);
        await retry.DisposeAsync();
        await retry.DisposeAsync();
        Assert.Equal(2, runtime.ImportCalls);
        Assert.Equal(1, runtime.Module.WriterCalls);
        Assert.Equal(1, runtime.Module.DisposeCalls);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DisconnectedCleanup_StillAttemptsModuleDisposal_AndRepeatedDisposalIsSafe(bool writerDisconnected, bool moduleDisconnected)
    {
        var runtime = new BrowserRuntime();
        var browser = new WorkspaceBrowserInterop(runtime);
        await browser.InitializeAsync("idea:11");
        runtime.Module.WriterError = writerDisconnected ? new JSDisconnectedException("circuit disconnected during writer cleanup") : null;
        runtime.Module.DisposeError = moduleDisconnected ? new JSDisconnectedException("circuit disconnected during module disposal") : null;

        await browser.DisposeAsync();
        await browser.DisposeAsync();

        Assert.Equal(1, runtime.Module.WriterCalls);
        Assert.Equal(1, runtime.Module.DisposeCalls);
        Assert.False(string.IsNullOrEmpty(runtime.Module.DisposedWriter));
    }

    [Fact]
    public async Task WriterCleanupFailure_StillDisposesTheImportedModule_AndDisposalIsIdempotent()
    {
        var runtime = new BrowserRuntime();
        var browser = new WorkspaceBrowserInterop(runtime);
        await browser.InitializeAsync("idea:11");
        runtime.Module.WriterError = new JSException("writer cleanup failed");
        runtime.Module.DisposeError = new JSException("module reference disconnected");

        await browser.DisposeAsync();
        await browser.DisposeAsync();

        Assert.Equal(1, runtime.Module.WriterCalls);
        Assert.Equal(1, runtime.Module.DisposeCalls);
        Assert.False(string.IsNullOrEmpty(runtime.Module.DisposedWriter));
    }

    [Fact]
    public async Task UnknownStoredDraftField_IsRejectedBeforeLosingAuthoredText_AndRawRecoveryRemainsExact()
    {
        var text = CompleteText();
        var raw = JsonSerializer.Serialize(new { contentDrafts = new { card = new { key = "card", futureAuthoredContent = text } } });
        var runtime = new BrowserRuntime();
        runtime.Module.Raw = raw;
        runtime.Module.Initialization = JsonSerializer.SerializeToElement(new { clientId = "browser-client", sessionId = "tab-session", draft = JsonSerializer.Deserialize<JsonElement>(raw) });
        await using var browser = new WorkspaceBrowserInterop(runtime);

        await Assert.ThrowsAsync<JsonException>(() => browser.InitializeAsync("idea:11").AsTask());
        Assert.Equal(raw, await browser.ReadStoredDraftAsync("idea:11"));
        Assert.Equal(0, runtime.Module.PreserveCalls);
        var preserved = await browser.PreserveAndClearDraftAsync("idea:11");
        Assert.Equal(raw, preserved.Raw);
        Assert.Equal("preserved-original", preserved.BackupKey);
        Assert.Equal(1, runtime.Module.PreserveCalls);
    }

    [Theory]
    [InlineData("{\"contentDrafts\":null}")]
    [InlineData("{\"checkpoint\":null}")]
    [InlineData("{\"plan\":{\"items\":[null]}}")]
    [InlineData("{\"pending\":{\"command\":null}}")]
    [InlineData("{\"plan\":{\"items\":[{\"id\":\"duplicate\"},{\"id\":\"duplicate\"}]}}")]
    public async Task IncompatibleStoredDraftShape_IsRejectedWithoutClearingTheOriginal(string raw)
    {
        var runtime = new BrowserRuntime();
        runtime.Module.Raw = raw;
        runtime.Module.Initialization = JsonSerializer.SerializeToElement(new { clientId = "browser-client", sessionId = "tab-session", draft = JsonSerializer.Deserialize<JsonElement>(raw) });
        await using var browser = new WorkspaceBrowserInterop(runtime);

        await Assert.ThrowsAsync<JsonException>(() => browser.InitializeAsync("idea:11").AsTask());
        Assert.Equal(raw, await browser.ReadStoredDraftAsync("idea:11"));
        Assert.Equal(0, runtime.Module.PreserveCalls);
    }

    [Fact]
    public async Task ValidCompleteDraftAndImmutablePendingEnvelope_RoundTripWithoutCoercion()
    {
        var text = CompleteText();
        var draft = new WorkspaceBrowserDraft
        {
            ContentDrafts = new() { ["$new"] = new() { Content = text, Dirty = true } },
            Plan = new() { Items = [new() { Id = "plan-item" }, new() { Id = "PLAN-ITEM" }] },
            Pending = new(new("immutable-operation", 8, "create_component", JsonSerializer.SerializeToElement(new { content = text }), "human"), "content:$new")
        };
        var runtime = new BrowserRuntime();
        runtime.Module.Initialization = JsonSerializer.SerializeToElement(new WorkspaceBrowserSession("browser-client", "tab-session", draft), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await using var browser = new WorkspaceBrowserInterop(runtime);

        var session = await browser.InitializeAsync("idea:11");

        Assert.NotNull(session.Draft);
        Assert.Equal(text, session.Draft.ContentDrafts["$new"].Content);
        Assert.Equal(["plan-item", "PLAN-ITEM"], session.Draft.Plan.Items.Select(item => item.Id));
        Assert.NotNull(session.Draft.Pending);
        Assert.Equal("immutable-operation", session.Draft.Pending.Command.OperationId);
        Assert.Equal(8, session.Draft.Pending.Command.ExpectedRevision);
        Assert.Equal(text, session.Draft.Pending.Command.Payload.GetProperty("content").GetString());
        Assert.Equal(0, runtime.Module.PreserveCalls);
    }

    private static string CompleteText() => "\r\n  ## Complete browser recovery Ω 漢字 🧭  \r\n" + new string('文', 40_000) + "\r\nFINAL PRESERVED REQUIREMENT  \r\n";

    internal sealed class BrowserRuntime : IJSRuntime
    {
        public Exception? ImportError { get; set; }
        public int ImportCalls { get; private set; }
        public BrowserModule Module { get; } = new();
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, default, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Assert.Equal("import", identifier);
            ImportCalls++;
            return ImportError is { } failure ? ValueTask.FromException<TValue>(failure) : ValueTask.FromResult((TValue)(object)Module);
        }
    }

    internal sealed class BrowserModule : IJSObjectReference
    {
        public JsonElement Initialization { get; set; } = JsonSerializer.SerializeToElement(new { clientId = "browser-client", sessionId = "tab-session", draft = (object?)null });
        public string? Raw { get; set; }
        public int PreserveCalls { get; private set; }
        public int WriterCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public string? DisposedWriter { get; private set; }
        public Exception? WriterError { get; set; }
        public Exception? DisposeError { get; set; }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, default, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            object? value;
            switch (identifier)
            {
                case "initialize": value = Initialization; break;
                case "readStoredDraft": value = Raw; break;
                case "preserveAndClearDraft": PreserveCalls++; value = new WorkspaceBrowserDraftRecovery("preserved-original", Raw); break;
                case "disposeWriter":
                    WriterCalls++;
                    DisposedWriter = Assert.IsType<string>(Assert.Single(args!));
                    return WriterError is { } failure ? ValueTask.FromException<TValue>(failure) : ValueTask.FromResult(default(TValue)!);
                default: throw new Xunit.Sdk.XunitException($"Unexpected browser method {identifier}.");
            }
            return ValueTask.FromResult((TValue)value!);
        }
        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return DisposeError is { } failure ? ValueTask.FromException(failure) : ValueTask.CompletedTask;
        }
    }
}
