using Microsoft.JSInterop;

namespace KanbanFlow.Components.Workspaces;

public sealed class WorkspaceBrowserInterop(IJSRuntime js) : IAsyncDisposable
{
    private const string ModulePath = "./Components/Pages/IdeaWorkspace.razor.js";
    private const string InitializeMethod = "initialize";
    private const string ReadMethod = "readDraft";
    private const string SaveMethod = "saveDraft";
    private const string DisposeWriterMethod = "disposeWriter";
    private readonly string writerId = Guid.NewGuid().ToString("N");
    private Task<IJSObjectReference>? import;
    private Task<IJSObjectReference> GetModuleAsync()
        => import ??= js.InvokeAsync<IJSObjectReference>("import", ModulePath).AsTask();
    public async ValueTask<WorkspaceBrowserSession> InitializeAsync(string workspaceId)
    {
        var module = await GetModuleAsync();
        return await module.InvokeAsync<WorkspaceBrowserSession>(InitializeMethod, workspaceId);
    }
    public async ValueTask<WorkspaceBrowserDraft?> ReadDraftAsync(string workspaceId)
        => await (await GetModuleAsync()).InvokeAsync<WorkspaceBrowserDraft?>(ReadMethod, workspaceId);
    public async ValueTask SaveDraftAsync(string workspaceId, WorkspaceBrowserDraft value, long version)
        => await (await GetModuleAsync()).InvokeVoidAsync(SaveMethod, workspaceId, value, writerId, version);
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (import is null) return;
            var module = await import;
            await module.InvokeVoidAsync(DisposeWriterMethod, writerId);
            await module.DisposeAsync();
        }
        catch (JSDisconnectedException) { }
    }
}
