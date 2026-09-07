using System.Globalization;
using System.Text.Json;
using KanbanFlow.Components.Shared;
using KanbanFlow.Models;
using KanbanFlow.Services;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Tests;

/// <summary>
/// Regression tests for the unified task history (#328).
///
/// These exercise the REAL merge the UI uses — <see cref="ActivityTimelineBuilder"/> is the exact
/// type <c>ActivityTimeline.razor</c> calls, not a re-implementation. Several of the cases below
/// are driven by rows the REAL <see cref="TaskService"/> wrote against REAL SQLite, so the
/// fixtures cannot drift from what the producer actually emits.
/// </summary>
public class ActivityTimelineTests
{
    private static readonly DateTime T0 = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

    private static TaskComment Comment(int id, string text, CommentType type, DateTime at, string? author = "System")
        => new() { Id = id, TaskItemId = 1, Text = text, Type = type, CreatedAt = at, Author = author };

    private static ActivityEvent Event(
        int id, string eventType, DateTime at,
        string? before = null, string? after = null, string? message = null, string? actor = "User")
        => new()
        {
            Id = id,
            ProjectId = 1,
            EntityType = "Task",
            EntityId = 1,
            EventType = eventType,
            CreatedAt = at,
            BeforeJson = before,
            AfterJson = after,
            Message = message,
            Actor = actor
        };

    private static string Snapshot(object o) => JsonSerializer.Serialize(o, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  1. THE CORE REGRESSION — the gap this feature exists to close.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The bug: field-level changes were recorded in ActivityEvent and never rendered. This fails
    /// against any build whose timeline ignores events.
    /// </summary>
    [Fact]
    public void Build_RendersFieldChangesFromActivityEvents_NotJustComments()
    {
        var events = new[]
        {
            Event(10, "TaskUpdated", T0,
                before: Snapshot(new { Status = "Todo", Priority = "Low", Assignee = (string?)null }),
                after: Snapshot(new { Status = "Todo", Priority = "High", Assignee = "claude" }),
                message: "Task 'x' updated: Priority, Assignee")
        };

        var timeline = ActivityTimelineBuilder.Build(comments: [], events: events);

        var entry = Assert.Single(timeline);
        Assert.Equal("TaskUpdated", entry.Kind);
        Assert.Collection(entry.Changes,
            c =>
            {
                Assert.Equal("Priority", c.Label);
                Assert.Equal("Low", c.Before);
                Assert.Equal("High", c.After);
            },
            c =>
            {
                Assert.Equal("Assigned", c.Label);
                Assert.Equal("—", c.Before);      // JSON null renders as an em dash, not "null"
                Assert.Equal("claude", c.After);
            });
    }

    /// <summary>
    /// THE DOUBLE-RENDER GUARD. TaskService writes a StatusChange COMMENT and a TaskStatusChanged
    /// EVENT for one transition (TaskService.cs:76-88 and :304-317). A naive concatenation shows
    /// it twice — which is the exact bug a unified timeline exists to remove.
    ///
    /// This asserts the COUNT, not mere presence: a presence check passes on the buggy build.
    /// </summary>
    [Fact]
    public void Build_SingleStatusChange_ProducesExactlyOneRow()
    {
        var comments = new[]
        {
            Comment(1, "Status changed from Todo to InProgress", CommentType.StatusChange, T0)
        };
        var events = new[]
        {
            Event(10, "TaskStatusChanged", T0.AddMilliseconds(3),
                before: Snapshot(new { Status = "Todo" }),
                after: Snapshot(new { Status = "InProgress" }),
                message: "Task 'x' status changed from Todo to InProgress")
        };

        var timeline = ActivityTimelineBuilder.Build(comments, events);

        Assert.Single(timeline);
        // The EVENT survives, because it is the one that carries the before/after.
        var entry = timeline[0];
        Assert.Equal("TaskStatusChanged", entry.Kind);
        Assert.False(entry.FromComment);
        var change = Assert.Single(entry.Changes);
        Assert.Equal("Status", change.Label);
        Assert.Equal("Todo", change.Before);
        Assert.Equal("InProgress", change.After);
    }

    /// <summary>
    /// The other direction: AddCommentAsync writes the comment AND a TaskCommentAdded event
    /// (TaskService.cs:685-687). Exactly one row, and it must be the COMMENT — that is the side
    /// carrying the markdown body.
    /// </summary>
    [Fact]
    public void Build_UserComment_ProducesExactlyOneRow_KeepingTheMarkdownBody()
    {
        var comments = new[] { Comment(1, "**bold** note", CommentType.Note, T0, author: "User") };
        var events = new[]
        {
            Event(10, "TaskCommentAdded", T0.AddMilliseconds(2),
                after: Snapshot(new { Text = "**bold** note", Author = "User" }),
                message: "Comment added")
        };

        var timeline = ActivityTimelineBuilder.Build(comments, events);

        var entry = Assert.Single(timeline);
        Assert.True(entry.FromComment);
        Assert.Equal("**bold** note", entry.Markdown);
    }

    /// <summary>
    /// De-duplication must require POSITIVE evidence of the duplicate. A status-change comment
    /// with NO paired event (rows predating the audit trail, or a path that emits none) is unique
    /// history and must survive — suppressing it would DELETE history in the name of merging it.
    /// </summary>
    [Fact]
    public void Build_SystemComment_WithNoPairedEvent_IsKept()
    {
        var comments = new[] { Comment(1, "Status changed from Todo to Done", CommentType.StatusChange, T0) };

        var timeline = ActivityTimelineBuilder.Build(comments, events: []);

        var entry = Assert.Single(timeline);
        Assert.True(entry.FromComment);
        Assert.Equal(TimelineCategory.Status, entry.Category);
    }

    /// <summary>
    /// The pairing window must not swallow a genuinely later change. Two status changes minutes
    /// apart are two events, not one.
    /// </summary>
    [Fact]
    public void Build_CommentOutsidePairingWindow_IsNotSuppressed()
    {
        var comments = new[] { Comment(1, "Status changed from Todo to InProgress", CommentType.StatusChange, T0) };
        var events = new[] { Event(10, "TaskStatusChanged", T0.AddMinutes(5), message: "later transition") };

        var timeline = ActivityTimelineBuilder.Build(comments, events);

        Assert.Equal(2, timeline.Count);
    }

    /// <summary>
    /// `Note` is user-authorable AND the type the service uses for its "Updated: …" shadow
    /// comment. A human note must never be suppressed just because a TaskUpdated event happens to
    /// land in the same second.
    /// </summary>
    [Fact]
    public void Build_UserNote_IsNotSuppressedByACoincidentTaskUpdatedEvent()
    {
        var comments = new[] { Comment(1, "Remember to check the migration", CommentType.Note, T0, "User") };
        var events = new[] { Event(10, "TaskUpdated", T0, message: "Task 'x' updated: Name") };

        var timeline = ActivityTimelineBuilder.Build(comments, events);

        Assert.Equal(2, timeline.Count);
        Assert.Contains(timeline, e => e.FromComment && e.Markdown == "Remember to check the migration");
    }

    /// <summary>…but the service's own "Updated: …" note IS its shadow and is suppressed.</summary>
    [Fact]
    public void Build_ServiceUpdatedNote_IsSuppressedByItsTaskUpdatedEvent()
    {
        var comments = new[] { Comment(1, "Updated: Name, Priority", CommentType.Note, T0) };
        var events = new[] { Event(10, "TaskUpdated", T0, message: "Task 'x' updated: Name, Priority") };

        var timeline = ActivityTimelineBuilder.Build(comments, events);

        var entry = Assert.Single(timeline);
        Assert.False(entry.FromComment);
    }

    /// <summary>
    /// THE NO-HISTORY-LOSS INVARIANT — found empirically, not by inspection.
    ///
    /// A 211-event task rendered only 203 rows: pairing was MANY-to-one, so under a burst of
    /// changes (an agent flipping status several times a second) one event suppressed several
    /// unrelated comments and eight rows vanished. The merge deleted the history it exists to
    /// present.
    ///
    /// The invariant: suppressions can never exceed genuine pairs. With N events and N comments
    /// where only K events are loaded (a truncated audit window), the result must be
    /// K events + (N − K) surviving comments = N rows. Never fewer.
    /// </summary>
    [Fact]
    public void Build_RapidFireChangesWithATruncatedEventWindow_LosesNoRows()
    {
        const int total = 60;
        const int loadedEvents = 40;   // the newest 40, as a capped window would return

        var comments = new List<TaskComment>();
        var events = new List<ActivityEvent>();
        for (var i = 0; i < total; i++)
        {
            // 200ms apart — far inside the 2s pairing window, so EVERY event is "near" ~20 comments.
            var at = T0.AddMilliseconds(200 * i);
            comments.Add(Comment(i + 1, $"Status changed from Todo to InProgress ({i})", CommentType.StatusChange, at));
            events.Add(Event(1000 + i, "TaskStatusChanged", at.AddMilliseconds(3),
                before: Snapshot(new { Status = "Todo" }), after: Snapshot(new { Status = "InProgress" }),
                message: $"transition {i}"));
        }

        var windowed = events.Skip(total - loadedEvents).ToList();

        var timeline = ActivityTimelineBuilder.Build(comments, windowed);

        Assert.Equal(total, timeline.Count);
        Assert.Equal(loadedEvents, timeline.Count(e => !e.FromComment));
        Assert.Equal(total - loadedEvents, timeline.Count(e => e.FromComment));

        // ATTRIBUTION, not just arithmetic. Each event must have absorbed ITS OWN comment (3ms
        // away), not a neighbour's (200ms away) — so the surviving comments are exactly the
        // OLDEST ones, whose events fell outside the window. Assigning in comment order instead
        // of best-match order keeps the count right while scattering the survivors arbitrarily,
        // and only this assertion catches that.
        var survivingCommentIds = timeline.Where(e => e.FromComment).Select(e => e.SourceId).OrderBy(x => x);
        Assert.Equal(Enumerable.Range(1, total - loadedEvents), survivingCommentIds);
    }

    /// <summary>
    /// The same invariant with the window intact: N pairs collapse to exactly N rows, all events.
    /// </summary>
    [Fact]
    public void Build_RapidFireChangesWithTheFullWindow_CollapsesToExactlyOneRowPerAction()
    {
        const int total = 40;
        var comments = new List<TaskComment>();
        var events = new List<ActivityEvent>();
        for (var i = 0; i < total; i++)
        {
            var at = T0.AddMilliseconds(200 * i);
            comments.Add(Comment(i + 1, $"Status changed ({i})", CommentType.StatusChange, at));
            events.Add(Event(1000 + i, "TaskStatusChanged", at.AddMilliseconds(3), message: $"t{i}"));
        }

        var timeline = ActivityTimelineBuilder.Build(comments, events);

        Assert.Equal(total, timeline.Count);
        Assert.All(timeline, e => Assert.False(e.FromComment));
    }

    /// <summary>
    /// Same one-to-one rule in the other direction: two comments posted in the same second, each
    /// with its own TaskCommentAdded event, must render as two rows — not one comment plus a
    /// stranded event, and not four.
    /// </summary>
    [Fact]
    public void Build_TwoCommentsInTheSameSecond_EachPairsWithItsOwnEvent()
    {
        var comments = new[]
        {
            Comment(1, "first", CommentType.Note, T0, "User"),
            Comment(2, "second", CommentType.Note, T0.AddMilliseconds(300), "User"),
        };
        var events = new[]
        {
            Event(10, "TaskCommentAdded", T0.AddMilliseconds(2), after: Snapshot(new { Text = "first", Author = "User" })),
            Event(11, "TaskCommentAdded", T0.AddMilliseconds(302), after: Snapshot(new { Text = "second", Author = "User" })),
        };

        var timeline = ActivityTimelineBuilder.Build(comments, events);

        Assert.Equal(2, timeline.Count);
        Assert.All(timeline, e => Assert.True(e.FromComment));
        Assert.Equal(new[] { "second", "first" }, timeline.Select(e => e.Markdown).ToArray());
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  1b. THE TWO PASSES MUST STAY DISJOINT
    //
    //  MatchBestFirst enforces one-to-one WITHIN a call. Nothing enforces it ACROSS the two
    //  passes except the two facts below — and the code used to claim otherwise: it threaded two
    //  `used` arrays through MatchBestFirst, documented as "letting a later pass respect an
    //  earlier pass's claims". They were dead. MatchBestFirst returns a fully materialised list,
    //  so both callers filled their array only AFTER the call returned; every element was false
    //  at every point it was read. Deleting them changed no behaviour, which is precisely why the
    //  real guarantees need tests: they were the only thing holding, and nothing said so.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// FACT 1 — a comment spent by pass 1 cannot be spent again by pass 2.
    ///
    /// One status change (comment + its TaskStatusChanged event) with an unrelated
    /// TaskCommentAdded event landing in the same instant, carrying the SAME text. Pass 1 spends
    /// the comment on the status event. If `!suppressedComment[ci]` were dropped from pass 2's
    /// predicate, that already-spent comment would absorb the TaskCommentAdded event as well —
    /// one comment silently deleting TWO events. The event must survive as its own row.
    /// </summary>
    [Fact]
    public void Build_TheTwoPassesClaimDisjointComments_APass1CommentCannotAbsorbASecondEvent()
    {
        const string text = "Status changed from Todo to InProgress";
        var comments = new[] { Comment(1, text, CommentType.StatusChange, T0) };
        var events = new[]
        {
            Event(10, "TaskStatusChanged", T0.AddMilliseconds(3),
                before: Snapshot(new { Status = "Todo" }), after: Snapshot(new { Status = "InProgress" }),
                message: "status"),
            Event(11, "TaskCommentAdded", T0.AddMilliseconds(1), after: Snapshot(new { Text = text, Author = "System" })),
        };

        var timeline = ActivityTimelineBuilder.Build(comments, events);

        // The comment is gone (pass 1). BOTH events remain: two records, two rows.
        Assert.Equal(2, timeline.Count);
        Assert.All(timeline, e => Assert.False(e.FromComment));
        Assert.Equal(new[] { "TaskStatusChanged", "TaskCommentAdded" }, timeline.Select(e => e.Kind).ToArray());
    }

    /// <summary>
    /// FACT 2 — the two passes draw from disjoint sets of EVENTS, because no comment type is
    /// superseded by "TaskCommentAdded" (pass 2's exclusive event type).
    ///
    /// This is the fact that decays silently. Nothing in the compiler stops someone adding
    /// <c>[CommentType.X] = "TaskCommentAdded"</c> to the supersession map; the suite would stay
    /// green while pass 1 and pass 2 both claimed the same event and one action rendered as none.
    ///
    /// Asserted over the WHOLE function, not the dictionary alone: SupersedingEventTypeFor has a
    /// second branch (the service's "Updated: …" Note prefix), and every CommentType goes through
    /// both. Enumerating the enum means a new member cannot be added outside this guard.
    /// </summary>
    [Fact]
    public void Build_TheTwoPassesClaimDisjointEvents_NoCommentTypeIsSupersededByTaskCommentAdded()
    {
        var resolve = typeof(ActivityTimelineBuilder).GetMethod(
            "SupersedingEventTypeFor",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(resolve);   // renamed away ⇒ this guard is not watching anything

        foreach (var type in Enum.GetValues<CommentType>())
        {
            // Both branches: an ordinary body, and the service's "Updated: …" shadow-note prefix.
            foreach (var text in new[] { "some text", "Updated: Name, Priority" })
            {
                var supersededBy = (string?)resolve!.Invoke(null, [Comment(1, text, type, T0)]);
                Assert.False(
                    supersededBy == "TaskCommentAdded",
                    $"CommentType.{type} (text: \"{text}\") is superseded by \"TaskCommentAdded\", which is " +
                    "pass 2's exclusive event type. Pass 1 and pass 2 can now claim the same event, " +
                    "so one action is suppressed twice and vanishes from the history.");
            }
        }
    }

    /// <summary>
    /// A TaskCommentAdded event whose text matches NO loaded comment is unique history — the
    /// comment row may have been deleted, or fallen outside the loaded page. Suppressing it on
    /// time proximity alone would silently drop it.
    ///
    /// The positive case (<c>Build_TwoCommentsInTheSameSecond…</c>) cannot prove this: best-first
    /// cost ordering alone produces the same answer there, so it passes with TextMatches removed.
    /// </summary>
    [Fact]
    public void Build_CommentAddedEvent_WithNonMatchingText_IsNotSuppressed()
    {
        var comments = new[] { Comment(1, "mine", CommentType.Note, T0, "User") };
        var events = new[]
        {
            Event(10, "TaskCommentAdded", T0.AddMilliseconds(2),
                after: Snapshot(new { Text = "someone else's deleted comment", Author = "bob" })),
        };

        var timeline = ActivityTimelineBuilder.Build(comments, events);

        Assert.Equal(2, timeline.Count);
        Assert.Contains(timeline, e => e.FromComment && e.Markdown == "mine");
        Assert.Contains(timeline, e => !e.FromComment && e.Kind == "TaskCommentAdded");
    }

    /// <summary>
    /// The documented legacy fallback: a row whose AfterJson carries no parsable text pairs on
    /// time alone. Rows written before the snapshot shape existed must still de-duplicate.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[1,2,3]")]
    public void Build_CommentAddedEvent_WithNoParsableText_FallsBackToTimeAlone(string? afterJson)
    {
        var comments = new[] { Comment(1, "mine", CommentType.Note, T0, "User") };
        var events = new[] { Event(10, "TaskCommentAdded", T0.AddMilliseconds(2), after: afterJson) };

        var entry = Assert.Single(ActivityTimelineBuilder.Build(comments, events));
        Assert.True(entry.FromComment);
        Assert.Equal("mine", entry.Markdown);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  2. ORDERING
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Build_OrdersNewestFirst_AndIsDeterministicOnTiedTimestamps()
    {
        var comments = new[]
        {
            Comment(1, "old", CommentType.Note, T0, "User"),
            Comment(2, "newer", CommentType.Note, T0.AddMinutes(10), "User"),
        };
        var events = new[]
        {
            Event(10, "TaskClaimed", T0.AddMinutes(20), message: "claimed"),
            // Same instant as event 10 — the Id tiebreak has to make this stable.
            Event(11, "TaskReleased", T0.AddMinutes(20), message: "released"),
        };

        var first = ActivityTimelineBuilder.Build(comments, events);
        var second = ActivityTimelineBuilder.Build(comments.Reverse(), events.Reverse());

        Assert.Equal(
            new[] { "released", "claimed", "newer", "old" },
            first.Select(e => e.Headline ?? e.Markdown).ToArray());
        Assert.Equal(
            first.Select(e => (e.SourceId, e.FromComment)).ToArray(),
            second.Select(e => (e.SourceId, e.FromComment)).ToArray());
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  3. THE DIFFER MUST NOT INVENT HISTORY, AND MUST NOT THROW
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The snapshot blobs are unversioned. A key that is ABSENT on one side means "this row
    /// predates the field" — reporting it as a change fabricates history that never happened.
    /// </summary>
    [Fact]
    public void DiffFields_MissingKeyOnEitherSide_IsNotReportedAsAChange()
    {
        // `priority` exists only in the newer snapshot.
        var changes = ActivityTimelineBuilder.DiffFields(
            beforeJson: Snapshot(new { Status = "Todo" }),
            afterJson: Snapshot(new { Status = "Todo", Priority = "High" }));

        Assert.Empty(changes);
    }

    /// <summary>…but an explicit JSON null IS present, and null → value is a real change.</summary>
    [Fact]
    public void DiffFields_ExplicitNullToValue_IsReported()
    {
        var changes = ActivityTimelineBuilder.DiffFields(
            Snapshot(new { Assignee = (string?)null }),
            Snapshot(new { Assignee = "claude" }));

        var change = Assert.Single(changes);
        Assert.Equal("Assigned", change.Label);
        Assert.Equal("—", change.Before);
        Assert.Equal("claude", change.After);
    }

    [Theory]
    [InlineData(null, "{}")]
    [InlineData("{}", null)]
    [InlineData("not json", "{}")]
    [InlineData("{}", "not json")]
    [InlineData("[1,2,3]", "{}")]
    [InlineData("", "")]
    public void DiffFields_MalformedOrOneSided_ReturnsEmptyInsteadOfThrowing(string? before, string? after)
    {
        Assert.Empty(ActivityTimelineBuilder.DiffFields(before, after));
    }

    /// <summary>A create/delete carries only one side; the row falls back to the audit Message.</summary>
    [Fact]
    public void Build_TaskCreated_FallsBackToTheAuditMessage()
    {
        var events = new[]
        {
            Event(10, "TaskCreated", T0, before: null,
                after: Snapshot(new { Status = "Todo", Name = "x" }), message: "Task 'x' created")
        };

        var entry = Assert.Single(ActivityTimelineBuilder.Build([], events));
        Assert.Empty(entry.Changes);
        Assert.Equal("Task 'x' created", entry.Headline);
        Assert.Equal("created", entry.Tone);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  4. XSS — the differ is a NEW render path and must not become an injection vector
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A task name mirrored verbatim from a GitHub issue title can contain markup. The differ
    /// must hand the value back as INERT TEXT — unescaped, unmodified, and above all not marked
    /// as HTML. The component renders it via plain `@change.After`, which Razor HTML-encodes; the
    /// contract this test pins is that nothing in the model layer pre-renders or pre-escapes it
    /// into something that could be trusted as markup.
    /// </summary>
    [Fact]
    public void DiffFields_MarkupInAValue_IsCarriedAsInertText()
    {
        const string payload = "<img src=x onerror=alert(1)>";

        var changes = ActivityTimelineBuilder.DiffFields(
            Snapshot(new { Name = "safe" }),
            Snapshot(new { Name = payload }));

        var change = Assert.Single(changes);
        Assert.Equal("Name", change.Label);
        // Verbatim string, NOT a MarkupString and NOT pre-escaped: any encoding is the renderer's
        // job, and double-encoding here would be its own bug.
        Assert.Equal(payload, change.After);
        Assert.IsType<string>(change.After);
    }

    /// <summary>
    /// TimelineFieldChange must expose no HTML-ish surface. If someone later changes Before/After
    /// to MarkupString to "make links work", this fails — which is the point.
    /// </summary>
    [Fact]
    public void TimelineFieldChange_ExposesOnlyStrings()
    {
        foreach (var prop in typeof(TimelineFieldChange).GetProperties())
            Assert.Equal(typeof(string), prop.PropertyType);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  5. RELATIVE TIME
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(30, "just now")]
    [InlineData(90, "1m ago")]
    [InlineData(60 * 5, "5m ago")]
    [InlineData(60 * 60 * 3, "3h ago")]
    [InlineData(60 * 60 * 24 * 3, "3d ago")]
    [InlineData(60 * 60 * 24 * 60, "2mo ago")]
    [InlineData(60 * 60 * 24 * 800, "2y ago")]
    public void Relative_FormatsHumanDurations(int agoSeconds, string expected)
    {
        var now = T0.AddSeconds(agoSeconds);
        Assert.Equal(expected, ActivityTimelineBuilder.Relative(T0, now));
    }

    /// <summary>Clock skew between two writes must never render "-3m ago".</summary>
    [Fact]
    public void Relative_FutureTimestamp_ClampsToJustNow()
        => Assert.Equal("just now", ActivityTimelineBuilder.Relative(T0.AddHours(2), T0));

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  5b. TIMEZONE — a zone error, not just a formatting error
    //
    //  SQLite has no zone type and AppDbContext has no HasConversion, so every DateTime read back
    //  through EF has Kind = Unspecified even though the value IS UTC. These pin the normalisation
    //  that makes that safe. They are written to be MACHINE-ZONE-INDEPENDENT: the trailing `Z`
    //  assertion and the Kind assertions hold identically on a UTC box and on US/Eastern, so they
    //  cannot pass vacuously in one place and fail in another.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    private static readonly DateTime StoredAsSqliteReturnsIt =
        new(2026, 8, 2, 22, 21, 58, DateTimeKind.Unspecified);

    [Fact]
    public void AsUtc_UnspecifiedIsRelabelledUtc_WithoutMovingTheInstant()
    {
        var result = ActivityTimelineBuilder.AsUtc(StoredAsSqliteReturnsIt);

        Assert.Equal(DateTimeKind.Utc, result.Kind);
        Assert.Equal(StoredAsSqliteReturnsIt.Ticks, result.Ticks);   // relabelled, NOT shifted
    }

    [Fact]
    public void AsUtc_LocalIsConverted_NotRelabelled()
    {
        var local = new DateTime(2026, 8, 2, 18, 21, 58, DateTimeKind.Local);

        var result = ActivityTimelineBuilder.AsUtc(local);

        Assert.Equal(DateTimeKind.Utc, result.Kind);
        Assert.Equal(local.ToUniversalTime().Ticks, result.Ticks);
    }

    [Fact]
    public void AsUtc_AlreadyUtc_IsUntouched()
    {
        var utc = new DateTime(2026, 8, 2, 22, 21, 58, DateTimeKind.Utc);
        Assert.Equal(utc, ActivityTimelineBuilder.AsUtc(utc));
    }

    /// <summary>
    /// THE ZONE GUARD. The tooltip's absolute half must carry a zone designator. `"O"` emits the
    /// trailing `Z` only for Kind = Utc — so this fails the instant the normalisation stops
    /// running, on ANY machine, including one sitting at UTC+0 where a "local != raw" assertion
    /// would pass vacuously.
    /// </summary>
    [Fact]
    public void FormatTooltip_AbsoluteHalfCarriesTheUtcDesignator()
    {
        var tooltip = ActivityTimelineBuilder.FormatTooltip(StoredAsSqliteReturnsIt);

        Assert.Contains("UTC: ", tooltip);
        Assert.EndsWith("Z", tooltip);
        Assert.Contains("2026-08-02T22:21:58", tooltip);   // the instant itself never moves
    }

    [Fact]
    public void FormatMachine_EmitsARoundTrippableUtcInstant()
    {
        var machine = ActivityTimelineBuilder.FormatMachine(StoredAsSqliteReturnsIt);

        Assert.EndsWith("Z", machine);
        var reparsed = DateTime.Parse(machine, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        Assert.Equal(DateTimeKind.Utc, reparsed.Kind);
        Assert.Equal(StoredAsSqliteReturnsIt.Ticks, reparsed.Ticks);
    }

    /// <summary>
    /// The displayed face value must be the instant expressed in the LOCAL zone — computed here
    /// with the zone-aware TimeZoneInfo API rather than by repeating the implementation, so a
    /// wrong-direction conversion cannot agree with the expectation.
    /// </summary>
    [Fact]
    public void FormatLocal_RendersTheInstantInTheLocalZone()
    {
        var expectedLocal = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(StoredAsSqliteReturnsIt, DateTimeKind.Utc), TimeZoneInfo.Local);

        Assert.Equal(
            expectedLocal.ToString("MMM d, yyyy h:mm tt", CultureInfo.CurrentCulture),
            ActivityTimelineBuilder.FormatLocal(StoredAsSqliteReturnsIt));

        // …and on any machine that is not sitting at UTC+0, it must NOT be the raw stored value.
        // Guarded so the suite is correct on a UTC box rather than silently untested there.
        var offset = TimeZoneInfo.Local.GetUtcOffset(StoredAsSqliteReturnsIt);
        if (offset != TimeSpan.Zero)
        {
            Assert.NotEqual(
                StoredAsSqliteReturnsIt.ToString("MMM d, yyyy h:mm tt", CultureInfo.CurrentCulture),
                ActivityTimelineBuilder.FormatLocal(StoredAsSqliteReturnsIt));
        }
    }

    /// <summary>
    /// Relative labels are computed from ticks, which ignore Kind — so a mismatched pair would
    /// produce an offset-sized error ("4h ago" for something that just happened). Mixing a
    /// Kind=Unspecified stored value with a Kind=Utc "now" must still read as minutes.
    /// </summary>
    [Fact]
    public void Relative_UnspecifiedStoredValueAgainstUtcNow_IsNotOffsetByTheLocalZone()
    {
        var storedUnspecified = new DateTime(2026, 8, 2, 22, 21, 58, DateTimeKind.Unspecified);
        var nowUtc = new DateTime(2026, 8, 2, 22, 26, 58, DateTimeKind.Utc);

        Assert.Equal("5m ago", ActivityTimelineBuilder.Relative(storedUnspecified, nowUtc));
    }

    /// <summary>
    /// The whole point of normalising at the boundary: entries carry an honest Kind, so nothing
    /// downstream has to guess. Built from rows shaped exactly as EF returns them.
    /// </summary>
    [Fact]
    public void Build_NormalisesStoredTimestampsToUtcKind()
    {
        var comments = new[] { Comment(1, "note", CommentType.Note, StoredAsSqliteReturnsIt, "User") };
        var events = new[] { Event(10, "TaskClaimed", StoredAsSqliteReturnsIt.AddMinutes(1), message: "claimed") };

        var timeline = ActivityTimelineBuilder.Build(comments, events);

        Assert.All(timeline, e => Assert.Equal(DateTimeKind.Utc, e.CreatedAtUtc.Kind));
        Assert.All(timeline, e => Assert.EndsWith("Z", ActivityTimelineBuilder.FormatMachine(e.CreatedAtUtc)));
    }

    /// <summary>
    /// The snapshot blobs carry the same zone-less strings. A `claimedAt` inside AfterJson must
    /// render as the LOCAL rendering of that UTC instant, not the raw text.
    /// </summary>
    [Fact]
    public void DiffFields_TimestampValue_IsRenderedInTheLocalZone()
    {
        var changes = ActivityTimelineBuilder.DiffFields(
            Snapshot(new { ClaimedAt = (DateTime?)null }),
            // Exactly the shape TaskService serialises: no trailing Z.
            "{\"claimedAt\":\"2026-08-02T22:21:58.8201407\"}");

        var change = Assert.Single(changes);
        var expected = TimeZoneInfo.ConvertTimeFromUtc(
            new DateTime(2026, 8, 2, 22, 21, 58, DateTimeKind.Utc), TimeZoneInfo.Local);
        Assert.Equal(expected.ToString("MMM d, yyyy h:mm tt", CultureInfo.CurrentCulture), change.After);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  6. AGAINST THE REAL PRODUCER — fixtures cannot drift from what TaskService actually writes
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// End-to-end over REAL SQLite and the REAL TaskService: create a task, change its status,
    /// edit two fields, add a comment — then merge exactly what the service persisted.
    ///
    /// Four user actions must produce FOUR rows. The service wrote 4 events and 4 comments (8
    /// rows); anything that renders more than four is the double-render bug.
    /// </summary>
    [Fact]
    public async Task Build_OverRealTaskServiceOutput_RendersOneRowPerUserAction()
    {
        using var factory = new SqliteTestFactory();
        int taskId;
        await using (var db = factory.CreateDbContext())
        {
            var project = new Project { Name = "P" };
            db.Projects.Add(project);
            await db.SaveChangesAsync();
            var feature = new Feature { Name = "F", ProjectId = project.Id };
            db.Features.Add(feature);
            await db.SaveChangesAsync();

            var svc = new TaskService(factory);
            var task = await svc.CreateAsync(new TaskCreate(feature.Id, "Original name"), actor: "alice");
            taskId = task.Id;

            await svc.ChangeStatusAsync(taskId, WorkStatus.InProgress, actor: "alice");
            await svc.UpdateAsync(taskId, new TaskUpdate(
                Name: "Renamed", Priority: TaskPriority.High), actor: "alice");
            await svc.AddCommentAsync(taskId, "please review", author: "bob", type: CommentType.Note, actor: "bob");
        }

        await using var read = factory.CreateDbContext();
        var comments = await read.TaskComments.Where(c => c.TaskItemId == taskId).AsNoTracking().ToListAsync();
        var events = await read.ActivityEvents
            .Where(e => e.EntityType == "Task" && e.EntityId == taskId).AsNoTracking().ToListAsync();

        // Sanity: the producer really did write both halves. If this ever drops to zero the test
        // below would pass vacuously.
        Assert.Equal(4, events.Count);
        Assert.Equal(4, comments.Count);

        var timeline = ActivityTimelineBuilder.Build(comments, events);

        Assert.Equal(4, timeline.Count);

        // Newest first: comment, field update, status change, creation.
        Assert.Equal("please review", timeline[0].Markdown);
        Assert.True(timeline[0].FromComment);

        Assert.Equal("TaskUpdated", timeline[1].Kind);
        Assert.Contains(timeline[1].Changes, c => c.Label == "Priority" && c.Before == "Low" && c.After == "High");
        Assert.Contains(timeline[1].Changes, c => c.Label == "Name" && c.Before == "Original name" && c.After == "Renamed");

        // TaskCreate leaves Status null → Backlog (ITaskService.cs:169).
        Assert.Equal("TaskStatusChanged", timeline[2].Kind);
        Assert.Contains(timeline[2].Changes, c => c.Label == "Status" && c.Before == "Backlog" && c.After == "InProgress");

        Assert.Equal("TaskCreated", timeline[3].Kind);
    }

    /// <summary>
    /// The XSS payload, end to end through the real producer: a task named with an HTML payload
    /// must come back out of the differ as the literal string.
    /// </summary>
    [Fact]
    public async Task Build_OverRealTaskServiceOutput_CarriesMarkupInANameAsInertText()
    {
        const string payload = "<img src=x onerror=alert('xss')>";

        using var factory = new SqliteTestFactory();
        int taskId;
        await using (var db = factory.CreateDbContext())
        {
            var project = new Project { Name = "P" };
            db.Projects.Add(project);
            await db.SaveChangesAsync();
            var feature = new Feature { Name = "F", ProjectId = project.Id };
            db.Features.Add(feature);
            await db.SaveChangesAsync();

            var svc = new TaskService(factory);
            var task = await svc.CreateAsync(new TaskCreate(feature.Id, "safe name"), actor: "alice");
            taskId = task.Id;
            await svc.UpdateAsync(taskId, new TaskUpdate(Name: payload), actor: "alice");
        }

        await using var read = factory.CreateDbContext();
        var comments = await read.TaskComments.Where(c => c.TaskItemId == taskId).AsNoTracking().ToListAsync();
        var events = await read.ActivityEvents
            .Where(e => e.EntityType == "Task" && e.EntityId == taskId).AsNoTracking().ToListAsync();

        var timeline = ActivityTimelineBuilder.Build(comments, events);
        var rename = Assert.Single(timeline, e => e.Kind == "TaskUpdated");
        var change = Assert.Single(rename.Changes, c => c.Label == "Name");

        Assert.Equal(payload, change.After);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  7. THE X → X COLLISION (#359)
    //
    //  A real change rendered as two IDENTICAL strings, because both sides were cut at 60
    //  characters and every synced description opens with the same ~70-character generated block.
    //  Measured against the live corpus: of 409 tasks, 404 carry a description, all 404 distinct —
    //  and all 404 rendered as the single string
    //  "<!-- kanbanflow-sync --> **Issue:** https://github.com/joelf…".
    //
    //  These tests are written over the WHOLE DisplayFields set, not over `description`. The cut
    //  is in Format(), which every field goes through; `name` is already past 60 characters on 268
    //  of those 409 tasks, and `assignee`/`claimedBy` are unbounded strings with no live data —
    //  exactly the members a guard written for the reported exemplar would have missed.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Length of the synthetic values the divergence sweep is built from.</summary>
    private const int SweepLength = 400;

    /// <summary>
    /// A deterministic long value whose ONLY distinguishing character is <paramref name="marker"/>
    /// at <paramref name="at"/>. Two of these with different markers share a prefix of exactly
    /// <paramref name="at"/> characters, which is what lets one test sweep the divergence point.
    /// The filler is lowercase and the markers are uppercase, so no filler character can
    /// accidentally equal a marker.
    /// </summary>
    private static string LongValueDivergingAt(int at, char marker)
    {
        var chars = new char[SweepLength];
        for (var i = 0; i < SweepLength; i++) chars[i] = (char)('a' + i % 26);
        chars[at] = marker;
        return new string(chars);
    }

    /// <summary>Overwrites two code units with U+1F600 GRINNING FACE — one astral character.</summary>
    private static string WithSurrogatePairAt(string text, int at)
    {
        var chars = text.ToCharArray();
        chars[at] = '\uD83D';
        chars[at + 1] = '\uDE00';
        return new string(chars);
    }

    private static bool HasLoneSurrogate(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]))
            {
                if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1])) return true;
                i++;
            }
            else if (char.IsLowSurrogate(s[i]))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The rendering rule that shipped BEFORE #359, reproduced verbatim so a fixture can PROVE it
    /// is a genuine collider. Without it a "renders distinctly" assertion could pass against a
    /// pair that never collided in the first place — a green test proving nothing.
    /// </summary>
    private static string SixtyCharRule(string raw)
    {
        var text = raw.ReplaceLineEndings(" ").Trim();
        return text.Length <= 60 ? text : text[..60] + "…";
    }

    /// <summary>
    /// A snapshot blob carrying ONE field under an arbitrary JSON name. The anonymous-object
    /// helper cannot do that, and the sweep has to address each <c>DisplayFields</c> entry by
    /// name. Dictionary KEYS are written verbatim — <c>JsonSerializerDefaults.Web</c> sets a
    /// property naming policy, not a dictionary key policy — which is what keeps them the exact
    /// camelCase names the differ looks up.
    /// </summary>
    private static string FieldSnapshot(string jsonName, string? value)
        => JsonSerializer.Serialize(
            new Dictionary<string, string?> { [jsonName] = value },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

    /// <summary>Exactly what <c>portfolio-sync.mjs:456-473</c> stamps onto every mirrored task.</summary>
    private const string SyncBlock =
        "<!-- kanbanflow-sync -->\n" +
        "**Issue:** https://github.com/joelfuller2016/KanbanFlow-v2/issues/359\n" +
        "**State:** OPEN · **Milestone:** M4: UI / UX Polish · **Labels:** bug, ui\n" +
        "**Open PR:** none\n" +
        "**Synced:** 2026-08-02T21:14:07.482Z\n" +
        "<!-- /kanbanflow-sync -->\n\n";

    public static TheoryData<string, string, int> EveryDisplayFieldAndDivergencePoint()
    {
        var data = new TheoryData<string, string, int>();
        foreach (var (json, label) in ActivityTimelineBuilder.DisplayFields)
        {
            // 0/30/59 fall INSIDE the old 60-character cut and rendered correctly before #359 —
            // they are here so the guard cannot pass by asserting something trivially true of
            // every pair. 59/60/61 straddle the cut. 70/71 are the reported case: the earliest
            // byte at which two different synced descriptions can diverge is 71. 200 and the final
            // character are deep past it.
            foreach (var at in new[] { 0, 30, 59, 60, 61, 70, 71, 200, SweepLength - 1 })
                data.Add(json, label, at);
        }
        return data;
    }

    /// <summary>
    /// THE LOAD-BEARING ASSERTION. For every field the timeline renders, and for a divergence at
    /// any position — before the cut, on it, and far past it — the two sides must render
    /// DIFFERENTLY, and each must actually SHOW its own changed character rather than merely
    /// differing somewhere. Fails on every position ≥ 60 against the code this replaces.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryDisplayFieldAndDivergencePoint))]
    public void DiffFields_ValuesDivergingAnywhere_NeverRenderAsTwoIdenticalStrings(
        string json, string label, int at)
    {
        var before = LongValueDivergingAt(at, 'X');
        var after = LongValueDivergingAt(at, 'Y');

        var change = Assert.Single(ActivityTimelineBuilder.DiffFields(
            FieldSnapshot(json, before), FieldSnapshot(json, after)));

        Assert.Equal(label, change.Label);
        Assert.NotEqual(change.Before, change.After);
        Assert.Contains("X", change.Before, StringComparison.Ordinal);
        Assert.Contains("Y", change.After, StringComparison.Ordinal);
        // Format() returns up to MaxValueLength content + 1 ellipsis = 61 chars for the
        // non-collision path. The collision path (Window) must stay within 60 chars total.
        // The bound here catches Window's old over-budget of 62 (60 content + 2 ellipses)
        // while permitting Format's expected 61-char non-collision output.
        Assert.True(change.Before.Length <= 61, $"Before rendered length {change.Before.Length} > 61: '{change.Before}'");
        Assert.True(change.After.Length <= 61, $"After rendered length {change.After.Length} > 61: '{change.After}'");
    }

    /// <summary>
    /// The sweep above is parameterised over <c>DisplayFields</c>, so an emptied or shortened list
    /// would make it pass VACUOUSLY. The set is pinned here: adding a field is a deliberate act
    /// that updates both, and the sweep then covers the new field automatically.
    /// </summary>
    [Fact]
    public void DisplayFields_IsTheClosedSetTheCollisionSweepEnumerates()
    {
        Assert.Equal(
            new[]
            {
                "status", "priority", "assignee", "claimedBy", "name",
                "featureId", "description", "sortOrder", "completedAt", "claimedAt",
                "text", "completed", "goals", "readme", "projectId", "taskItemId", "dependsOnTaskItemId",
                "parentFeatureId", "parentSubtaskId", "promotedProjectId", "archived"
            },
            ActivityTimelineBuilder.DisplayFields.Select(f => f.Json).ToArray());
    }

    /// <summary>The reported case, against the real producer's output shape.</summary>
    [Fact]
    public void DiffFields_SyncedDescriptionBodyRewritten_ShowsTheBodyNotTheSyncBlock()
    {
        var before = SyncBlock + "Original body text.";
        var after = SyncBlock + "COMPLETELY REWRITTEN body with new repro steps and acceptance criteria.";

        // NON-VACUITY: under the rule that shipped, these two ARE the same string.
        Assert.Equal(SixtyCharRule(before), SixtyCharRule(after));

        var change = Assert.Single(ActivityTimelineBuilder.DiffFields(
            FieldSnapshot("description", before), FieldSnapshot("description", after)));

        Assert.Equal("Description", change.Label);
        Assert.NotEqual(change.Before, change.After);
        Assert.Contains("Original body text.", change.Before, StringComparison.Ordinal);
        Assert.Contains("COMPLETELY REWRITTEN", change.After, StringComparison.Ordinal);

        // The row leads with the change, not with the boilerplate every synced task shares.
        Assert.StartsWith("…", change.Before, StringComparison.Ordinal);
        Assert.DoesNotContain("**Issue:**", change.Before, StringComparison.Ordinal);
        Assert.DoesNotContain("**Issue:**", change.After, StringComparison.Ordinal);
    }

    /// <summary>
    /// A change CONFINED TO THE SYNC BLOCK — the guaranteed trigger, since every sync that sees an
    /// issue close rewrites `**State:**`, which sits past character 60.
    /// </summary>
    [Fact]
    public void DiffFields_SyncedDescriptionStateFlipped_ShowsBothStates()
    {
        var before = SyncBlock + "Body.";
        var after = SyncBlock.Replace("**State:** OPEN", "**State:** CLOSED", StringComparison.Ordinal) + "Body.";

        Assert.Equal(SixtyCharRule(before), SixtyCharRule(after));

        var change = Assert.Single(ActivityTimelineBuilder.DiffFields(
            FieldSnapshot("description", before), FieldSnapshot("description", after)));

        Assert.NotEqual(change.Before, change.After);
        Assert.Contains("**State:** OPEN", change.Before, StringComparison.Ordinal);
        Assert.Contains("**State:** CLOSED", change.After, StringComparison.Ordinal);
    }

    /// <summary>
    /// The SECOND collider, and the reason this fix is not description-only: synced task names are
    /// "#&lt;n&gt; " plus a GitHub issue title, and 268 of the 409 live tasks carry one longer than
    /// the cut. A rename editing the tail collides exactly the same way.
    /// </summary>
    [Fact]
    public void DiffFields_SyncedNameEditedPastTheCap_ShowsTheEditedTail()
    {
        const string before = "#149 P1: Audit trail only covers Task entities — extend ActivityEvent to all mutations (UI + REST)";
        const string after = "#149 P1: Audit trail only covers Task entities — extend ActivityEvent to all mutations (UI + REST + MCP)";

        Assert.Equal(SixtyCharRule(before), SixtyCharRule(after));

        var change = Assert.Single(ActivityTimelineBuilder.DiffFields(
            FieldSnapshot("name", before), FieldSnapshot("name", after)));

        Assert.Equal("Name", change.Label);
        Assert.Contains("(UI + REST)", change.Before, StringComparison.Ordinal);
        Assert.Contains("(UI + REST + MCP)", change.After, StringComparison.Ordinal);
    }

    /// <summary>
    /// GOAL 5 — a short-field change renders byte-identically to what shipped: the raw value,
    /// verbatim, with no window and no ellipsis. The 60-character cap is CORRECT for these.
    /// </summary>
    [Theory]
    [InlineData("status", "Status", "Todo", "Done")]
    [InlineData("priority", "Priority", "Low", "Critical")]
    [InlineData("assignee", "Assigned", "alice", "bob")]
    [InlineData("claimedBy", "Claimed by", "alice", "bob")]
    [InlineData("name", "Name", "short name", "other name")]
    [InlineData("featureId", "Feature", "12", "13")]
    [InlineData("sortOrder", "Order", "1", "2")]
    public void DiffFields_ValuesUnderTheCap_RenderVerbatim(
        string json, string label, string before, string after)
    {
        var change = Assert.Single(ActivityTimelineBuilder.DiffFields(
            FieldSnapshot(json, before), FieldSnapshot(json, after)));

        Assert.Equal(label, change.Label);
        Assert.Equal(before, change.Before);
        Assert.Equal(after, change.After);
        Assert.DoesNotContain("…", change.Before, StringComparison.Ordinal);
        Assert.DoesNotContain("…", change.After, StringComparison.Ordinal);
    }

    /// <summary>
    /// GOAL 5 for the LONG case: a pair that already rendered distinctly is left exactly as it
    /// was — the cap still applies, the window does not fire. Only a COLLISION routes a value
    /// through the new path.
    /// </summary>
    [Fact]
    public void DiffFields_LongValuesThatAlreadyDiffer_KeepTheSixtyCharacterCap()
    {
        var before = LongValueDivergingAt(10, 'X');
        var after = LongValueDivergingAt(10, 'Y');

        var change = Assert.Single(ActivityTimelineBuilder.DiffFields(
            FieldSnapshot("description", before), FieldSnapshot("description", after)));

        Assert.Equal(SixtyCharRule(before), change.Before);
        Assert.Equal(SixtyCharRule(after), change.After);
    }

    /// <summary>
    /// Format() truncates long non-colliding values with a plain <c>text[..MaxValueLength]</c>
    /// slice. If the high half of a surrogate pair sits at index 59, that slice emits the orphaned
    /// high surrogate — mojibake that reaches the DOM. The fix mirrors the boundary check already
    /// present in Window(). Divergence at 10 ensures Format() never sees a collision, so the
    /// values go through the ordinary (non-windowed) truncation path.
    /// </summary>
    [Fact]
    public void DiffFields_SurrogatePairAtFormatTruncationBoundary_IsNeverSplit()
    {
        // High surrogate at 59, low at 60 — straddles text[..60].
        var before = WithSurrogatePairAt(LongValueDivergingAt(10, 'X'), 59);
        var after  = WithSurrogatePairAt(LongValueDivergingAt(10, 'Y'), 59);

        Assert.NotEqual(SixtyCharRule(before), SixtyCharRule(after));  // confirm non-colliding

        var change = Assert.Single(ActivityTimelineBuilder.DiffFields(
            FieldSnapshot("description", before), FieldSnapshot("description", after)));

        Assert.False(HasLoneSurrogate(change.Before), $"Before carries a lone surrogate: {change.Before}");
        Assert.False(HasLoneSurrogate(change.After),  $"After carries a lone surrogate: {change.After}");
    }

    /// <summary>
    /// GOAL 6 — a GitHub issue title is mirrored verbatim into these rows, so a payload past the
    /// cut must survive the window as INERT TEXT: unescaped, unmodified, still a plain string.
    /// Encoding is the renderer's job (<c>@change.After</c>); pre-escaping here would
    /// double-encode, and marking it as HTML would be stored XSS.
    /// </summary>
    [Fact]
    public void DiffFields_MarkupPastTheCapInACollidingPair_IsStillInertText()
    {
        const string payload = "<img src=x onerror=alert(1)>";
        var prefix = new string('p', 80);
        var before = prefix + "safe";
        var after = prefix + payload;

        Assert.Equal(SixtyCharRule(before), SixtyCharRule(after));

        var change = Assert.Single(ActivityTimelineBuilder.DiffFields(
            FieldSnapshot("description", before), FieldSnapshot("description", after)));

        Assert.Contains(payload, change.After, StringComparison.Ordinal);
        Assert.DoesNotContain("&lt;", change.After, StringComparison.Ordinal);
        Assert.IsType<string>(change.After);
    }

    /// <summary>
    /// The window slices by UTF-16 code unit, so both of its edges can land between the halves of
    /// an astral character. A lone surrogate is not text — it is mojibake that survives all the
    /// way to the DOM. Divergence at 100 puts the shared window at [88, 148); the parameter walks
    /// one emoji across both edges.
    /// </summary>
    [Theory]
    [InlineData(86)]
    [InlineData(87)]
    [InlineData(88)]
    [InlineData(89)]
    [InlineData(146)]
    [InlineData(147)]
    [InlineData(148)]
    [InlineData(149)]
    public void DiffFields_SurrogatePairStraddlingAWindowEdge_IsNeverSplit(int pairAt)
    {
        var before = WithSurrogatePairAt(LongValueDivergingAt(100, 'X'), pairAt);
        var after = WithSurrogatePairAt(LongValueDivergingAt(100, 'Y'), pairAt);

        var change = Assert.Single(ActivityTimelineBuilder.DiffFields(
            FieldSnapshot("description", before), FieldSnapshot("description", after)));

        Assert.NotEqual(change.Before, change.After);
        Assert.False(HasLoneSurrogate(change.Before), $"Before carries a lone surrogate: {change.Before}");
        Assert.False(HasLoneSurrogate(change.After), $"After carries a lone surrogate: {change.After}");
    }

    /// <summary>
    /// A DELIBERATE non-repair, pinned so it stays a decision rather than an accident. Two
    /// instants in the same minute still render alike — but that collision is not truncation: the
    /// displayed text is DERIVED from the value, not a prefix of it, so windowing the raw ISO
    /// string would put an unconverted UTC fragment where a local time belongs. That is a second
    /// clock, which is exactly what #306 removed from this file. What this pins is that the
    /// one-clock convention survives the collision handling.
    /// </summary>
    [Fact]
    public void DiffFields_TimestampsInTheSameMinute_StayOnTheOneClock()
    {
        var change = Assert.Single(ActivityTimelineBuilder.DiffFields(
            "{\"claimedAt\":\"2026-08-02T22:21:48.8201407\"}",
            "{\"claimedAt\":\"2026-08-02T22:21:58.9901407\"}"));

        var expected = TimeZoneInfo
            .ConvertTimeFromUtc(new DateTime(2026, 8, 2, 22, 21, 0, DateTimeKind.Utc), TimeZoneInfo.Local)
            .ToString("MMM d, yyyy h:mm tt", CultureInfo.CurrentCulture);

        Assert.Equal(expected, change.Before);
        Assert.Equal(expected, change.After);
        Assert.DoesNotContain("2026-08-02T", change.Before, StringComparison.Ordinal);
        Assert.DoesNotContain("…", change.After, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other deliberate non-repair: values that are equal ONCE COLLAPSED differ only in line
    /// endings or surrounding whitespace. Collapsing is what removed the delta, so no window can
    /// show it — and neither side may gain a spurious ellipsis pretending otherwise.
    /// </summary>
    [Fact]
    public void DiffFields_DifferenceOnlyInLineEndings_IsNotWindowed()
    {
        var change = Assert.Single(ActivityTimelineBuilder.DiffFields(
            FieldSnapshot("description", "alpha\nbeta"),
            FieldSnapshot("description", "alpha beta")));

        Assert.Equal("alpha beta", change.Before);
        Assert.Equal("alpha beta", change.After);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  Adversarial payload shapes (#374 AC6)
    //
    //  DiffFields is documented to "yield empty rather than throwing: a corrupt audit row must
    //  not take the panel down." The existing theory proves that for syntactically-broken text
    //  ("not json") and a non-object root ("[1,2,3]"). It does NOT cover the shapes below, and
    //  they fail through DIFFERENT code paths — a BOM and a depth overflow are not "not json",
    //  they are inputs a reader accepts right up until it does not.
    //
    //  Every case here asserts the same two properties: it does not throw, and nothing that
    //  reaches a rendered field carries JSON punctuation. `Build` is exercised alongside
    //  `DiffFields` because the component renders entries, not raw diffs, and the fallback to
    //  `Message` is part of the contract being guarded.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Every payload the differ may legally be handed, and why it is a distinct path.</summary>
    public static TheoryData<string, string, string> AdversarialPayloads() => new()
    {
        // A BOM survives a round-trip through a text editor or a mis-encoded write. Utf8JsonReader
        // rejects it, but only after the string looked well-formed to every eye that read it.
        { "\uFEFF{\"status\":\"Todo\"}", "\uFEFF{\"status\":\"Done\"}", "BOM-prefixed" },
        // Valid JSON followed by junk: Parse reads a complete document and THEN finds more input.
        { "{\"status\":\"Todo\"} trailing", "{\"status\":\"Done\"}", "trailing garbage" },
        { "{\"status\":\"Todo\"}", "{\"status\":\"Done\"} trailing", "trailing garbage (after side)" },
        // ~200 levels, well past JsonDocument's default depth limit of 64.
        { DeeplyNested(200), DeeplyNested(200), "over-deep nesting" },
        // Legal JSON. Not a parse failure at all — a semantic ambiguity the differ must survive.
        { "{\"status\":\"Todo\",\"status\":\"Sneaky\"}", "{\"status\":\"Done\",\"status\":\"Sneaky\"}", "duplicate keys" },
    };

    private static string DeeplyNested(int depth) =>
        string.Concat(Enumerable.Repeat("{\"a\":", depth)) + "1" + new string('}', depth);

    [Theory]
    [MemberData(nameof(AdversarialPayloads))]
    public void DiffFields_AdversarialPayloads_NeverThrowAndNeverLeakJson(
        string before, string after, string shape)
    {
        var changes = ActivityTimelineBuilder.DiffFields(before, after);

        foreach (var change in changes)
        {
            Assert.Contains(change.Label, DisplayLabels);
            foreach (var value in new[] { change.Before, change.After })
            {
                Assert.DoesNotContain("{", value, StringComparison.Ordinal);
                Assert.DoesNotContain("[", value, StringComparison.Ordinal);
                Assert.DoesNotContain("\":", value, StringComparison.Ordinal);
            }
        }

        // …and the same payload routed through the full merge renders no JSON either. An entry
        // with no derivable diff must fall back to its recorded Message, never to its payload.
        var entry = Assert.Single(ActivityTimelineBuilder.Build(
            null, [Event(9, "TaskUpdated", T0, before, after, message: "Task updated")]));

        Assert.DoesNotContain("{", entry.Headline ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("{", entry.Badge, StringComparison.Ordinal);
        Assert.False(shape is "BOM-prefixed" or "trailing garbage" or "trailing garbage (after side)" or "over-deep nesting"
            && entry.Changes.Count > 0,
            $"{shape}: a payload the parser rejects must produce NO changes, so the row falls " +
            "back to its audit Message. Changes derived from an unparseable payload are invented.");
    }

    /// <summary>The labels <c>DisplayFields</c> can emit — mirrored here so a whitelist change is loud.</summary>
    private static readonly string[] DisplayLabels =
    [
        "Status", "Priority", "Assigned", "Claimed by", "Name",
        "Feature", "Description", "Order", "Completed", "Claimed at"
    ];

    // AC6's fifth shape — a STRUCTURED (object/array) value in a whitelisted slot — is NOT
    // guarded here, deliberately. Writing that assertion proved it is a live defect rather than
    // missing coverage: RawValue's fallback arm (`_ => new JsonRead(true, el.GetRawText())`,
    // ActivityTimelineModel.cs:542) hands an object's RAW TEXT to Format, so
    //     {"assignee":{"name":"claude","id":7}}  →  {"assignee":{"name":"joel","id":9}}
    // renders `Assigned: {"name":"claude","id":7} → {"name":"joel","id":9}` — raw JSON on screen,
    // against #328 AC2's "Raw JSON is never shown to the user". #374 is scoped to tests only, so
    // the fix is filed separately rather than smuggled in here; the failing assertion and its
    // observed output are recorded on that issue. Add the guard WITH the fix, not before it.
}
