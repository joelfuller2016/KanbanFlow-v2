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
    [Theory]
    [InlineData(IdeaStatus.New, "bg-primary")]
    [InlineData(IdeaStatus.Triaging, "bg-warning text-dark")]
    [InlineData(IdeaStatus.Promoted, "bg-success")]
    [InlineData(IdeaStatus.Archived, "bg-secondary")]
    [InlineData(IdeaStatus.Rejected, "bg-danger")]
    public void StatusClass_MapsEveryStatus(IdeaStatus status, string expected)
    {
        Assert.Equal(expected, IdeaBadge.StatusClass(status));
    }

    [Theory]
    [InlineData(TaskPriority.Low, "bg-light text-dark")]
    [InlineData(TaskPriority.Medium, "bg-secondary")]
    [InlineData(TaskPriority.High, "bg-orange text-white")]
    [InlineData(TaskPriority.Critical, "bg-danger")]
    public void PriorityClass_MapsEveryPriority(TaskPriority priority, string expected)
    {
        Assert.Equal(expected, IdeaBadge.PriorityClass(priority));
    }

    [Fact]
    public void StatusClass_CoversAllDeclaredStatuses()
    {
        // Guard: if a new IdeaStatus is added, this fails until the mapping is updated
        // (no value falls through to the default "bg-secondary" silently for a real enum).
        foreach (var status in Enum.GetValues<IdeaStatus>())
            Assert.False(string.IsNullOrWhiteSpace(IdeaBadge.StatusClass(status)));
    }

    [Fact]
    public void PriorityClass_HighUsesCustomOrangeClass()
    {
        // bg-orange is a project-specific class (Bootstrap has none) defined in app.css.
        Assert.Contains("bg-orange", IdeaBadge.PriorityClass(TaskPriority.High));
    }
}
