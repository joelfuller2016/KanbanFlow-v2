namespace KanbanFlow.Models;

public class TaskComment
{
    public int Id { get; set; }
    public int TaskItemId { get; set; }
    public required string Text { get; set; }
    public string? Author { get; set; }
    public CommentType Type { get; set; } = CommentType.Comment;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public TaskItem TaskItem { get; set; } = null!;
}

// ── ON-DISK CONTRACT — read before you touch this ────────────────────────────────────────
// TaskComments.Type is persisted as a RAW ORDINAL int. There is no HasConversion anywhere in
// the model, so THESE NUMBERS ARE THE DATABASE FORMAT, not an implementation detail.
//
// You MAY append a new member with a value greater than every value below.
// You MAY NOT change, reorder, or REMOVE any member below. Ever.
//
// Removing PriorityChange (4) and AssigneeChange (5) — as #159-AC2 and #191-L2 both proposed —
// shifts every later member down by two: a row stored as 6 (Note) reads back as Question, and
// 10 (Handoff) / 11 (Verification) become undefined. Silent, total mis-typing of user comments.
//
// And it TESTS CLEAN on a dev box: this machine's DB holds only ordinals {0,1,2}. A full green
// suite proves nothing. PersistedEnumOrdinalTests is what actually catches it — the ordinals are
// asserted there explicitly, so a reorder/removal fails the build instead of corrupting installs.
public enum CommentType
{
    Comment = 0,
    StatusChange = 1,
    Created = 2,
    FeatureChange = 3,
    PriorityChange = 4,
    AssigneeChange = 5,
    Note = 6,
    Decision = 7,
    Question = 8,
    Blocked = 9,
    Handoff = 10,
    Verification = 11
}
