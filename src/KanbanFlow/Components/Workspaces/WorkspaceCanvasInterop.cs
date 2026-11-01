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
            try { await imported.DisposeAsync(); }
            catch (JSDisconnectedException) { }
            catch (JSException) { }
            catch (OperationCanceledException) { }
            return;
        }
        module = imported;
        await module.InvokeVoidAsync(InitializeMethod, canvas, callback);
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        var m = module;
        if (m is null) return;
        module = null;
        try { await m.InvokeVoidAsync(DisposeMethod, element); }
        catch (JSDisconnectedException) { }
        catch (JSException) { }
        catch (OperationCanceledException) { }
        try { await m.DisposeAsync(); }
        catch (JSDisconnectedException) { }
        catch (JSException) { }
        catch (OperationCanceledException) { }
    }
}
