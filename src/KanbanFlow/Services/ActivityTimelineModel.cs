using System.Globalization;
using System.Text;
using System.Text.Json;
using KanbanFlow.Models;

namespace KanbanFlow.Services;

/// <summary>
/// What a single timeline row MEANS, independent of which table it came from.
/// The filter chips select on meaning, not on source: an old system-written
/// <see cref="CommentType.StatusChange"/> comment is a <see cref="StatusChange"/>
/// row exactly like a <c>TaskStatusChanged</c> activity event is.
/// </summary>
public enum TimelineEntryKind
{
    Comment,
    StatusChange,
    FieldChange
}

/// <summary>Which rows the user asked to see. Mirrors the chips in ActivityTimeline.razor.</summary>
public enum TimelineFilter
{
    All,
    Comments,
    Status,
    FieldChanges
}

/// <summary>One rendered "Status: Todo → InProgress" line. Values are already display-safe.</summary>
/// <param name="Field">Human label, e.g. "Status". Comes from a fixed whitelist — never from the JSON.</param>
/// <param name="Before">Display value, "—" when absent/empty.</param>
/// <param name="After">Display value, "—" when absent/empty.</param>
public sealed record TimelineFieldChange(string Field, string Before, string After);

/// <summary>
/// A merged timeline row. Everything on it is display-ready; the component renders
/// these and nothing else, which is what keeps raw <c>BeforeJson</c>/<c>AfterJson</c>
/// out of the UI by construction rather than by discipline.
/// </summary>
public sealed record TimelineEntry
{
    /// <summary>Stable @key: "c-{commentId}" or "a-{eventId}".</summary>
    public required string Key { get; init; }

    public required TimelineEntryKind Kind { get; init; }

    /// <summary>UTC instant, exactly as stored. Rendering decides the clock — see ActivityTimeline.razor.</summary>
    public required DateTime CreatedAtUtc { get; init; }

    /// <summary>Never blank: a missing actor is "System", which is what every writer stores anyway.</summary>
    public required string Actor { get; init; }

    /// <summary>Badge text, e.g. "Note", "Status", "Created".</summary>
    public required string Label { get; init; }

    /// <summary>Markdown body — comments only. Null for activity events.</summary>
    public string? Body { get; init; }

    /// <summary>Derived field diffs. Empty when nothing could be derived, in which case <see cref="Summary"/> is used.</summary>
    public IReadOnlyList<TimelineFieldChange> Changes { get; init; } = [];

    /// <summary>Fallback line (the event's <c>Message</c>) used when no diff is derivable.</summary>
    public string? Summary { get; init; }

    /// <summary>Set for comment rows only; drives the existing border/badge styling.</summary>
    public CommentType? CommentType { get; init; }

    /// <summary>Set for activity rows only, e.g. "TaskStatusChanged".</summary>
    public string? EventType { get; init; }

    /// <summary>True when this row came from the ActivityEvents table.</summary>
    public bool IsActivityEvent => EventType is not null;
}

/// <summary>Per-chip counts, resolved once at merge time (never in a render-path property).</summary>
public sealed record TimelineCounts(int All, int Comments, int Status, int FieldChanges);

/// <summary>
/// The merged timeline plus how much of it was DISCARDED by <see cref="ActivityTimelineModel.MaxEntries"/>.
/// Without this the component cannot tell "this task has no more history" from "we stopped
/// looking", and a silent slice of a longer trail is exactly the dishonesty #328 exists to end
/// (the underlying lifetime cap is tracked by #332).
/// </summary>
/// <param name="Entries">The rows to render — newest first, at most <see cref="ActivityTimelineModel.MaxEntries"/>.</param>
/// <param name="TotalAvailable">How many rows the merge produced before the cap was applied.</param>
public sealed record TimelineResult(IReadOnlyList<TimelineEntry> Entries, int TotalAvailable)
{
    public bool IsTruncated => TotalAvailable > Entries.Count;
}

/// <summary>
/// Pure merge + diff logic for the task history timeline (#328). Deliberately free of any
/// Blazor / EF / IO dependency: that is what makes the differ mutation-provable in xUnit
/// without a browser, and what lets ONE implementation back BOTH task surfaces
/// (TaskDetailPanel = card click, EditItemModal = outline sidebar edit).
/// </summary>
public static class ActivityTimelineModel
{
    /// <summary>Blank actor/author displays as this. Every writer in TaskService already stores it.</summary>
    public const string UnknownActor = "System";

    /// <summary>Displayed when a value is null, absent, or empty.</summary>
    public const string EmptyValue = "—";

    /// <summary>
    /// Bigger than this and we do not even parse — fall back to the event's Message.
    /// A task Description is unbounded, so BeforeJson/AfterJson are unbounded too, and
    /// this runs inside a Blazor Server render for every row on screen.
    /// </summary>
    public const int MaxJsonChars = 32 * 1024;

    /// <summary>Per-value display cap. Descriptions are the reason this exists.</summary>
    public const int MaxValueChars = 80;

    /// <summary>Per-event cap on rendered change lines.</summary>
    public const int MaxChangesPerEvent = 8;

    /// <summary>Total rows the timeline will build. Newest are kept.</summary>
    public const int MaxEntries = 500;

    /// <summary>
    /// How far apart a comment and its activity event may be and still be treated as the
    /// same recorded action. They are written in one SaveChanges, microseconds apart, so
    /// this is generous by three orders of magnitude — deliberately, because being wrong
    /// in the other direction double-renders every status change a user ever made.
    /// </summary>
    public static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(2);

    // ── The differ's WHITELIST ───────────────────────────────────────────────────────────
    // A closed set of keys we know how to label. Anything else in the JSON — a field added
    // later, renamed, or an entirely foreign payload — is IGNORED rather than rendered, so
    // there is no path by which unlabelled JSON reaches the user. Keys match the camelCase
    // that TaskService's TaskActivitySnapshot emits (JsonSerializerDefaults.Web), and are
    // matched case-insensitively so a serializer-option change cannot silently blank the UI.
    private static readonly (string Key, string Label)[] TrackedFields =
    [
        ("name", "Name"),
        ("status", "Status"),
        ("priority", "Priority"),
        ("assignee", "Assignee"),
        ("claimedBy", "Claimed by"),
        // TaskActivitySnapshot emits claimedAt, and TimestampKeys below already knows how to
        // render it — it was simply never looked up, so a claim showed WHO but never WHEN.
        ("claimedAt", "Claimed at"),
        ("featureId", "Feature"),
        ("description", "Description"),
        ("completedAt", "Completed"),
        ("sortOrder", "Order")
    ];

    // Keys whose values are ISO timestamps, rendered as local time instead of raw round-trip text.
    private static readonly HashSet<string> TimestampKeys =
        new(StringComparer.OrdinalIgnoreCase) { "completedAt", "claimedAt" };

    /// <summary>
    /// Merge comments and activity events into ONE chronological timeline, newest first.
    /// </summary>
    /// <remarks>
    /// Call this from <c>OnParametersSet</c>, never from a render-path computed property:
    /// computed properties re-run on every render, and this parses JSON.
    /// </remarks>
    public static IReadOnlyList<TimelineEntry> Build(
        IEnumerable<TaskComment>? comments,
        IEnumerable<ActivityEvent>? events) => BuildResult(comments, events).Entries;

    /// <summary>
    /// <see cref="Build"/> plus the truncation state the UI needs to be honest about the cap.
    /// </summary>
    public static TimelineResult BuildResult(
        IEnumerable<TaskComment>? comments,
        IEnumerable<ActivityEvent>? events)
    {
        var commentList = comments?.Where(c => c is not null).ToList() ?? [];
        var eventList = events?.Where(e => e is not null).ToList() ?? [];

        // Resolve every event ONCE, up front. Pairing below needs to know what an event actually
        // changed — not merely its type — and the diff is the expensive part of this whole merge.
        var eventEntries = new List<(ActivityEvent Source, TimelineEntry Entry)>(eventList.Count);
        foreach (var activity in eventList)
        {
            eventEntries.Add((activity, FromActivityEvent(activity)));
        }

        // ── Pass 1: fold away the system-written change comments their events supersede ───────
        // A system change comment is the lower-fidelity ECHO of an activity event (same action,
        // same instant, no before/after values). Two rules make the fold safe:
        //
        //   EVIDENCE — drop it only when a matching event is actually present, so a task whose
        //   history predates the audit boundary (#88) keeps its comments.
        //
        //   ONE-TO-ONE — and only when that event has not already accounted for a DIFFERENT
        //   comment. An existence check was the bug: one event inside the window suppressed
        //   every nearby row, so three status changes made in the same second (an agent doing
        //   exactly what this app is built for) rendered as one, losing real history.
        var consumedEvents = new HashSet<int>();
        var survivingComments = new List<TaskComment>(commentList.Count);

        foreach (var comment in commentList)
        {
            if (IsSystemChangeComment(comment.Type) &&
                TryConsumeMatchingEvent(eventEntries, consumedEvents, comment))
            {
                continue;
            }

            survivingComments.Add(comment);
        }

        var entries = new List<TimelineEntry>(survivingComments.Count + eventEntries.Count);

        foreach (var comment in survivingComments)
        {
            entries.Add(FromComment(comment));
        }

        // ── Pass 2: fold away the TaskCommentAdded events their comment rows supersede ────────
        // Same two rules, mirrored: pair against a comment that SURVIVED pass 1 and that no
        // other event has already claimed. Five comments posted in the same second must leave
        // five rows, not one.
        var consumedComments = new HashSet<int>();

        foreach (var (source, entry) in eventEntries)
        {
            if (string.Equals(source.EventType, "TaskCommentAdded", StringComparison.Ordinal) &&
                TryConsumeMatchingComment(survivingComments, consumedComments, source.CreatedAt))
            {
                continue;
            }

            entries.Add(entry);
        }

        var ordered = entries
            .OrderByDescending(e => e.CreatedAtUtc)
            .ThenByDescending(e => e.Key, StringComparer.Ordinal)
            .ToList();

        return new TimelineResult(
            ordered.Count <= MaxEntries ? ordered : ordered.Take(MaxEntries).ToList(),
            ordered.Count);
    }

    /// <summary>Counts for the filter chips, resolved once at merge time.</summary>
    public static TimelineCounts Count(IReadOnlyList<TimelineEntry> entries) => new(
        entries.Count,
        entries.Count(e => e.Kind == TimelineEntryKind.Comment),
        entries.Count(e => e.Kind == TimelineEntryKind.StatusChange),
        entries.Count(e => e.Kind == TimelineEntryKind.FieldChange));

    /// <summary>Chip predicate.</summary>
    public static bool Matches(TimelineEntry entry, TimelineFilter filter) => filter switch
    {
        TimelineFilter.All => true,
        TimelineFilter.Comments => entry.Kind == TimelineEntryKind.Comment,
        TimelineFilter.Status => entry.Kind == TimelineEntryKind.StatusChange,
        TimelineFilter.FieldChanges => entry.Kind == TimelineEntryKind.FieldChange,
        _ => true
    };

    /// <summary>Applies a chip to a built timeline.</summary>
    public static IReadOnlyList<TimelineEntry> Filter(IReadOnlyList<TimelineEntry> entries, TimelineFilter filter) =>
        filter == TimelineFilter.All ? entries : entries.Where(e => Matches(e, filter)).ToList();

    // ── Field diffing ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Derive human-readable change lines from a pair of activity snapshots.
    /// Returns an EMPTY list — never a raw-JSON string — whenever the payload is missing,
    /// oversized, malformed, not an object, one-sided, or carries no whitelisted field.
    /// The caller falls back to the event's Message in that case.
    /// </summary>
    /// <remarks>
    /// BOTH sides are required. A one-sided snapshot is a creation or a deletion, and diffing it
    /// against nothing produces a wall of "Status: Todo → —" lines that assert a change nobody
    /// made — the Message ("Task 'X' created") says it correctly in one line. An oversized or
    /// malformed side is the same hazard with a worse cause: it must NOT be read as "the value
    /// went away".
    /// </remarks>
    public static IReadOnlyList<TimelineFieldChange> DeriveChanges(string? beforeJson, string? afterJson)
    {
        JsonDocument? before = null;
        JsonDocument? after = null;

        try
        {
            before = TryParseObject(beforeJson);
            after = TryParseObject(afterJson);

            if (before is null || after is null)
                return [];

            var changes = new List<TimelineFieldChange>();

            foreach (var (key, label) in TrackedFields)
            {
                var hasBefore = TryGetProperty(before, key, out var beforeValue);
                var hasAfter = TryGetProperty(after, key, out var afterValue);

                // Absent on both sides: the field simply is not part of this payload.
                if (!hasBefore && !hasAfter)
                    continue;

                // A structured value (object/array) has no honest one-line rendering and is the
                // only way JSON could leak, so the whole field is dropped rather than stringified.
                if ((hasBefore && IsStructured(beforeValue)) || (hasAfter && IsStructured(afterValue)))
                    continue;

                var beforeText = hasBefore ? Display(beforeValue, key) : EmptyValue;
                var afterText = hasAfter ? Display(afterValue, key) : EmptyValue;

                if (string.Equals(beforeText, afterText, StringComparison.Ordinal))
                    continue;

                changes.Add(new TimelineFieldChange(label, beforeText, afterText));

                if (changes.Count >= MaxChangesPerEvent)
                    break;
            }

            return changes;
        }
        finally
        {
            before?.Dispose();
            after?.Dispose();
        }
    }

    // ── Relative time ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// "just now" / "5m ago" / "3h ago" / "2d ago", falling back to an absolute local date
    /// beyond a month. Both arguments are UTC, so this is timezone-independent by construction
    /// and cannot become a second clock.
    /// </summary>
    public static string Relative(DateTime utc, DateTime nowUtc)
    {
        var delta = AsUtc(nowUtc) - AsUtc(utc);

        // A clock skew or a future-stamped row must not render "-3m ago".
        if (delta < TimeSpan.Zero)
            delta = TimeSpan.Zero;

        if (delta < TimeSpan.FromSeconds(45)) return "just now";
        if (delta < TimeSpan.FromMinutes(60)) return $"{(int)Math.Max(1, delta.TotalMinutes)}m ago";
        if (delta < TimeSpan.FromHours(24)) return $"{(int)delta.TotalHours}h ago";
        if (delta < TimeSpan.FromDays(30)) return $"{(int)delta.TotalDays}d ago";
        return Absolute(utc);
    }

    /// <summary>
    /// THE clock (#306/#328). Every timestamp on both task surfaces goes through here, so
    /// there is exactly one convention: stored UTC rendered in the server's local time.
    /// (Blazor Server: "local" is the server's zone. For this single-user localhost app the
    /// server IS the client machine, which is what the already-correct comment rows relied on.)
    /// </summary>
    public static string Absolute(DateTime utc) =>
        AsUtc(utc).ToLocalTime().ToString("MMM d, yyyy h:mm tt", CultureInfo.InvariantCulture);

    /// <summary>
    /// THE tooltip. One string for every timestamp in both task surfaces: the absolute local
    /// time (so a relative "3h ago" can be pinned down) plus the round-trip UTC instant that
    /// #306 asks for. Before this, three of the panel's five timestamps had no tooltip at all
    /// and EditItemModal's had neither a tooltip nor local time.
    /// </summary>
    public static string Tooltip(DateTime utc) => $"{Absolute(utc)} (UTC: {Iso(utc)})";

    /// <summary>
    /// THE machine-readable instant — the <c>&lt;time datetime&gt;</c> attribute, and the value
    /// inside <see cref="Tooltip"/>'s "UTC:" label.
    /// </summary>
    /// <remarks>
    /// Goes through <see cref="AsUtc"/> first, and that is the whole point. EF/SQLite hands these
    /// back as <see cref="DateTimeKind.Unspecified"/>, and "O" on an Unspecified value emits NO
    /// trailing Z — a zoneless string wearing a UTC label, which every consumer (and every
    /// <c>new Date(...)</c>) reads as local time. The e2e harness already carries a `utcIso`
    /// helper for exactly this reason on the other side of the wire.
    /// </remarks>
    public static string Iso(DateTime utc) =>
        AsUtc(utc).ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// Normalise any DateTime to a Kind=Utc instant. Unspecified is TREATED as UTC — which is
    /// what this codebase stores, what <see cref="DateTime.ToLocalTime"/> already assumed, and
    /// what makes one convention possible at all.
    /// </summary>
    public static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    // ── Internals ────────────────────────────────────────────────────────────────────────

    private static TimelineEntry FromComment(TaskComment comment) => new()
    {
        Key = $"c-{comment.Id}",
        Kind = KindOf(comment.Type),
        CreatedAtUtc = comment.CreatedAt,
        Actor = ActorOf(comment.Author),
        Label = CommentLabel(comment.Type),
        Body = comment.Text,
        CommentType = comment.Type
    };

    private static TimelineEntry FromActivityEvent(ActivityEvent activity)
    {
        var changes = DeriveChanges(activity.BeforeJson, activity.AfterJson);

        return new TimelineEntry
        {
            Key = $"a-{activity.Id}",
            Kind = string.Equals(activity.EventType, "TaskStatusChanged", StringComparison.Ordinal)
                ? TimelineEntryKind.StatusChange
                : TimelineEntryKind.FieldChange,
            CreatedAtUtc = activity.CreatedAt,
            Actor = ActorOf(activity.Actor),
            Label = EventLabel(activity.EventType),
            Changes = changes,
            // Only ever the writer-authored Message. If it is missing too, the label + timestamp
            // still carry the fact that something happened; raw JSON is not an option.
            Summary = changes.Count > 0 ? null : Trim(activity.Message),
            EventType = activity.EventType
        };
    }

    /// <summary>
    /// One actor convention for both task surfaces: a blank actor/author is
    /// <see cref="UnknownActor"/>, which is what every writer in TaskService already stores.
    /// (TaskDetailPanel used to render blank authors as "Anonymous" while EditItemModal
    /// rendered the same rows as "System".)
    /// </summary>
    public static string ActorOf(string? actor) =>
        string.IsNullOrWhiteSpace(actor) ? UnknownActor : actor.Trim();

    private static string? Trim(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>System-written change comments — the ones an activity event supersedes.</summary>
    private static bool IsSystemChangeComment(CommentType type) => type is
        Models.CommentType.StatusChange or
        Models.CommentType.Created or
        Models.CommentType.FeatureChange or
        Models.CommentType.PriorityChange or
        Models.CommentType.AssigneeChange;

    /// <summary>
    /// Claim the ONE event that accounts for <paramref name="comment"/>, or claim nothing.
    /// The nearest unclaimed candidate wins, so a burst of same-second changes pairs off
    /// row-for-row instead of collapsing into whichever event happened to be enumerated first.
    /// </summary>
    private static bool TryConsumeMatchingEvent(
        List<(ActivityEvent Source, TimelineEntry Entry)> events,
        HashSet<int> consumed,
        TaskComment comment)
    {
        var eventType = EventTypeFor(comment.Type);
        if (eventType is null) return false;

        var expectedField = ExpectedFieldFor(comment.Type);
        var bestIndex = -1;
        var bestDelta = TimeSpan.MaxValue;

        for (var i = 0; i < events.Count; i++)
        {
            if (consumed.Contains(i)) continue;

            var (source, entry) = events[i];
            if (!string.Equals(source.EventType, eventType, StringComparison.Ordinal)) continue;

            var delta = (source.CreatedAt - comment.CreatedAt).Duration();
            if (delta > DuplicateWindow) continue;

            // TaskService writes one "TaskUpdated" for a name, description, priority or assignee
            // edit alike, so type + window alone lets a NAME edit swallow an unrelated
            // "Updated: Assignee" comment written in the same second. When the event says what it
            // changed, make it agree; when it cannot (one-sided or unparseable snapshot), type and
            // window are all there is and the pairing falls back to them.
            if (expectedField is not null && entry.Changes.Count > 0 &&
                !entry.Changes.Any(c => string.Equals(c.Field, expectedField, StringComparison.Ordinal)))
            {
                continue;
            }

            if (delta < bestDelta)
            {
                bestDelta = delta;
                bestIndex = i;
            }
        }

        if (bestIndex < 0) return false;

        consumed.Add(bestIndex);
        return true;
    }

    /// <summary>Mirror of <see cref="TryConsumeMatchingEvent"/> for the TaskCommentAdded fold.</summary>
    private static bool TryConsumeMatchingComment(
        List<TaskComment> comments,
        HashSet<int> consumed,
        DateTime eventAt)
    {
        var bestIndex = -1;
        var bestDelta = TimeSpan.MaxValue;

        for (var i = 0; i < comments.Count; i++)
        {
            if (consumed.Contains(i)) continue;

            var delta = (comments[i].CreatedAt - eventAt).Duration();
            if (delta > DuplicateWindow) continue;

            if (delta < bestDelta)
            {
                bestDelta = delta;
                bestIndex = i;
            }
        }

        if (bestIndex < 0) return false;

        consumed.Add(bestIndex);
        return true;
    }

    private static string? EventTypeFor(CommentType type) => type switch
    {
        Models.CommentType.StatusChange => "TaskStatusChanged",
        Models.CommentType.Created => "TaskCreated",
        Models.CommentType.FeatureChange => "TaskFeatureChanged",
        Models.CommentType.PriorityChange => "TaskUpdated",
        Models.CommentType.AssigneeChange => "TaskUpdated",
        _ => null
    };

    /// <summary>
    /// The change line an event must carry to count as this comment's echo. Null where the event
    /// type is already unambiguous (TaskCreated is one-sided and has no derivable diff at all).
    /// Labels must match <see cref="TrackedFields"/>.
    /// </summary>
    private static string? ExpectedFieldFor(CommentType type) => type switch
    {
        Models.CommentType.StatusChange => "Status",
        Models.CommentType.FeatureChange => "Feature",
        Models.CommentType.PriorityChange => "Priority",
        Models.CommentType.AssigneeChange => "Assignee",
        _ => null
    };

    private static TimelineEntryKind KindOf(CommentType type) => type switch
    {
        Models.CommentType.StatusChange => TimelineEntryKind.StatusChange,
        Models.CommentType.Created or
        Models.CommentType.FeatureChange or
        Models.CommentType.PriorityChange or
        Models.CommentType.AssigneeChange => TimelineEntryKind.FieldChange,
        _ => TimelineEntryKind.Comment
    };

    private static string CommentLabel(CommentType type) => type switch
    {
        Models.CommentType.Comment => "Comment",
        Models.CommentType.StatusChange => "Status",
        Models.CommentType.Created => "Created",
        Models.CommentType.FeatureChange => "Feature",
        Models.CommentType.PriorityChange => "Priority",
        Models.CommentType.AssigneeChange => "Assignee",
        _ => type.ToString()
    };

    private static string EventLabel(string? eventType) => eventType switch
    {
        "TaskCreated" => "Created",
        "TaskStatusChanged" => "Status",
        "TaskFeatureChanged" => "Feature",
        "TaskUpdated" => "Updated",
        "TaskClaimed" => "Claimed",
        "TaskReleased" => "Released",
        "TaskDeleted" => "Deleted",
        "TaskCommentAdded" => "Comment",
        null or "" => "Activity",
        // Unknown event types are shown by name (an identifier the app itself wrote), never by payload.
        _ => eventType
    };

    private static JsonDocument? TryParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        if (json.Length > MaxJsonChars) return null;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            doc.Dispose();
            return null;
        }

        return doc;
    }

    private static bool TryGetProperty(JsonDocument? doc, string key, out JsonElement value)
    {
        value = default;
        if (doc is null) return false;

        if (doc.RootElement.TryGetProperty(key, out value))
            return value.ValueKind != JsonValueKind.Undefined;

        // Case-insensitive fallback: the snapshot is camelCase today, but a serializer-option
        // change must degrade to "still works", not to "silently blank".
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (string.Equals(prop.Name, key, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return prop.Value.ValueKind != JsonValueKind.Undefined;
            }
        }

        return false;
    }

    private static bool IsStructured(JsonElement value) =>
        value.ValueKind is JsonValueKind.Object or JsonValueKind.Array;

    private static string Display(JsonElement value, string key)
    {
        var text = value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            JsonValueKind.True => "Yes",
            JsonValueKind.False => "No",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String => FormatString(value.GetString(), key),
            // Unreachable: IsStructured already rejected object/array. Belt-and-braces so a
            // future ValueKind cannot fall through to GetRawText().
            _ => string.Empty
        };

        return Truncate(Collapse(text));
    }

    private static string FormatString(string? raw, string key)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        // Absolute() normalises the Kind itself (AsUtc), so a snapshot timestamp serialised
        // without a zone designator lands on the same clock as every other rendered instant.
        if (TimestampKeys.Contains(key) &&
            DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var parsed))
        {
            return Absolute(parsed);
        }

        return raw;
    }

    private static string Collapse(string text)
    {
        if (text.Length == 0) return text;

        // Newlines and runs of whitespace would break the single-line "before → after" shape.
        var sb = new StringBuilder(text.Length);
        var lastWasSpace = false;

        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (lastWasSpace || sb.Length == 0) continue;
                sb.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                sb.Append(ch);
                lastWasSpace = false;
            }
        }

        while (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
        return sb.ToString();
    }

    private static string Truncate(string text)
    {
        if (text.Length == 0) return EmptyValue;
        return text.Length <= MaxValueChars ? text : string.Concat(text.AsSpan(0, MaxValueChars), "…");
    }
}
