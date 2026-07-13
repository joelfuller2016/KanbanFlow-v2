using System.Globalization;
using KanbanFlow.Models;

namespace KanbanFlow.Services;

/// <summary>
/// Pure presentation helpers for the badges and chips on an idea card (#180 Phase 4, #282).
/// Extracted to a static, side-effect-free class so they can be unit-tested without bUnit (the
/// project has no bUnit; real-SQLite + Playwright is the sanctioned no-fake test pattern) — which
/// is the whole reason anything lives here rather than in a <c>.razor</c> <c>@code</c> block, where
/// it would be unreachable from a test. The High-priority badge uses the project's custom
/// <c>bg-orange</c> class (Bootstrap has none) defined in <c>wwwroot/app.css</c>.
/// </summary>
public static class IdeaBadge
{
    /// <summary>Longest tooltip <see cref="PlanPreview"/> will emit before eliding.</summary>
    private const int PlanPreviewMaxLength = 120;

    /// <summary>Tooltip for a plan with no readable content of its own.</summary>
    private const string PlanFallback = "Has a plan";

    /// <summary>Bootstrap badge classes for an <see cref="IdeaStatus"/>.</summary>
    public static string StatusClass(IdeaStatus status) => status switch
    {
        IdeaStatus.New => "bg-primary",
        IdeaStatus.Triaging => "bg-warning text-dark",
        IdeaStatus.Promoted => "bg-success",
        IdeaStatus.Archived => "bg-secondary",
        IdeaStatus.Rejected => "bg-danger",
        _ => "bg-secondary"
    };

    /// <summary>
    /// Bootstrap badge classes for a <see cref="TaskPriority"/>. Matches the app-wide priority
    /// palette used by <c>KanbanCard.razor</c> and <c>ProjectOutline.razor</c> so colours are
    /// consistent everywhere. High uses the project's custom <c>bg-orange</c> (Bootstrap has none).
    /// </summary>
    public static string PriorityClass(TaskPriority priority) => priority switch
    {
        TaskPriority.Critical => "bg-danger",
        TaskPriority.High => "bg-orange text-white",
        TaskPriority.Medium => "bg-warning text-dark",
        TaskPriority.Low => "bg-secondary",
        _ => "bg-secondary"
    };

    /// <summary>
    /// The plan's opening line, stripped of markdown chrome and capped, for the Plan chip's tooltip
    /// (#282): <c>"**Problem** Zero agent presence in the UI."</c> → <c>"Problem Zero agent presence
    /// in the UI."</c>
    ///
    /// <para>A heading is a LABEL, not content — on its own it would yield a one-word tooltip
    /// ("Scope") that says nothing — so a heading is held and folded into the first real line that
    /// follows it: <c>"# Scope\nProve the branch renders."</c> → <c>"Scope: Prove the branch
    /// renders."</c></para>
    ///
    /// <para>The most SPECIFIC heading wins. Stacked headings ("# Scope" then "## Sub-scope") used to
    /// pair with <em>each other</em> — the second one was not recognised as a heading because a
    /// heading was already held, so it was treated as the content line and the tooltip came out as
    /// "Scope: Sub-scope": two labels and still no content, on exactly the well-structured plan most
    /// worth previewing. Each heading now simply replaces the last, and the loop keeps going until it
    /// finds a real line. (Gemini, PR #300.)</para>
    ///
    /// <para>A <c>title</c> is a mouse-only affordance; <c>aria-label="Has a plan"</c> carries the
    /// screen reader and the full plan is one Enter away in the modal (accepted gap —
    /// accessibility.md §7).</para>
    /// </summary>
    public static string PlanPreview(string? plan)
    {
        if (string.IsNullOrWhiteSpace(plan))
            return PlanFallback;

        string? heading = null;

        // StringReader, not Split: this loop RETURNS on the first content line, so a plan of the
        // usual shape ("## Problem" / "Users can't find X." / "## Approach" / …) reads TWO lines and
        // leaves. Split would materialise every line of the document first — an eager array behind a
        // lazy scan — and PlanPreview runs once per card, per render. ReadLine also handles \r\n and
        // a lone \r, which Split('\n') does not. (Gemini, PR #300.)
        using var reader = new StringReader(plan);

        while (reader.ReadLine() is { } rawLine)
        {
            var trimmed = rawLine.Trim();
            var isHeading = trimmed.StartsWith('#');

            var line = trimmed.Replace("**", "").Replace("`", "")
                              .TrimStart('#', '>', '-', '*', '_', ' ')
                              .Trim();

            if (line.Length == 0)
                continue;

            // Hold it and keep looking. NO `heading is null` guard: that is what made a second,
            // more specific heading masquerade as content (see the remarks above).
            if (isHeading)
            {
                heading = line;
                continue;
            }

            return Elide(heading is null ? line : $"{heading}: {line}");
        }

        // A plan that is nothing BUT headings still deserves its most specific one as the tooltip —
        // and it goes through the SAME cap. A heading is user text of unbounded length, and this exit
        // used to return it raw, so a lone `# <400 chars>` rendered a 400-char tooltip. There was a
        // heading-only test and there was a cap test; nothing tested the cap ON the heading-only
        // path. (Copilot, PR #300.)
        return heading is null ? PlanFallback : Elide(heading);
    }

    /// <summary>
    /// The one place the cap is applied. Every exit from <see cref="PlanPreview"/> that can return
    /// user text routes through it, so a future third exit cannot quietly ship an unbounded tooltip
    /// the way the heading-only one did.
    /// </summary>
    private static string Elide(string preview)
    {
        if (preview.Length <= PlanPreviewMaxLength)
            return preview;

        var cut = GraphemeSafeCut(preview, PlanPreviewMaxLength);

        // A single grapheme longer than the ENTIRE cap (a base char under ~120 stacked combining
        // marks — "Zalgo" text). There is no honest prefix to show, so say what we always say
        // when a plan has no readable content: the generic tooltip, never a broken half-glyph.
        return cut == 0
            ? PlanFallback
            : string.Concat(preview.AsSpan(0, cut).TrimEnd(), "…");
    }

    /// <summary>
    /// How many UTF-16 chars of <paramref name="text"/> fit within <paramref name="maxLength"/>
    /// without cutting a grapheme cluster in half. Returns 0 if even the first cluster does not fit.
    ///
    /// <para>A <c>char</c> is not a character. Slicing blindly at the cap corrupts the tooltip two
    /// distinct ways, and only the first is a "broken" string in the .NET sense:</para>
    ///
    /// <list type="number">
    /// <item><description><b>A split surrogate pair.</b> 🎯 is stored as two <c>char</c>s; keeping
    /// only the first leaves a LONE SURROGATE, which is not a character at all. UTF-8 cannot encode
    /// it, so the moment Blazor serialises the <c>title</c> attribute the encoder substitutes U+FFFD
    /// and the user sees �.</description></item>
    /// <item><description><b>A split grapheme cluster.</b> Subtler, and the reason this is not just
    /// a <c>char.IsHighSurrogate</c> check: 🇺🇸 is ONE glyph made of TWO surrogate pairs. Cutting
    /// between them splits no pair — the result is perfectly well-formed UTF-16 — but it renders as
    /// a stray lone regional indicator (🇺). Same for skin-tone modifiers, ZWJ sequences (👨‍👩‍👧‍👦) and
    /// combining accents: all well-formed, all wrong.</description></item>
    /// </list>
    ///
    /// <para><see cref="StringInfo.GetNextTextElementLength(ReadOnlySpan{char})"/> is the only thing
    /// that gets BOTH right, so the walk is by text element, never by <c>char</c>. It costs at most
    /// <paramref name="maxLength"/> iterations over an already-capped string.</para>
    /// </summary>
    private static int GraphemeSafeCut(string text, int maxLength)
    {
        if (text.Length <= maxLength)
            return text.Length;

        var cut = 0;
        while (cut < maxLength)
        {
            var next = StringInfo.GetNextTextElementLength(text.AsSpan(cut));

            // `next <= 0` cannot happen here (cut < maxLength < text.Length, so the span is never
            // empty), but a zero-width element would spin this loop forever. Cheap insurance.
            if (next <= 0 || cut + next > maxLength)
                break;

            cut += next;
        }

        return cut;
    }
}
