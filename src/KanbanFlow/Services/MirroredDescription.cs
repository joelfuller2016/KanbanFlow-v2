using System.Globalization;
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
    /// How much of a description is examined. The sync's body cap is 30,000 chars, so an unbounded
    /// <see cref="RegexOptions.Singleline"/> scan would read up to 30 KB per card per render hunting
    /// a header that is always within the first few hundred bytes (the largest measured across 320
    /// issues is 296). The live board holds 185 cards, and a filter keystroke re-renders all of
    /// them. An excerpt needs only the head of the body, so both jobs are satisfied by one slice.
    /// </summary>
    public const int ScanChars = 4096;

    /// <summary>Longest excerpt <see cref="Excerpt"/> emits before eliding.</summary>
    public const int ExcerptMaxLength = 180;

    // \A\s* anchors both patterns to the start of the description, allowing only intended leading
    // whitespace. Without the anchor, any balanced <!-- kanbanflow-sync --> block quoted anywhere
    // in the first ScanChars of a hand-written description would be treated as owned metadata —
    // IssueUrl() would return the quoted URL and Excerpt() would strip the quoted text.
    private static readonly Regex SyncBlockRe = new(
        @"\A\s*<!--\s*kanbanflow-sync\s*-->.*?<!--\s*/kanbanflow-sync\s*-->",
        RegexOptions.Singleline | RegexOptions.Compiled);

    // Must stay the OPENING half of SyncBlockRe verbatim (same anchor, same whitespace class).
    // The guard in Excerpt() previously used the literal "<!-- kanbanflow-sync", which missed
    // "<!--kanbanflow-sync-->" (no spaces). Derived from the shared pattern so the guard cannot
    // drift from what SyncBlockRe accepts.
    private static readonly Regex SyncOpenRe = new(
        @"\A\s*<!--\s*kanbanflow-sync\s*-->",
        RegexOptions.Compiled);

    private static readonly Regex IssueUrlRe = new(
        @"\*\*Issue:\*\*\s*(?<u>https://github\.com/[^\s)<]+)",
        RegexOptions.Compiled);

    // Leading markdown furniture - heading hashes, quote carets, list bullets. The marker must be
    // FOLLOWED BY WHITESPACE, which is what makes it markdown rather than content. The first version
    // was the character class `^[#>\-\*\s]+`, which ate any run of those characters and corrupted
    // real text: a line opening `-5 degrees below baseline` excerpted as `5 degrees below baseline`,
    // inverting the sign, and `--dry-run skips the write` lost its flag. A review found both.
    private static readonly Regex LeadMarkupRe = new(
        @"^[ \t]*(?:\#{1,6}[ \t]+|>[ \t]*|[-*+][ \t]+)", RegexOptions.Multiline | RegexOptions.Compiled);
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

    // Slicing at a fixed UTF-16 index can retain a LONE HIGH SURROGATE when an astral character
    // (an emoji, most CJK extension B, mathematical alphanumerics) straddles the boundary. That is
    // ill-formed text: it renders as a replacement glyph and corrupts any downstream comparison.
    // Dropping the orphan costs at most one code unit and is always safe.
    private static ReadOnlySpan<char> WithoutOrphanSurrogate(ReadOnlySpan<char> s) =>
        s.Length > 0 && char.IsHighSurrogate(s[^1]) ? s[..^1] : s;

    private static string? Head(string? description) => string.IsNullOrEmpty(description)
        ? null
        : description.Length > ScanChars
            ? WithoutOrphanSurrogate(description.AsSpan(0, ScanChars)).ToString()
            : description;

    /// <summary>
    /// The GitHub issue URL this description was mirrored from, or <c>null</c> for a description
    /// that was hand-written rather than synced. Anchored to the <c>**Issue:**</c> marker the sync
    /// itself writes, so a hand-authored description simply does not match.
    /// </summary>
    public static string? IssueUrl(string? description)
    {
        var head = Head(description);
        if (head is null) return null;

        // The URL is read ONLY from inside the sync block, never from the description at large.
        // Matching `**Issue:** <url>` anywhere meant a hand-written description opening with that
        // line got a GitHub affordance pointing wherever its author chose — the card would show a
        // "#123" chip linking to an unrelated repository, indistinguishable from a real mirror.
        // The block is the sync's own signed envelope, so it is the only place the claim is good.
        // A block whose closing marker falls outside the scan window simply does not match, and no
        // link is shown: the safe direction.
        var block = SyncBlockRe.Match(head);
        if (!block.Success) return null;

        var m = IssueUrlRe.Match(block.Value);
        if (!m.Success) return null;

        // A URL written at the end of a sentence carries the stop into the match, and the resulting
        // href 404s on GitHub. Trailing `.`/`,`/`;`/`:` are never part of a GitHub issue URL.
        return m.Groups["u"].Value.TrimEnd('.', ',', ';', ':');
    }

    /// <summary>
    /// <c>"#123"</c> when <see cref="IssueUrl"/> ends in an issue number, so the card carries a
    /// number the reader can quote; <c>"GitHub"</c> when the URL points at something else (a repo,
    /// a discussion), and <c>null</c> when the description is not a mirror at all.
    /// </summary>
    public static string? IssueLabel(string? description) => LabelForUrl(IssueUrl(description));

    /// <summary>The label for an already-resolved issue URL. See <see cref="IssueLabel"/>.</summary>
    public static string? LabelForUrl(string? url)
    {
        if (url is null) return null;
        // Strip a fragment, a query and any trailing slash before reading the number. Without this
        // `.../issues/123#issuecomment-4`, `.../issues/123?x=y` and `.../issues/123/` all fell
        // through to the unnumbered "GitHub" label despite naming a perfectly good issue.
        var path = url.Split('#')[0].Split('?')[0].TrimEnd('/');
        var tail = path[(path.LastIndexOf('/') + 1)..];
        // ASCII digits only: char.IsDigit is Unicode-wide, so `issues/١٢٣` would
        // otherwise yield an Arabic-Indic "#١٢٣" that matches no issue.
        return tail.Length > 0 && tail.All(c => c is >= '0' and <= '9') ? "#" + tail : "GitHub";
    }

    /// <summary>
    /// Accessible name for the card's GitHub link, or <c>null</c> when there is no link to name.
    /// </summary>
    /// <remarks>
    /// <c>target="_blank"</c> moves the user out of the app with no announcement, and the icon
    /// conveys the destination only visually, so the label has to say both in words. Wording and
    /// the "(opens in a new tab)" suffix follow <c>EditItemModal.razor</c>, which already carries
    /// this exact treatment for its issue and PR buttons — two different phrasings for the same
    /// affordance would be worse than either one alone.
    /// </remarks>
    public static string? IssueAriaLabel(string? description) => AriaLabelForLabel(IssueLabel(description));

    /// <summary>The aria label for an already-resolved label. See <see cref="IssueAriaLabel"/>.</summary>
    public static string? AriaLabelForLabel(string? label) => label is null
        ? null
        : label.StartsWith('#')
            ? $"GitHub issue {label} (opens in a new tab)"
            : "View on GitHub (opens in a new tab)";

    /// <summary>
    /// The description's opening text as a single plain-text line: sync header removed, leading
    /// markdown furniture stripped, ALL whitespace (newlines included) collapsed to single spaces
    /// so the excerpt runs across paragraph boundaries, elided at <see cref="ExcerptMaxLength"/>.
    /// Returns <c>null</c> when nothing readable survives — a description that is only a sync
    /// header, or empty to begin with.
    /// </summary>
    public static string? Excerpt(string? description)
    {
        var head = Head(description);
        if (head is null) return null;

        // Symmetry with IssueUrl: if the block's CLOSING marker falls outside the scan window the
        // Replace below matches nothing and the raw header - marker, URL, milestone, labels - is
        // excerpted straight onto the card. IssueUrl already refuses that case; this one silently
        // rendered it. Latent today (the largest header measured across 320 issues is 296 chars
        // against a 4,096 window) but it is the failure this class exists to prevent.
        if (SyncOpenRe.IsMatch(head) && !SyncBlockRe.IsMatch(head))
        {
            return null;
        }

        var stripped = SyncBlockRe.Replace(head, "");
        stripped = LinkRe.Replace(stripped, "$1");
        stripped = EmphasisRe.Replace(stripped, "");
        var text = WhitespaceRe.Replace(LeadMarkupRe.Replace(stripped, " "), " ").Trim();
        // `\s` and char.IsWhiteSpace both REJECT the Cf (format) category, so a description of only
        // U+200B / U+FEFF / U+00AD survived as a length-1 "non-empty" string, and the card rendered
        // a visually empty <p> with padding that also forced the reveal open. A body pasted with a
        // leading BOM or zero-width space is the realistic source.
        if (!text.Any(c => !char.IsWhiteSpace(c) && !char.IsControl(c)
                           && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format))
        {
            return null;
        }

        return text.Length > ExcerptMaxLength
            ? string.Concat(WithoutOrphanSurrogate(text.AsSpan(0, ExcerptMaxLength)).TrimEnd(), "…")
            : text;
    }
}
