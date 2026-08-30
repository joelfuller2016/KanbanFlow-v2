using System.Text.Json;
using KanbanFlow.Services;

namespace KanbanFlow.Tests;

/// <summary>
/// Guards the shipped <c>ClaimTtl</c> configuration value and the TimeSpan trap it fell into.
/// </summary>
/// <remarks>
/// <para>
/// <c>appsettings.json</c> shipped <c>"24:00:00"</c>, intending 24 hours. It is <b>24 days</b>:
/// .NET's <see cref="TimeSpan"/> parser reads <c>d:hh:mm</c> when the first field exceeds 23, so
/// the value resolved to 576 hours and #429's crashed-agent scenario stayed broken by a factor of
/// 24. The whole suite was green throughout, because every other claim test injects its own
/// 1-hour TTL and nothing asserted what the app would actually boot with.
/// </para>
/// <para>
/// These tests read the real file rather than a fixture. A test that re-declares the value it is
/// checking cannot catch a wrong value in the file it is meant to guard.
/// </para>
/// </remarks>
public sealed class ClaimTtlConfigurationTests
{
    /// <summary>Locates the real appsettings.json by walking up from the test binary.</summary>
    private static string AppSettingsPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "KanbanFlow", "appsettings.json");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            "Could not locate src/KanbanFlow/appsettings.json by walking up from " + AppContext.BaseDirectory);
    }

    private static string? ClaimTtlRaw()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(AppSettingsPath()));
        return doc.RootElement.TryGetProperty("ClaimTtl", out var v) ? v.GetString() : null;
    }

    /// <summary>
    /// The shipped value must parse to the intended 24 hours — not 24 days.
    /// </summary>
    [Fact]
    public void Shipped_ClaimTtl_parses_to_twenty_four_hours()
    {
        var raw = ClaimTtlRaw();
        Assert.NotNull(raw);
        Assert.True(TimeSpan.TryParse(raw, out var parsed), $"ClaimTtl '{raw}' does not parse as a TimeSpan.");

        Assert.Equal(TimeSpan.FromHours(24), parsed);
        Assert.Equal(24, parsed.TotalHours);   // states the units the bug got wrong
    }

    /// <summary>
    /// The shipped value must agree with the in-code default, so the two cannot drift apart
    /// unnoticed the way they just did.
    /// </summary>
    [Fact]
    public void Shipped_ClaimTtl_matches_ClaimPolicy_DefaultTtl()
    {
        Assert.True(TimeSpan.TryParse(ClaimTtlRaw(), out var parsed));
        Assert.Equal(ClaimPolicy.DefaultTtl, parsed);
    }

    [Fact]
    public void DefaultTtl_is_twenty_four_hours()
        => Assert.Equal(TimeSpan.FromHours(24), ClaimPolicy.DefaultTtl);

    /// <summary>
    /// The shipped value must also satisfy the startup upper bound, so the app cannot ship a
    /// configuration its own boot validation rejects.
    /// </summary>
    [Fact]
    public void Shipped_ClaimTtl_is_within_the_startup_bounds()
    {
        Assert.True(TimeSpan.TryParse(ClaimTtlRaw(), out var parsed));
        Assert.True(parsed > TimeSpan.Zero, "ClaimTtl must be positive.");
        Assert.True(parsed <= ClaimPolicy.MaxTtl, $"ClaimTtl {parsed} exceeds ClaimPolicy.MaxTtl {ClaimPolicy.MaxTtl}.");
    }

    /// <summary>
    /// Documents the parser trap itself, so the next person to write a TTL sees why the obvious
    /// spelling is wrong. Every row here is measured, not assumed.
    /// </summary>
    [Theory]
    [InlineData("24:00:00", 576)]    // the bug: 24 DAYS, because the first field exceeds 23
    [InlineData("1.00:00:00", 24)]   // the correct spelling for one day
    [InlineData("23:00:00", 23)]     // 23 or less IS read as hours
    [InlineData("00:30:00", 0.5)]
    [InlineData("30", 720)]          // a bare number is DAYS, not minutes
    public void TimeSpan_parsing_reads_a_leading_field_above_23_as_days(string raw, double expectedHours)
    {
        Assert.True(TimeSpan.TryParse(raw, out var parsed));
        Assert.Equal(expectedHours, parsed.TotalHours);
    }

    /// <summary>
    /// <see cref="ClaimPolicy.MaxTtl"/> must be small enough that <c>now - ttl</c> cannot underflow
    /// <see cref="DateTime.MinValue"/> — the arithmetic <see cref="ClaimPolicy.StaleCutoff"/>
    /// performs on every claim.
    /// </summary>
    [Fact]
    public void MaxTtl_cannot_underflow_the_stale_cutoff_arithmetic()
    {
        var cutoff = ClaimPolicy.StaleCutoff(DateTime.UtcNow, ClaimPolicy.MaxTtl);
        Assert.True(cutoff > DateTime.MinValue);

        // And the bound is load-bearing: without it, a merely-parseable TTL throws here.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ClaimPolicy.StaleCutoff(DateTime.UtcNow, TimeSpan.MaxValue));
    }
}
