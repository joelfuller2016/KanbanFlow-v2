using KanbanFlow.Components.Shared;
using Microsoft.JSInterop;

namespace KanbanFlow.Tests;

public sealed class ProjectOutlineCancellationTests
{
    [Fact]
    public async Task CancelledReveal_DoesNotPoisonSharedImport()
    {
        var runtime = new Runtime();
        await using var interop = new ProjectOutlineInterop(runtime);
        using var cancelled = new CancellationTokenSource();
        var reveal = interop.RevealAsync(default, 1, false, cancelled.Token).AsTask();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reveal);
        var module = new Module();
        runtime.Import.SetResult(module);
        using var current = new CancellationTokenSource();
        await interop.RevealAsync(default, 2, true, current.Token);
        Assert.Equal(1, runtime.Imports);
        Assert.Equal(current.Token, module.Token);
    }

    [Fact]
    public async Task RevealCancellation_ReachesTheJavaScriptInvocation()
    {
        var runtime = new Runtime();
        var module = new Module { WaitForCancellation = true };
        runtime.Import.SetResult(module);
        await using var interop = new ProjectOutlineInterop(runtime);
        using var cancellation = new CancellationTokenSource();
        var reveal = interop.RevealAsync(default, 1, true, cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reveal);
        Assert.Equal(cancellation.Token, module.Token);
    }

    private sealed class Runtime : IJSRuntime
    {
        public TaskCompletionSource<IJSObjectReference> Import { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Imports { get; private set; }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Assert.Equal("import", identifier);
            Assert.False(cancellationToken.CanBeCanceled);
            Imports++;
            return (TValue)await Import.Task;
        }
    }

    private sealed class Module : IJSObjectReference
    {
        public CancellationToken Token { get; private set; }
        public bool WaitForCancellation { get; init; }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Assert.Equal("revealSelection", identifier);
            Token = cancellationToken;
            if (WaitForCancellation) await Task.Delay(Timeout.Infinite, cancellationToken);
            return default!;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
