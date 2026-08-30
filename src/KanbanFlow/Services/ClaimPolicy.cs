namespace KanbanFlow.Services;

/// <summary>Resolved claim TTL, validated once at startup and injected everywhere it is needed.</summary>
/// <remarks>
/// Consumers must never parse <c>ClaimTtl</c> from <c>IConfiguration</c> independently — do that
/// once in <c>Program.cs</c> and inject this record instead so a malformed or non-positive value
/// is rejected before the first request, not on it.
/// </remarks>
public sealed record ClaimTtlSetting
{
    /// <summary>The validated TTL. Construction throws rather than deferring the failure to a request.</summary>
    public TimeSpan Value { get; }

    /// <param name="value">Must be positive and at most <see cref="ClaimPolicy.MaxTtl"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside those bounds.</exception>
    public ClaimTtlSetting(TimeSpan value)
    {
        ClaimPolicy.ValidateTtl(value, nameof(value));
        Value = value;
    }
}

/// <summary>
/// Decides whether a task claim has gone stale (#429).
/// </summary>
/// <remarks>
/// <para>
/// A claim has no expiry in the data model — <c>ClaimedAt</c> is written on every claim and
/// projected into a dozen API payloads, but before this it was never compared to the clock
/// anywhere in <c>src/</c>. A crashed or rate-limited agent therefore held its task forever, and
/// because <c>ClaimedByOther</c> is the <em>primary</em> sort key in <see cref="TaskRanking"/>,
/// that task sank to the bottom of every other actor's recommendations permanently.
/// </para>
/// <para>
/// Staleness is <strong>derived on read</strong>, not reaped by a background service. That is a
/// deliberate choice: a reaper would be this repo's first <c>IHostedService</c>, it would destroy
/// the record of who held the claim, and it would have no actor to attribute its audit event to.
/// Deriving leaves <c>ClaimedBy</c> intact — an honest record of who abandoned the work — while
/// making the claim ignorable and takeable.
/// </para>
/// <para>
/// Pure and DB-free, exactly like <see cref="TaskRanking"/>, so the boundary can be unit-tested
/// without a database and without any test sleeping in real time.
/// </para>
/// </remarks>
public static class ClaimPolicy
{
    /// <summary>Default time-to-live for a claim before another actor may take it over.</summary>
    /// <remarks>
    /// 24 hours: long enough that a slow human or a long agent run is never stolen from, short
    /// enough that a crash does not park work for a week.
    /// </remarks>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(24);

    /// <summary>Upper bound on a configured claim TTL.</summary>
    /// <remarks>
    /// <see cref="StaleCutoff"/> computes <c>now - ttl</c>, which throws
    /// <see cref="ArgumentOutOfRangeException"/> once the result underflows
    /// <see cref="DateTime.MinValue"/> (about 740,000 days) — and it runs on every claim, so an
    /// unbounded TTL turns a config typo into a 500 on every claim rather than a boot failure.
    /// One year is far beyond any legitimate claim lifetime.
    /// </remarks>
    public static readonly TimeSpan MaxTtl = TimeSpan.FromDays(365);

    /// <summary>
    /// How far ahead of "now" a claim timestamp may sit before it is treated as corrupt.
    /// </summary>
    /// <remarks>
    /// Small deliberately: this is an allowance for ordinary clock jitter between the app and a
    /// database round-trip, not for a genuinely wrong clock. Anything further ahead cannot be a
    /// legitimate claim, and treating it as fresh would make it permanent.
    /// </remarks>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>
    /// True when a claim should be treated as abandoned.
    /// </summary>
    /// <param name="claimedBy">The current holder, or null/blank when unclaimed.</param>
    /// <param name="claimedAt">When the claim was taken. UTC.</param>
    /// <param name="now">The current time. UTC. Injected so tests need not sleep.</param>
    /// <param name="ttl">How long a claim remains fresh.</param>
    /// <returns>
    /// <c>false</c> for an unclaimed task — there is nothing to expire. Otherwise <c>true</c> once
    /// the claim is older than <paramref name="ttl"/>.
    /// </returns>
    /// <remarks>
    /// A task claimed with a <c>null</c> <paramref name="claimedAt"/> counts as <strong>stale</strong>.
    /// That combination should not occur (<c>ClaimAsync</c> always writes both), but a legacy row or
    /// a direct database edit could produce it, and the alternative — treating it as fresh — would
    /// make such a claim permanent, which is the exact failure this policy exists to remove.
    /// </remarks>
    /// <summary>
    /// Resolves and validates a configured claim TTL, or throws with a message naming the trap.
    /// </summary>
    /// <remarks>
    /// Extracted from <c>Program.cs</c> so the boot contract is TESTABLE. It previously lived in
    /// top-level statements with no seam, so nothing could fail if the validation were deleted —
    /// and the guard test re-implemented the bound rather than invoking it, which is the very
    /// anti-pattern that let the 24-day value ship. (Round-2 review, PR #435.)
    /// </remarks>
    /// <param name="raw">
    /// The configured value. Null, empty, OR WHITESPACE-ONLY selects <see cref="DefaultTtl"/> — a
    /// blank config entry means "unset", not "malformed". (The doc previously said "null or empty"
    /// while the code tested whitespace too; codex caught the mismatch in PR #435 round 3.)
    /// </param>
    /// <exception cref="InvalidOperationException">Malformed, non-positive, or above <see cref="MaxTtl"/>.</exception>
    public static TimeSpan ResolveTtl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return DefaultTtl;

        // TryParse, not Parse: a malformed value must be reported by the same exception as a
        // non-positive one, naming the setting and the expected shape.
        if (!TimeSpan.TryParse(raw, out var parsed))
            throw new InvalidOperationException(
                $"ClaimTtl must be a TimeSpan such as '1.00:00:00' or '00:30:00'; got '{raw}'.");

        // The bounds live in ValidateTtl so config, ClaimTtlSetting and TaskService cannot drift
        // apart; only the MESSAGE differs here, because a config error deserves the trap spelled out.
        if (parsed <= TimeSpan.Zero)
            throw new InvalidOperationException($"ClaimTtl must be positive; got '{raw}'.");

        if (parsed > MaxTtl)
            throw new InvalidOperationException(
                $"ClaimTtl must be {MaxTtl} or less; got '{raw}' (parsed as {parsed}). "
                + "TimeSpan reads a bare number as DAYS, and treats a first field above 23 as days "
                + "too, so \"24:00:00\" is 24 DAYS — write \"1.00:00:00\" for one day.");

        return parsed;
    }

    /// <summary>
    /// The single bounds check for a claim TTL, whatever produced it.
    /// </summary>
    /// <remarks>
    /// <see cref="ResolveTtl"/> guards the CONFIG path, but a TTL can also arrive by direct
    /// construction — <c>new ClaimTtlSetting(...)</c> or <c>new TaskService(factory, ttl)</c> —
    /// and those bypassed the upper bound entirely: <c>TimeSpan.MaxValue</c> constructed fine and
    /// then threw on the next claim, from inside <see cref="StaleCutoff"/>, which is exactly the
    /// deferred-failure mode the config guard exists to prevent. Every entry point now lands here.
    /// (codex, PR #435 round 3.)
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Non-positive, or above <see cref="MaxTtl"/>.</exception>
    public static void ValidateTtl(TimeSpan ttl, string paramName)
    {
        if (ttl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(paramName, ttl, "Claim TTL must be positive.");

        if (ttl > MaxTtl)
            throw new ArgumentOutOfRangeException(
                paramName, ttl, $"Claim TTL must be {MaxTtl} or less; StaleCutoff underflows beyond it.");
    }

    public static bool IsStale(string? claimedBy, DateTime? claimedAt, DateTime now, TimeSpan ttl)
    {
        if (string.IsNullOrWhiteSpace(claimedBy))
            return false;   // unclaimed: nothing to expire

        if (claimedAt is null)
            return true;    // claimed but undated — see remarks

        // A timestamp in the FUTURE beyond the skew allowance is stale, not eternally fresh.
        // `now - claimedAt` is negative for a future claim, so without this it can never exceed
        // ttl and the claim is permanent and unstealable — issue #429 reintroduced for any host
        // whose clock runs fast. A claim cannot legitimately be made after now, so a value past
        // the allowance is a corrupt or skewed write, and the safe reading of a corrupt claim is
        // the same as an undated one: stealable. (codex, PR #435.)
        if (claimedAt.Value > now + MaxClockSkew)
            return true;

        // Strictly greater-than, so a claim exactly at the TTL boundary is still FRESH. The
        // boundary has to fall on one side deliberately; favouring the holder means a claim is
        // never stolen a tick early.
        return now - claimedAt.Value > ttl;
    }

    /// <summary>
    /// The instant before which a claim is considered stale — the cutoff for a
    /// <c>WHERE ClaimedAt &lt; @cutoff</c> predicate in a conditional update.
    /// </summary>
    /// <remarks>
    /// Kept next to <see cref="IsStale"/> on purpose: the in-memory check and the SQL predicate
    /// are two expressions of one rule, and letting them drift is how a task becomes stealable by
    /// the ranker but not by the claim, or the reverse.
    /// </remarks>
    public static DateTime StaleCutoff(DateTime now, TimeSpan ttl) => now - ttl;
}
