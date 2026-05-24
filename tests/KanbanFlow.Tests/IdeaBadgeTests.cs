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
}
