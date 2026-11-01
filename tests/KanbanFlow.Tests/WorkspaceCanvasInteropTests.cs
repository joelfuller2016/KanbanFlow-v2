using KanbanFlow.Components.Workspaces;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using System.Reflection;

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

    [Theory]
    [InlineData("controller", "success")]
    [InlineData("controller", "disconnected")]
    [InlineData("controller", "javascript")]
    [InlineData("controller", "canceled")]
    [InlineData("controller", "unexpected")]
    [InlineData("module", "disconnected")]
    [InlineData("module", "javascript")]
    [InlineData("module", "canceled")]
    [InlineData("module", "unexpected")]
    public async Task InitializedCanvasDisposal_ReleasesControllerModuleAndCallbackOnce_AcrossTeardownFailures(string stage, string failure)
    {
        var runtime = new DelayedImportRuntime();
        var canvas = new WorkspaceCanvas();
        var callback = DotNetObjectReference.Create(canvas);
        var interop = await Initialize(runtime, callback);
        SetField(canvas, "interop", interop);
        SetField(canvas, "dotNet", callback);
        Exception? teardownError = failure switch
        {
            "success" => null,
            "disconnected" => new JSDisconnectedException("Circuit ended during canvas teardown"),
            "javascript" => new JSException("JavaScript failed during canvas teardown"),
            "canceled" => new OperationCanceledException("Canvas teardown was canceled"),
            "unexpected" => new InvalidOperationException("Unexpected canvas teardown failure"),
            _ => throw new Xunit.Sdk.XunitException("Unknown teardown fixture")
        };
        if (stage == "controller") runtime.Module.ControllerDisposeError = teardownError;
        else runtime.Module.DisposeError = teardownError;

        if (failure == "unexpected")
            Assert.Same(teardownError, await Assert.ThrowsAsync<InvalidOperationException>(() => canvas.DisposeAsync().AsTask()));
        else await canvas.DisposeAsync();

        Assert.Equal(1, runtime.Module.ControllerDisposeCalls);
        Assert.Equal(1, runtime.Module.DisposeCalls);
        Assert.Throws<ObjectDisposedException>(() => callback.Value);
        await canvas.DisposeAsync();
        await interop.DisposeAsync();
        Assert.Equal(1, runtime.Module.ControllerDisposeCalls);
        Assert.Equal(1, runtime.Module.DisposeCalls);
    }

    [Fact]
    public async Task ConcurrentCanvasDisposal_CleansEachOwnedResourceOnce_AcrossTheTeardownAwait()
    {
        var runtime = new DelayedImportRuntime();
        var canvas = new WorkspaceCanvas();
        var callback = DotNetObjectReference.Create(canvas);
        var interop = await Initialize(runtime, callback);
        SetField(canvas, "interop", interop);
        SetField(canvas, "dotNet", callback);
        runtime.Module.ControllerDisposeGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        var disposing = canvas.DisposeAsync().AsTask();
        Assert.False(disposing.IsCompleted);
        Assert.Equal(1, runtime.Module.ControllerDisposeCalls);
        await canvas.DisposeAsync();
        await interop.DisposeAsync();
        Assert.Equal(1, runtime.Module.ControllerDisposeCalls);
        Assert.Equal(0, runtime.Module.DisposeCalls);
        Assert.Same(canvas, callback.Value);
        runtime.Module.ControllerDisposeGate.SetResult();
        await disposing.WaitAsync(CallbackTimeout);

        Assert.Equal(1, runtime.Module.ControllerDisposeCalls);
        Assert.Equal(1, runtime.Module.DisposeCalls);
        Assert.Throws<ObjectDisposedException>(() => callback.Value);
    }

    [Theory]
    [InlineData("import", "disconnected")]
    [InlineData("import", "javascript")]
    [InlineData("import", "canceled")]
    [InlineData("initialize", "disconnected")]
    [InlineData("initialize", "javascript")]
    [InlineData("initialize", "canceled")]
    public async Task CanvasInitializationFailure_ReleasesPartialResourcesAndCallback_WithoutDisposingTheComponent(string stage, string failure)
    {
        var runtime = new DelayedImportRuntime();
        var canvas = new WorkspaceCanvas();
        SetRuntime(canvas, runtime);
        var initializing = (Task<bool>)InvokeLifecycle(canvas, "InitializeCanvasAsync")!;
        await runtime.ImportEntered.Task.WaitAsync(CallbackTimeout);
        var callback = Field<DotNetObjectReference<WorkspaceCanvas>>(canvas, "dotNet");
        if (stage == "import") runtime.ImportCompleted.SetException(InitializationFailure(failure));
        else
        {
            runtime.Module.InitializeError = InitializationFailure(failure);
            runtime.ImportCompleted.SetResult(runtime.Module);
        }

        Assert.False(await initializing.WaitAsync(CallbackTimeout));
        Assert.Null(Field<WorkspaceCanvasInterop?>(canvas, "interop"));
        Assert.Null(Field<DotNetObjectReference<WorkspaceCanvas>?>(canvas, "dotNet"));
        Assert.Throws<ObjectDisposedException>(() => callback.Value);
        Assert.False(Field<bool>(canvas, "disposed"));
        Assert.Equal(stage == "initialize" ? 1 : 0, runtime.Module.InitializeCalls);
        Assert.Equal(stage == "initialize" ? 1 : 0, runtime.Module.ControllerDisposeCalls);
        Assert.Equal(stage == "initialize" ? 1 : 0, runtime.Module.DisposeCalls);

        var retryRuntime = new DelayedImportRuntime();
        SetRuntime(canvas, retryRuntime);
        var retry = (Task<bool>)InvokeLifecycle(canvas, "InitializeCanvasAsync")!;
        await retryRuntime.ImportEntered.Task.WaitAsync(CallbackTimeout);
        retryRuntime.ImportCompleted.SetResult(retryRuntime.Module);
        Assert.True(await retry.WaitAsync(CallbackTimeout));
        await canvas.DisposeAsync();
        Assert.Equal(1, retryRuntime.Module.InitializeCalls);
        Assert.Equal(1, retryRuntime.Module.ControllerDisposeCalls);
        Assert.Equal(1, retryRuntime.Module.DisposeCalls);
    }

    [Theory]
    [InlineData("disconnected")]
    [InlineData("javascript")]
    [InlineData("canceled")]
    public async Task InitializationFailureAfterComponentDisposal_DoesNotRenderOrRetainCallbacks(string failure)
    {
        var runtime = new DelayedImportRuntime();
        var canvas = new WorkspaceCanvas();
        SetRuntime(canvas, runtime);
        // This real lifecycle method has no renderer attached. Any attempted fallback render would fail.
        var rendering = (Task)InvokeLifecycle(canvas, "OnAfterRenderAsync", true)!;
        await runtime.ImportEntered.Task.WaitAsync(CallbackTimeout);
        var callback = Field<DotNetObjectReference<WorkspaceCanvas>>(canvas, "dotNet");
        await canvas.DisposeAsync();
        runtime.ImportCompleted.SetException(InitializationFailure(failure));

        await rendering.WaitAsync(CallbackTimeout);
        Assert.Throws<ObjectDisposedException>(() => callback.Value);
        Assert.Null(Field<WorkspaceCanvasInterop?>(canvas, "interop"));
        Assert.Null(Field<DotNetObjectReference<WorkspaceCanvas>?>(canvas, "dotNet"));
        Assert.False(Field<bool>(canvas, "_canvasError"));
        await (Task)InvokeLifecycle(canvas, "OnAfterRenderAsync", true)!;
        Assert.Equal(1, runtime.ImportCalls);
        Assert.Equal(0, runtime.Module.InitializeCalls);
    }

    private static Exception InitializationFailure(string failure) => failure switch
    {
        "disconnected" => new JSDisconnectedException("Circuit ended during canvas initialization"),
        "javascript" => new JSException("Canvas module failed to initialize"),
        "canceled" => new OperationCanceledException("Canvas initialization was canceled"),
        _ => throw new Xunit.Sdk.XunitException("Unknown initialization fixture")
    };

    private static T Field<T>(object target, string name)
        => (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;
    private static object? InvokeLifecycle(object target, string name, params object[] args)
        => target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args);
    private static void SetRuntime(WorkspaceCanvas canvas, IJSRuntime runtime)
        => typeof(WorkspaceCanvas).GetProperty("JS", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(canvas, runtime);

    private static async Task<WorkspaceCanvasInterop> Initialize(DelayedImportRuntime runtime, DotNetObjectReference<WorkspaceCanvas> callback)
    {
        var interop = new WorkspaceCanvasInterop(runtime);
        var initializing = interop.InitializeAsync(default, callback).AsTask();
        await runtime.ImportEntered.Task.WaitAsync(CallbackTimeout);
        runtime.ImportCompleted.SetResult(runtime.Module);
        await initializing.WaitAsync(CallbackTimeout);
        return interop;
    }

    private static void SetField(object target, string name, object value)
        => target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);

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
        public Exception? InitializeError { get; set; }
        public Exception? ControllerDisposeError { get; set; }
        public TaskCompletionSource? ControllerDisposeGate { get; set; }
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
                if (InitializeError is { } failure) return ValueTask.FromException<TValue>(failure);
            }
            else
            {
                Assert.Equal("dispose", identifier);
                ControllerDisposeCalls++;
                DisposedElement = Assert.IsType<ElementReference>(Assert.Single(args!));
                if (ControllerDisposeError is { } failure) return ValueTask.FromException<TValue>(failure);
                if (ControllerDisposeGate is { } gate) return new(WaitForControllerDisposal<TValue>(gate.Task));
            }
            return ValueTask.FromResult(default(TValue)!);
        }
        private static async Task<TValue> WaitForControllerDisposal<TValue>(Task gate)
        {
            await gate;
            return default!;
        }
        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return DisposeError is { } failure ? ValueTask.FromException(failure) : ValueTask.CompletedTask;
        }
    }
}
