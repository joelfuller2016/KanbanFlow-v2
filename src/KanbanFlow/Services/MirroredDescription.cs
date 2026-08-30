using System.Text.RegularExpressions;

namespace KanbanFlow.Services;

/// <summary>
/// Reads the sync header that <c>scripts/portfolio-sync.mjs</c> writes at the top of every
/// description it mirrors from GitHub, so a board card can show the issue's own first line and a
/// link to it rather than five lines of sync metadata.
/// </summary>
/// <remarks>
/// <para>The mirrored shape, written by <c>descriptionFor()</c> in the sync script:</para>
/// <code>
/// &lt;!-- kanbanflow-sync --&gt;
/// **Issue:** https://github.com/owner/repo/issues/123
/// **State:** OPEN · **Milestone:** M1 · **Labels:** bug
/// **Open PR:** none
/// **Synced:** 2026-08-30T12:00:00.000Z
/// &lt;!-- /kanbanflow-sync --&gt;
///
/// (the issue body)
/// </code>
/// <para>
/// Excerpting that raw would put the metadata where the reader expects the issue's first sentence,
/// and the issue URL — the single most useful thing on a mirrored card — would stay buried behind a
/// panel open and a scroll.
/// </para>
/// <para>
/// Static and side-effect-free for the same reason <see cref="IdeaBadge"/> is: this project has no
/// bUnit, so anything left in a <c>.razor</c> <c>@code</c> block is unreachable from a test.
/// </para>
/// </remarks>
public static class MirroredDescription
{
    /// <summary>
    /// How much of a description is examined. The sync's body cap is 64,000 chars, so an unbounded
    /// <see cref="RegexOptions.Singleline"/> scan would read up to 64 KB per card per render (a full
    /// board renders ~50) hunting a header that is always within the first few hundred bytes. An
    /// excerpt needs only the head of the body, so both jobs are satisfied by the same slice.
    /// </summary>
    public const int ScanChars = 4096;

    /// <summary>Longest excerpt <see cref="Excerpt"/> emits before eliding.</summary>
    public const int ExcerptMaxLength = 180;

    private static readonly Regex SyncBlockRe = new(
        @"<!--\s*kanbanflow-sync\s*-->.*?<!--\s*/kanbanflow-sync\s*-->",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex IssueUrlRe = new(
        @"\*\*Issue:\*\*\s*(?<u>https://github\.com/[^\s)<]+)",
        RegexOptions.Compiled);

    // Leading markdown furniture — heading hashes, quote carets, list bullets.
    private static readonly Regex LeadMarkupRe = new(@"^[#>\-\*\s]+", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex WhitespaceRe = new(@"\s+", RegexOptions.Compiled);

    // Inline markup, stripped because the excerpt renders as PLAIN TEXT in a <p>. Leaving it raw put
    // literal asterisks on the card — an observed excerpt opened
    // "…the two sections that are not yet in any standard: **§4 Need & usefulness** and…" — which
    // reads as debris rather than as emphasis.
    //
    // A link keeps its TEXT and drops its target: "[read the full issue](https://…)" is 60 useless
    // characters of URL inside a 180-character budget.
    private static readonly Regex LinkRe = new(@"\[([^\]]*)\]\([^)]*\)", RegexOptions.Compiled);

    // Deliberately conservative: `**`, `__` and backticks only. A lone `*` or `_` is NOT stripped,
    // because this project's issue bodies are full of snake_case identifiers and file globs, and
    // mangling `portfolio_sync` or `*.razor` to make one italic marker disappear is the worse trade.
    private static readonly Regex EmphasisRe = new(@"\*\*|__|`", RegexOptions.Compiled);

    private static string? Head(string? description) => string.IsNullOrEmpty(description)
        ? null
        : description.Length > ScanChars ? description[..ScanChars] : description;

    /// <summary>
    /// The GitHub issue URL this description was mirrored from, or <c>null</c> for a description
    /// that was hand-written rather than synced. Anchored to the <c>**Issue:**</c> marker the sync
    /// itself writes, so a hand-authored description simply does not match.
    /// </summary>
    public static string? IssueUrl(string? description)
    {
        var head = Head(description);
        if (head is null) return null;
        var m = IssueUrlRe.Match(head);
        return m.Success ? m.Groups["u"].Value : null;
    }

    /// <summary>
    /// <c>"#123"</c> when <see cref="IssueUrl"/> ends in an issue number, so the card carries a
    /// number the reader can quote; <c>"GitHub"</c> when the URL points at something else (a repo,
    /// a discussion), and <c>null</c> when the description is not a mirror at all.
    /// </summary>
    public static string? IssueLabel(string? description)
    {
        var url = IssueUrl(description);
        if (url is null) return null;
        var tail = url[(url.LastIndexOf('/') + 1)..];
        return tail.Length > 0 && tail.All(char.IsDigit) ? "#" + tail : "GitHub";
    }

    /// <summary>
    /// The description's first readable line as plain text: sync header removed, leading markdown
    /// furniture stripped, whitespace collapsed, elided at <see cref="ExcerptMaxLength"/>. Returns
    /// <c>null</c> when nothing readable survives — a description that is only a sync header, or
    /// empty to begin with.
    /// </summary>
    public static string? Excerpt(string? description)
    {
        var head = Head(description);
        if (head is null) return null;

        var stripped = SyncBlockRe.Replace(head, "");
        stripped = LinkRe.Replace(stripped, "$1");
        stripped = EmphasisRe.Replace(stripped, "");
        var text = WhitespaceRe.Replace(LeadMarkupRe.Replace(stripped, " "), " ").Trim();
        if (text.Length == 0) return null;

        return text.Length > ExcerptMaxLength
            ? string.Concat(text.AsSpan(0, ExcerptMaxLength).TrimEnd(), "…")
            : text;
    }
}
