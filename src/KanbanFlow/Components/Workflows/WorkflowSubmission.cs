using KanbanFlow.Models.Workflows;

namespace KanbanFlow.Components.Workflows;

public sealed class WorkflowSubmission(WorkflowCommandInput command)
{
    public WorkflowCommandInput Command { get; } = command;
    public bool Succeeded { get; set; }
    public int? Version { get; set; }
    public WorkflowContext? Context { get; set; }
    // A later explicit retry can confirm the original submission after its handler returned.
    public Action<WorkflowSubmission>? OnConfirmed { get; init; }
}
