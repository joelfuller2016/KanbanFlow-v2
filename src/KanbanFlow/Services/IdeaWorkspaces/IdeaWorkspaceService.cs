using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using KanbanFlow.Data;
using KanbanFlow.Models;
using KanbanFlow.Models.IdeaWorkspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Services.IdeaWorkspaces;

/// <summary>Shared audited save boundary for canvas content, placement and resumable context.</summary>
public sealed partial class IdeaWorkspaceService : IIdeaWorkspaceService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions CommandJsonOptions = new(JsonOptions)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    public static IReadOnlyList<string> ComponentTypes { get; } =
        ["frame", "note", "markdown", "research", "image", "question", "decision", "checklist", "table", "plan", "task-reference"];

    public IdeaWorkspaceService(IDbContextFactory<AppDbContext> dbFactory)
        => _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));

    public async Task<IdeaWorkspaceContext> GetAsync(string workspaceId, CancellationToken ct = default)
    {
        var key = IdeaWorkspaceKey.Parse(workspaceId);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.Database.OpenConnectionAsync(ct);
        if (db.Database.GetDbConnection() is not SqliteConnection connection)
            throw new InvalidOperationException("Idea workspaces require the application's SQLite provider.");
        await using var sqliteTransaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: true);
        await using var transaction = await db.Database.UseTransactionAsync(sqliteTransaction, ct);
        var state = await db.Set<IdeaWorkspaceState>().AsNoTracking()
            .SingleOrDefaultAsync(s => s.WorkspaceId == workspaceId, ct);
        var source = await ReadSourceAsync(db, key, ct);
        if (state is null && source is null)
            throw NotFound("workspace_not_found", $"Workspace '{workspaceId}' does not exist.");
        var context = await BuildContextAsync(db, key, state, source, ct);
        await sqliteTransaction.CommitAsync(ct);
        return context;
    }

    public async Task<IdeaWorkspaceMutationResult> ExecuteAsync(
        string workspaceId, IdeaWorkspaceCommand command, CancellationToken ct = default)
    {
        var key = IdeaWorkspaceKey.Parse(workspaceId);
        ArgumentNullException.ThrowIfNull(command);
        ValidateIdentifier(command.OperationId, "OperationId", 200);
        ValidateActor(command.Actor);
        if (command.ExpectedRevision < 0)
            throw Invalid("invalid_revision", "ExpectedRevision cannot be negative.");
        if (command.Payload.ValueKind != JsonValueKind.Object)
            throw Invalid("invalid_payload", "Payload must be a JSON object.");
        ValidateJsonProperties(command.Payload);
        var requestHash = HashCommand(workspaceId, command);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.Database.OpenConnectionAsync(ct);
        if (db.Database.GetDbConnection() is not SqliteConnection connection)
            throw new InvalidOperationException("Idea workspaces require the application's SQLite provider.");

        ct.ThrowIfCancellationRequested();
        try
        {
            // IMMEDIATE obtains the SQLite writer reservation before reading revision or receipts.
            // EF's context joins the same transaction, so audit/receipt failure rolls back the state.
            await using var sqliteTransaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
            await using var transaction = await db.Database.UseTransactionAsync(sqliteTransaction, ct);
            var previousReceipt = await db.Set<IdeaWorkspaceReceipt>().AsNoTracking()
                .SingleOrDefaultAsync(r => r.WorkspaceId == workspaceId && r.OperationId == command.OperationId, ct);
            if (previousReceipt is not null)
            {
                if (previousReceipt.RequestHash != requestHash)
                    throw Conflict("operation_conflict", "This operation ID was already used for different command content.");
                return Deserialize<IdeaWorkspaceMutationResult>(previousReceipt.ResultJson) with { Replayed = true };
            }

            var state = await db.Set<IdeaWorkspaceState>().SingleOrDefaultAsync(s => s.WorkspaceId == workspaceId, ct);
            var source = await ReadSourceAsync(db, key, ct);
            if (state is null && source is null)
                throw NotFound("workspace_not_found", $"Workspace '{workspaceId}' does not exist.");
            var revision = state?.Revision ?? 0;
            if (revision != command.ExpectedRevision)
                throw Conflict("revision_conflict", $"Workspace revision is {revision}; expected {command.ExpectedRevision}. Reload before applying this change.");

            var original = state is null ? source! : Deserialize<IdeaWorkspaceSource>(state.OriginalSourceJson);
            var document = state is null ? CreateDocument(source!) : Deserialize<IdeaWorkspaceDocument>(state.DocumentJson);
            var projectId = state?.LinkedProjectId ?? source?.ProjectId ?? original.ProjectId;
            var before = state is null ? null : SerializeSnapshot(state, document);
            var now = DateTime.UtcNow;
            var affected = await ApplyCommandAsync(db, key, document, projectId, command, now, ct);
            ValidateDocument(document);

            if (state is null)
            {
                state = new IdeaWorkspaceState
                {
                    WorkspaceId = workspaceId,
                    Kind = key.Kind,
                    SourceId = key.SourceId,
                    LinkedProjectId = projectId,
                    Revision = 0,
                    OriginalSourceJson = Serialize(original),
                    DocumentJson = "",
                    CreatedAt = now,
                    UpdatedAt = now
                };
                db.Set<IdeaWorkspaceState>().Add(state);
            }
            state.Revision = checked(revision + 1);
            state.DocumentJson = Serialize(document);
            state.UpdatedAt = now;
            var activity = new ActivityEvent
            {
                ProjectId = projectId ?? 0,
                EntityType = key.AuditEntityType,
                EntityId = key.SourceId,
                Actor = command.Actor,
                EventType = "IdeaWorkspaceChanged",
                BeforeJson = before,
                AfterJson = SerializeSnapshot(state, document),
                Message = command.Action,
                CreatedAt = now
            };
            db.ActivityEvents.Add(activity);
            await db.SaveChangesAsync(ct);
            var result = new IdeaWorkspaceMutationResult(workspaceId, state.Revision, command.OperationId,
                command.Action, affected.ComponentId, affected.PlacementId, activity.Id);
            db.Set<IdeaWorkspaceReceipt>().Add(new IdeaWorkspaceReceipt
            {
                WorkspaceId = workspaceId,
                OperationId = command.OperationId,
                RequestHash = requestHash,
                ResultJson = Serialize(result),
                Actor = command.Actor,
                CreatedAt = now
            });
            await db.SaveChangesAsync(ct);
            await sqliteTransaction.CommitAsync(ct);
            return result;
        }
        catch (JsonException)
        {
            throw Invalid("invalid_payload", "Payload contains an invalid value for this command.");
        }
        catch (DbUpdateConcurrencyException)
        {
            throw Conflict("revision_conflict", "The workspace changed while this command was being saved. Reload and retry.");
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
        {
            throw Conflict("workspace_busy", "Another workspace writer is active. Retry this operation ID after reloading.");
        }
    }

    private static async Task<IdeaWorkspaceContext> BuildContextAsync(
        AppDbContext db, IdeaWorkspaceKey key, IdeaWorkspaceState? state, IdeaWorkspaceSource? source, CancellationToken ct)
    {
        var original = state is null ? source! : Deserialize<IdeaWorkspaceSource>(state.OriginalSourceJson);
        var document = state is null ? CreateDocument(original) : Deserialize<IdeaWorkspaceDocument>(state.DocumentJson);
        var projectId = state?.LinkedProjectId ?? source?.ProjectId ?? original.ProjectId;
        var projectExists = projectId is not null && await db.Projects.AsNoTracking().AnyAsync(p => p.Id == projectId, ct);
        var lastActivityId = await db.ActivityEvents.AsNoTracking()
            .Where(a => a.EntityType == key.AuditEntityType && a.EntityId == key.SourceId)
            .Select(a => (int?)a.Id).MaxAsync(ct) ?? 0;
        return new IdeaWorkspaceContext(key.WorkspaceId, key.Kind, key.SourceId, source?.Title ?? original.Title,
            state?.Revision ?? 0, source, original, source is null, projectId, projectId is not null && !projectExists,
            $"/ideas/workspace/{key.Kind}/{key.SourceId}", projectExists ? $"/project/{projectId}" : null,
            projectId is null ? null : $"project:{projectId}", state?.UpdatedAt ?? source?.UpdatedAt ?? original.UpdatedAt,
            lastActivityId, document);
    }

    private static async Task<IdeaWorkspaceSource?> ReadSourceAsync(AppDbContext db, IdeaWorkspaceKey key, CancellationToken ct)
    {
        if (key.Kind == "idea")
        {
            var idea = await db.Ideas.AsNoTracking().SingleOrDefaultAsync(i => i.Id == key.SourceId, ct);
            return idea is null ? null : new IdeaWorkspaceSource("idea", idea.Id, idea.Title, idea.Body, idea.Plan,
                null, idea.Status.ToString(), idea.Tags, idea.Priority?.ToString(), idea.PromotedProjectId, idea.CreatedAt, idea.UpdatedAt);
        }
        var project = await db.Projects.AsNoTracking().SingleOrDefaultAsync(p => p.Id == key.SourceId, ct);
        return project is null ? null : new IdeaWorkspaceSource("project", project.Id, project.Name,
            project.Description, project.Goals, project.Readme, null, null, null, project.Id, project.CreatedAt, project.UpdatedAt);
    }

    private static IdeaWorkspaceDocument CreateDocument(IdeaWorkspaceSource source)
    {
        var document = new IdeaWorkspaceDocument();
        var names = new[] { "Brief", "Research", "Explore", "Decisions", "Plan" };
        for (var i = 0; i < names.Length; i++)
        {
            var id = "frame-" + names[i].ToLowerInvariant();
            document.Components.Add(new IdeaWorkspaceComponent
            {
                Id = id, Type = "frame", Title = names[i], CreatedBy = "Source",
                CreatedAt = source.CreatedAt, UpdatedAt = source.UpdatedAt
            });
            document.Placements.Add(new IdeaWorkspacePlacement
            {
                Id = "place-" + id, ComponentId = id, X = (i % 2) * 680, Y = (i / 2) * 460,
                Width = 640, Height = 420, Order = i
            });
        }
        if (source.Body is not null)
            AddSourceComponent(document, "source-brief", "Original brief", source.Body, "frame-brief", source);
        if (source.Plan is not null)
            AddSourceComponent(document, "source-plan", "Original plan", source.Plan, "frame-plan", source);
        return document;
    }

    private static void AddSourceComponent(IdeaWorkspaceDocument document, string id, string title,
        string content, string parentId, IdeaWorkspaceSource source)
    {
        var parent = document.Placements.Single(p => p.ComponentId == parentId);
        document.Components.Add(new IdeaWorkspaceComponent
        {
            Id = id, Type = "markdown", Title = title, Content = content, CreatedBy = "Source",
            CreatedAt = source.CreatedAt, UpdatedAt = source.UpdatedAt
        });
        document.Placements.Add(new IdeaWorkspacePlacement
        {
            Id = "place-" + id, ComponentId = id, ParentId = parent.Id,
            X = 20, Y = 60, Width = 580, Height = 300, Order = 0
        });
    }

    private static string SerializeSnapshot(IdeaWorkspaceState state, IdeaWorkspaceDocument document)
        => Serialize(new
        {
            state.WorkspaceId, state.Kind, state.SourceId, state.LinkedProjectId, state.Revision,
            OriginalSource = Deserialize<IdeaWorkspaceSource>(state.OriginalSourceJson),
            Document = document, state.CreatedAt, state.UpdatedAt
        });

    internal static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
    internal static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new InvalidOperationException("Stored idea workspace data is null.");

    private static string HashCommand(string workspaceId, IdeaWorkspaceCommand command)
    {
        var envelope = JsonSerializer.SerializeToElement(new
        {
            workspaceId, command.OperationId, command.ExpectedRevision, command.Action,
            command.Payload, command.Actor
        }, JsonOptions);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteCanonicalJson(writer, envelope);
        }
        return Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
    }

    private static void WriteCanonicalJson(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonicalJson(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray()) WriteCanonicalJson(writer, item);
            writer.WriteEndArray();
        }
        else
        {
            // String values (including authored Markdown whitespace) are never normalized.
            value.WriteTo(writer);
        }
    }

    private static IdeaWorkspaceException Invalid(string code, string message) => new(400, code, message);
    private static IdeaWorkspaceException NotFound(string code, string message) => new(404, code, message);
    private static IdeaWorkspaceException Conflict(string code, string message) => new(409, code, message);
}
