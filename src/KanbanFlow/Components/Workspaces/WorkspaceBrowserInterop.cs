using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.JSInterop;

namespace KanbanFlow.Components.Workspaces;

public sealed class WorkspaceBrowserInterop(IJSRuntime js) : IAsyncDisposable
{
    private const string ModulePath = "./Components/Pages/IdeaWorkspace.razor.js";
    private const string InitializeMethod = "initialize";
    private const string ReadMethod = "readDraft";
    private const string SaveMethod = "saveDraft";
    private const string DisposeWriterMethod = "disposeWriter";
    private const string ReadStoredDraftMethod = "readStoredDraft";
    private const string PreserveDraftMethod = "preserveAndClearDraft";
    private static readonly JsonSerializerOptions DraftJson = new(JsonSerializerDefaults.Web)
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly string writerId = Guid.NewGuid().ToString("N");
    private Task<IJSObjectReference>? import;
    private Task<IJSObjectReference> GetModuleAsync()
        => import ??= js.InvokeAsync<IJSObjectReference>("import", ModulePath).AsTask();
    public async ValueTask<WorkspaceBrowserSession> InitializeAsync(string workspaceId)
    {
        var module = await GetModuleAsync();
        var value = await module.InvokeAsync<JsonElement>(InitializeMethod, workspaceId);
        var session = value.Deserialize<WorkspaceBrowserSession>(DraftJson)
            ?? throw new JsonException("The browser workspace session is missing.");
        if (string.IsNullOrWhiteSpace(session.ClientId) || string.IsNullOrWhiteSpace(session.SessionId))
            throw new JsonException("The browser workspace session identity is missing.");
        session.Draft?.Validate();
        return session;
    }
    public async ValueTask<WorkspaceBrowserDraft?> ReadDraftAsync(string workspaceId)
    {
        var value = await (await GetModuleAsync()).InvokeAsync<JsonElement>(ReadMethod, workspaceId);
        var draft = value.ValueKind == JsonValueKind.Null ? null : value.Deserialize<WorkspaceBrowserDraft>(DraftJson);
        draft?.Validate();
        return draft;
    }
    public async ValueTask<string?> ReadStoredDraftAsync(string workspaceId)
        => await (await GetModuleAsync()).InvokeAsync<string?>(ReadStoredDraftMethod, workspaceId);
    public async ValueTask<WorkspaceBrowserDraftRecovery> PreserveAndClearDraftAsync(string workspaceId)
        => await (await GetModuleAsync()).InvokeAsync<WorkspaceBrowserDraftRecovery>(PreserveDraftMethod, workspaceId);
    public async ValueTask SaveDraftAsync(string workspaceId, WorkspaceBrowserDraft value, long version)
        => await (await GetModuleAsync()).InvokeVoidAsync(SaveMethod, workspaceId, value, writerId, version);
    public async ValueTask DisposeAsync()
    {
        var pendingImport = import;
        import = null;
        IJSObjectReference? module = null;
        try
        {
            if (pendingImport is null) return;
            module = await pendingImport;
            await module.InvokeVoidAsync(DisposeWriterMethod, writerId);
        }
        catch (JSDisconnectedException) { }
        catch (JSException) { }
        catch (OperationCanceledException) { }
        finally
        {
            if (module is not null)
            {
                try { await module.DisposeAsync(); }
                catch (JSDisconnectedException) { }
                catch (JSException) { }
                catch (OperationCanceledException) { }
            }
        }
    }
}
