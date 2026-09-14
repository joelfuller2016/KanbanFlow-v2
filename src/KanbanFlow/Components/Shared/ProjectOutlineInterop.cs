using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace KanbanFlow.Components.Shared;

internal sealed class ProjectOutlineInterop(IJSRuntime js) : IAsyncDisposable
{
    private const string ModulePath = "./Components/Shared/ProjectOutline.razor.js";
    private const string RevealMethod = "revealSelection";
    private Task<IJSObjectReference>? moduleTask;
    private bool disposed;

    public async ValueTask RevealAsync(ElementReference items, int request, bool focus, CancellationToken ct = default)
    {
        if (disposed) return;
        moduleTask ??= js.InvokeAsync<IJSObjectReference>("import", ModulePath).AsTask();
        var module = await moduleTask.WaitAsync(ct);
        if (!disposed) await module.InvokeVoidAsync(RevealMethod, ct, items, request, focus);
    }

    public async ValueTask DisposeAsync()
    {
        disposed = true;
        if (moduleTask is null) return;
        try
        {
            var module = await moduleTask;
            await module.DisposeAsync();
        }
        catch (JSDisconnectedException) { }
    }
}
