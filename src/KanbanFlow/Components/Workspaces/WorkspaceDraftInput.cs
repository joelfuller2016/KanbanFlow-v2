using Microsoft.AspNetCore.Components;

namespace KanbanFlow.Components.Workspaces;

/// <summary>Keeps sequential field edits local while a parent awaits browser persistence.</summary>
public sealed class WorkspaceDraftInput<T>(T initial)
{
    public T Value { get; private set; } = initial;
    private string? identity;
    private object? authority;
    private int outstandingChanges;
    private int generation;

    public void Receive(T value, string nextIdentity, object? authorityStamp = null)
    {
        if (identity != nextIdentity)
        {
            generation++;
            outstandingChanges = 0;
        }
        // A refresh/reapply is authoritative even when an older field persistence call is still awaiting JS.
        if (identity != nextIdentity || !Equals(authority, authorityStamp) || outstandingChanges == 0) Value = value;
        identity = nextIdentity;
        authority = authorityStamp;
    }

    public async Task ChangeAsync(T value, EventCallback<T> onChanged)
    {
        var currentGeneration = generation;
        Value = value;
        outstandingChanges++;
        try { await onChanged.InvokeAsync(value); }
        finally { if (currentGeneration == generation) outstandingChanges--; }
    }
}
