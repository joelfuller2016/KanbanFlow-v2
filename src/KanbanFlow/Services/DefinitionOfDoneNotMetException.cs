namespace KanbanFlow.Services;

/// <summary>
/// Thrown when a task is moved to <see cref="Models.WorkStatus.Done"/> while one or more of its
/// acceptance criteria are still unchecked, and no override reason was supplied.
/// </summary>
/// <remarks>
/// <para>
/// The gate lives in <see cref="TaskService"/> rather than at the REST edge deliberately: every
/// surface (Blazor UI, REST, native MCP) funnels through <see cref="ITaskService"/>, so one guard
/// binds all three. Putting it at the edge would re-declare service policy — the divergence #302
/// exists to stop.
/// </para>
/// <para>
/// A task with <em>zero</em> acceptance criteria is never gated; the check only fires when the
/// task has criteria and at least one is incomplete. Rows already in <c>Done</c> are never
/// retroactively invalidated — the gate applies to the transition, not to stored state.
/// </para>
/// <para>
/// The API layer maps this to HTTP 422 with a
/// <c>{ message, taskId, unmetCount, unmet }</c> body.
/// </para>
/// </remarks>
public sealed class DefinitionOfDoneNotMetException : Exception
{
    public int TaskId { get; }

    /// <summary>The texts of the acceptance criteria that are still incomplete, in board order.</summary>
    public IReadOnlyList<string> Unmet { get; }

    public int UnmetCount => Unmet.Count;

    public DefinitionOfDoneNotMetException(int taskId, IReadOnlyList<string> unmet)
        : base($"Task {taskId} cannot be marked Done: {unmet.Count} acceptance criterion/criteria are not met. "
               + "Complete them, or supply an override reason to record why the task is done anyway.")
    {
        TaskId = taskId;
        Unmet = unmet;
    }
}
