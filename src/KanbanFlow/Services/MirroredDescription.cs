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
    /// How much of a description is examined for the card preview and metadata. This bounds
    /// rendering work independently of the complete stored description's length. It does not
    /// shorten storage or the detail editor. The generated header normally occupies the first
    /// few hundred characters, and the preview needs only the opening body text.
    /// </summary>
    public const int ScanChars = 4096;

    /// <summary>Longest excerpt <see cref="Excerpt"/> emits before eliding.</summary>
    public const int ExcerptMaxLength = 180;

    // \A[ \t]* anchors both patterns to the start of the description, allowing only horizontal
    // whitespace before the sync marker. \s* was broader — it matched newlines — so a hand-written
    // description that opened with blank lines then quoted a complete sync block was treated as a
    // mirror: IssueUrl() returned the quoted URL and Excerpt() stripped the quoted text.
    // The sync producer writes the marker at offset zero, so newlines before it are not intentional
    // leading whitespace; they are content that disqualifies the block.
    private static readonly Regex SyncBlockRe = new(
        @"\A[ \t]*<!--\s*kanbanflow-sync\s*-->.*?<!--\s*/kanbanflow-sync\s*-->",
        RegexOptions.Singleline | RegexOptions.Compiled);

    // Must stay the OPENING half of SyncBlockRe verbatim (same anchor, same whitespace class).
    // The guard in Excerpt() previously used the literal "<!-- kanbanflow-sync", which missed
    // "<!--kanbanflow-sync-->" (no spaces). Derived from the shared pattern so the guard cannot
    // drift from what SyncBlockRe accepts.
    private static readonly Regex SyncOpenRe = new(
        @"\A[ \t]*<!--\s*kanbanflow-sync\s*-->",
        RegexOptions.Compiled);

    // One `**Key:** value` record per line, which is exactly how descriptionFor() writes the block.
    // `k` is the FIRST `**…:**` on the line and cannot span one — `[^*\r\n]` excludes both the
    // asterisk that would start a second marker and the newline that would run into the next
    // record. That single restriction is what makes `**Open PR:**` pasted into a LABEL part of the
    // `**State:**` record's value rather than a record of its own. See SyncRecord.
    //
    // No trailing `$`: .NET's multiline `$` matches before `\n` only, so on a CRLF description —
    // which a browser textarea will happily produce for a description a human has edited — the
    // stranded `\r` would make every record fail to match and every chip silently vanish.
    // `[^\r\n]*` already stops at the line end, so the anchor is redundant as well as harmful.
    private static readonly Regex SyncRecordRe = new(
        @"(?<line>^\*\*(?<k>[^*\r\n]+):\*\*[^\r\n]*)",
        RegexOptions.Multiline | RegexOptions.Compiled);

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

    // Deliberately conservative: `**` and backticks only. A lone `*` or `_` is NOT stripped,
    // because this project's issue bodies are full of snake_case identifiers and file globs, and
    // mangling `portfolio_sync` or `*.razor` to make one italic marker disappear is the worse trade.
    // `__` was originally included alongside `**`, but double-underscore appears in Python dunder
    // names (`__init__`, `__all__`) and other identifiers; removing it without a Markdown delimiter
    // check would corrupt those: `__init__` → `init`. Omitted; underscore-delimited text is kept.
    private static readonly Regex EmphasisRe = new(@"\*\*|`", RegexOptions.Compiled);

    // Slicing at a fixed UTF-16 index can retain a LONE HIGH SURROGATE when an astral character
    // (an emoji, most CJK extension B, mathematical alphanumerics) straddles the boundary. That is
    // ill-formed text: it renders as a replacement glyph and corrupts any downstream comparison.
    // Dropping the orphan costs at most one code unit and is always safe.
    /// <summary>Drops the trailing code unit when <paramref name="s"/> ends with a lone high surrogate.</summary>
    private static ReadOnlySpan<char> WithoutOrphanSurrogate(ReadOnlySpan<char> s) =>
        s.Length > 0 && char.IsHighSurrogate(s[^1]) ? s[..^1] : s;

    /// <summary>Up to <see cref="ScanChars"/> UTF-16 code units from <paramref name="description"/>, with a trailing high surrogate removed; returns <c>null</c> when the description is null or empty.</summary>
    private static string? Head(string? description) => string.IsNullOrEmpty(description)
        ? null
        : description.Length > ScanChars
            ? WithoutOrphanSurrogate(description.AsSpan(0, ScanChars)).ToString()
            : description;

    /// <summary>
    /// The <c>kanbanflow-sync</c> block at the head of a mirrored description — its markers
    /// included — or <c>null</c> when this description is not a mirror.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This identifies the generated header's position and shape, not its author. A user with
    /// task-edit access can type the same block at offset zero. The anchor prevents quoted body
    /// content from being interpreted as metadata; it is not authentication or durable identity.
    /// </para>
    /// <para>
    /// Every consumer that presents the block's contents — the card's GitHub chip, the
    /// edit modal's Issue and PR buttons — must ask this method rather than re-deriving the test.
    /// Asking it in two places is exactly how the modal came to answer differently from the card:
    /// its own copy was unanchored, so a forged block pasted anywhere into a description rendered
    /// the attacker's chosen GitHub link, on a button styled to look like the real one.
    /// </para>
    /// <para>
    /// A block whose closing marker falls beyond <see cref="ScanChars"/> does not match and yields
    /// <c>null</c>: the safe direction, and the same answer the card and the modal both want.
    /// </para>
    /// </remarks>
    public static string? SyncBlock(string? description)
    {
        var head = Head(description);
        if (head is null) return null;

        var block = SyncBlockRe.Match(head);
        return block.Success ? block.Value : null;
    }

    /// <summary>
    /// The single whole LINE inside the owned sync block whose record key is <paramref name="key"/>
    /// — <c>"Issue"</c>, <c>"Open PR"</c> — or <c>null</c> when the block is not owned, carries no
    /// such record, or carries more than one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Header position is not a trust boundary. Two of the values inside it come from GitHub
    /// users: <c>descriptionFor()</c>
    /// interpolates the issue's label names and milestone title verbatim into
    /// <c>**State:** … · **Milestone:** … · **Labels:** …</c>, and that line is emitted ABOVE
    /// <c>**Open PR:**</c>.
    /// </para>
    /// <para>
    /// So a label named <c>**Open PR:** https://github.com/a/b/pull/1</c> — 42 characters, inside
    /// GitHub's 50-character label cap, and creatable by anyone with label write on a synced
    /// repository — puts a second, forged <c>**Open PR:**</c> marker inside a genuinely owned
    /// block. A reader that scans the block for the marker finds the forgery FIRST, because it is
    /// on the earlier line. Measured against the real producer and the real component: the edit
    /// modal rendered <c>PR #1 ↗</c> linking to the attacker's repository, on a button styled
    /// exactly like the real one. That is the phishing surface the ownership anchor was added to
    /// close, reopened from inside the envelope.
    /// </para>
    /// <para>
    /// The fix is to stop treating the block as a bag of text and read it as what the producer
    /// writes: one <c>**Key:** value</c> RECORD PER LINE. A line's key is the first
    /// <c>**…:**</c> on it, so <c>**Open PR:**</c> appearing further along the <c>**State:**</c>
    /// line is part of that record's VALUE and can never be mistaken for a record of its own.
    /// </para>
    /// <para>
    /// Duplicates FAIL CLOSED rather than picking one. A label carrying a literal newline would
    /// forge a whole line rather than part of one, and there is no honest way to choose between two
    /// records claiming the same key: an ambiguous provenance claim is not a link worth showing.
    /// Returning <c>null</c> costs a chip; returning the wrong one costs the reader.
    /// </para>
    /// <para>
    /// The whole line is returned rather than the parsed value so that each caller still applies
    /// its own strict pattern to it (<c>**Issue:** &lt;url&gt;</c>, <c>**Open PR:** &lt;url&gt;</c>)
    /// — this method narrows WHERE they may look, and does not take over WHAT they accept.
    /// </para>
    /// </remarks>
    public static string? SyncRecord(string? description, string key)
    {
        var block = SyncBlock(description);
        if (block is null) return null;

        string? found = null;
        foreach (Match m in SyncRecordRe.Matches(block))
        {
            if (!string.Equals(m.Groups["k"].Value, key, StringComparison.Ordinal)) continue;
            if (found is not null) return null;   // ambiguous → fail closed
            found = m.Groups["line"].Value.Trim();
        }
        return found;
    }

    /// <summary>
    /// The GitHub issue URL this description was mirrored from, or <c>null</c> for a description
    /// without a matching header record. The URL is normalized before either its href or label
    /// is derived. Editable metadata is an advisory link, not proof of repository identity.
    /// </summary>
    public static string? IssueUrl(string? description)
    {
        return UrlFromRecord(description, "Issue");
    }

    /// <summary>The normalized pull request URL from its header record, or null.</summary>
    public static string? PullRequestUrl(string? description)
    {
        var url = UrlFromRecord(description, "Open PR");
        return ItemNumberForUrl(url, "pull") is null ? null : url;
    }

    private static string? UrlFromRecord(string? description, string key)
    {
        var line = SyncRecord(description, key);
        if (line is null) return null;

        // Read the value at its start. Searching for another **Issue:** marker inside the value
        // would let free text forge a second record even after SyncRecord isolated the first one.
        var value = line[(key.Length + 5)..].TrimStart(); // **Key:**
        var candidate = value.Split([' ', '\t', '<'], 2)[0];
        // Preserve the complete query/fragment token: parentheses and final punctuation can be
        // meaningful there. Keep the existing sentence-stop cleanup only for a bare path.
        if (candidate.IndexOfAny(['?', '#']) < 0)
            candidate = candidate.Split(')', 2)[0].TrimEnd('.', ',', ';', ':');
        if (!candidate.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com"
            || uri.UserInfo.Length != 0 || !uri.IsDefaultPort)
        {
            return null;
        }

        // Uri resolves dot segments. Derive every label from this same destination, rather than
        // matching a number in the unnormalized text and linking somewhere different.
        return uri.AbsoluteUri;
    }

    /// <summary>The ASCII item number for an exact GitHub owner/repository/item-kind/number path.</summary>
    public static string? ItemNumberForUrl(string? url, string itemKind)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com"
            || uri.UserInfo.Length != 0 || !uri.IsDefaultPort) return null;

        var parts = uri.AbsolutePath.Trim('/').Split('/');
        return parts.Length == 4 && parts[0].Length > 0 && parts[1].Length > 0
            && parts[2] == itemKind && parts[3].Length > 0
            && parts[3].All(c => c is >= '0' and <= '9') ? parts[3] : null;
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
        var number = ItemNumberForUrl(url, "issues");
        return number is null ? "GitHub" : "#" + number;
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
