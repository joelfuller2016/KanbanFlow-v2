using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KanbanFlow.Data;
using KanbanFlow.Models;
using KanbanFlow.Models.IdeaWorkspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Services.IdeaWorkspaces;

public sealed partial class IdeaWorkspaceService
{
    private const int DefaultPageSize = 50;
    private const int MaximumPageSize = 500;
    private static readonly TimeSpan SnapshotLifetime = TimeSpan.FromMinutes(10);
    private const int MaximumSnapshots = 64;
    private static readonly object SnapshotLock = new();
    private static readonly Dictionary<string, QuerySnapshot> Snapshots = new(StringComparer.Ordinal);

    private sealed record QuerySnapshot(string Id, object Owner, string Type, string Scope, string Fingerprint,
        int PageSize, object[] Items, long? Revision, TimeProvider Clock,
        DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

    private sealed record QueryCursor(int Version, string Type, string SnapshotId, int Offset);
    private sealed record ChangeCursor(int Version, string Type, string WorkspaceId,
        int After, int LastId, int Watermark, int Limit, int Total, long Revision);

    public async Task<IdeaWorkspacePage<IdeaWorkspaceSummary>> ListAsync(
        string? kind = null, string? status = null, string? q = null, string? cursor = null,
        int? limit = null, CancellationToken ct = default)
    {
        var normalizedKind = NormalizeKind(kind);
        var normalizedStatus = NormalizeStatus(status);
        var search = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        var fingerprint = QueryFingerprint(normalizedKind, normalizedStatus, search?.ToUpperInvariant());
        if (cursor is not null)
            return ReadSnapshotPage<IdeaWorkspaceSummary>(cursor, "catalog", "catalog", fingerprint, limit);

        var size = PageSize(limit);
        var items = await ReadSnapshotAsync(async db =>
        {
            var ideas = await db.Ideas.AsNoTracking().ToListAsync(ct);
            var projects = await db.Projects.AsNoTracking().ToListAsync(ct);
            var states = await db.Set<IdeaWorkspaceState>().AsNoTracking().ToListAsync(ct);
            var stateById = states.ToDictionary(s => s.WorkspaceId, StringComparer.Ordinal);
            var sources = new Dictionary<string, IdeaWorkspaceSource>(StringComparer.Ordinal);
            foreach (var idea in ideas)
                sources.Add($"idea:{idea.Id}", new IdeaWorkspaceSource("idea", idea.Id, idea.Title, idea.Body, idea.Plan,
                    null, idea.Status.ToString(), idea.Tags, idea.Priority?.ToString(), idea.PromotedProjectId, idea.CreatedAt, idea.UpdatedAt));
            foreach (var project in projects)
                sources.Add($"project:{project.Id}", new IdeaWorkspaceSource("project", project.Id, project.Name,
                    project.Description, project.Goals, project.Readme, null, null, null, project.Id, project.CreatedAt, project.UpdatedAt));
            var projectIds = projects.Select(p => p.Id).ToHashSet();
            var documents = states.ToDictionary(s => s.WorkspaceId,
                s => Deserialize<IdeaWorkspaceDocument>(s.DocumentJson), StringComparer.Ordinal);
            var keys = sources.Keys.Concat(stateById.Keys).Distinct(StringComparer.Ordinal);
            var result = new List<IdeaWorkspaceSummary>();
            foreach (var workspaceId in keys)
            {
                var key = IdeaWorkspaceKey.Parse(workspaceId);
                sources.TryGetValue(workspaceId, out var source);
                stateById.TryGetValue(workspaceId, out var state);
                var original = state is null ? source! : Deserialize<IdeaWorkspaceSource>(state.OriginalSourceJson);
                var current = source ?? original;
                var projectId = state?.LinkedProjectId ?? source?.ProjectId ?? original.ProjectId;
                var linked = key.Kind == "idea" && projectId is not null;
                if (normalizedKind is not null &&
                    (normalizedKind == "linked" ? !linked : key.Kind != normalizedKind)) continue;
                if (normalizedStatus is not null && (key.Kind != "idea" || current.Status != normalizedStatus)) continue;
                documents.TryGetValue(workspaceId, out var document);
                IdeaWorkspaceDocument? sharedDocument = null;
                if (projectId is not null) documents.TryGetValue($"project:{projectId}", out sharedDocument);
                if (search is not null && !Matches(current, original, document, sharedDocument, search)) continue;
                var updatedAt = state is null || current.UpdatedAt > state.UpdatedAt ? current.UpdatedAt : state.UpdatedAt;
                result.Add(new IdeaWorkspaceSummary(workspaceId, key.Kind, key.SourceId, current.Title,
                    current.Status, current.Tags, projectId, linked, state?.Revision ?? 0, updatedAt,
                    source is null, $"/ideas/workspace/{key.Kind}/{key.SourceId}",
                    projectId is not null && projectIds.Contains(projectId.Value) ? $"/project/{projectId}" : null));
            }
            return result.OrderByDescending(i => i.UpdatedAt).ThenBy(i => i.WorkspaceId, StringComparer.Ordinal).ToArray();
        }, ct);
        return StartSnapshotPage(items, "catalog", "catalog", fingerprint, size, null);
    }

    public async Task<IdeaWorkspacePage<IdeaWorkspaceComponentSummary>> ListComponentsAsync(
        string workspaceId, string? cursor = null, int? limit = null, CancellationToken ct = default)
    {
        IdeaWorkspaceKey.Parse(workspaceId);
        var fingerprint = QueryFingerprint(workspaceId);
        if (cursor is not null)
            return ReadSnapshotPage<IdeaWorkspaceComponentSummary>(cursor, "components", workspaceId, fingerprint, limit);
        var size = PageSize(limit);
        var context = await GetAsync(workspaceId, ct);
        var items = context.Document.Components.OrderBy(c => c.Id, StringComparer.Ordinal)
            .Select(c => new IdeaWorkspaceComponentSummary(c.Id, c.Type, c.Title, c.TaskId,
                context.Document.Placements.Count(p => p.ComponentId == c.Id), c.Content.Length)).ToArray();
        return StartSnapshotPage(items, "components", workspaceId, fingerprint, size, context.Revision);
    }

    public async Task<IdeaWorkspaceComponentDetail> GetComponentAsync(
        string workspaceId, string componentId, CancellationToken ct = default)
    {
        var key = IdeaWorkspaceKey.Parse(workspaceId);
        ValidateIdentifier(componentId, "componentId");
        return await ReadSnapshotAsync(async db =>
        {
            var context = await LoadContextAsync(db, key, ct);
            var component = FindComponent(context.Document, componentId);
            IdeaWorkspaceTaskReference? task = null;
            if (component.Type == "task-reference" && component.TaskId is { } taskId)
            {
                var item = await db.Tasks.AsNoTracking().Include(t => t.Feature)
                    .SingleOrDefaultAsync(t => t.Id == taskId, ct);
                if (item is not null)
                    task = new IdeaWorkspaceTaskReference(item.Id, item.Name, item.Description,
                        item.Status.ToString(), item.Priority.ToString(), item.Assignee,
                        item.FeatureId, item.Feature.ProjectId, item.UpdatedAt);
            }
            return new IdeaWorkspaceComponentDetail(workspaceId, context.Revision, component,
                context.Document.Placements.Where(p => p.ComponentId == componentId).ToArray(),
                context.Document.Comments.Where(c => c.ComponentId == componentId).ToArray(),
                task, component.Type == "task-reference" && task is null);
        }, ct);
    }

    public async Task<IdeaWorkspaceChangePage> GetChangesAsync(
        string workspaceId, string? cursor = null, int? afterActivityId = null,
        int? limit = null, CancellationToken ct = default)
    {
        var key = IdeaWorkspaceKey.Parse(workspaceId);
        ChangeCursor? continuation = null;
        if (cursor is not null)
        {
            continuation = DecodeCursor<ChangeCursor>(cursor);
            if (continuation.Version != 1 || continuation.Type != "changes" || continuation.WorkspaceId != workspaceId)
                throw Invalid("cursor_mismatch", "This cursor belongs to another read operation or workspace.");
            if (limit is not null && limit != continuation.Limit || afterActivityId is not null && afterActivityId != continuation.After)
                throw Invalid("cursor_mismatch", "Keep the initial page size and activity origin when continuing this history.");
            if (continuation.After < 0 || continuation.LastId < continuation.After ||
                continuation.Watermark < continuation.LastId || continuation.Total < 0 || continuation.Revision < 0)
                throw Invalid("invalid_cursor", "The history cursor contains an invalid position.");
        }
        var size = PageSize(continuation?.Limit ?? limit);
        var start = continuation?.After ?? afterActivityId ?? 0;
        if (start < 0) throw Invalid("invalid_activity_cursor", "Activity position cannot be negative.");
        return await ReadSnapshotAsync(async db =>
        {
            var context = await LoadContextAsync(db, key, ct);
            var watermark = continuation?.Watermark ?? context.LastActivityId;
            if (start > watermark || watermark > context.LastActivityId)
                throw Invalid("invalid_activity_cursor", "Activity position is outside this workspace's saved history.");
            var query = db.ActivityEvents.AsNoTracking()
                .Where(a => a.EntityType == key.AuditEntityType && a.EntityId == key.SourceId && a.Id > start && a.Id <= watermark);
            var total = await query.CountAsync(ct);
            if (continuation is not null && total != continuation.Total)
                throw Conflict("history_changed", "Saved history changed during traversal. Restart this history read.");
            var lastId = continuation?.LastId ?? start;
            var rows = await query.Where(a => a.Id > lastId).OrderBy(a => a.Id).Take(size + 1)
                .Select(a => new IdeaWorkspaceChange(a.Id, a.EventType, a.Actor,
                    a.BeforeJson, a.AfterJson, a.Message, a.CreatedAt)).ToListAsync(ct);
            var more = rows.Count > size;
            var items = rows.Take(size).ToArray();
            var revision = continuation?.Revision ?? context.Revision;
            var next = more ? EncodeCursor(new ChangeCursor(1, "changes", workspaceId, start,
                items[^1].Id, watermark, size, total, revision)) : null;
            return new IdeaWorkspaceChangePage(items, total, next, size, watermark, start, revision);
        }, ct);
    }

    private async Task<T> ReadSnapshotAsync<T>(Func<AppDbContext, Task<T>> read, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.Database.OpenConnectionAsync(ct);
        if (db.Database.GetDbConnection() is not SqliteConnection connection)
            throw new InvalidOperationException("Idea workspaces require the application's SQLite provider.");
        await using var sqliteTransaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: true);
        await using var transaction = await db.Database.UseTransactionAsync(sqliteTransaction, ct);
        var result = await read(db);
        await sqliteTransaction.CommitAsync(ct);
        return result;
    }

    private static async Task<IdeaWorkspaceContext> LoadContextAsync(AppDbContext db, IdeaWorkspaceKey key, CancellationToken ct)
    {
        var state = await db.Set<IdeaWorkspaceState>().AsNoTracking().SingleOrDefaultAsync(s => s.WorkspaceId == key.WorkspaceId, ct);
        var source = await ReadSourceAsync(db, key, ct);
        if (state is null && source is null)
            throw NotFound("workspace_not_found", $"Workspace '{key.WorkspaceId}' does not exist.");
        return await BuildContextAsync(db, key, state, source, ct);
    }

    private IdeaWorkspacePage<T> StartSnapshotPage<T>(T[] items, string type, string scope,
        string fingerprint, int size, long? revision)
    {
        if (items.Length <= size)
            return new IdeaWorkspacePage<T>(items, items.Length, null, size, revision);
        var now = _timeProvider.GetUtcNow();
        var snapshot = new QuerySnapshot(Guid.NewGuid().ToString("N"), _dbFactory, type, scope,
            fingerprint, size, items.Cast<object>().ToArray(), revision, _timeProvider, now, now + SnapshotLifetime);
        lock (SnapshotLock)
        {
            RemoveExpiredSnapshots();
            while (Snapshots.Count >= MaximumSnapshots)
            {
                var oldest = Snapshots.Values.OrderBy(s => s.CreatedAt).First();
                Snapshots.Remove(oldest.Id);
            }
            Snapshots.Add(snapshot.Id, snapshot);
        }
        return SnapshotPage<T>(snapshot, 0);
    }

    private IdeaWorkspacePage<T> ReadSnapshotPage<T>(string cursor, string type, string scope,
        string fingerprint, int? limit)
    {
        var parsed = DecodeCursor<QueryCursor>(cursor);
        if (parsed.Version != 1 || parsed.Type != type || string.IsNullOrEmpty(parsed.SnapshotId) || parsed.Offset < 1)
            throw Invalid("cursor_mismatch", "This cursor belongs to another read operation or has an invalid position.");
        lock (SnapshotLock)
        {
            RemoveExpiredSnapshots();
            if (!Snapshots.TryGetValue(parsed.SnapshotId, out var snapshot))
                throw Conflict("cursor_expired", "The read snapshot expired, was evicted or belongs to a previous process. Restart this read.");
            if (!ReferenceEquals(snapshot.Owner, _dbFactory) || snapshot.Type != type || snapshot.Scope != scope ||
                snapshot.Fingerprint != fingerprint || limit is not null && limit != snapshot.PageSize)
                throw Invalid("cursor_mismatch", "Keep the initial workspace, filters and page size when continuing this snapshot.");
            if (parsed.Offset >= snapshot.Items.Length || parsed.Offset % snapshot.PageSize != 0)
                throw Invalid("invalid_cursor", "The snapshot cursor contains an invalid page position.");
            return SnapshotPage<T>(snapshot, parsed.Offset);
        }
    }

    private static IdeaWorkspacePage<T> SnapshotPage<T>(QuerySnapshot snapshot, int offset)
    {
        var items = snapshot.Items.Skip(offset).Take(snapshot.PageSize).Cast<T>().ToArray();
        var nextOffset = offset + items.Length;
        var next = nextOffset < snapshot.Items.Length
            ? EncodeCursor(new QueryCursor(1, snapshot.Type, snapshot.Id, nextOffset)) : null;
        return new IdeaWorkspacePage<T>(items, snapshot.Items.Length, next, snapshot.PageSize,
            snapshot.Revision, snapshot.Id, snapshot.ExpiresAt);
    }

    private static void RemoveExpiredSnapshots()
    {
        foreach (var snapshot in Snapshots.Values.Where(s => s.Clock.GetUtcNow() >= s.ExpiresAt).ToArray())
            Snapshots.Remove(snapshot.Id);
    }

    private static int PageSize(int? value)
    {
        var result = value ?? DefaultPageSize;
        if (result is < 1 or > MaximumPageSize)
            throw Invalid("invalid_limit", $"Page limit must be between 1 and {MaximumPageSize}.");
        return result;
    }

    private static string? NormalizeKind(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) return null;
        var normalized = kind.Trim().ToLowerInvariant();
        return normalized is "idea" or "project" or "linked" ? normalized
            : throw Invalid("invalid_kind", "Kind must be idea, project or linked.");
    }

    private static string? NormalizeStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return null;
        return Enum.GetNames<IdeaStatus>().SingleOrDefault(s => s.Equals(status.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw Invalid("invalid_status", "Status must be a supported IdeaStatus name.");
    }

    private static bool Matches(IdeaWorkspaceSource source, IdeaWorkspaceSource original,
        IdeaWorkspaceDocument? document, IdeaWorkspaceDocument? shared, string search)
    {
        bool Has(string? value) => value?.Contains(search, StringComparison.OrdinalIgnoreCase) == true;
        bool SourceHas(IdeaWorkspaceSource item) => Has(item.Title) || Has(item.Body) || Has(item.Plan) || Has(item.Readme) || Has(item.Tags);
        bool DocumentHas(IdeaWorkspaceDocument? item) => item is not null &&
            (item.Components.Any(c => Has(c.Title) || Has(c.Content) || Has(c.Url)) || item.Comments.Any(c => Has(c.Text)) ||
             item.PlanRevisions.Any(p => Has(p.Title) || p.Items.Any(i => Has(i.Title) || Has(i.Description) || i.AcceptanceCriteria.Any(Has))) ||
             item.Checkpoints.Any(c => Has(c.Objective) || c.NextActions.Any(Has) || c.OpenQuestions.Any(Has)));
        return SourceHas(source) || SourceHas(original) || DocumentHas(document) || DocumentHas(shared);
    }

    private static string QueryFingerprint(params string?[] values)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(values, JsonOptions)));

    private static string EncodeCursor<T>(T value)
        => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static T DecodeCursor<T>(string cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > 2048)
            throw Invalid("invalid_cursor", "Cursor is malformed or exceeds the supported size.");
        try
        {
            var encoded = cursor.Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + (4 - encoded.Length % 4) % 4, '=');
            return JsonSerializer.Deserialize<T>(Convert.FromBase64String(encoded), CommandJsonOptions)
                ?? throw Invalid("invalid_cursor", "Cursor cannot be empty.");
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw Invalid("invalid_cursor", "Cursor is malformed or uses an unsupported contract.");
        }
    }
}
