using KanbanFlow.Components.Workspaces;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace KanbanFlow.Tests;

public sealed class WorkspaceCanvasInteropTests
{
    private static readonly TimeSpan CallbackTimeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData("success")]
    [InlineData("disconnected")]
    [InlineData("javascript")]
    [InlineData("canceled")]
    public async Task ImportCompletingAfterDisposal_CleansUpOnceWithoutInitializing_EvenWhenTeardownFails(string cleanup)
    {
        var runtime = new DelayedImportRuntime();
        var interop = new WorkspaceCanvasInterop(runtime);
        using var callback = DotNetObjectReference.Create(new WorkspaceCanvas());
        var initializing = interop.InitializeAsync(default, callback).AsTask();
        await runtime.ImportEntered.Task.WaitAsync(CallbackTimeout);
        Assert.False(initializing.IsCompleted);

        await interop.DisposeAsync();
        Assert.Equal(0, runtime.Module.DisposeCalls);
        runtime.Module.DisposeError = cleanup switch
        {
            "success" => null,
            "disconnected" => new JSDisconnectedException("Circuit ended while import was pending"),
            "javascript" => new JSException("Module cleanup failed after import"),
            "canceled" => new OperationCanceledException("Module cleanup canceled after import"),
            _ => throw new Xunit.Sdk.XunitException("Unknown cleanup fixture")
        };
        runtime.ImportCompleted.SetResult(runtime.Module);

        await initializing.WaitAsync(CallbackTimeout);
        await interop.DisposeAsync();

        Assert.Equal(1, runtime.ImportCalls);
        Assert.Equal(1, runtime.Module.DisposeCalls);
        Assert.Equal(0, runtime.Module.InitializeCalls);
        Assert.Equal(0, runtime.Module.ControllerDisposeCalls);
    }

    [Fact]
    public async Task ImportCompletingBeforeDisposal_InitializesAndDisposesTheExistingControllerNormally()
    {
        var runtime = new DelayedImportRuntime();
        var interop = new WorkspaceCanvasInterop(runtime);
        using var callback = DotNetObjectReference.Create(new WorkspaceCanvas());
        var canvas = default(ElementReference);
        var initializing = interop.InitializeAsync(canvas, callback).AsTask();
        await runtime.ImportEntered.Task.WaitAsync(CallbackTimeout);
        runtime.ImportCompleted.SetResult(runtime.Module);

        await initializing.WaitAsync(CallbackTimeout);

        Assert.Equal(1, runtime.Module.InitializeCalls);
        Assert.Equal(canvas, runtime.Module.InitializedElement);
        Assert.Same(callback, runtime.Module.InitializedCallback);
        Assert.Equal(0, runtime.Module.DisposeCalls);
        await interop.DisposeAsync();
        Assert.Equal(1, runtime.Module.ControllerDisposeCalls);
        Assert.Equal(canvas, runtime.Module.DisposedElement);
        Assert.Equal(1, runtime.Module.DisposeCalls);
    }

    // Only the asynchronous JS import/module boundary is simulated. The interop lifecycle is real.
    private sealed class DelayedImportRuntime : IJSRuntime
    {
        public TaskCompletionSource ImportEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IJSObjectReference> ImportCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CanvasModule Module { get; } = new();
        public int ImportCalls { get; private set; }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, default, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Assert.Equal("import", identifier);
            Assert.Equal("./Components/Workspaces/WorkspaceCanvas.razor.js", Assert.Single(args!));
            ImportCalls++;
            ImportEntered.TrySetResult();
            return new(ReturnImported<TValue>());
        }
        private async Task<TValue> ReturnImported<TValue>() => (TValue)(object)await ImportCompleted.Task;
    }

    private sealed class CanvasModule : IJSObjectReference
    {
        public Exception? DisposeError { get; set; }
        public int InitializeCalls { get; private set; }
        public int ControllerDisposeCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public ElementReference InitializedElement { get; private set; }
        public ElementReference DisposedElement { get; private set; }
        public object? InitializedCallback { get; private set; }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, default, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "initialize")
            {
                Assert.Equal(2, args!.Length);
                InitializeCalls++;
                InitializedElement = Assert.IsType<ElementReference>(args[0]);
                InitializedCallback = args[1];
            }
            else
            {
                Assert.Equal("dispose", identifier);
                ControllerDisposeCalls++;
                DisposedElement = Assert.IsType<ElementReference>(Assert.Single(args!));
            }
            return ValueTask.FromResult(default(TValue)!);
        }
        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return DisposeError is { } failure ? ValueTask.FromException(failure) : ValueTask.CompletedTask;
        }
    }
}
