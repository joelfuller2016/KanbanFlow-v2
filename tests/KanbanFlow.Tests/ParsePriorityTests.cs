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

    /// <summary>
    /// <see cref="EnumParse.ParseOr{TEnum}"/> on its own — this asserts the shared helper's
    /// behaviour directly, and says nothing about who calls it. (Renamed after review: the old
    /// name claimed to prove delegation, which the assertions below never touched.)
    /// </summary>
    [Fact]
    public void ParseOr_rejects_undefined_values_for_any_enum()
    {
        Assert.Equal(TaskPriority.Low, EnumParse.ParseOr("99", TaskPriority.Low));
        Assert.Equal(WorkStatus.Backlog, EnumParse.ParseOr("99", WorkStatus.Backlog));
        Assert.Equal(WorkStatus.Done, EnumParse.ParseOr("done", WorkStatus.Backlog));
    }

    /// <summary>
    /// The fallback must be the CALLER'S value, not <c>default</c>.
    /// </summary>
    /// <remarks>
    /// Every other invalid-input assertion here happens to use a zero-valued fallback
    /// (<c>TaskPriority.Low</c> and <c>WorkStatus.Backlog</c> are both <c>0</c>), so an
    /// implementation that wrongly returned <c>default(TEnum)</c> instead of the supplied fallback
    /// would satisfy all of them. This is the one case that can tell those two apart. (Raised by
    /// codex in review of PR #441.)
    /// </remarks>
    [Theory]
    [InlineData("99")]
    [InlineData("garbage")]
    [InlineData("")]
    [InlineData(null)]
    public void ParseOr_returns_the_supplied_fallback_not_default(string? input)
    {
        Assert.NotEqual(default, TaskPriority.Critical);  // guards the premise of this test
        Assert.Equal(TaskPriority.Critical, EnumParse.ParseOr(input, TaskPriority.Critical));
        Assert.Equal(WorkStatus.Done, EnumParse.ParseOr(input, WorkStatus.Done));
    }

    /// <summary>
    /// Whitespace-padded valid input still parses — behaviour inherited from
    /// <c>Enum.TryParse</c> and worth pinning, since a future "tidy up" that trimmed or rejected
    /// padding would silently change what the REST endpoints accept.
    /// </summary>
    [Theory]
    [InlineData(" Critical ", TaskPriority.Critical)]
    [InlineData(" 3 ", TaskPriority.Critical)]
    [InlineData("+3", TaskPriority.Critical)]   // a signed decimal IS accepted
    [InlineData("0x3", TaskPriority.Low)]       // hex is NOT — falls back
    public void ParsePriority_accepts_padded_input(string input, TaskPriority expected)
        => Assert.Equal(expected, ProjectView.ParsePriority(input));

    /// <summary>
    /// A comma-separated list of NAMES is accepted by <c>Enum.TryParse</c> and OR-ed, and the
    /// result can be a defined value that passes <c>Enum.IsDefined</c>. Pinned because the doc on
    /// <see cref="EnumParse.TryParseDefined{TEnum}"/> now states it explicitly: no UNDEFINED value
    /// escapes, but "exactly one name" is not a guarantee this helper makes. Measured, not assumed.
    /// (Raised by codex and the review subagent on PR #441.)
    /// </summary>
    [Fact]
    public void TryParseDefined_accepts_a_comma_separated_name_list_and_the_doc_says_so()
    {
        // Medium|High == 1|2 == 3 == Critical. Deliberately NOT "Low,Critical": Low is 0, so
        // 0|3 == 3 either way and that pair cannot tell OR apart from a "last token wins"
        // implementation. This pair can — last-wins would yield High. (codex, round 2 of PR #441.)
        Assert.True(EnumParse.TryParseDefined<TaskPriority>("Medium,High", out var ored));
        Assert.Equal(TaskPriority.Critical, ored);

        // A NUMERIC list is a different story — it does not resolve, so it is correctly rejected.
        Assert.False(EnumParse.TryParseDefined<TaskPriority>("1,2", out var numeric));
        Assert.Equal(default, numeric);
    }

    /// <summary>
    /// A drift tripwire, NOT a proof of delegation.
    /// </summary>
    /// <remarks>
    /// <c>ParsePriority</c> currently IS <c>ParseOr(input, Low)</c>, so this asserts
    /// <c>f(x) == f(x)</c> and cannot fail today — including against a broken
    /// <see cref="EnumParse"/>, where both sides break identically. Its value is forward-looking:
    /// if someone later reimplements the guard locally, sampled disagreement on these inputs is
    /// what catches it. Do not count it as coverage of the fix. (codex, PR #441.)
    /// </remarks>
    [Theory]
    [InlineData("99")]
    [InlineData("4")]
    [InlineData("-1")]
    [InlineData("garbage")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("critical")]
    [InlineData("Low")]
    [InlineData("3")]
    public void ParsePriority_agrees_with_the_shared_helper_it_delegates_to(string? input)
        => Assert.Equal(EnumParse.ParseOr(input, TaskPriority.Low), ProjectView.ParsePriority(input));

    /// <summary>
    /// The rejected out-value must not escape. <c>Enum.TryParse</c> assigns <c>(TEnum)99</c>
    /// before <c>Enum.IsDefined</c> rejects it, so without an explicit reset the caller receives
    /// the very undefined value the guard exists to keep out. (CodeRabbit, PR #441 — measured at
    /// 99 before the fix.)
    /// </summary>
    [Theory]
    [InlineData("99")]
    [InlineData("4")]
    [InlineData("-1")]
    [InlineData("garbage")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParseDefined_leaves_value_at_default_when_it_returns_false(string? input)
    {
        Assert.False(EnumParse.TryParseDefined<TaskPriority>(input, out var value));
        Assert.Equal(default, value);
    }
}
