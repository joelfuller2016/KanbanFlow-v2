using KanbanFlow.Services;

namespace KanbanFlow.Tests;

/// <summary>
/// Pins <see cref="MirroredDescription"/>, the reader behind a board card's hover reveal.
/// </summary>
/// <remarks>
/// <para>
/// The defect these pin: a synced card showed a truncated title and nothing else, so the issue URL
/// — the most useful thing on a mirrored card — was reachable only by opening the detail panel and
/// scrolling past a five-line sync header. Excerpting the description naively would have surfaced
/// that header instead of the issue's own first sentence, which is worse than showing nothing.
/// </para>
/// <para>
/// <see cref="MirroredFixture"/> is not hand-written. It is the verbatim output of
/// <c>descriptionFor()</c> in <c>scripts/portfolio-sync.mjs</c>, captured by running that function,
/// so this test cannot drift from the producer by agreeing with a remembered format. The middle dot
/// separator and the exact marker spelling are the producer's, not mine.
/// </para>
/// </remarks>
public sealed class MirroredDescriptionTests
{
    private const string MirroredFixture =
        "<!-- kanbanflow-sync -->\n"
        + "**Issue:** https://github.com/joelfuller2016/KanbanFlow-v2/issues/454\n"
        + "**State:** OPEN · **Milestone:** M1 · **Labels:** bug, ui\n"
        + "**Open PR:** none\n"
        + "**Synced:** 2026-08-30T22:41:13.367Z\n"
        + "<!-- /kanbanflow-sync -->\n"
        + "\n"
        + "## 1. Surfaced by\n"
        + "\n"
        + "The board card truncates its title and shows nothing else.\n"
        + "\n"
        + "Second paragraph here.";

    [Fact]
    public void Excerpt_ShowsTheIssueBody_NotTheSyncHeader()
    {
        // NON-VACUITY: the needles below are absence checks, and an absence check passes trivially
        // against an empty string or a fixture that never contained them. Prove they are present in
        // the input first, so the assertions that follow are informative rather than decorative.
        Assert.Contains("**Synced:**", MirroredFixture);
        Assert.Contains("**Issue:**", MirroredFixture);
        Assert.Contains("kanbanflow-sync", MirroredFixture);

        var excerpt = MirroredDescription.Excerpt(MirroredFixture);

        Assert.NotNull(excerpt);
        Assert.NotEqual("", excerpt);
        Assert.DoesNotContain("Synced:", excerpt);
        Assert.DoesNotContain("**Issue:**", excerpt);
        Assert.DoesNotContain("kanbanflow-sync", excerpt);
        Assert.DoesNotContain("Milestone:", excerpt);

        // The body's own first words, with the heading hashes stripped.
        Assert.StartsWith("1. Surfaced by", excerpt);
        Assert.Contains("truncates its title", excerpt);
    }

    [Fact]
    public void IssueUrl_AndLabel_ComeFromTheSyncHeader()
    {
        Assert.Equal(
            "https://github.com/joelfuller2016/KanbanFlow-v2/issues/454",
            MirroredDescription.IssueUrl(MirroredFixture));
        Assert.Equal("#454", MirroredDescription.IssueLabel(MirroredFixture));
    }

    // A hand-authored description must fall through untouched: the card shows its text and no
    // GitHub affordance. Both regexes are anchored to markers only the sync writes, so this is the
    // path every non-mirrored task takes.
    [Fact]
    public void HandWrittenDescription_HasNoIssueLink_AndExcerptsItsOwnText()
    {
        const string plain = "Refactor the deadline guard so it fires below the stride threshold.";

        Assert.Null(MirroredDescription.IssueUrl(plain));
        Assert.Null(MirroredDescription.IssueLabel(plain));
        Assert.Equal(plain, MirroredDescription.Excerpt(plain));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\t  ")]
    public void EmptyInput_YieldsNulls(string? description)
    {
        Assert.Null(MirroredDescription.IssueUrl(description));
        Assert.Null(MirroredDescription.IssueLabel(description));
        Assert.Null(MirroredDescription.Excerpt(description));
    }

    // A description that is ONLY a sync header has no body to excerpt. Returning "" here would make
    // the card render an empty <p> with padding — a reveal that opens onto nothing.
    [Fact]
    public void HeaderOnlyDescription_ExcerptsToNull_ButStillLinks()
    {
        var headerOnly = MirroredFixture[..(MirroredFixture.IndexOf("<!-- /kanbanflow-sync -->", StringComparison.Ordinal)
            + "<!-- /kanbanflow-sync -->".Length)];

        Assert.Null(MirroredDescription.Excerpt(headerOnly));
        Assert.Equal("#454", MirroredDescription.IssueLabel(headerOnly));
    }

    [Fact]
    public void Excerpt_ElidesAtTheDeclaredLength()
    {
        var body = new string('w', MirroredDescription.ExcerptMaxLength + 500);
        var excerpt = MirroredDescription.Excerpt(body);

        Assert.NotNull(excerpt);
        // Elided to the cap plus the one-character ellipsis, never the whole string.
        // The LITERAL 180, not `ExcerptMaxLength + 1`. Deriving both sides from the constant made
        // this invariant under the constant's value - a review measured 96, 120, 400 and 3000 all
        // leaving the suite green, so the number the card's layout depends on was pinned by nothing.
        Assert.Equal(180, MirroredDescription.ExcerptMaxLength);
        Assert.Equal(181, excerpt!.Length);
        Assert.EndsWith("…", excerpt);
    }

    // The scan bound is a PERFORMANCE guard on a 30,000-char cap, and a bound that silently lost the
    // header would be worse than no bound: the card would drop its GitHub link on exactly the long
    // issues that most need one. The header is always first, so the bound must never reach it.
    [Fact]
    public void ScanBound_DoesNotCostTheHeaderOnAMaximumLengthBody()
    {
        var huge = MirroredFixture + "\n\n" + new string('z', 64_000);

        Assert.True(huge.Length > MirroredDescription.ScanChars,
            "fixture must exceed the scan bound or this proves nothing");
        Assert.Equal("#454", MirroredDescription.IssueLabel(huge));

        // OUTPUT-side non-vacuity. `Assert.DoesNotContain(needle, null)` PASSES in xunit, so an
        // absence check alone is satisfied by an excerpt that is null or empty - and "return null
        // for anything over ScanChars" is precisely the mutant that would blank the hover reveal on
        // every real mirrored card, the median mirrored description being ~11,900 chars against a
        // 4,096 bound. Controlling only the INPUT needle left this PR's headline defect
        // undetectable; a positive claim about the content closes it.
        var excerpt = MirroredDescription.Excerpt(huge);
        Assert.NotNull(excerpt);
        Assert.StartsWith("1. Surfaced by", excerpt);
        Assert.DoesNotContain("Synced:", excerpt);
    }

    // The excerpt renders as plain text in a <p>, so markdown syntax in it is debris, not emphasis.
    // Observed on the real board before this: a card read "...not yet in any standard: **§4 Need &
    // usefulness** and **§5 Workflow analysis**".
    [Fact]
    public void Excerpt_StripsInlineMarkdown_AndKeepsLinkText()
    {
        const string body = "The **bold** part, the `code` part, and a [link text](https://example.com/x) here.";

        var excerpt = MirroredDescription.Excerpt(body);

        Assert.NotNull(excerpt);
        Assert.DoesNotContain("**", excerpt);
        Assert.DoesNotContain("`", excerpt);
        Assert.DoesNotContain("https://example.com", excerpt);
        Assert.DoesNotContain("](", excerpt);
        // The words survive; only the syntax goes.
        Assert.Contains("bold", excerpt);
        Assert.Contains("code", excerpt);
        Assert.Contains("link text", excerpt);
    }

    // The conservative half of the same rule, and the reason a lone `*` or `_` is left alone: this
    // project's issue bodies are full of snake_case identifiers and file globs, and mangling them to
    // remove one italic marker is the worse trade. This pins the trade so it is not "tidied" later.
    [Fact]
    public void Excerpt_DoesNotMangleSnakeCaseOrGlobs()
    {
        const string body = "portfolio_sync writes to *.razor and reads KANBANFLOW_DB_PATH.";

        var excerpt = MirroredDescription.Excerpt(body);

        Assert.Contains("portfolio_sync", excerpt);
        Assert.Contains("KANBANFLOW_DB_PATH", excerpt);
        // The STAR is the point: asserting ".razor" alone passed even under a widened emphasis rule
        // that ate the leading `*` - the exact mangling this test claims to forbid.
        Assert.Contains("*.razor", excerpt);
    }

    // The scan bound is not only a cost guard — it is where "the header" is DEFINED to be.
    //
    // Written as a cost guard alone it was unprovable: deleting the bound changed no output any
    // assertion here could see, because an excerpt drawn from the first 4 KB and one drawn from the
    // first 64 KB are the same 180 characters. Mutating it away passed 10/10.
    //
    // It does have a behavioural edge, and it is the one worth pinning. The sync writes the header
    // at the very top; a `**Issue:**` line appearing thousands of characters INTO a body is quoted
    // prose — an issue that discusses the sync format, say — and must never become the card's link.
    // Unbounded, the regex would happily scan down and adopt it, and the card would link to whatever
    // repository the author happened to be quoting.
    [Fact]
    public void AnIssueMarkerQuotedDeepInTheBody_IsNotMistakenForTheHeader()
    {
        // A REAL sync block, so `SyncBlockRe` is satisfied and the scan bound is the ONLY thing
        // that can refuse the URL. The earlier fixture had no block at all, so once `IssueUrl`
        // was hardened to require one it returned null before `ScanChars` was ever consulted,
        // and this test silently stopped pinning the bound it exists for - while its comment
        // kept claiming it did. A review caught it by deleting the bound: 17/17 stayed green.
        var quoted = "<!-- kanbanflow-sync -->\n"
            + "**Labels:** " + new string('p', MirroredDescription.ScanChars) + "\n"
            + "**Issue:** https://github.com/someone-else/other-repo/issues/1\n"
            + "<!-- /kanbanflow-sync -->\n\nbody";

        Assert.True(quoted.IndexOf("**Issue:**", StringComparison.Ordinal) > MirroredDescription.ScanChars,
            "the marker must sit beyond the bound or this proves nothing");
        // Non-vacuity: the same marker IS found when it sits at the top, so a null here means the
        // bound refused it, not that the pattern never matches.
        Assert.Equal("#454", MirroredDescription.IssueLabel(MirroredFixture));

        Assert.Null(MirroredDescription.IssueUrl(quoted));
    }

    // The card's GitHub link is target="_blank", which moves the user out of the app with no
    // announcement, and its icon conveys the destination visually only. Added after a review found
    // the link had a `title` and nothing else, while EditItemModal.razor already carries the
    // "(opens in a new tab)" wording for the same affordance.
    [Fact]
    public void IssueAriaLabel_NamesTheDestinationAndTheNewTab()
    {
        var label = MirroredDescription.IssueAriaLabel(MirroredFixture);

        Assert.NotNull(label);
        Assert.Contains("#454", label);
        Assert.Contains("opens in a new tab", label);
        // Matches EditItemModal's wording exactly; two phrasings for one affordance is worse than one.
        Assert.Equal("GitHub issue #454 (opens in a new tab)", label);
    }

    [Fact]
    public void IssueAriaLabel_IsNullWithoutALink_AndUnnumberedForANonIssueUrl()
    {
        Assert.Null(MirroredDescription.IssueAriaLabel("a hand-written description"));

        const string repoLink = "<!-- kanbanflow-sync -->\n"
            + "**Issue:** https://github.com/joelfuller2016/KanbanFlow-v2\n"
            + "<!-- /kanbanflow-sync -->\n\nbody";
        Assert.Equal("View on GitHub (opens in a new tab)", MirroredDescription.IssueAriaLabel(repoLink));
    }

    // The issue URL is read ONLY from inside the sync block. Found by an independent review: with
    // the pattern matched against the whole description, a hand-written task whose first line is
    // "**Issue:** https://github.com/anyone/anything/issues/1" got a "#1" chip on its card linking
    // wherever its author chose, indistinguishable from a real mirror.
    [Fact]
    public void IssueUrl_IsIgnoredOutsideTheSyncBlock()
    {
        const string spoofed = "**Issue:** https://github.com/someone-else/other-repo/issues/123\n"
            + "This is an ordinary hand-written task.";

        // Non-vacuity: the pattern DOES match this text when not confined to the block, and the
        // same helper finds the real one — so null below is the block requirement, not a dead regex.
        Assert.Contains("**Issue:** https://github.com/", spoofed);
        Assert.Equal("#454", MirroredDescription.IssueLabel(MirroredFixture));

        Assert.Null(MirroredDescription.IssueUrl(spoofed));
        Assert.Null(MirroredDescription.IssueLabel(spoofed));
        Assert.Null(MirroredDescription.IssueAriaLabel(spoofed));
    }

    // Slicing at a fixed UTF-16 index can keep a lone high surrogate when an astral character
    // straddles the boundary — ill-formed text that renders as a replacement glyph.
    [Fact]
    public void Excerpt_NeverSplitsASurrogatePair()
    {
        var emoji = char.ConvertFromUtf32(0x1F600);            // one astral char, two UTF-16 units
        var body = new string('a', MirroredDescription.ExcerptMaxLength - 1) + emoji + new string('z', 40);

        // Non-vacuity: the naive slice at this boundary really is ill-formed.
        Assert.True(char.IsHighSurrogate(body[MirroredDescription.ExcerptMaxLength - 1]),
            "fixture must straddle the boundary or this proves nothing");

        var excerpt = MirroredDescription.Excerpt(body);

        Assert.NotNull(excerpt);
        // The ellipsis is the last char; the char before it must not be an orphaned high surrogate.
        var beforeEllipsis = excerpt![..^1];
        Assert.False(beforeEllipsis.Length > 0 && char.IsHighSurrogate(beforeEllipsis[^1]),
            "excerpt must not end in a lone high surrogate");
        Assert.Equal(MirroredDescription.ExcerptMaxLength - 1, beforeEllipsis.Length);
    }

    // A URL that is not an issue link still gets an affordance, just an unnumbered one.
    [Fact]
    public void NonIssueUrl_LabelsAsGitHub()
    {
        const string repoLink = "<!-- kanbanflow-sync -->\n"
            + "**Issue:** https://github.com/joelfuller2016/KanbanFlow-v2\n"
            + "<!-- /kanbanflow-sync -->\n\nbody";

        Assert.Equal("GitHub", MirroredDescription.IssueLabel(repoLink));
    }

    // The guard in Excerpt() exists to catch an opening marker whose CLOSING marker falls outside
    // the scan window. Without it, SyncBlockRe.Replace matches nothing and the raw header —
    // marker, URL, milestone, labels, timestamp — leaks into the excerpt. Deleting the guard left
    // the suite green before this test was added.
    [Fact]
    public void Excerpt_RefusesASyncBlockWhoseCloseFallsBeyondTheScanBound()
    {
        var unterminated = "<!-- kanbanflow-sync -->\n"
            + "**Issue:** https://github.com/joelfuller2016/KanbanFlow-v2/issues/454\n"
            + "**Labels:** " + new string('p', MirroredDescription.ScanChars) + "\n"
            + "<!-- /kanbanflow-sync -->\n\nbody";

        // Non-vacuity: the closing marker really is beyond the bound, and a terminated block does excerpt.
        Assert.True(
            unterminated.IndexOf("<!-- /kanbanflow-sync -->", StringComparison.Ordinal)
                > MirroredDescription.ScanChars,
            "the closing marker must sit beyond the bound or this proves nothing");
        Assert.StartsWith("1. Surfaced by", MirroredDescription.Excerpt(MirroredFixture));

        Assert.Null(MirroredDescription.Excerpt(unterminated));
    }

    // A URL written at the end of a sentence carries the stop into the match, and the href 404s on
    // GitHub. The TrimEnd in IssueUrl strips it before the link is used.
    [Theory]
    [InlineData(".")]
    [InlineData(",")]
    [InlineData(";")]
    [InlineData(":")]
    public void IssueUrl_DropsATrailingSentenceStop(string stop)
    {
        var body = "<!-- kanbanflow-sync -->\n"
            + "**Issue:** https://github.com/joelfuller2016/KanbanFlow-v2/issues/454" + stop + "\n"
            + "**State:** OPEN\n"
            + "<!-- /kanbanflow-sync -->\n\nbody text";

        Assert.Equal(
            "https://github.com/joelfuller2016/KanbanFlow-v2/issues/454",
            MirroredDescription.IssueUrl(body));
        Assert.Equal("#454", MirroredDescription.IssueLabel(body));
    }

    // Without fragment/query/trailing-slash normalisation these all fell through to the unnumbered
    // "GitHub" label despite naming a perfectly good issue.
    [Theory]
    [InlineData("#issuecomment-4")]
    [InlineData("?utm_source=x")]
    [InlineData("/")]
    public void IssueLabel_ReadsTheNumberThroughFragmentsQueriesAndTrailingSlashes(string suffix)
    {
        var body = "<!-- kanbanflow-sync -->\n"
            + "**Issue:** https://github.com/joelfuller2016/KanbanFlow-v2/issues/454" + suffix + "\n"
            + "**State:** OPEN\n"
            + "<!-- /kanbanflow-sync -->\n\nbody text";

        Assert.Equal("#454", MirroredDescription.IssueLabel(body));
        Assert.Equal("GitHub issue #454 (opens in a new tab)", MirroredDescription.IssueAriaLabel(body));
    }

    // Both SyncBlockRe and SyncOpenRe are anchored with \A[ \t]*, so a balanced <!-- kanbanflow-sync -->
    // block quoted anywhere in the body of a hand-written task is NOT treated as owned metadata.
    // Without anchoring, IssueUrl() would return the quoted URL and Excerpt() would strip the quoted
    // text — contradicting the comments that call the block an ownership envelope.
    [Fact]
    public void IssueUrl_RejectsAQuotedSyncBlockInBodyText()
    {
        const string quotedBlock =
            "<!-- kanbanflow-sync -->\n"
            + "**Issue:** https://github.com/joelfuller2016/KanbanFlow-v2/issues/999\n"
            + "**State:** OPEN\n"
            + "<!-- /kanbanflow-sync -->";
        var body = "This task documents the sync format.\n\n" + quotedBlock + "\n\nThe block above is an example only.";

        Assert.Null(MirroredDescription.IssueUrl(body));
        Assert.Null(MirroredDescription.IssueLabel(body));
        // Excerpt should open with the preamble text rather than be null. The raw block text
        // (comment markers, URL) is not stripped from the excerpt — Excerpt() only removes
        // the owned block at the START; the key guarantee is that no GitHub link is shown.
        var excerpt = MirroredDescription.Excerpt(body);
        Assert.NotNull(excerpt);
        Assert.StartsWith("This task documents the sync format", excerpt);
    }

    // `SyncBlockRe` accepts `<!--\s*kanbanflow-sync\s*-->`, which covers no-space and tab variants
    // that the old literal Contains check missed. A variant opener whose closing marker falls outside
    // the scan window must still return null — the guard must match the regex, not a hand-picked literal.
    [Theory]
    [InlineData("<!--kanbanflow-sync-->")]
    [InlineData("<!--\tkanbanflow-sync -->")]
    public void Excerpt_RefusesWhitespaceVariantOpenersWhoseCloseFallsBeyondTheScanBound(string opener)
    {
        var unterminated = opener + "\n"
            + "**Issue:** https://github.com/joelfuller2016/KanbanFlow-v2/issues/454\n"
            + "**Labels:** " + new string('p', MirroredDescription.ScanChars) + "\n"
            + "<!-- /kanbanflow-sync -->\n\nbody";

        Assert.True(
            unterminated.IndexOf("<!-- /kanbanflow-sync -->", StringComparison.Ordinal)
                > MirroredDescription.ScanChars,
            "the closing marker must sit beyond the bound or this proves nothing");
        Assert.Null(MirroredDescription.Excerpt(unterminated));
    }

    // \A\s* previously accepted newlines, so a hand-written description that opened with blank lines
    // then quoted a complete sync block was treated as a mirror. \A[ \t]* restricts the allowed
    // prefix to horizontal whitespace only, which the sync producer never emits anyway.
    [Fact]
    public void IssueUrl_RejectsAQuotedSyncBlockPrecededByBlankLines()
    {
        var body = "\n\n<!-- kanbanflow-sync -->\n"
            + "**Issue:** https://github.com/joelfuller2016/KanbanFlow-v2/issues/999\n"
            + "**State:** OPEN\n"
            + "<!-- /kanbanflow-sync -->\n\nThe preceding blank lines are hand-written content.";

        // Non-vacuity: the same block at offset zero IS treated as a mirror.
        var atZero = "<!-- kanbanflow-sync -->\n"
            + "**Issue:** https://github.com/joelfuller2016/KanbanFlow-v2/issues/999\n"
            + "**State:** OPEN\n"
            + "<!-- /kanbanflow-sync -->\n\nbody";
        Assert.Equal("#999", MirroredDescription.IssueLabel(atZero));

        Assert.Null(MirroredDescription.IssueUrl(body));
        Assert.Null(MirroredDescription.IssueLabel(body));
    }

    [Fact]
    public void Excerpt_TreatsLeadingNewlineSyncBlockAsPlainText()
    {
        var body = "\n\n<!-- kanbanflow-sync -->\n"
            + "**Issue:** https://github.com/joelfuller2016/KanbanFlow-v2/issues/999\n"
            + "**State:** OPEN\n"
            + "<!-- /kanbanflow-sync -->\n\ntext after.";

        // The block is not owned (leading newlines disqualify it), so it is not stripped.
        // The raw block text becomes part of the excerpt — there is no GitHub link, which is the
        // key safety property (tested in IssueUrl_RejectsAQuotedSyncBlockPrecededByBlankLines).
        var excerpt = MirroredDescription.Excerpt(body);
        Assert.NotNull(excerpt);
        // Non-vacuity: the block text IS in the excerpt (not stripped), and no issue label appears.
        Assert.Contains("kanbanflow-sync", excerpt);
        Assert.Contains("issues/999", excerpt);
    }

    // EmphasisRe previously removed `__`, which corrupted Python dunder names and similar identifiers.
    // `__init__` → `init`, `__all__` → `all`. The fix removes `__` from the pattern entirely.
    [Theory]
    [InlineData("__init__", "__init__")]
    [InlineData("__all__", "__all__")]
    [InlineData("path/__main__.py", "path/__main__.py")]
    public void Excerpt_PreservesDoubleUnderscoreIdentifiers(string input, string expectedSubstring)
    {
        // Non-vacuity: ** IS removed, so the fix is not vacuous.
        var boldInput = "**bold** " + input;
        var excerpt = MirroredDescription.Excerpt(boldInput);
        Assert.NotNull(excerpt);
        Assert.Contains(expectedSubstring, excerpt);
        Assert.DoesNotContain("**", excerpt);
    }

    // ── The ownership anchor, and the fact that it is asked for in exactly one place ─────────────

    /// <summary>
    /// The envelope test itself: only a block the sync wrote — at offset zero — is owned.
    /// </summary>
    /// <remarks>
    /// A description is free text the user can edit, so the marker pair inside one proves nothing;
    /// it can simply be typed. The anchor is the whole of the claim, and everything that renders a
    /// GitHub link from this block depends on it.
    /// </remarks>
    [Fact]
    public void SyncBlock_IsOnlyTheEnvelopeAtTheHeadOfTheDescription()
    {
        // Non-vacuity: a genuine mirror does yield its block, markers included, body excluded.
        var owned = MirroredDescription.SyncBlock(MirroredFixture);
        Assert.NotNull(owned);
        Assert.StartsWith("<!-- kanbanflow-sync -->", owned);
        Assert.EndsWith("<!-- /kanbanflow-sync -->", owned);
        Assert.Contains("issues/454", owned);
        Assert.DoesNotContain("Surfaced by", owned);

        // A FORGED block: the sync's own markers, an attacker's repository, one line of preamble
        // in front of it. Anyone who can edit a task description can write this.
        const string forged = "Notes on how the sync header is written:\n\n"
            + "<!-- kanbanflow-sync -->\n"
            + "**Issue:** https://github.com/attacker/looks-legit/issues/1\n"
            + "**Open PR:** https://github.com/attacker/looks-legit/pull/1\n"
            + "<!-- /kanbanflow-sync -->\n\nThat is the format.";

        // Non-vacuity: the forgery is well-formed — the same block at offset zero IS owned.
        Assert.NotNull(MirroredDescription.SyncBlock(forged[forged.IndexOf("<!--", StringComparison.Ordinal)..]));

        Assert.Null(MirroredDescription.SyncBlock(forged));
        Assert.Null(MirroredDescription.IssueUrl(forged));
    }

    /// <summary>
    /// Nothing outside <see cref="MirroredDescription"/> may pattern-match the sync markers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the guard the defect asked for rather than a guard on the defect. The anchor was
    /// added to the card's parser and not to <c>EditItemModal.razor</c>'s, which carried a second,
    /// unanchored copy — so the forged link the card had stopped rendering still rendered in the
    /// modal, on a button styled exactly like the genuine one. Two parsers is the cause; one
    /// parser is the fix; this case is what keeps a third from appearing.
    /// </para>
    /// <para>
    /// The needle is the REGEX form of the marker, not the marker itself. Prose all over this
    /// repository quotes <c>&lt;!-- kanbanflow-sync --&gt;</c> perfectly legitimately (this very
    /// file does); <c>kanbanflow-sync\s*--&gt;</c> only ever appears inside a pattern string.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheSyncMarkerIsPatternMatchedInExactlyOnePlace()
    {
        const string patternForm = @"kanbanflow-sync\s*-->";
        var src = Path.Combine(RepositorySource.Root(), "src");

        var sources = Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            // Build output re-emits every .razor as generated C#, markers and all.
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();

        // NON-VACUITY: the sweep found the tree, and the needle is spelled the way the one legitimate
        // parser spells it. Without this a typo in either would make the case pass by finding nothing.
        Assert.True(sources.Count > 20, $"Only {sources.Count} source files found under {src}.");
        var parser = Assert.Single(sources,
            f => Path.GetFileName(f).Equals("MirroredDescription.cs", StringComparison.Ordinal));
        Assert.Contains(patternForm, File.ReadAllText(parser), StringComparison.Ordinal);

        var duplicates = sources
            .Where(f => f != parser && File.ReadAllText(f).Contains(patternForm, StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(RepositorySource.Root(), f))
            .ToList();

        Assert.True(duplicates.Count == 0,
            $"A second sync-block parser lives in: {string.Join(", ", duplicates)}. The block is an OWNERSHIP "
            + "claim — only a block the sync itself wrote, at offset zero, may be believed — and a second copy "
            + "of that test is a second chance to get it wrong. That is not hypothetical: this is precisely how "
            + "EditItemModal.razor kept rendering an attacker-chosen GitHub link after the card path stopped. "
            + "Call MirroredDescription.SyncBlock instead of re-deriving the pattern.");
    }

    /// <summary>The modal reaches its GitHub chips through the shared envelope test.</summary>
    /// <remarks>
    /// The absence check above says no file re-implements the isolation; this says the file that
    /// needs it actually asks for it, so deleting the call outright cannot pass both.
    /// </remarks>
    [Fact]
    public void EditItemModal_AsksMirroredDescriptionWhetherTheBlockIsOwned()
    {
        var modal = RepositorySource.Read("src/KanbanFlow/Components/Shared/EditItemModal.razor");

        // NON-VACUITY: the modal is still a consumer — it still renders Issue/PR chips from the block.
        Assert.Contains("GitHubIssueUrl", modal, StringComparison.Ordinal);
        Assert.Contains("**Issue:**", modal, StringComparison.Ordinal);

        Assert.Contains("MirroredDescription.SyncBlock(", modal, StringComparison.Ordinal);
    }
}
