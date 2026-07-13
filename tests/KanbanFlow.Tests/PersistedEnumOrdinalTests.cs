using KanbanFlow.Models;

namespace KanbanFlow.Tests;

/// <summary>
/// The schema lock for every enum that reaches disk (#301).
///
/// KanbanFlow persists all of these as RAW ORDINAL ints — <c>git grep -c HasConversion -- src/</c>
/// returns zero. That makes the numbers below the DATABASE FORMAT, not an implementation detail,
/// and it makes an ordinary-looking C# edit a silent data-corruption event:
///
///   * reorder a member  → every stored row after it is re-typed
///   * remove a member   → same, shifted down (this is exactly what #159-AC2 and #191-L2 proposed
///                          for CommentType.PriorityChange(4) / AssigneeChange(5), which sit in the
///                          MIDDLE of the enum with six live members after them)
///   * insert in-place   → same
///   * append at the end → SAFE (this is the only permitted change)
///
/// And none of that reproduces locally. Measured on the dev database: TaskComments.Type holds only
/// {0,1,2}; Ideas.Status holds only {0,1,3} (Promoted=2 has zero rows); TaskDependencies is EMPTY.
/// Every persisted enum's live data sits in the low ordinals, so a mid-enum deletion compiles,
/// passes the full suite, and only corrupts installs that used the richer values.
///
/// These tests are the thing that fails instead.
/// </summary>
public class PersistedEnumOrdinalTests
{
    // ── The locked on-disk contract ──────────────────────────────────────────────────────────
    // Each map is the set of ordinals ALREADY WRITTEN TO DATABASES IN THE WILD. They are frozen.
    // To add a value, append it to the enum with a number above the max here and add it here too.
    // Never edit an existing pair. If a test below fails, the fix is to revert the enum — not to
    // update the expectation.

    private static readonly Dictionary<string, int> CommentTypeLock = new()
    {
        ["Comment"] = 0,
        ["StatusChange"] = 1,
        ["Created"] = 2,
        ["FeatureChange"] = 3,
        ["PriorityChange"] = 4,
        ["AssigneeChange"] = 5,
        ["Note"] = 6,
        ["Decision"] = 7,
        ["Question"] = 8,
        ["Blocked"] = 9,
        ["Handoff"] = 10,
        ["Verification"] = 11,
    };

    private static readonly Dictionary<string, int> WorkStatusLock = new()
    {
        ["Backlog"] = 0, ["Todo"] = 1, ["InProgress"] = 2, ["Review"] = 3, ["Done"] = 4,
    };

    private static readonly Dictionary<string, int> TaskPriorityLock = new()
    {
        ["Low"] = 0, ["Medium"] = 1, ["High"] = 2, ["Critical"] = 3,
    };

    private static readonly Dictionary<string, int> IdeaStatusLock = new()
    {
        ["New"] = 0, ["Triaging"] = 1, ["Promoted"] = 2, ["Archived"] = 3, ["Rejected"] = 4,
    };

    private static readonly Dictionary<string, int> TaskDependencyTypeLock = new()
    {
        ["Blocks"] = 0, ["DependsOn"] = 1,
    };

    private static readonly Dictionary<string, int> MemoryTypeLock = new()
    {
        ["Episodic"] = 0, ["Semantic"] = 1, ["Procedural"] = 2, ["Decision"] = 3,
    };

    private static readonly Dictionary<string, int> MemoryScopeLock = new()
    {
        ["Project"] = 0, ["Feature"] = 1, ["Agent"] = 2, ["Session"] = 3,
    };

    private static readonly Dictionary<string, int> RequirementPriorityLock = new()
    {
        ["P0"] = 0, ["P1"] = 1, ["P2"] = 2,
    };

    private static readonly Dictionary<string, int> RequirementStatusLock = new()
    {
        ["Open"] = 0, ["InProgress"] = 1, ["Done"] = 2,
    };

    /// <summary>Every enum that reaches a database column, with its frozen ordinals.</summary>
    public static TheoryData<string, Type, Dictionary<string, int>> PersistedEnums() => new()
    {
        { nameof(CommentType), typeof(CommentType), CommentTypeLock },
        { nameof(WorkStatus), typeof(WorkStatus), WorkStatusLock },
        { nameof(TaskPriority), typeof(TaskPriority), TaskPriorityLock },
        { nameof(IdeaStatus), typeof(IdeaStatus), IdeaStatusLock },
        { nameof(TaskDependencyType), typeof(TaskDependencyType), TaskDependencyTypeLock },
        { nameof(MemoryType), typeof(MemoryType), MemoryTypeLock },
        { nameof(MemoryScope), typeof(MemoryScope), MemoryScopeLock },
        { nameof(RequirementPriority), typeof(RequirementPriority), RequirementPriorityLock },
        { nameof(RequirementStatus), typeof(RequirementStatus), RequirementStatusLock },
    };

    /// <summary>
    /// Catches a REMOVED member and a MOVED member — the two edits that silently re-type existing
    /// rows. This is the assertion that #159-AC2 would have tripped.
    /// </summary>
    [Theory]
    [MemberData(nameof(PersistedEnums))]
    public void LockedMembers_KeepTheirOrdinals(string enumName, Type enumType, Dictionary<string, int> locked)
    {
        foreach (var (member, expected) in locked)
        {
            Assert.True(
                Enum.IsDefined(enumType, member),
                $"{enumName}.{member} was REMOVED. It is persisted as the raw int {expected}, and every " +
                $"member after it shifts down — silently re-typing existing rows on every install that " +
                $"used them. Restore it. If it is genuinely dead, leave the member in place (or reserve " +
                $"the slot) rather than compacting the enum.");

            var actual = Convert.ToInt32(Enum.Parse(enumType, member));
            Assert.True(
                actual == expected,
                $"{enumName}.{member} MOVED from {expected} to {actual}. That number is the on-disk " +
                $"format: every row already stored as {expected} now reads back as a different member. " +
                $"Revert the enum — do not update this expectation.");
        }
    }

    /// <summary>
    /// Appending is the ONE safe change, and this proves a new member was actually appended rather
    /// than slipped into the middle. #247 (adding <c>CommentType.Reflection</c>) is safe only at
    /// ordinal 12 — nothing but this test enforces that.
    /// </summary>
    [Theory]
    [MemberData(nameof(PersistedEnums))]
    public void NewMembers_AreAppendedAboveTheLockedRange(string enumName, Type enumType, Dictionary<string, int> locked)
    {
        var highestLocked = locked.Values.Max();

        foreach (var member in Enum.GetNames(enumType))
        {
            if (locked.ContainsKey(member))
                continue;

            var value = Convert.ToInt32(Enum.Parse(enumType, member));
            Assert.True(
                value > highestLocked,
                $"{enumName}.{member} = {value} was inserted INSIDE the locked range (0..{highestLocked}). " +
                $"It collides with a member already written to disk. Give it {highestLocked + 1} or higher, " +
                $"and add it to the lock map in {nameof(PersistedEnumOrdinalTests)}.");
        }
    }

    /// <summary>
    /// Guards the guard. If someone adds an enum member without adding it here, the lock silently
    /// stops covering it — so require the lock map to be exhaustive, not merely consistent.
    /// </summary>
    [Theory]
    [MemberData(nameof(PersistedEnums))]
    public void LockMap_CoversEveryMember(string enumName, Type enumType, Dictionary<string, int> locked)
    {
        var uncovered = Enum.GetNames(enumType).Where(m => !locked.ContainsKey(m)).ToArray();

        Assert.True(
            uncovered.Length == 0,
            $"{enumName} has member(s) missing from the lock map: {string.Join(", ", uncovered)}. " +
            $"Every persisted member must be pinned here, or a later reorder of it goes unnoticed. " +
            $"Add them with their exact ordinals.");

        var duplicateOrdinals = locked
            .GroupBy(kv => kv.Value)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: [{string.Join(", ", g.Select(kv => kv.Key))}]")
            .ToArray();

        Assert.True(
            duplicateOrdinals.Length == 0,
            $"{enumName} lock map has duplicate ordinals: {string.Join("; ", duplicateOrdinals)}. " +
            $"C# allows two enum members to share an int value, so a collision slips past " +
            $"LockedMembers_KeepTheirOrdinals and NewMembers_AreAppendedAboveTheLockedRange. " +
            $"Each locked ordinal must be unique.");
    }
}
