namespace KanbanFlow.Services;

/// <summary>Resolved claim TTL, validated once at startup and injected everywhere it is needed.</summary>
/// <remarks>
/// Consumers must never parse <c>ClaimTtl</c> from <c>IConfiguration</c> independently — do that
/// once in <c>Program.cs</c> and inject this record instead so a malformed or non-positive value
/// is rejected before the first request, not on it.
/// </remarks>
public sealed record ClaimTtlSetting(TimeSpan Value);

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
    public static bool IsStale(string? claimedBy, DateTime? claimedAt, DateTime now, TimeSpan ttl)
    {
        if (string.IsNullOrWhiteSpace(claimedBy))
            return false;   // unclaimed: nothing to expire

        if (claimedAt is null)
            return true;    // claimed but undated — see remarks

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
