namespace KanbanFlow;

/// <summary>
/// The shared guarded enum parse (#89 → #205 → #305): used by the requirement and task-dependency
/// endpoints and by <c>ProjectView</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>Enum.TryParse</c> alone accepts <em>any in-range numeric string</em>: <c>"99"</c> parses
/// successfully to <c>(TEnum)99</c> and returns <see langword="true"/>. A caller that writes
/// <c>TryParse(...) ? parsed : Fallback</c> therefore never reaches its own fallback for numeric
/// junk, and an undefined enum value escapes into the rest of the program. Pairing it with
/// <c>Enum.IsDefined</c> is what makes the fallback honest.
/// </para>
/// <para>
/// This lives here rather than inside <see cref="ApiEndpoints"/> because it is not an API concern.
/// It was private to that class until #305, which is why
/// <c>ProjectView.ParsePriority</c> — a Razor component, unable to reach it — grew its own
/// unguarded copy and became the last unguarded string parse in <c>src/</c>. A rule enforced in
/// one class is a rule the rest of the codebase cannot follow.
/// </para>
/// <para>
/// NOT yet the only copy, and this doc will not pretend otherwise. Roughly seventeen call sites
/// still hand-roll the same <c>TryParse</c> + <c>IsDefined</c> pair — including a same-named
/// <c>ParsePriority</c> in <c>Mcp/KanbanMcpTools.cs</c> whose own comment calls itself "one DRY
/// enum-parse". #305 scoped itself to the one site that was actually WRONG; consolidating the rest
/// is mechanical and tracked separately. Prefer this helper in new code.
/// </para>
/// </remarks>
internal static class EnumParse
{
    /// <summary>
    /// Parses <paramref name="raw"/> to <typeparamref name="TEnum"/> case-insensitively, rejecting
    /// null/blank AND undefined values.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when <paramref name="raw"/> resolves to a value declared by
    /// <typeparamref name="TEnum"/>. Note the inherited breadth of <c>Enum.TryParse</c>: a
    /// comma-separated list of NAMES is accepted and OR-ed, so <c>"Low,Critical"</c> resolves to
    /// <c>Critical</c> and passes (measured; a numeric list such as <c>"1,2"</c> does not). No
    /// UNDEFINED value can escape — that is the guarantee this method exists for — but a caller
    /// needing exactly-one-name must say so itself. On <see langword="false"/>, <paramref name="value"/> is
    /// <c>default(TEnum)</c> — guaranteed by an explicit reset, NOT by <c>Enum.TryParse</c>, which
    /// assigns the undefined value before <c>Enum.IsDefined</c> can reject it. Callers may rely on
    /// this; a test pins it.
    /// </returns>
    internal static bool TryParseDefined<TEnum>(string? raw, out TEnum value) where TEnum : struct, Enum
    {
        value = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        // Enum.TryParse ASSIGNS before we can reject: for "99" it stores (TEnum)99 and returns
        // true, and only Enum.IsDefined then says no. Left as-is, `value` would hold 99 on a
        // false return — the exact undefined value this method exists to keep out, handed to the
        // caller through the out parameter. Reset it so the documented contract is true rather
        // than merely intended. (CodeRabbit, PR #441; measured at 99 before fixing.)
        if (Enum.TryParse(raw, ignoreCase: true, out value) && Enum.IsDefined(value))
            return true;

        value = default;
        return false;
    }

    /// <summary>
    /// Parses <paramref name="raw"/> to <typeparamref name="TEnum"/>, falling back to
    /// <paramref name="fallback"/> for null, blank, unparseable, or undefined input.
    /// </summary>
    /// <remarks>
    /// The fallback-shaped overload, for render paths that must always produce a value rather than
    /// reject the request. Same guard as <see cref="TryParseDefined{TEnum}"/> — expressed this way
    /// so a caller cannot accidentally write the unguarded ternary this type exists to prevent.
    /// </remarks>
    internal static TEnum ParseOr<TEnum>(string? raw, TEnum fallback) where TEnum : struct, Enum
        => TryParseDefined<TEnum>(raw, out var parsed) ? parsed : fallback;
}
