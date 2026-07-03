using KanbanFlow;

namespace KanbanFlow.Tests;

/// <summary>
/// Unit regression tests for <see cref="ApiEndpoints.NormalizeOptionalText"/> — the single-sourced
/// partial-update string rule the requirement PUT routes through (#260, sibling of the project/feature
/// PUT #203 fix). The load-bearing guarantee: an OMITTED field (null) must PRESERVE the stored value
/// (a rename that sends only Text must not wipe Description), an explicit empty/whitespace value CLEARS
/// it, and a real value is trimmed. Exercises the REAL production helper directly (no fakes).
/// </summary>
public sealed class NormalizeOptionalTextTests
{
    // ── Omitted (null incoming) PRESERVES the current value — the #260 data-loss guard. ──

    [Fact]
    public void NullIncoming_PreservesExistingValue()
    {
        Assert.Equal("Existing description", ApiEndpoints.NormalizeOptionalText(null, "Existing description"));
    }

    [Fact]
    public void NullIncoming_PreservesExistingNull()
    {
        Assert.Null(ApiEndpoints.NormalizeOptionalText(null, null));
    }

    // ── Explicit empty/whitespace CLEARS to null (regardless of the current value). ──

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void EmptyOrWhitespaceIncoming_ClearsToNull(string incoming)
    {
        Assert.Null(ApiEndpoints.NormalizeOptionalText(incoming, "Existing description"));
    }

    // ── A real value is trimmed and stored, overwriting whatever was there. ──

    [Theory]
    [InlineData("New value", "New value")]
    [InlineData("  padded  ", "padded")]
    [InlineData("\tTabbed\n", "Tabbed")]
    public void NonBlankIncoming_TrimmedAndStored(string incoming, string expected)
    {
        Assert.Equal(expected, ApiEndpoints.NormalizeOptionalText(incoming, "Old value"));
        // Also overwrites a previously-null column.
        Assert.Equal(expected, ApiEndpoints.NormalizeOptionalText(incoming, null));
    }
}
