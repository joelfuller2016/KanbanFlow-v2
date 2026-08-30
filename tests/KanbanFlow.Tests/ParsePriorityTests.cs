using KanbanFlow.Components.Pages;
using KanbanFlow.Models;

namespace KanbanFlow.Tests;

/// <summary>
/// Regression tests for <c>ProjectView.ParsePriority</c> — the last unguarded enum parse in
/// <c>src/</c> before #305, and for <see cref="EnumParse.ParseOr{TEnum}"/>, the shared guard it
/// now routes through.
/// </summary>
/// <remarks>
/// <para>
/// The defect was never a crash. <c>Enum.TryParse&lt;TaskPriority&gt;("99", ...)</c> returns
/// <see langword="true"/> and yields <c>(TaskPriority)99</c>, so the site's own
/// <c>: TaskPriority.Low</c> fallback was unreachable for numeric input — the fallback was a lie,
/// and the undefined value fell through <c>GetPriorityBadgeClass</c>'s default arm to a wrong
/// badge. These tests fail against the pre-#305 implementation on the "99", "4" and "-1" cases
/// and pass on every other case both before and after, so they pin the fix rather than the
/// rewrite.
/// </para>
/// <para>
/// They call the REAL production method (no fakes, no re-implementation) — reachable because
/// <c>ParsePriority</c> was widened from <c>private</c> to <c>internal</c> and the app project
/// already sets <c>InternalsVisibleTo("KanbanFlow.Tests")</c>.
/// </para>
/// </remarks>
public sealed class ParsePriorityTests
{
    [Theory]
    [InlineData("99")]      // the bug: pre-#305 this returned (TaskPriority)99
    [InlineData("4")]       // first out-of-range ordinal — TaskPriority stops at Critical=3
    [InlineData("-1")]
    [InlineData("2147483648")] // wider than int, so TryParse itself rejects it
    [InlineData("garbage")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ParsePriority_falls_back_to_Low_for_invalid_input(string? input)
        => Assert.Equal(TaskPriority.Low, ProjectView.ParsePriority(input));

    [Theory]
    [InlineData("critical", TaskPriority.Critical)]
    [InlineData("Critical", TaskPriority.Critical)]
    [InlineData("CRITICAL", TaskPriority.Critical)]
    [InlineData("high", TaskPriority.High)]
    [InlineData("Medium", TaskPriority.Medium)]
    [InlineData("low", TaskPriority.Low)]
    public void ParsePriority_preserves_case_insensitive_names(string input, TaskPriority expected)
        => Assert.Equal(expected, ProjectView.ParsePriority(input));

    /// <summary>
    /// Every defined name round-trips, so the guard rejects undefined values without also
    /// rejecting a value someone adds to the enum later.
    /// </summary>
    [Fact]
    public void ParsePriority_round_trips_every_defined_value()
    {
        foreach (var defined in Enum.GetValues<TaskPriority>())
            Assert.Equal(defined, ProjectView.ParsePriority(defined.ToString()));
    }

    /// <summary>
    /// The ordinal STRING of a defined value is still accepted — this is the behaviour the
    /// <c>Enum.IsDefined</c> guard must not break while rejecting out-of-range ordinals.
    /// </summary>
    [Fact]
    public void ParsePriority_accepts_in_range_ordinal_strings()
        => Assert.Equal(TaskPriority.Critical, ProjectView.ParsePriority("3"));

    [Fact]
    public void ParseOr_is_the_shared_guard_behind_ParsePriority()
    {
        Assert.Equal(TaskPriority.Low, EnumParse.ParseOr("99", TaskPriority.Low));
        Assert.Equal(WorkStatus.Backlog, EnumParse.ParseOr("99", WorkStatus.Backlog));
        Assert.Equal(WorkStatus.Done, EnumParse.ParseOr("done", WorkStatus.Backlog));
    }
}
