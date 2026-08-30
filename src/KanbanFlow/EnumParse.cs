namespace KanbanFlow;

/// <summary>
/// The one guarded enum parse this codebase uses at every trust boundary (#89 → #205 → #305).
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
/// unguarded copy and became the last unguarded parse in <c>src/</c>. A rule enforced in one
/// class is a rule the rest of the codebase cannot follow.
/// </para>
/// </remarks>
internal static class EnumParse
{
    /// <summary>
    /// Parses <paramref name="raw"/> to <typeparamref name="TEnum"/> case-insensitively, rejecting
    /// null/blank AND undefined values.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> only when <paramref name="raw"/> names a value actually declared by
    /// <typeparamref name="TEnum"/>. On <see langword="false"/>, <paramref name="value"/> is
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
