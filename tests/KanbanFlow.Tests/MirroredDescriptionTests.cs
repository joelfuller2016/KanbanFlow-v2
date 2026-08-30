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
        Assert.Equal(MirroredDescription.ExcerptMaxLength + 1, excerpt!.Length);
        Assert.EndsWith("…", excerpt);
        Assert.NotEqual(body, excerpt);
    }

    // The scan bound is a PERFORMANCE guard on a 64,000-char cap, and a bound that silently loses
    // the header would be worse than no bound: the card would drop its GitHub link on exactly the
    // long issues that most need one. The header is always first, so the bound must never reach it.
    [Fact]
    public void ScanBound_DoesNotCostTheHeaderOnAMaximumLengthBody()
    {
        var huge = MirroredFixture + "\n\n" + new string('z', 64_000);

        Assert.True(huge.Length > MirroredDescription.ScanChars,
            "fixture must exceed the scan bound or this proves nothing");
        Assert.Equal("#454", MirroredDescription.IssueLabel(huge));
        Assert.DoesNotContain("Synced:", MirroredDescription.Excerpt(huge));
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
        Assert.Contains(".razor", excerpt);
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
        var quoted = "A hand-written task, no sync header.\n"
            + new string('p', MirroredDescription.ScanChars)
            + "\n**Issue:** https://github.com/someone-else/other-repo/issues/1\n";

        Assert.True(quoted.IndexOf("**Issue:**", StringComparison.Ordinal) > MirroredDescription.ScanChars,
            "the marker must sit beyond the bound or this proves nothing");
        // Non-vacuity: the same marker IS found when it sits at the top, so a null here means the
        // bound refused it, not that the pattern never matches.
        Assert.Equal("#454", MirroredDescription.IssueLabel(MirroredFixture));

        Assert.Null(MirroredDescription.IssueUrl(quoted));
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
}
