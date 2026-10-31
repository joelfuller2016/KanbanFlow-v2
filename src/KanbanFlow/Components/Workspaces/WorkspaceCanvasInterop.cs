using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace KanbanFlow.Components.Workspaces;

public sealed class WorkspaceCanvasInterop(IJSRuntime js) : IAsyncDisposable
{
    private const string ModulePath = "./Components/Workspaces/WorkspaceCanvas.razor.js";
    private const string InitializeMethod = "initialize";
    private const string DisposeMethod = "dispose";
    private IJSObjectReference? module;
    private ElementReference element;
    private bool _disposed;

    public async ValueTask InitializeAsync(ElementReference canvas, DotNetObjectReference<WorkspaceCanvas> callback)
    {
        element = canvas;
        var imported = await js.InvokeAsync<IJSObjectReference>("import", ModulePath);
        if (_disposed)
        {
            await imported.DisposeAsync();
            return;
        }
        module = imported;
        await module.InvokeVoidAsync(InitializeMethod, canvas, callback);
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        try
        {
            if (module is null) return;
            await module.InvokeVoidAsync(DisposeMethod, element);
            await module.DisposeAsync();
        }
        catch (JSDisconnectedException) { }
    }
}
