using KanbanFlow.Models;
using KanbanFlow.Services;

namespace KanbanFlow.Tests;

/// <summary>
/// Real-code tests for the pure <see cref="IdeaBadge"/> mappings used by the Ideas inbox
/// cards (#180 Phase 4). These assert the exact Bootstrap badge classes — including the
/// project's custom <c>bg-orange</c> for High priority — so a palette regression is caught
/// without bUnit (which this repo intentionally does not use).
/// </summary>
public sealed class IdeaBadgeTests
{
    // Exhaustive + guarded: every declared IdeaStatus maps to its expected badge class, AND a
    // newly-added enum value fails here (no expected entry) instead of silently falling through
    // to the non-empty default — which a non-empty-only assertion would not catch.
    [Fact]
    public void StatusClass_MapsEveryDeclaredStatus()
    {
        var expected = new Dictionary<IdeaStatus, string>
        {
            [IdeaStatus.New] = "bg-primary",
            [IdeaStatus.Triaging] = "bg-warning text-dark",
            [IdeaStatus.Promoted] = "bg-success",
            [IdeaStatus.Archived] = "bg-secondary",
            [IdeaStatus.Rejected] = "bg-danger",
        };
        foreach (var status in Enum.GetValues<IdeaStatus>())
        {
            Assert.True(expected.ContainsKey(status),
                $"No expected badge for {status} — add it to IdeaBadge.StatusClass and this test.");
            Assert.Equal(expected[status], IdeaBadge.StatusClass(status));
        }
    }

    [Theory]
    [InlineData(TaskPriority.Low, "bg-secondary")]
    [InlineData(TaskPriority.Medium, "bg-warning text-dark")]
    [InlineData(TaskPriority.High, "bg-orange text-white")]
    [InlineData(TaskPriority.Critical, "bg-danger")]
    public void PriorityClass_MapsEveryPriority(TaskPriority priority, string expected)
    {
        Assert.Equal(expected, IdeaBadge.PriorityClass(priority));
    }

    [Fact]
    public void PriorityClass_HighUsesCustomOrangeClass()
    {
        // bg-orange is a project-specific class (Bootstrap has none) defined in app.css.
        Assert.Contains("bg-orange", IdeaBadge.PriorityClass(TaskPriority.High));
    }

    // ── PlanPreview: the Plan chip's tooltip (#282) ───────────────────────────────────────────
    // A pure string function with real edge cases, which is exactly why it was moved OUT of
    // IdeaCard.razor's @code block (unreachable from a test) and into IdeaBadge (#300).

    [Fact]
    public void PlanPreview_StacksHeadings_TakesTheMostSpecificOneAndKeepsLookingForContent()
    {
        // THE REGRESSION (Gemini, PR #300). A `heading is null` guard meant the SECOND heading was
        // not treated as a heading at all — it became the "content" line, and the tooltip came back
        // as two labels and no content: "Scope: Sub-scope". A well-structured plan, i.e. the kind
        // most worth previewing, was the one that previewed worst.
        var preview = IdeaBadge.PlanPreview("# Scope\n## Sub-scope\nProve the branch renders.");

        Assert.Equal("Sub-scope: Prove the branch renders.", preview);
        Assert.DoesNotContain("Scope: Sub-scope", preview);
    }

    [Fact]
    public void PlanPreview_FoldsASingleHeadingIntoTheLineItIntroduces()
    {
        Assert.Equal("Scope: Prove the branch renders.",
            IdeaBadge.PlanPreview("# Scope\nProve the branch renders."));
    }

    [Fact]
    public void PlanPreview_StripsMarkdownChromeFromAPlainOpeningLine()
    {
        Assert.Equal("Problem Zero agent presence in the UI.",
            IdeaBadge.PlanPreview("**Problem** Zero agent presence in the UI."));
    }

    [Fact]
    public void PlanPreview_SkipsBlankLinesBetweenHeadingAndContent()
    {
        Assert.Equal("Scope: The first real line.",
            IdeaBadge.PlanPreview("# Scope\n\n   \n- The first real line.\nA later line."));
    }

    [Fact]
    public void PlanPreview_HeadingOnlyPlan_FallsBackToItsMostSpecificHeading()
    {
        // Nothing but labels: the deepest one is still better than a generic "Has a plan".
        Assert.Equal("Sub-scope", IdeaBadge.PlanPreview("# Scope\n## Sub-scope"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\t\n  ")]
    public void PlanPreview_EmptyOrWhitespacePlan_FallsBackToTheGenericTooltip(string? plan)
    {
        Assert.Equal("Has a plan", IdeaBadge.PlanPreview(plan));
    }

    [Fact]
    public void PlanPreview_ElidesAtTheCapAndNeverExceedsIt()
    {
        // 120-char cap + a one-char ellipsis, and the elision must not leave a dangling space.
        var preview = IdeaBadge.PlanPreview(new string('a', 200));

        Assert.EndsWith("…", preview);
        Assert.Equal(121, preview.Length);
        Assert.Equal(new string('a', 120) + "…", preview);
    }

    [Fact]
    public void PlanPreview_ShortPlanIsNotElided()
    {
        var preview = IdeaBadge.PlanPreview("Short and sweet.");

        Assert.Equal("Short and sweet.", preview);
        Assert.DoesNotContain("…", preview);
    }

    [Fact]
    public void PlanPreview_DoesNotSplitASurrogatePairAtTheCap()
    {
        // 🎯 (U+1F3AF) is ONE character stored as TWO UTF-16 chars. Here it straddles the 120-char
        // cap exactly: its HIGH surrogate lands at index 119, its LOW at index 120. A naive
        // AsSpan(0, 120) keeps the high half and drops the low one, emitting a LONE SURROGATE —
        // not a character at all, and the thing the user sees as � in the chip's tooltip.
        var plan = new string('a', 119) + "\U0001F3AF" + new string('b', 50);

        var preview = IdeaBadge.PlanPreview(plan);

        Assert.False(HasUnpairedSurrogate(preview),
            $"preview contains an unpaired surrogate: {Describe(preview)}");
        // And the observable consequence: what Blazor hands the browser is UTF-8, and encoding a
        // lone surrogate to UTF-8 substitutes U+FFFD. THIS is the visible garbage.
        Assert.DoesNotContain('�', AsBrowserWouldSeeIt(preview));
        Assert.EndsWith("…", preview);
        Assert.True(preview.Length <= PlanPreviewCap + 1,
            $"preview overran the cap: {preview.Length} chars");
    }

    [Fact]
    public void PlanPreview_DoesNotSplitAGraphemeClusterAtTheCap()
    {
        // The stronger case, and why a surrogate-pair-only guard is not enough. 🇺🇸 is ONE grapheme
        // built from TWO surrogate pairs (4 UTF-16 chars). Cutting it down the middle splits no
        // surrogate pair at all — it yields a perfectly well-formed LONE REGIONAL INDICATOR (🇺),
        // which renders as a stray letter-in-a-box. No �, so a surrogate-only check waves it
        // through; only a grapheme-cluster-aware cut refuses to emit half a flag.
        var plan = new string('a', 118) + "\U0001F1FA\U0001F1F8" + new string('b', 50);

        var preview = IdeaBadge.PlanPreview(plan);

        Assert.False(HasUnpairedSurrogate(preview), $"unpaired surrogate: {Describe(preview)}");
        Assert.DoesNotContain("\U0001F1FA", preview); // no half-flag
        Assert.Equal(new string('a', 118) + "…", preview);
    }

    [Fact]
    public void PlanPreview_SingleGraphemeLongerThanTheWholeCap_FallsBackRatherThanEmitHalfAGlyph()
    {
        // "e" + 200 combining acute accents is ONE extended grapheme cluster, 201 UTF-16 chars long
        // — so not one single char of it fits inside the 120 cap. There is no honest prefix, and the
        // one thing we must never do is emit half of it.
        var plan = "e" + new string('\u0301', 200); // COMBINING ACUTE ACCENT

        var preview = IdeaBadge.PlanPreview(plan);

        Assert.Equal("Has a plan", preview);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Mirrors IdeaBadge.PlanPreviewMaxLength (private there, asserted against here).</summary>
    private const int PlanPreviewCap = 120;

    /// <summary>True if any surrogate in <paramref name="s"/> lacks its other half.</summary>
    private static bool HasUnpairedSurrogate(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]))
            {
                if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1]))
                    return true;
                i++; // a well-formed pair; skip its low half
            }
            else if (char.IsLowSurrogate(s[i]))
            {
                return true; // a low surrogate with no high half before it
            }
        }
        return false;
    }

    /// <summary>
    /// The string as it reaches the browser: Blazor serialises the <c>title</c> attribute as UTF-8,
    /// and UTF-8 cannot represent a lone surrogate — the encoder substitutes U+FFFD (�).
    /// </summary>
    private static string AsBrowserWouldSeeIt(string s) =>
        System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(s));

    /// <summary>Renders the tail of a string as code units, so a failure names the broken char.</summary>
    private static string Describe(string s) =>
        string.Join(" ", s.TakeLast(4).Select(c => $"U+{(int)c:X4}"));
}
