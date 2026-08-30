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

    /// <summary>
    /// Matches what the ASP.NET Core JSON configuration provider tolerates. Without this, adding
    /// a <c>// why 1.00:00:00 and not 24:00:00</c> comment to appsettings.json — precisely the
    /// comment this bug invites — would red these guards.
    /// </summary>
    private static readonly JsonDocumentOptions JsonOptions =
        new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static string? ClaimTtlRaw()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(AppSettingsPath()), JsonOptions);
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
    /// The shipped value must survive the REAL boot validation — invoked, not re-implemented.
    /// </summary>
    /// <remarks>
    /// The first version of this test asserted <c>parsed &gt; Zero &amp;&amp; parsed &lt;= MaxTtl</c> itself.
    /// That reads like coverage of the startup guard but stays green if the guard is deleted
    /// outright — the same anti-pattern that let the 24-day value ship, applied to the bound
    /// instead of the value. It now calls <see cref="ClaimPolicy.ResolveTtl"/>, which is what
    /// <c>Program.cs</c> calls. (Round-2 review, PR #435.)
    /// </remarks>
    [Fact]
    public void Shipped_ClaimTtl_survives_the_real_startup_validation()
        => Assert.Equal(TimeSpan.FromHours(24), ClaimPolicy.ResolveTtl(ClaimTtlRaw()));

    /// <summary>
    /// Every appsettings*.json is checked, not just the base file: a <c>ClaimTtl</c> added to the
    /// Development overlay would ship the identical bug in the environment agents actually run in,
    /// and a base-file-only guard would stay green.
    /// </summary>
    [Fact]
    public void Every_appsettings_overlay_declares_a_valid_ClaimTtl()
    {
        var baseDir = Path.GetDirectoryName(AppSettingsPath())!;
        foreach (var file in Directory.GetFiles(baseDir, "appsettings*.json"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file), JsonOptions);
            if (!doc.RootElement.TryGetProperty("ClaimTtl", out var v)) continue;

            var ex = Record.Exception(() => ClaimPolicy.ResolveTtl(v.GetString()));
            Assert.True(ex is null, $"{Path.GetFileName(file)} declares an invalid ClaimTtl: {ex?.Message}");
        }
    }

    /// <summary>
    /// The boot contract itself, exercised through the seam <c>Program.cs</c> uses.
    /// </summary>
    [Theory]
    [InlineData(null)]              // absent -> DefaultTtl
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveTtl_falls_back_to_the_default_when_unset(string? raw)
        => Assert.Equal(ClaimPolicy.DefaultTtl, ClaimPolicy.ResolveTtl(raw));

    [Theory]
    [InlineData("garbage")]         // malformed
    [InlineData("-1.00:00:00")]     // negative
    [InlineData("00:00:00")]        // zero
    [InlineData("10675199")]        // parses, but underflows StaleCutoff on every claim
    [InlineData("366")]             // 366 days — just past MaxTtl
    public void ResolveTtl_rejects_values_the_app_cannot_run_with(string raw)
        => Assert.Throws<InvalidOperationException>(() => ClaimPolicy.ResolveTtl(raw));

    /// <summary>
    /// The rejection message must name the trap, because the trap is the whole story: the value
    /// that shipped was a plausible-looking "24:00:00".
    /// </summary>
    [Fact]
    public void ResolveTtl_rejection_message_explains_the_days_vs_hours_trap()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ClaimPolicy.ResolveTtl("400"));
        Assert.Contains("24 DAYS", ex.Message);
        Assert.Contains("1.00:00:00", ex.Message);
    }

    /// <summary>
    /// A claim timestamped in the FUTURE must be stale, not eternally fresh.
    /// </summary>
    /// <remarks>
    /// <c>now - claimedAt</c> is negative for a future claim, so it can never exceed the TTL. Left
    /// unhandled, a host with a fast clock writes a permanent, unstealable claim — issue #429
    /// reintroduced by the very code meant to close it. (codex, PR #435.)
    /// </remarks>
    [Fact]
    public void A_future_claim_timestamp_is_stale_not_permanent()
    {
        var now = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);

        // Inside the skew allowance: ordinary jitter, still the holder's claim.
        Assert.False(ClaimPolicy.IsStale("alice", now.AddMinutes(1), now, TimeSpan.FromHours(1)));

        // Beyond it: a corrupt or skewed write, and therefore stealable.
        Assert.True(ClaimPolicy.IsStale("alice", now.AddHours(1), now, TimeSpan.FromHours(1)));
        Assert.True(ClaimPolicy.IsStale("alice", now.AddYears(10), now, TimeSpan.FromHours(1)));
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
