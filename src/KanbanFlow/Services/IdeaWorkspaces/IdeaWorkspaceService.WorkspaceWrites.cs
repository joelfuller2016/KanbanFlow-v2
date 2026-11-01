using KanbanFlow.Data;
using KanbanFlow.Models;
using KanbanFlow.Models.IdeaWorkspaces;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Services.IdeaWorkspaces;

public sealed partial class IdeaWorkspaceService
{
    private sealed class WorkspaceWrite(
        IdeaWorkspaceKey key, IdeaWorkspaceState state, IdeaWorkspaceSource? source,
        IdeaWorkspaceDocument document, string? before, bool isNew)
    {
        public IdeaWorkspaceKey Key { get; } = key;
        public IdeaWorkspaceState State { get; } = state;
        public IdeaWorkspaceSource? Source { get; set; } = source;
        public IdeaWorkspaceDocument Document { get; } = document;
        public string? Before { get; } = before;
        public long OriginalRevision { get; } = state.Revision;
        public bool IsNew { get; } = isNew;
        public bool Changed { get; set; }
        public ActivityEvent? Activity { get; set; }
        public int? ProjectId => Key.Kind == "project" ? Key.SourceId : State.LinkedProjectId ?? Source?.ProjectId;
    }

    /// <summary>All affected workspaces share the caller's reserved SQLite transaction.</summary>
    private sealed class WorkspaceWrites(AppDbContext db, IdeaWorkspaceCommand command, DateTime now)
    {
        private readonly Dictionary<string, WorkspaceWrite> writes = new(StringComparer.Ordinal);
        public AppDbContext Db { get; } = db;
        public IdeaWorkspaceCommand Command { get; } = command;
        public DateTime Now { get; } = now;
        public IdeaWorkspacePublicationResult? Publication { get; set; }

        public async Task<WorkspaceWrite> LoadAsync(IdeaWorkspaceKey key, CancellationToken ct)
        {
            if (writes.TryGetValue(key.WorkspaceId, out var current)) return current;
            var state = await Db.Set<IdeaWorkspaceState>().SingleOrDefaultAsync(s => s.WorkspaceId == key.WorkspaceId, ct);
            var source = await ReadSourceAsync(Db, key, ct);
            if (state is null && source is null)
                throw NotFound("workspace_not_found", $"Workspace '{key.WorkspaceId}' does not exist.");
            var isNew = state is null;
            var document = state is null ? CreateDocument(source!) : Deserialize<IdeaWorkspaceDocument>(state.DocumentJson);
            var before = state is null ? null : SerializeSnapshot(state, document);
            if (key.Kind == "project" && CurrentCanonicalPlan(document, key.SourceId) is { } accepted)
            {
                var storedAccepted = document.PlanRevisions.Single(p => p.Id == accepted.Id);
                for (var index = 0; index < storedAccepted.Items.Count; index++)
                    storedAccepted.Items[index].Provenance ??= accepted.Items[index].Provenance;
            }
            if (state is null)
            {
                state = new IdeaWorkspaceState
                {
                    WorkspaceId = key.WorkspaceId, Kind = key.Kind, SourceId = key.SourceId,
                    LinkedProjectId = source!.ProjectId, Revision = 0,
                    OriginalSourceJson = Serialize(source), DocumentJson = Serialize(document),
                    CreatedAt = Now, UpdatedAt = Now
                };
                Db.Set<IdeaWorkspaceState>().Add(state);
            }
            var result = new WorkspaceWrite(key, state, source, document, before, isNew);
            writes.Add(key.WorkspaceId, result);
            ObserveSource(result);
            return result;
        }

        private void ObserveSource(WorkspaceWrite workspace)
        {
            if (workspace.Source is not { } observed) return;
            var last = workspace.Document.SourceSnapshots.LastOrDefault()?.Source
                ?? Deserialize<IdeaWorkspaceSource>(workspace.State.OriginalSourceJson);
            if (observed == last) return;
            workspace.Document.SourceSnapshots.Add(new(NewIdentifier(), observed, Command.Actor, Now));
            workspace.Changed = true;
        }

        public async Task<WorkspaceWrite?> CaptureProjectAsync(int projectId, bool required, CancellationToken ct)
        {
            var key = new IdeaWorkspaceKey("project", projectId);
            var source = await ReadSourceAsync(Db, key, ct);
            if (source is null)
            {
                if (required) throw NotFound("project_not_found", $"Project {projectId} no longer exists. Saved workspaces remain available; the project is not recreated.");
                return null;
            }
            var project = await LoadAsync(key, ct);
            if (project.IsNew) project.Changed = true;
            return project;
        }

        public WorkspaceWrite? FindProject(int? projectId)
            => projectId is { } id ? writes.GetValueOrDefault($"project:{id}") : null;

        public async Task SaveAsync(CancellationToken ct)
        {
            // Association can also update the legacy idea source after the initial observation.
            foreach (var change in writes.Values) ObserveSource(change);
            foreach (var change in writes.Values.Where(w => w.Changed))
            {
                ValidateDocument(change.Document);
                change.State.Revision = checked(change.OriginalRevision + 1);
                change.State.DocumentJson = Serialize(change.Document);
                change.State.UpdatedAt = Now;
                change.Activity = new ActivityEvent
                {
                    ProjectId = change.ProjectId ?? 0, EntityType = change.Key.AuditEntityType,
                    EntityId = change.Key.SourceId, Actor = Command.Actor,
                    EventType = "IdeaWorkspaceChanged", BeforeJson = change.Before,
                    AfterJson = SerializeSnapshot(change.State, change.Document),
                    Message = Command.Action, CreatedAt = Now
                };
                Db.ActivityEvents.Add(change.Activity);
            }
            await Db.SaveChangesAsync(ct);
        }
    }
}
