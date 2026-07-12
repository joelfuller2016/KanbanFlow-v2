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
}
