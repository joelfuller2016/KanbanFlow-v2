using KanbanFlow;
using KanbanFlow.Models;

namespace KanbanFlow.Tests;

/// <summary>
/// Unit regression tests for <see cref="EnumParse.TryParseDefined{TEnum}"/> — the single-sourced
/// enum-input guard the requirement (create + PUT) and task-dependency (create) endpoints route through
/// (#205, #153). <c>Enum.TryParse</c> alone accepts ANY in-range numeric string (e.g. "99"), which the
/// old endpoints then persisted as a bogus enum value; the helper pairs it with <c>Enum.IsDefined</c> so
/// only defined names/ids parse and null/blank is rejected. These exercise the REAL production helper
/// directly (no fakes) across the three enums that flow through it: <see cref="RequirementPriority"/>,
/// <see cref="RequirementStatus"/>, and <see cref="TaskDependencyType"/>.
/// </summary>
public sealed class TryParseDefinedEnumTests
{
    // ── Out-of-range numeric strings must be REJECTED — this is the #205 bug: Enum.TryParse alone
    //    accepts any in-range int, so "99" parsed and silently persisted before the IsDefined pairing. ──

    [Theory]
    [InlineData("99")]
    [InlineData("999")]
    [InlineData("-1")]
    public void Rejects_OutOfRangeNumericString_RequirementPriority(string raw)
    {
        // Standard TryX contract: on a false return the out value is unspecified (for the
        // in-range-but-undefined numeric path it holds the parsed value, not default), so callers
        // MUST ignore it — every requirement/dependency call site 422s without reading it. We assert
        // only the load-bearing guarantee: an undefined value is rejected.
        Assert.False(EnumParse.TryParseDefined<RequirementPriority>(raw, out _));
    }

    [Theory]
    [InlineData("99")]
    [InlineData("999")]
    [InlineData("-1")]
    public void Rejects_OutOfRangeNumericString_RequirementStatus(string raw)
    {
        Assert.False(EnumParse.TryParseDefined<RequirementStatus>(raw, out _));
    }

    [Theory]
    [InlineData("99")]
    [InlineData("999")]
    [InlineData("-1")]
    public void Rejects_OutOfRangeNumericString_TaskDependencyType(string raw)
    {
        Assert.False(EnumParse.TryParseDefined<TaskDependencyType>(raw, out _));
    }

    // ── Unknown NAMES must be rejected too. ──

    [Theory]
    [InlineData("banana")]
    [InlineData("P99")]
    [InlineData("Openish")]
    public void Rejects_UnknownName(string raw)
    {
        Assert.False(EnumParse.TryParseDefined<RequirementPriority>(raw, out _));
    }

    // ── Valid NAMES parse case-insensitively → true + the correct value. ──

    [Theory]
    [InlineData("P0", RequirementPriority.P0)]
    [InlineData("p0", RequirementPriority.P0)]
    [InlineData("P1", RequirementPriority.P1)]
    [InlineData("p2", RequirementPriority.P2)]
    public void Accepts_ValidPriorityName_CaseInsensitive(string raw, RequirementPriority expected)
    {
        Assert.True(EnumParse.TryParseDefined<RequirementPriority>(raw, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("Open", RequirementStatus.Open)]
    [InlineData("open", RequirementStatus.Open)]
    [InlineData("InProgress", RequirementStatus.InProgress)]
    [InlineData("inprogress", RequirementStatus.InProgress)]
    [InlineData("DONE", RequirementStatus.Done)]
    public void Accepts_ValidStatusName_CaseInsensitive(string raw, RequirementStatus expected)
    {
        Assert.True(EnumParse.TryParseDefined<RequirementStatus>(raw, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("Blocks", TaskDependencyType.Blocks)]
    [InlineData("blocks", TaskDependencyType.Blocks)]
    [InlineData("DependsOn", TaskDependencyType.DependsOn)]
    [InlineData("dependson", TaskDependencyType.DependsOn)]
    public void Accepts_ValidDependencyTypeName_CaseInsensitive(string raw, TaskDependencyType expected)
    {
        Assert.True(EnumParse.TryParseDefined<TaskDependencyType>(raw, out var value));
        Assert.Equal(expected, value);
    }

    // ── Valid NUMERIC ids for a defined member parse → true + the correct value. ──

    [Theory]
    [InlineData("0", RequirementPriority.P0)]
    [InlineData("1", RequirementPriority.P1)]
    [InlineData("2", RequirementPriority.P2)]
    public void Accepts_DefinedNumericId_RequirementPriority(string raw, RequirementPriority expected)
    {
        Assert.True(EnumParse.TryParseDefined<RequirementPriority>(raw, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("0", RequirementStatus.Open)]
    [InlineData("1", RequirementStatus.InProgress)]
    [InlineData("2", RequirementStatus.Done)]
    public void Accepts_DefinedNumericId_RequirementStatus(string raw, RequirementStatus expected)
    {
        Assert.True(EnumParse.TryParseDefined<RequirementStatus>(raw, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("0", TaskDependencyType.Blocks)]
    [InlineData("1", TaskDependencyType.DependsOn)]
    public void Accepts_DefinedNumericId_TaskDependencyType(string raw, TaskDependencyType expected)
    {
        Assert.True(EnumParse.TryParseDefined<TaskDependencyType>(raw, out var value));
        Assert.Equal(expected, value);
    }

    // ── Whitespace-PADDED valid input: Enum.TryParse trims surrounding whitespace, so a padded name or
    //    numeric id still parses to the defined member. Locks this boundary down (only null/blank/
    //    whitespace-ONLY input is rejected — see the Rejects_NullOrBlank_* cases below). ──

    [Theory]
    [InlineData(" P0 ", RequirementPriority.P0)]
    [InlineData("\tP1", RequirementPriority.P1)]
    [InlineData(" 2 ", RequirementPriority.P2)]
    public void Accepts_WhitespacePaddedValidInput_RequirementPriority(string raw, RequirementPriority expected)
    {
        Assert.True(EnumParse.TryParseDefined<RequirementPriority>(raw, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData(" Open ", RequirementStatus.Open)]
    [InlineData(" 1 ", RequirementStatus.InProgress)]
    public void Accepts_WhitespacePaddedValidInput_RequirementStatus(string raw, RequirementStatus expected)
    {
        Assert.True(EnumParse.TryParseDefined<RequirementStatus>(raw, out var value));
        Assert.Equal(expected, value);
    }

    // ── null / empty / whitespace → false (all three enums). ──

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_NullOrBlank_RequirementPriority(string? raw)
    {
        Assert.False(EnumParse.TryParseDefined<RequirementPriority>(raw, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_NullOrBlank_RequirementStatus(string? raw)
    {
        Assert.False(EnumParse.TryParseDefined<RequirementStatus>(raw, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_NullOrBlank_TaskDependencyType(string? raw)
    {
        Assert.False(EnumParse.TryParseDefined<TaskDependencyType>(raw, out _));
    }
}
