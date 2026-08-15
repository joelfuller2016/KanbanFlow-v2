using System.Reflection;
using System.Text.Json;
using KanbanFlow.Models;
using KanbanFlow.Services;

namespace KanbanFlow.Tests;

/// <summary>
/// Guards <see cref="ActivityTimelineModel"/> — the pure merge + diff behind the task-history
/// timeline (#328). The premise this file exists to hold: the ActivityEvent trail is recorded,
/// is queryable at <c>GET /api/tasks/{id}/activity</c>, and — before #328 — was rendered by
/// NEITHER task surface. The merge test below is the one that goes red if the events ever fall
/// out of the timeline again.
/// </summary>
public class ActivityTimelineModelTests
{
    // Fixed instants so ordering assertions never depend on wall-clock timing.
    private static readonly DateTime T0 = new(2026, 7, 12, 17, 19, 0, DateTimeKind.Utc);

    // ── Fixture: the REAL producer's shape ───────────────────────────────────────────────────
    // TaskService.TaskActivitySnapshot is what actually fills BeforeJson/AfterJson, with
    // JsonSerializerDefaults.Web (camelCase). A hand-rolled fixture that drifts from it would
    // give a green suite over input the app never produces, so the serializer options match
    // exactly and ProducerFieldNames_AreCoveredByTheDiffer (below) pins the field NAMES to the
    // producer itself by reflection.
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static string Snapshot(
        string status = "Todo",
        string priority = "Low",
        string? assignee = null,
        string name = "Ship the timeline",
        string? description = null,
        int featureId = 2,
        double sortOrder = 0,
        DateTime? completedAt = null) =>
        JsonSerializer.Serialize(new
        {
            id = 41,
            featureId,
            name,
            description,
            status,
            priority,
            assignee,
            claimedBy = (string?)null,
            claimedAt = (DateTime?)null,
            sortOrder,
            completedAt
        }, Web);

    private static ActivityEvent Event(
        int id, string eventType, DateTime at, string? before, string? after,
        string? message = null, string? actor = "claude") => new()
        {
            Id = id,
            ProjectId = 1,
            EntityType = "Task",
            EntityId = 41,
            Actor = actor,
            EventType = eventType,
            BeforeJson = before,
            AfterJson = after,
            Message = message,
            CreatedAt = at
        };

    private static TaskComment Comment(
        int id, string text, DateTime at,
        CommentType type = CommentType.Note, string? author = "User") => new()
        {
            Id = id,
            TaskItemId = 41,
            Text = text,
            Author = author,
            Type = type,
            CreatedAt = at
        };

    // ── THE guard (#328 AC1/AC2) ─────────────────────────────────────────────────────────────

    [Fact]
    public void Build_interleaves_activity_events_with_comments_newest_first()
    {
        var comments = new[]
        {
            Comment(1, "Starting on this", T0),
            Comment(2, "Handing off", T0.AddMinutes(20), CommentType.Handoff)
        };

        var events = new[]
        {
            Event(1, "TaskStatusChanged", T0.AddMinutes(10),
                Snapshot(status: "Todo"), Snapshot(status: "InProgress"),
                "Task 'Ship the timeline' status changed from Todo to InProgress"),
            Event(2, "TaskUpdated", T0.AddMinutes(30),
                Snapshot(priority: "Low"), Snapshot(priority: "High"),
                "Task 'Ship the timeline' updated: Priority")
        };

        var timeline = ActivityTimelineModel.Build(comments, events);

        // Newest first, and the two SOURCES are genuinely interleaved — not comments then events.
        Assert.Equal(new[] { "a-2", "c-2", "a-1", "c-1" }, timeline.Select(e => e.Key).ToArray());

        // The trail is present at all. This is the assertion that must go RED when
        // ActivityEvents are dropped from the merge.
        var fromTrail = timeline.Where(e => e.IsActivityEvent).ToList();
        Assert.Equal(2, fromTrail.Count);

        // AC2: each event renders as a readable change line derived from Before/After JSON.
        var status = timeline.Single(e => e.Key == "a-1");
        Assert.Equal(TimelineEntryKind.StatusChange, status.Kind);
        Assert.Equal("claude", status.Actor);
        Assert.Contains(status.Changes, c => c.Field == "Status" && c.Before == "Todo" && c.After == "InProgress");

        var priority = timeline.Single(e => e.Key == "a-2");
        Assert.Equal(TimelineEntryKind.FieldChange, priority.Kind);
        Assert.Contains(priority.Changes, c => c.Field == "Priority" && c.Before == "Low" && c.After == "High");

        // Comments keep their body; events never carry one.
        Assert.Equal("Handing off", timeline.Single(e => e.Key == "c-2").Body);
        Assert.All(fromTrail, e => Assert.Null(e.Body));
    }

    [Fact]
    public void Build_tolerates_null_inputs()
    {
        Assert.Empty(ActivityTimelineModel.Build(null, null));
        Assert.Single(ActivityTimelineModel.Build([Comment(1, "hi", T0)], null));
        Assert.Single(ActivityTimelineModel.Build(null,
            [Event(1, "TaskCreated", T0, null, Snapshot(), "Task created")]));
    }

    // ── The differ ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DeriveChanges_reports_only_the_fields_that_actually_changed()
    {
        var changes = ActivityTimelineModel.DeriveChanges(
            Snapshot(status: "Todo", priority: "Low", assignee: null),
            Snapshot(status: "Done", priority: "Low", assignee: "claude"));

        Assert.Equal(2, changes.Count);
        Assert.Contains(changes, c => c is { Field: "Status", Before: "Todo", After: "Done" });
        Assert.Contains(changes, c => c is { Field: "Assignee", After: "claude" }
                                      && c.Before == ActivityTimelineModel.EmptyValue);
        Assert.DoesNotContain(changes, c => c.Field == "Priority");
    }

    [Theory]
    [InlineData(null, null)]                                   // both absent
    [InlineData("", "")]                                       // both blank
    [InlineData("{ not json", "{ still not json")]             // malformed
    [InlineData("[1,2,3]", "[4,5,6]")]                         // valid JSON, wrong root kind
    [InlineData("\"a string\"", "\"another\"")]                // scalar root
    [InlineData("{\"totallyUnknown\":1}", "{\"totallyUnknown\":2}")] // renamed / foreign schema
    public void DeriveChanges_degrades_to_nothing_rather_than_leaking_the_payload(string? before, string? after)
    {
        Assert.Empty(ActivityTimelineModel.DeriveChanges(before, after));
    }

    [Fact]
    public void DeriveChanges_refuses_oversized_payloads()
    {
        // A task Description is unbounded, so a snapshot is too. Over the cap we do not parse —
        // the event still renders, from its Message.
        var huge = Snapshot(description: new string('x', ActivityTimelineModel.MaxJsonChars + 10));
        Assert.True(huge.Length > ActivityTimelineModel.MaxJsonChars);
        Assert.Empty(ActivityTimelineModel.DeriveChanges(Snapshot(), huge));
        Assert.Empty(ActivityTimelineModel.DeriveChanges(huge, Snapshot()));
    }

    [Fact]
    public void A_one_sided_snapshot_yields_no_diff_rather_than_a_wall_of_fake_changes()
    {
        // TaskCreated has no BeforeJson; TaskDeleted has no AfterJson. Diffing either against
        // nothing renders "Name: Ship the timeline → —", "Status: Todo → —" … — a list of
        // changes nobody made. The Message already says it correctly in one line.
        Assert.Empty(ActivityTimelineModel.DeriveChanges(null, Snapshot()));
        Assert.Empty(ActivityTimelineModel.DeriveChanges(Snapshot(), null));

        var created = Assert.Single(ActivityTimelineModel.Build(null,
            [Event(1, "TaskCreated", T0, null, Snapshot(), "Task 'Ship the timeline' created")]));
        Assert.Empty(created.Changes);
        Assert.Equal("Task 'Ship the timeline' created", created.Summary);
    }

    [Fact]
    public void DeriveChanges_drops_structured_values_instead_of_stringifying_them()
    {
        // If a snapshot field ever becomes an object/array, the ONLY way JSON could reach the
        // user is a stringified fallback. There isn't one: the whole field is dropped.
        var before = """{"status":"Todo","assignee":{"name":"claude","id":7}}""";
        var after = """{"status":"Done","assignee":{"name":"joel","id":9}}""";

        var changes = ActivityTimelineModel.DeriveChanges(before, after);

        Assert.Single(changes);
        Assert.Equal("Status", changes[0].Field);
        Assert.DoesNotContain(changes, c => c.Field == "Assignee");
    }

    [Fact]
    public void DeriveChanges_collapses_and_truncates_long_values()
    {
        var longText = new string('a', ActivityTimelineModel.MaxValueChars + 50);
        var changes = ActivityTimelineModel.DeriveChanges(
            Snapshot(description: "line one\n\n   line two"),
            Snapshot(description: longText));

        var description = changes.Single(c => c.Field == "Description");
        Assert.Equal("line one line two", description.Before);          // newlines collapsed to one line
        Assert.Equal(ActivityTimelineModel.MaxValueChars + 1, description.After.Length);
        Assert.EndsWith("…", description.After);
    }

    [Fact]
    public void An_edit_past_the_display_cap_is_still_reported_as_a_change()
    {
        // Truncation is for DISPLAY. Compare the truncated forms instead and two descriptions
        // differing at character 95 collapse to the identical 80-char string, so a real edit
        // reports as "no change" — and because a sibling field usually did change,
        // changes.Count > 0 also suppresses the Message that would have named it. The edit
        // disappears from the audit trail on a screen whose whole job is the audit trail.
        var head = new string('a', ActivityTimelineModel.MaxValueChars + 10);
        var before = head + "ORIGINAL ending";
        var after = head + "REWRITTEN ending";

        var changes = ActivityTimelineModel.DeriveChanges(
            Snapshot(description: before, priority: "Low"),
            Snapshot(description: after, priority: "High"));

        Assert.Contains(changes, c => c.Field == "Description");
        Assert.Contains(changes, c => c.Field == "Priority");

        // …and it is still truncated for display.
        var description = changes.Single(c => c.Field == "Description");
        Assert.EndsWith("…", description.Before);
        Assert.EndsWith("…", description.After);
        Assert.Equal(ActivityTimelineModel.MaxValueChars + 1, description.Before.Length);
    }

    [Fact]
    public void An_unchanged_field_is_not_reported_even_when_both_sides_exceed_the_cap()
    {
        // The other direction of the same guard: comparing untruncated must not make every long
        // field look changed.
        var same = new string('a', ActivityTimelineModel.MaxValueChars + 40) + " tail";

        var changes = ActivityTimelineModel.DeriveChanges(
            Snapshot(description: same, priority: "Low"),
            Snapshot(description: same, priority: "High"));

        Assert.DoesNotContain(changes, c => c.Field == "Description");
        Assert.Single(changes);
    }

    [Fact]
    public void The_snapshots_internal_bookkeeping_fields_stay_out_of_the_timeline()
    {
        // sortOrder rides along in the snapshot but TaskService.cs:639-648 says it "must NOT emit
        // an activity event or comment (the #88 audit boundary)" — and a board drag emits
        // TaskStatusChanged, whose snapshots carry the new rank. Tracking it would render
        // "Order: 3 → 1.5", an internal fractional rank (#307) shown nowhere else in the app.
        // featureId is excluded for a different reason: a raw FK is not the human-readable line
        // AC2 asks for, and this model is pure — it has no DbContext to resolve a name with.
        var changes = ActivityTimelineModel.DeriveChanges(
            Snapshot(status: "Todo", sortOrder: 3, featureId: 2),
            Snapshot(status: "InProgress", sortOrder: 1.5, featureId: 5));

        Assert.Single(changes);
        Assert.Equal("Status", changes[0].Field);
    }

    [Fact]
    public void A_feature_move_falls_back_to_its_message_rather_than_a_raw_foreign_key()
    {
        var entry = Assert.Single(ActivityTimelineModel.Build(null,
        [
            Event(1, "TaskFeatureChanged", T0, Snapshot(featureId: 2), Snapshot(featureId: 5),
                "Task 'Ship the timeline' reassigned from feature 2 to feature 5")
        ]));

        Assert.Empty(entry.Changes);
        Assert.Equal("Task 'Ship the timeline' reassigned from feature 2 to feature 5", entry.Summary);
    }

    [Fact]
    public void DeriveChanges_matches_field_names_case_insensitively()
    {
        // The snapshot is camelCase today. A serializer-option change must degrade to
        // "still works", not to a silently blank timeline.
        var changes = ActivityTimelineModel.DeriveChanges(
            """{"Status":"Todo"}""", """{"Status":"Review"}""");

        Assert.Contains(changes, c => c is { Field: "Status", Before: "Todo", After: "Review" });
    }

    // ── Fallback: Message, never JSON (#328 AC2) ─────────────────────────────────────────────

    [Fact]
    public void An_event_with_no_derivable_diff_falls_back_to_its_message_and_never_to_json()
    {
        var payload = """{"someFieldWeDoNotKnow":"and its value","nested":{"deep":1}}""";

        var entry = Assert.Single(ActivityTimelineModel.Build(null,
            [Event(9, "TaskWeirdEvent", T0, payload, payload, "Something happened to the task")]));

        Assert.Empty(entry.Changes);
        Assert.Equal("Something happened to the task", entry.Summary);

        // Scan EVERY string the component can render, including the change lines — the summary
        // alone is a value this test set itself, so asserting only over it would pass against a
        // differ that leaked through Changes.
        var rendered = new List<string?> { entry.Summary, entry.Label, entry.Actor, entry.Body };
        rendered.AddRange(entry.Changes.SelectMany(c => new[] { c.Field, c.Before, c.After }));

        foreach (var text in rendered)
        {
            if (text is null) continue;
            Assert.DoesNotContain("{", text, StringComparison.Ordinal);
            Assert.DoesNotContain("\"", text, StringComparison.Ordinal);
            Assert.DoesNotContain("someFieldWeDoNotKnow", text, StringComparison.Ordinal);
            Assert.DoesNotContain("and its value", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_payload_that_mixes_known_and_unknown_keys_renders_only_the_known_ones()
    {
        // The load-bearing half: a payload the differ CAN partly read must still not carry the
        // unreadable half through. Same shape as a snapshot gaining a field we do not label yet.
        var before = """{"status":"Todo","secretInternalRank":{"a":1},"unlabelledNewField":"leak-me"}""";
        var after = """{"status":"Done","secretInternalRank":{"a":2},"unlabelledNewField":"leak-me-too"}""";

        var entry = Assert.Single(ActivityTimelineModel.Build(null,
            [Event(1, "TaskStatusChanged", T0, before, after, "status changed")]));

        var change = Assert.Single(entry.Changes);
        Assert.Equal("Status", change.Field);

        var rendered = new List<string?> { entry.Summary, entry.Label, entry.Actor, entry.Body };
        rendered.AddRange(entry.Changes.SelectMany(c => new[] { c.Field, c.Before, c.After }));
        foreach (var text in rendered.Where(t => t is not null))
        {
            Assert.DoesNotContain("leak-me", text!, StringComparison.Ordinal);
            Assert.DoesNotContain("secretInternalRank", text!, StringComparison.Ordinal);
            Assert.DoesNotContain("{", text!, StringComparison.Ordinal);
        }
    }

    // ── Ordering + the entry cap ─────────────────────────────────────────────────────────────

    [Fact]
    public void Same_instant_rows_tiebreak_numerically_not_lexicographically()
    {
        // Ordering by the string key sorts "c-9" above "c-10". Ten comments stamped at the same
        // second is not hypothetical: a bulk import or a mirrored GitHub sync writes exactly that.
        var sameInstant = Enumerable.Range(1, 12)
            .Select(i => Comment(i, $"comment {i}", T0))
            .ToList();

        var keys = ActivityTimelineModel.Build(sameInstant, null).Select(e => e.Key).ToArray();

        Assert.Equal(
            Enumerable.Range(1, 12).Reverse().Select(i => $"c-{i}").ToArray(),
            keys);
    }

    [Fact]
    public void The_entry_cap_keeps_the_newest_and_reports_what_it_hid()
    {
        // A silent cap on the surface whose whole job is audit honesty is the same class of
        // untruth this issue exists to fix — so Build tells the caller how much it dropped.
        var many = Enumerable.Range(1, ActivityTimelineModel.MaxEntries + 25)
            .Select(i => Comment(i, $"comment {i}", T0.AddMinutes(i)))
            .ToList();

        var timeline = ActivityTimelineModel.Build(many, null, out var totalBeforeCap);

        Assert.Equal(ActivityTimelineModel.MaxEntries, timeline.Count);
        Assert.Equal(ActivityTimelineModel.MaxEntries + 25, totalBeforeCap);
        Assert.Equal($"c-{ActivityTimelineModel.MaxEntries + 25}", timeline[0].Key);   // newest kept
        Assert.DoesNotContain(timeline, e => e.Key == "c-1");                          // oldest dropped
    }

    [Fact]
    public void An_uncapped_timeline_reports_no_hidden_rows()
    {
        ActivityTimelineModel.Build([Comment(1, "only one", T0)], null, out var totalBeforeCap);
        Assert.Equal(1, totalBeforeCap);
    }

    [Fact]
    public void An_event_with_neither_diff_nor_message_still_renders_without_json()
    {
        var entry = Assert.Single(ActivityTimelineModel.Build(null,
            [Event(9, "TaskCreated", T0, null, """{"unknown":"payload"}""", message: null)]));

        Assert.Empty(entry.Changes);
        Assert.Null(entry.Summary);
        Assert.Equal("Created", entry.Label);   // the label + timestamp still say something happened
    }

    // ── Duplicate suppression ────────────────────────────────────────────────────────────────

    [Fact]
    public void A_TaskCommentAdded_event_is_suppressed_when_its_comment_row_is_present()
    {
        // AddCommentAsync writes the comment AND a TaskCommentAdded event in one SaveChanges.
        // Rendering both is the same sentence twice.
        var timeline = ActivityTimelineModel.Build(
            [Comment(1, "Please review", T0)],
            [Event(5, "TaskCommentAdded", T0.AddMilliseconds(3), null,
                """{"text":"Please review","author":"User"}""", "Comment added")]);

        var entry = Assert.Single(timeline);
        Assert.Equal("c-1", entry.Key);
        Assert.Equal("Please review", entry.Body);
    }

    [Fact]
    public void A_TaskCommentAdded_event_survives_when_its_comment_row_is_gone()
    {
        // Suppression must be evidence-based, not assumed: with no comment to stand in for it,
        // dropping the event would silently shorten the audit trail.
        var timeline = ActivityTimelineModel.Build(
            [Comment(1, "unrelated, much later", T0.AddHours(3))],
            [Event(5, "TaskCommentAdded", T0, null, null, "Comment added")]);

        Assert.Contains(timeline, e => e.Key == "a-5");
    }

    [Fact]
    public void A_system_status_comment_is_suppressed_when_its_activity_event_is_present()
    {
        var timeline = ActivityTimelineModel.Build(
            [Comment(1, "Status changed from Todo to InProgress", T0, CommentType.StatusChange, "User")],
            [Event(5, "TaskStatusChanged", T0.AddMilliseconds(2),
                Snapshot(status: "Todo"), Snapshot(status: "InProgress"),
                "Task status changed from Todo to InProgress")]);

        var entry = Assert.Single(timeline);
        Assert.Equal("a-5", entry.Key);
        Assert.Contains(entry.Changes, c => c is { Field: "Status", Before: "Todo", After: "InProgress" });
    }

    [Fact]
    public void A_legacy_system_comment_with_no_matching_event_is_kept()
    {
        // History that predates the uniform audit boundary (#88) has the comment and no event.
        // Suppressing by TYPE rather than by EVIDENCE would erase it.
        var timeline = ActivityTimelineModel.Build(
            [Comment(1, "Status changed from Todo to InProgress", T0, CommentType.StatusChange, "System")],
            []);

        var entry = Assert.Single(timeline);
        Assert.Equal("c-1", entry.Key);
        Assert.Equal(TimelineEntryKind.StatusChange, entry.Kind);
        Assert.Equal("System", entry.Actor);
    }

    [Fact]
    public void A_system_comment_is_not_suppressed_by_an_unrelated_event_at_the_same_instant()
    {
        // Same instant, WRONG event type — a feature move does not account for a status comment.
        var timeline = ActivityTimelineModel.Build(
            [Comment(1, "Status changed from Todo to InProgress", T0, CommentType.StatusChange, "System")],
            [Event(5, "TaskFeatureChanged", T0, Snapshot(featureId: 2), Snapshot(featureId: 3), "moved")]);

        Assert.Equal(2, timeline.Count);
    }

    // ── Filter chips (#328 AC4) ──────────────────────────────────────────────────────────────

    [Fact]
    public void Filter_chips_partition_the_timeline_by_meaning_not_by_source_table()
    {
        var timeline = ActivityTimelineModel.Build(
            [
                Comment(1, "a note", T0),
                // A legacy system status comment counts as STATUS even though it lives in the
                // comments table — the chips filter on meaning.
                Comment(2, "Status changed from Todo to Review", T0.AddHours(1), CommentType.StatusChange, "System")
            ],
            [
                Event(3, "TaskStatusChanged", T0.AddHours(2), Snapshot(status: "Review"), Snapshot(status: "Done"), "done"),
                Event(4, "TaskUpdated", T0.AddHours(3), Snapshot(priority: "Low"), Snapshot(priority: "High"), "updated")
            ]);

        var counts = ActivityTimelineModel.Count(timeline);
        Assert.Equal(4, counts.All);
        Assert.Equal(1, counts.Comments);
        Assert.Equal(2, counts.Status);
        Assert.Equal(1, counts.FieldChanges);

        Assert.Equal(4, ActivityTimelineModel.Filter(timeline, TimelineFilter.All).Count);
        Assert.Equal(["c-1"], ActivityTimelineModel.Filter(timeline, TimelineFilter.Comments).Select(e => e.Key));
        Assert.Equal(["a-3", "c-2"], ActivityTimelineModel.Filter(timeline, TimelineFilter.Status).Select(e => e.Key));
        Assert.Equal(["a-4"], ActivityTimelineModel.Filter(timeline, TimelineFilter.FieldChanges).Select(e => e.Key));

        // The chip counts and the chip contents must agree — a badge that disagrees with the
        // list below it is worse than no badge.
        Assert.Equal(counts.Comments, ActivityTimelineModel.Filter(timeline, TimelineFilter.Comments).Count);
        Assert.Equal(counts.Status, ActivityTimelineModel.Filter(timeline, TimelineFilter.Status).Count);
        Assert.Equal(counts.FieldChanges, ActivityTimelineModel.Filter(timeline, TimelineFilter.FieldChanges).Count);
        Assert.Equal(counts.All, counts.Comments + counts.Status + counts.FieldChanges);
    }

    [Fact]
    public void A_blank_actor_reads_as_System_on_both_sources()
    {
        var timeline = ActivityTimelineModel.Build(
            [Comment(1, "no author", T0, CommentType.Note, author: "   ")],
            [Event(2, "TaskUpdated", T0.AddMinutes(1), Snapshot(), Snapshot(name: "x"), "updated", actor: null)]);

        Assert.All(timeline, e => Assert.Equal(ActivityTimelineModel.UnknownActor, e.Actor));
    }

    // ── ONE CLOCK (#306 folded into #328) ────────────────────────────────────────────────────

    [Fact]
    public void Absolute_renders_the_stored_UTC_instant_in_local_time()
    {
        var offset = TimeZoneInfo.Local.GetUtcOffset(T0);

        Assert.Equal(T0.ToLocalTime().ToString("MMM d, yyyy h:mm tt"), ActivityTimelineModel.Absolute(T0));

        // On a UTC box the two strings are identical and this proves nothing, so only assert the
        // difference where there IS one. (In US Eastern — the repo owner's zone — this is the
        // exact 4-hour split #306 documents: the comment said 9:19 AM, "Updated:" said 1:19 PM.)
        if (offset != TimeSpan.Zero)
        {
            Assert.NotEqual(T0.ToString("MMM d, yyyy h:mm tt"), ActivityTimelineModel.Absolute(T0));
        }
    }

    [Fact]
    public void A_DateTime_read_back_from_SQLite_with_Unspecified_kind_is_still_treated_as_UTC()
    {
        // EF/SQLite hands these back as Kind=Unspecified. DateTime.ToLocalTime() treats an
        // Unspecified value as UTC, which is what makes one convention possible at all — and is
        // what the two already-correct comment rows relied on before this change.
        var unspecified = DateTime.SpecifyKind(T0, DateTimeKind.Unspecified);
        Assert.Equal(ActivityTimelineModel.Absolute(T0), ActivityTimelineModel.Absolute(unspecified));
    }

    [Fact]
    public void Tooltip_carries_both_the_local_time_and_the_round_trip_UTC_instant()
    {
        var tooltip = ActivityTimelineModel.Tooltip(T0);

        Assert.Contains(ActivityTimelineModel.Absolute(T0), tooltip, StringComparison.Ordinal);
        Assert.Contains($"UTC: {T0:O}", tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void The_UTC_tooltip_is_tagged_UTC_even_for_the_Unspecified_kind_the_database_returns()
    {
        // The Kind production actually hands us. `ToString("O")` on an Unspecified DateTime emits
        // NO zone designator, so a tooltip built straight off it says "UTC:" above a string that
        // is not a UTC instant, and <time datetime> claims local to every machine consumer.
        // Asserting only over a Kind=Utc fixture passes against exactly that bug — the one Kind
        // the app never produces is the one that hides it.
        var fromDatabase = DateTime.SpecifyKind(T0, DateTimeKind.Unspecified);

        Assert.EndsWith("Z", ActivityTimelineModel.RoundTripUtc(fromDatabase), StringComparison.Ordinal);
        Assert.Equal(ActivityTimelineModel.RoundTripUtc(T0), ActivityTimelineModel.RoundTripUtc(fromDatabase));
        Assert.Contains($"UTC: {T0:O}", ActivityTimelineModel.Tooltip(fromDatabase), StringComparison.Ordinal);
    }

    [Fact]
    public void A_local_kind_value_is_converted_rather_than_relabelled()
    {
        // Defence in depth: nothing passes Local today, but silently stamping "Utc" on a Local
        // value would shift the instant by the whole offset.
        var local = T0.ToLocalTime();
        Assert.Equal(ActivityTimelineModel.RoundTripUtc(T0), ActivityTimelineModel.RoundTripUtc(local));
        Assert.Equal(ActivityTimelineModel.Absolute(T0), ActivityTimelineModel.Absolute(local));
    }

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(44, "just now")]
    [InlineData(60 * 5, "5m ago")]
    [InlineData(60 * 60 * 3, "3h ago")]
    [InlineData(60 * 60 * 24 * 2, "2d ago")]
    public void Relative_reads_as_elapsed_time(int elapsedSeconds, string expected)
    {
        var now = T0.AddSeconds(elapsedSeconds);
        Assert.Equal(expected, ActivityTimelineModel.Relative(T0, now));
    }

    [Fact]
    public void Relative_never_renders_a_negative_age()
    {
        // Clock skew, or a row stamped a moment ahead of the render, must not read "-3m ago".
        Assert.Equal("just now", ActivityTimelineModel.Relative(T0.AddMinutes(5), T0));
    }

    [Fact]
    public void Relative_falls_back_to_the_absolute_local_date_beyond_a_month()
    {
        var now = T0.AddDays(45);
        Assert.Equal(ActivityTimelineModel.Absolute(T0), ActivityTimelineModel.Relative(T0, now));
    }

    // ── The whitelist is pinned to the REAL producer ─────────────────────────────────────────

    [Fact]
    public void The_differ_understands_the_field_names_TaskService_actually_emits()
    {
        // The differ is a WHITELIST over TaskService.TaskActivitySnapshot's keys. A rename there
        // would silently blank every change line while the suite stayed green on hand-written
        // fixtures — so read the producer's own output and diff against it. (The lookup is
        // runtime reflection, so a rename fails THIS TEST, not the compiler.)
        var snapshotMethod = typeof(TaskService).GetMethod(
            "TaskActivitySnapshot", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.True(snapshotMethod is not null,
            "TaskService.TaskActivitySnapshot could not be found. It is what fills " +
            "ActivityEvent.BeforeJson/AfterJson, and ActivityTimelineModel's field whitelist is " +
            "keyed to the names it emits. If it moved or was renamed, re-check that whitelist " +
            "before deleting this test — a stale whitelist blanks every change line in the UI.");

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        string Real(WorkStatus status, TaskPriority priority, string? assignee) =>
            JsonSerializer.Serialize(snapshotMethod!.Invoke(null, [new TaskItem
            {
                Id = 41,
                FeatureId = 2,
                Name = "Ship the timeline",
                Status = status,
                Priority = priority,
                Assignee = assignee
            }]), options);

        var changes = ActivityTimelineModel.DeriveChanges(
            Real(WorkStatus.Todo, TaskPriority.Low, null),
            Real(WorkStatus.InProgress, TaskPriority.High, "claude"));

        Assert.Contains(changes, c => c is { Field: "Status", Before: "Todo", After: "InProgress" });
        Assert.Contains(changes, c => c is { Field: "Priority", Before: "Low", After: "High" });
        Assert.Contains(changes, c => c is { Field: "Assignee", After: "claude" });

        // And the hand-written fixture in this file agrees with the real one, so every other
        // test here is exercising the shape the app genuinely stores.
        Assert.Equal(
            JsonDocument.Parse(Real(WorkStatus.Todo, TaskPriority.Low, null)).RootElement
                .EnumerateObject().Select(p => p.Name).Order().ToArray(),
            JsonDocument.Parse(Snapshot()).RootElement
                .EnumerateObject().Select(p => p.Name).Order().ToArray());
    }
}
