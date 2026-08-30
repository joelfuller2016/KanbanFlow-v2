using System.Globalization;
using System.Text.Json;
using KanbanFlow.Models;

namespace KanbanFlow.Components.Shared;

/// <summary>
/// Which filter chip an entry belongs to. Keep this a small closed set — the chip row in
/// <c>ActivityTimeline.razor</c> enumerates it, so a new member must get a chip or it becomes
/// reachable only through "All".
/// </summary>
public enum TimelineCategory
{
    /// <summary>Human discussion: a user-authored <see cref="TaskComment"/>.</summary>
    Comment,
    /// <summary>A status transition (the kanban-relevant one).</summary>
    Status,
    /// <summary>Everything else the audit trail records: field edits, create/delete, claim/release.</summary>
    Change
}

/// <summary>One "Status: Todo → InProgress" line inside a timeline entry.</summary>
/// <param name="Label">Human label, e.g. "Priority".</param>
/// <param name="Before">Formatted old value; never null — an absent value renders as an em dash.</param>
/// <param name="After">Formatted new value; never null.</param>
public sealed record TimelineFieldChange(string Label, string Before, string After);

/// <summary>
/// One row of the unified history. Produced from EITHER a <see cref="TaskComment"/> (discussion)
/// or an <see cref="ActivityEvent"/> (durable audit record) — never both for the same real-world
/// action; see <see cref="ActivityTimelineBuilder"/> for the de-duplication contract.
/// </summary>
public sealed record TimelineEntry
{
    /// <summary>Always UTC, exactly as stored. Rendering converts to local; comparisons must not.</summary>
    public required DateTime CreatedAtUtc { get; init; }

    public required TimelineCategory Category { get; init; }

    /// <summary>
    /// The originating discriminator: an <see cref="ActivityEvent.EventType"/> ("TaskStatusChanged")
    /// or "Comment:{CommentType}" for a comment row. Drives the icon/colour switch in the component.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>Short badge text shown next to the actor, e.g. "Status" / "Note".</summary>
    public required string Badge { get; init; }

    /// <summary>
    /// CSS tone suffix — the component emits <c>kf-tl-{Tone}</c>, which sets the dot colour and
    /// picks the icon. Lives here rather than in the markup so it is unit-testable and so the
    /// per-comment-type colouring TaskDetailPanel used to do inline survives the move.
    /// EVERY value must have a matching <c>.kf-tl-*</c> rule in app.css.
    /// </summary>
    public required string Tone { get; init; }

    public string? Actor { get; init; }

    /// <summary>Plain-text fallback headline, used when <see cref="Changes"/> is empty.</summary>
    public string? Headline { get; init; }

    /// <summary>Markdown body — comment rows only. Null for audit events.</summary>
    public string? Markdown { get; init; }

    /// <summary>Derived field-level diff. Empty when no diff is derivable.</summary>
    public IReadOnlyList<TimelineFieldChange> Changes { get; init; } = [];

    /// <summary>Row id of the source record; used for stable ordering and @key.</summary>
    public int SourceId { get; init; }

    /// <summary>True when built from a <see cref="TaskComment"/>, false for an <see cref="ActivityEvent"/>.</summary>
    public bool FromComment { get; init; }
}

/// <summary>
/// Merges the two halves of a task's history into ONE chronological trail.
///
/// ── WHY DE-DUPLICATION IS THE WHOLE PROBLEM ────────────────────────────────────────────────
/// <see cref="Services.TaskService"/> writes a <see cref="TaskComment"/> AND an
/// <see cref="ActivityEvent"/> in the SAME <c>SaveChangesAsync</c> for every system-driven
/// change (TaskService.cs:76-88 status, :182-193 feature move, :304-317 status-via-update,
/// :320-334 field update, :399-414 create, :685-687 comment-added). A naive concatenation of
/// the two sources therefore shows EVERY change twice.
///
/// The pairing rules below are directional and each suppression requires POSITIVE evidence of
/// its duplicate — never a blanket "system comments are noise":
///
///   • An ActivityEvent is richer for a CHANGE (it carries Before/AfterJson, so we can render
///     "Status: Todo → InProgress"). So the paired system COMMENT is dropped — but only when a
///     matching event is actually present. Rows written before the audit trail existed, or by a
///     path that emits no event, keep their comment rather than vanishing.
///   • A TaskComment is richer for DISCUSSION (markdown body, comment type). So the paired
///     "TaskCommentAdded" EVENT is dropped — again only when the comment row is actually there.
///
/// Both directions are guarded so the merge can only ever REMOVE a proven duplicate, never a
/// unique piece of history. That invariant is what <c>ActivityTimelineTests</c> pins.
/// ───────────────────────────────────────────────────────────────────────────────────────────
/// </summary>
public static class ActivityTimelineBuilder
{
    /// <summary>
    /// How far apart a comment and its paired event may be stamped and still count as the same
    /// action. They are written in one <c>SaveChangesAsync</c> but each calls
    /// <c>DateTime.UtcNow</c> separately, so they differ by microseconds — never seconds. Two
    /// seconds is generous slack, and small enough that two genuinely distinct user actions
    /// cannot collide in practice.
    /// </summary>
    public static readonly TimeSpan PairingWindow = TimeSpan.FromSeconds(2);

    private const string EmptyValue = "—"; // em dash

    /// <summary>
    /// The ONLY comment types that a paired ActivityEvent may suppress, mapped to the event type
    /// that supersedes them. A closed whitelist: a comment type absent from this map is treated
    /// as human discussion and is never dropped, whatever else happened at the same instant.
    /// </summary>
    private static readonly Dictionary<CommentType, string> SupersedingEventByCommentType = new()
    {
        [CommentType.StatusChange] = "TaskStatusChanged",
        [CommentType.Created] = "TaskCreated",
        [CommentType.FeatureChange] = "TaskFeatureChanged",
        [CommentType.PriorityChange] = "TaskUpdated",
        [CommentType.AssigneeChange] = "TaskUpdated",
    };

    /// <summary>
    /// The service's field-update path writes a <see cref="CommentType.Note"/> whose text is
    /// literally <c>"Updated: Name, Priority"</c> (TaskService.cs:320-327), paired with a
    /// TaskUpdated event. Note is ALSO user-authorable, so it cannot be suppressed by type —
    /// this prefix plus a coincident TaskUpdated event is the narrowest safe discriminator.
    /// </summary>
    private const string ServiceUpdateNotePrefix = "Updated: ";

    /// <summary>
    /// Snapshot fields worth showing, in display order, with their human labels. A closed
    /// whitelist over <c>TaskService.TaskActivitySnapshot</c> (TaskService.cs:18-31): a field
    /// added there without a label here is simply not rendered rather than leaking a raw JSON key.
    /// "id" and "featureId" are deliberately handled — id is never interesting, featureId is.
    ///
    /// INTERNAL (tests reach it via InternalsVisibleTo) so the tests can parameterise over the
    /// WHOLE set rather than a hand-picked exemplar. The X → X collision (#359) was reported
    /// against <c>description</c>, but the truncation that causes it lives in <see cref="Format"/>
    /// and applies to every entry here — <c>name</c> is already 60+ characters on 268 of the
    /// 409 live tasks. A guard written for one field would be correct for the case someone measured
    /// and silently wrong for the rest, so the guard enumerates THIS list; a field added below is
    /// covered the moment it is added.
    /// </summary>
    internal static IReadOnlyList<(string Json, string Label)> DisplayFields { get; } =
    [
        ("status", "Status"),
        ("priority", "Priority"),
        ("assignee", "Assigned"),
        ("claimedBy", "Claimed by"),
        ("name", "Name"),
        ("featureId", "Feature"),
        ("description", "Description"),
        ("sortOrder", "Order"),
        ("completedAt", "Completed"),
        ("claimedAt", "Claimed at"),
    ];

    /// <summary>Badge text + category for each audit event type this app emits.</summary>
    private static (string Badge, TimelineCategory Category) DescribeEvent(string eventType) => eventType switch
    {
        "TaskStatusChanged" => ("Status", TimelineCategory.Status),
        "TaskCreated" => ("Created", TimelineCategory.Change),
        "TaskDeleted" => ("Deleted", TimelineCategory.Change),
        "TaskUpdated" => ("Updated", TimelineCategory.Change),
        "TaskFeatureChanged" => ("Moved", TimelineCategory.Change),
        "TaskClaimed" => ("Claimed", TimelineCategory.Change),
        "TaskClaimRenewed" => ("Renewed", TimelineCategory.Change),
        "TaskClaimStolen" => ("Taken over", TimelineCategory.Change),
        "TaskReleased" => ("Released", TimelineCategory.Change),
        "TaskCommentAdded" => ("Comment", TimelineCategory.Comment),
        _ => (eventType, TimelineCategory.Change)
    };

    /// <summary>
    /// Builds the merged, newest-first history. Both inputs may be in any order; neither is mutated.
    /// </summary>
    public static List<TimelineEntry> Build(
        IEnumerable<TaskComment>? comments,
        IEnumerable<ActivityEvent>? events)
    {
        // Stable input order so the greedy pairing below is deterministic regardless of how the
        // caller happened to query.
        var commentList = comments?.OrderBy(c => AsUtc(c.CreatedAt)).ThenBy(c => c.Id).ToList() ?? [];
        var eventList = events?.OrderBy(e => AsUtc(e.CreatedAt)).ThenBy(e => e.Id).ToList() ?? [];

        var suppressedComment = new bool[commentList.Count];
        var suppressedEvent = new bool[eventList.Count];

        // Pass 1 — a system comment is dropped in favour of ITS event (the event carries the diff).
        foreach (var (ci, ei) in MatchBestFirst(
                     commentList.Count, eventList.Count,
                     (ci, ei) => SupersedingEventTypeFor(commentList[ci]) is { } type
                                 && eventList[ei].EventType == type,
                     (ci, ei) => Delta(commentList[ci].CreatedAt, eventList[ei].CreatedAt)))
        {
            suppressedComment[ci] = true;
        }

        // The event side of pass 2's predicate, resolved ONCE PER EVENT rather than once per
        // (comment, event) pair. The predicate is evaluated O(n·m) times and JsonDocument.Parse
        // was inside it, so a task with a few hundred comments re-parsed tens of thousands of
        // documents — on the synchronous render path, since Rebuild() runs on every
        // OnParametersSet. Null for every non-comment event; those never reach the text test.
        var eventCommentText = new string?[eventList.Count];
        for (var i = 0; i < eventList.Count; i++)
            if (eventList[i].EventType == "TaskCommentAdded")
                eventCommentText[i] = ReadStringProperty(eventList[i].AfterJson, "text")?.Trim();

        // Pass 2 — a TaskCommentAdded event is dropped in favour of ITS comment (which has the
        // markdown body).
        //
        // DISJOINTNESS FROM PASS 1 rests on exactly two facts, both load-bearing:
        //   1. `!suppressedComment[ci]` — a comment pass 1 already spent cannot be spent again.
        //   2. "TaskCommentAdded" is never a VALUE in SupersedingEventByCommentType, so the two
        //      passes draw from disjoint sets of events and cannot both claim one.
        // Map a comment type to "TaskCommentAdded" and fact 2 dies, turning one action into a
        // double suppression. `Build_TheTwoPassesClaimDisjointEvents_*` pins both.
        foreach (var (ci, ei) in MatchBestFirst(
                     commentList.Count, eventList.Count,
                     (ci, ei) => eventList[ei].EventType == "TaskCommentAdded"
                                 && !suppressedComment[ci]
                                 && TextMatches(eventCommentText[ei], commentList[ci]),
                     (ci, ei) => Delta(commentList[ci].CreatedAt, eventList[ei].CreatedAt)))
        {
            suppressedEvent[ei] = true;
        }

        var entries = new List<TimelineEntry>(commentList.Count + eventList.Count);
        for (var ci = 0; ci < commentList.Count; ci++)
            if (!suppressedComment[ci]) entries.Add(FromComment(commentList[ci]));
        for (var ei = 0; ei < eventList.Count; ei++)
            if (!suppressedEvent[ei]) entries.Add(FromEvent(eventList[ei]));

        // Newest first. SourceId is the tie-break so ordering is deterministic when two rows share
        // a timestamp (they routinely do — same SaveChangesAsync), and comments sort ahead of
        // events at an identical instant so a reply reads next to what it replies to.
        entries.Sort(static (a, b) =>
        {
            var byTime = b.CreatedAtUtc.CompareTo(a.CreatedAtUtc);
            if (byTime != 0) return byTime;
            var byOrigin = b.FromComment.CompareTo(a.FromComment);
            if (byOrigin != 0) return byOrigin;
            return b.SourceId.CompareTo(a.SourceId);
        });

        return entries;
    }

    /// <summary>
    /// Which event type, if any, may supersede this comment. Null ⇒ human discussion: never
    /// suppressed, whatever else happened at the same instant.
    /// </summary>
    private static string? SupersedingEventTypeFor(TaskComment comment)
    {
        if (SupersedingEventByCommentType.TryGetValue(comment.Type, out var mapped))
            return mapped;

        if (comment.Type == CommentType.Note &&
            comment.Text.StartsWith(ServiceUpdateNotePrefix, StringComparison.Ordinal))
            return "TaskUpdated";

        return null;
    }

    /// <summary>
    /// The event's AfterJson is <c>{"text":…,"author":…}</c> (TaskService.cs:687). Matching the
    /// text stops an unrelated comment posted in the same second from absorbing the event. A row
    /// with no parsable text (legacy/hand-written) falls back to time alone.
    /// </summary>
    /// <param name="eventText">
    /// The event's already-extracted, already-trimmed comment text, or null when the row carries
    /// none. Passed in rather than parsed here because the caller evaluates this O(n·m) times.
    /// </param>
    private static bool TextMatches(string? eventText, TaskComment comment)
        => eventText is null || string.Equals(eventText, comment.Text.Trim(), StringComparison.Ordinal);

    private static long Delta(DateTime a, DateTime b) => Math.Abs((AsUtc(a) - AsUtc(b)).Ticks);

    // ── PAIRING IS ONE-TO-ONE AND BEST-MATCH-FIRST, AND BOTH HALVES ARE LOAD-BEARING ──────────
    // Measured, not reasoned about — a 211-event task was the instrument:
    //
    //   v1, "is ANY event of the right type within the window?" (many-to-one):
    //       211 events + 211 comments → 203 rows. Eight pieces of history DELETED by the merge.
    //   v2, one-to-one but assigned in comment order:
    //       211 rows — count restored — but an old comment whose own event fell outside the
    //       loaded window grabbed a NEIGHBOUR's event, cascading the mis-assignment forward, so
    //       the surviving representations interleaved arbitrarily instead of "newest N as events,
    //       oldest as comments".
    //   v3 (this): candidates sorted by |Δt| and taken greedily.
    //       A true pair is written in one SaveChangesAsync — microseconds apart — while the next
    //       distinct action is orders of magnitude further away. Best-first therefore consumes
    //       every genuine pair before any coincidental one can compete for it.
    //
    // Cost is O(n·m log(n·m)) over ONE task's rows (hundreds), and only over candidates that pass
    // the type/text filter and the window.
    // ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Greedy minimum-cost one-to-one matching between comment and event indices.
    /// <paramref name="eligible"/> gates which pairs may match at all; <paramref name="cost"/> is
    /// the tick distance. Pairs outside <see cref="PairingWindow"/> are never considered.
    ///
    /// ONE-TO-ONE IS ENFORCED HERE, by <c>takenComments</c>/<c>takenEvents</c> below — nowhere
    /// else. The result is fully materialised before it is returned, so a caller cannot influence
    /// this matching from inside its own foreach body; anything a later pass must respect has to
    /// be visible to <paramref name="eligible"/> at call time. (This method used to take two
    /// `used` arrays for that purpose, documented as letting a later pass respect an earlier
    /// pass's claims. They could never work: both callers filled them only AFTER the call had
    /// returned, so every element was false at every point this method read them. They are gone —
    /// see <see cref="Build"/> for the two facts that actually make the passes disjoint.)
    /// </summary>
    private static List<(int CommentIndex, int EventIndex)> MatchBestFirst(
        int commentCount, int eventCount,
        Func<int, int, bool> eligible,
        Func<int, int, long> cost)
    {
        var candidates = new List<(long Cost, int Ci, int Ei)>();
        for (var ci = 0; ci < commentCount; ci++)
        {
            for (var ei = 0; ei < eventCount; ei++)
            {
                // Window FIRST. Both filters are pure and ANDed, so the candidate set is identical
                // either way — but `eligible` is the expensive half (pass 2's used to parse JSON)
                // and the window rejects almost every pair, so ordering is a pure win.
                var d = cost(ci, ei);
                if (d > PairingWindow.Ticks) continue;
                if (!eligible(ci, ei)) continue;

                candidates.Add((d, ci, ei));
            }
        }

        // Cost first; index tie-breaks make the result identical for identical input, which is
        // what keeps the rendered order stable across renders.
        candidates.Sort(static (x, y) =>
        {
            var byCost = x.Cost.CompareTo(y.Cost);
            if (byCost != 0) return byCost;
            var byComment = x.Ci.CompareTo(y.Ci);
            return byComment != 0 ? byComment : x.Ei.CompareTo(y.Ei);
        });

        var takenComments = new bool[commentCount];
        var takenEvents = new bool[eventCount];
        var matches = new List<(int, int)>();
        foreach (var (_, ci, ei) in candidates)
        {
            if (takenComments[ci] || takenEvents[ei]) continue;
            takenComments[ci] = true;
            takenEvents[ei] = true;
            matches.Add((ci, ei));
        }
        return matches;
    }

    /// <summary>Reads one string property from a JSON object, tolerating null/malformed input.</summary>
    private static string? ReadStringProperty(string? json, string name)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            return RawValue(doc.RootElement, name).Text;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static TimelineEntry FromComment(TaskComment comment) => new()
    {
        // Normalised HERE, once, at the boundary — every consumer (display, tooltip, relative
        // label, ordering) then works with a value whose Kind is honest.
        CreatedAtUtc = AsUtc(comment.CreatedAt),
        Category = comment.Type switch
        {
            CommentType.StatusChange => TimelineCategory.Status,
            CommentType.Created or CommentType.FeatureChange
                or CommentType.PriorityChange or CommentType.AssigneeChange => TimelineCategory.Change,
            _ => TimelineCategory.Comment
        },
        Kind = $"Comment:{comment.Type}",
        Badge = CommentBadge(comment.Type),
        Tone = CommentTone(comment.Type),
        Actor = comment.Author,
        Markdown = comment.Text,
        SourceId = comment.Id,
        FromComment = true
    };

    private static TimelineEntry FromEvent(ActivityEvent ev)
    {
        var (badge, category) = DescribeEvent(ev.EventType);
        return new TimelineEntry
        {
            CreatedAtUtc = AsUtc(ev.CreatedAt),
            Category = category,
            Kind = ev.EventType,
            Badge = badge,
            Tone = EventTone(ev.EventType, category),
            Actor = ev.Actor,
            Headline = ev.Message,
            Changes = DiffFields(ev.BeforeJson, ev.AfterJson),
            SourceId = ev.Id,
            FromComment = false
        };
    }

    private static string CommentBadge(CommentType type) => type switch
    {
        CommentType.Comment => "Comment",
        CommentType.StatusChange => "Status",
        CommentType.Created => "Created",
        CommentType.FeatureChange => "Moved",
        CommentType.PriorityChange => "Priority",
        CommentType.AssigneeChange => "Assigned",
        _ => type.ToString()
    };

    /// <summary>
    /// Comment-type accents. This is the information TaskDetailPanel used to carry in its
    /// per-comment left border + badge colour (its <c>GetCommentBorderClass</c> /
    /// <c>GetCommentTypeBadgeClass</c>); folding the list into the rail must not lose it — a
    /// Blocked note has to stay visibly red.
    /// </summary>
    private static string CommentTone(CommentType type) => type switch
    {
        CommentType.Decision => "decision",
        CommentType.Question => "question",
        CommentType.Blocked => "blocked",
        CommentType.Handoff => "handoff",
        CommentType.Verification => "verification",
        CommentType.Created => "created",
        CommentType.StatusChange => "status",
        CommentType.FeatureChange or CommentType.PriorityChange or CommentType.AssigneeChange => "change",
        _ => "comment"
    };

    private static string EventTone(string eventType, TimelineCategory category) => eventType switch
    {
        "TaskCreated" => "created",
        "TaskDeleted" => "deleted",
        "TaskClaimed" or "TaskReleased" or "TaskClaimRenewed" or "TaskClaimStolen" => "claim",
        _ => category switch
        {
            TimelineCategory.Comment => "comment",
            TimelineCategory.Status => "status",
            _ => "change"
        }
    };

    /// <summary>
    /// Diffs two snapshot JSON blobs into human "Label: before → after" lines.
    ///
    /// Returns EMPTY (so the caller falls back to <see cref="ActivityEvent.Message"/>) when either
    /// side is absent — a create has no Before and a delete has no After, and "everything changed
    /// from nothing" is noise, not history. Malformed JSON also yields empty rather than throwing:
    /// a corrupt audit row must not take the panel down.
    /// </summary>
    public static IReadOnlyList<TimelineFieldChange> DiffFields(string? beforeJson, string? afterJson)
    {
        if (string.IsNullOrWhiteSpace(beforeJson) || string.IsNullOrWhiteSpace(afterJson))
            return [];

        JsonDocument before, after;
        try
        {
            before = JsonDocument.Parse(beforeJson);
        }
        catch (JsonException) { return []; }

        using (before)
        {
            try
            {
                after = JsonDocument.Parse(afterJson);
            }
            catch (JsonException) { return []; }

            using (after)
            {
                if (before.RootElement.ValueKind != JsonValueKind.Object ||
                    after.RootElement.ValueKind != JsonValueKind.Object)
                    return [];

                var changes = new List<TimelineFieldChange>();
                foreach (var (json, label) in DisplayFields)
                {
                    var b = RawValue(before.RootElement, json);
                    var a = RawValue(after.RootElement, json);

                    // NEVER invent a change from a MISSING key. The snapshot blobs are unversioned
                    // (ActivityEvent.BeforeJson/AfterJson are plain nullable strings), so a row
                    // written before a field existed simply lacks that key — reporting it as
                    // "— → High" would be the differ fabricating history. Absence on EITHER side
                    // means "unknown", so the field is skipped.
                    //
                    // An explicit JSON `null` is NOT absence: `"assignee": null → "alice"` is a
                    // real assignment and must render. That is why presence is tracked separately
                    // from value.
                    if (!b.Present || !a.Present)
                        continue;
                    if (string.Equals(b.Text, a.Text, StringComparison.Ordinal))
                        continue;

                    // The two sides are formatted AS A PAIR, never independently. Formatting them
                    // one at a time is what let a real change render as "X → X" (#359) — see
                    // FormatChange.
                    var (beforeText, afterText) = FormatChange(json, b.Text, a.Text);
                    changes.Add(new TimelineFieldChange(label, beforeText, afterText));
                }
                return changes;
            }
        }
    }

    /// <summary>
    /// A JSON property read: whether the key EXISTED, and its value as a comparable string.
    /// The two are distinct — an absent key is "unknown" (old snapshot), an explicit null is a
    /// real, renderable empty value.
    /// </summary>
    private readonly record struct JsonRead(bool Present, string? Text);

    /// <summary>
    /// Reads one property. Lookup is case-insensitive: the writer uses
    /// <c>JsonSerializerDefaults.Web</c> (camelCase) today, but rows written under a different
    /// policy must still diff rather than silently show nothing.
    /// </summary>
    private static JsonRead RawValue(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el))
        {
            var found = false;
            foreach (var prop in obj.EnumerateObject())
            {
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    el = prop.Value;
                    found = true;
                    break;
                }
            }
            if (!found) return new JsonRead(false, null);
        }

        return el.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => new JsonRead(true, null),
            JsonValueKind.String => new JsonRead(true, el.GetString()),
            _ => new JsonRead(true, el.GetRawText())
        };
    }

    private const int MaxValueLength = 60;

    /// <summary>Elision marker. One character, so it never dominates the window it trims.</summary>
    private const string Ellipsis = "…";

    /// <summary>
    /// How much of the IDENTICAL run is kept ahead of the first difference when a pair collides.
    /// Enough to anchor the eye ("…/issues/351 **State:** OPEN"), small enough that the change
    /// itself is what the row leads with. Must stay well under <see cref="MaxValueLength"/> — the
    /// difference guarantee in <see cref="FormatChange"/> depends on the divergence point landing
    /// inside the window, and this is the slack that puts it there.
    /// </summary>
    private const int DiffContextLength = 12;

    private static string Format(string jsonName, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return EmptyValue;

        if (FormatTimestamp(jsonName, raw) is { } stamped)
            return stamped;

        var text = Collapse(raw);
        if (text.Length == 0)
            return EmptyValue;
        if (text.Length <= MaxValueLength)
            return text;

        var end = MaxValueLength;
        // Never cut between the halves of a surrogate pair — mirrors Window()'s end-trim.
        if (char.IsLowSurrogate(text[end]))
            end--;
        return text[..end] + Ellipsis;
    }

    /// <summary>
    /// The timestamp branch of <see cref="Format"/>, split out because
    /// <see cref="FormatChange"/> has to ASK whether a value took it.
    ///
    /// Timestamps inside the snapshot blob are serialized from the same Kind=Unspecified
    /// DateTimes, so they too arrive with NO zone designator ("2026-08-02T22:21:48.82"). They
    /// ARE UTC — AssumeUniversal says so explicitly instead of letting the parser default to
    /// local, which would shift them by one offset. Rendered on the same clock as every other
    /// timestamp here (local, per #306) rather than as a raw ISO string.
    ///
    /// Returns null when this is not a timestamp field, or when the value does not parse — an
    /// unparseable value falls through to the ordinary text path rather than being dropped.
    /// </summary>
    private static string? FormatTimestamp(string jsonName, string? raw)
    {
        if (jsonName is not ("completedAt" or "claimedAt") || string.IsNullOrWhiteSpace(raw))
            return null;

        return DateTime.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var ts)
            ? FormatLocal(ts)
            : null;
    }

    /// <summary>One line, no surrounding space — the shape every length decision below is made on.</summary>
    private static string Collapse(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? string.Empty : raw.ReplaceLineEndings(" ").Trim();

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  WHY THE TWO SIDES ARE FORMATTED TOGETHER — #359
    //
    //  Format() is a pure function of ONE value, so it cannot know that the value it just cut at
    //  60 characters is about to be shown next to another value that cuts to the same 60. That is
    //  the whole defect: every task mirrored from GitHub opens with an identical ~70-character
    //  generated block —
    //
    //      <!-- kanbanflow-sync -->
    //      **Issue:** https://github.com/<owner>/<repo>/issues/<n>
    //
    //  — so the cut lands 10 characters INSIDE the constant prefix and both operands collapse to
    //  the same string. The row then says "Description changed" and shows X → X. Measured against
    //  the live corpus (409 tasks, 404 with a description, 404 distinct values): ALL 404 render as
    //  the single string "<!-- kanbanflow-sync --> **Issue:** https://github.com/joelf…".
    //
    //  RAISING THE CAP IS NOT THE FIX. A 1,800-character before/after on a timeline row is
    //  unreadable, and the next generated prefix that happens to be longer reintroduces the bug.
    //  The cap is CORRECT for the short enum-shaped fields (status maxes out at "InProgress",
    //  priority at "Critical"), so it stays.
    //
    //  THE DEFECT IS NOT DESCRIPTION-ONLY. Format() caps all ten DisplayFields. Of those, four are
    //  unbounded strings and can therefore exceed the cap — description, name, assignee, claimedBy
    //  (TaskItem.cs:7-20) — and `name` is already over it on 268 of 409 live tasks, so a rename
    //  editing the TAIL of a synced "#<n> <github issue title>" collides exactly the same way. The
    //  remaining six cannot reach 60 characters: status/priority are enum ToString()s, featureId
    //  is an int, sortOrder a double, and completedAt/claimedAt render through FormatTimestamp.
    //
    //  So the routing rule is NOT a list of field names — a list would have to be maintained, and
    //  the two fields with no live data today (assignee, claimedBy) are exactly the ones such a
    //  list would omit. The rule is the COLLISION ITSELF: re-window only when two values that
    //  genuinely differ have rendered identically. Every field is routed; none is excluded; and
    //  for every pair that already renders distinctly the output is byte-identical to before.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Formats the two sides of one field change so that a REAL change can never render as
    /// "X → X".
    ///
    /// GUARANTEE: if <paramref name="beforeRaw"/> and <paramref name="afterRaw"/> collapse to
    /// different one-line texts, the returned pair differs. The window both sides share starts at
    /// the same offset and is <see cref="MaxValueLength"/> wide, while the first difference sits
    /// at most <see cref="DiffContextLength"/> characters past that start — so the differing
    /// character is inside both windows, or one side ends before it and the two windows differ in
    /// length. <c>DiffFields_ValuesDivergingAnywhere_NeverRenderAsTwoIdenticalStrings</c> pins it over every
    /// entry in <see cref="DisplayFields"/>.
    ///
    /// Two collisions are deliberately NOT repaired here, because truncation is not their cause
    /// and a wider window cannot cure them:
    ///   • Both sides render through <see cref="FormatTimestamp"/> and land in the same MINUTE.
    ///     The displayed text is DERIVED from the value, not a prefix of it, so windowing the raw
    ///     ISO text would put an unconverted UTC fragment where a local time belongs — a second
    ///     clock, which is precisely what #306 removed from this file.
    ///   • The two values are equal once collapsed (they differ only in line endings or in
    ///     surrounding whitespace). There is no visible delta to window to.
    /// Both are tracked separately; see the tests that pin the current behaviour.
    /// </summary>
    private static (string Before, string After) FormatChange(string jsonName, string? beforeRaw, string? afterRaw)
    {
        var before = Format(jsonName, beforeRaw);
        var after = Format(jsonName, afterRaw);

        // Not a collision: today's rendering already tells the two apart. This is the overwhelming
        // majority of rows (a status flip, a priority bump) and they must come out UNCHANGED.
        if (!string.Equals(before, after, StringComparison.Ordinal))
            return (before, after);

        // Derived rendering — see the remarks. Asking FormatTimestamp rather than testing the
        // field name keeps this a question about the MECHANISM that produced the string.
        if (FormatTimestamp(jsonName, beforeRaw) is not null || FormatTimestamp(jsonName, afterRaw) is not null)
            return (before, after);

        var b = Collapse(beforeRaw);
        var a = Collapse(afterRaw);
        if (b.Length == 0 || a.Length == 0 || string.Equals(b, a, StringComparison.Ordinal))
            return (before, after);

        var start = WindowStart(b, a);
        return (Window(b, start), Window(a, start));
    }

    /// <summary>
    /// Where the shared window opens: <see cref="DiffContextLength"/> characters before the first
    /// difference. Both sides get this ONE offset — that is what makes the two windows comparable
    /// and the difference guarantee hold.
    /// </summary>
    private static int WindowStart(string a, string b)
    {
        var limit = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < limit && a[i] == b[i]) i++;

        // `start` never exceeds the common prefix, so the two strings agree at that index and it
        // does not matter which one the rune snap is measured against.
        return SnapToRuneStart(a, Math.Max(0, i - DiffContextLength));
    }

    /// <summary>
    /// A slice of <paramref name="text"/> anchored at <paramref name="start"/>, marked with an
    /// ellipsis on whichever end elides text, and guaranteed to fit within
    /// <see cref="MaxValueLength"/> characters even when both ends elide. Plain text throughout:
    /// no markup is produced here, so the caller's <c>@change.Before</c> stays Razor-encoded
    /// (#340/#341).
    /// </summary>
    private static string Window(string text, int start)
    {
        // Reserve one slot for the leading ellipsis (if any); another will be reserved below
        // when truncation is needed on the trailing end, so the combined output never exceeds
        // MaxValueLength even when both ellipses fire.
        var budget = MaxValueLength - (start > 0 ? 1 : 0);
        var end = Math.Min(text.Length, start + budget);
        if (end < text.Length)
        {
            // Reserve one char for the trailing ellipsis so total stays within MaxValueLength.
            end--;
            // Never cut between the halves of a surrogate pair — a lone surrogate is not text.
            if (end > start && char.IsLowSurrogate(text[end])) end--;
        }

        return (start > 0 ? Ellipsis : string.Empty)
             + text[start..end]
             + (end < text.Length ? Ellipsis : string.Empty);
    }

    /// <summary>Moves an index back off the low half of a surrogate pair.</summary>
    private static int SnapToRuneStart(string text, int index)
        => index > 0 && index < text.Length && char.IsLowSurrogate(text[index]) ? index - 1 : index;

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  TIMEZONE — one convention, applied once, here.
    //
    //  Every DateTime in this app is written as DateTime.UtcNow and stored in SQLite, which has
    //  no zone type: EF hands it back with Kind = Unspecified (there is no HasConversion anywhere
    //  in AppDbContext that would restore it). Both hosts read these rows through EF IN-PROCESS,
    //  not over HTTP, so there is exactly one path — but its Kind is still a lie by omission.
    //
    //  Kind = Unspecified is a live hazard for a UTC value:
    //    • `value.ToUniversalTime()` treats Unspecified as LOCAL and would shift it again.
    //    • `value.ToString("O")` on Unspecified emits NO trailing `Z`, so a tooltip labelled
    //      "UTC:" would show an instant that is not marked as UTC — the same ambiguity #306 is
    //      about, one layer down.
    //  (`ToLocalTime()` happens to treat Unspecified as UTC, which is why the naive code appears
    //   to work — but "happens to" is not a contract to build a clock on.)
    //
    //  So: normalise EXPLICITLY at the boundary and never inspect Kind again downstream.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Stamps a stored instant as the UTC value it actually is. Unspecified is RE-LABELLED (the
    /// ticks already are UTC); a genuinely Local value is CONVERTED, never relabelled — relabelling
    /// a local value is precisely the off-by-one-offset bug this exists to prevent.
    /// </summary>
    public static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    /// <summary>The face value: the instant rendered in the viewer's (server's) local zone, per #306.</summary>
    public static string FormatLocal(DateTime utc)
        => AsUtc(utc).ToLocalTime().ToString("MMM d, yyyy h:mm tt", CultureInfo.CurrentCulture);

    /// <summary>
    /// The hover value: local for cross-reference plus the unambiguous absolute instant. The
    /// round-trip half ALWAYS carries a zone designator — that trailing `Z` is what proves the
    /// normalisation above ran, and is what the regression test asserts.
    /// </summary>
    public static string FormatTooltip(DateTime utc)
    {
        var u = AsUtc(utc);
        return $"{u.ToLocalTime().ToString("MMM d, yyyy h:mm tt", CultureInfo.CurrentCulture)} (local) · UTC: {u:O}";
    }

    /// <summary>Machine-readable value for a <c>&lt;time datetime&gt;</c> attribute.</summary>
    public static string FormatMachine(DateTime utc) => AsUtc(utc).ToString("O");

    /// <summary>
    /// "3d ago". Both arguments are UTC instants — this is deliberately timezone-free, so the
    /// relative label cannot disagree with the local absolute time shown beside it.
    /// </summary>
    public static string Relative(DateTime utc, DateTime nowUtc)
    {
        // Normalise BOTH sides: DateTime subtraction ignores Kind and just subtracts ticks, so a
        // mismatched pair would silently produce an offset-sized error in the relative label.
        var delta = AsUtc(nowUtc) - AsUtc(utc);
        // A row stamped in the future (clock skew between writes) must not render "-3m ago".
        if (delta < TimeSpan.Zero) delta = TimeSpan.Zero;

        if (delta.TotalSeconds < 45) return "just now";
        if (delta.TotalMinutes < 60) return $"{Math.Max(1, (int)delta.TotalMinutes)}m ago";
        if (delta.TotalHours < 24) return $"{(int)delta.TotalHours}h ago";
        if (delta.TotalDays < 30) return $"{(int)delta.TotalDays}d ago";
        if (delta.TotalDays < 365) return $"{(int)(delta.TotalDays / 30)}mo ago";
        return $"{(int)(delta.TotalDays / 365)}y ago";
    }
}
