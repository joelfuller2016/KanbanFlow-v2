using KanbanFlow.Models.Workflows;

namespace KanbanFlow.Components.Workflows;

public sealed class WorkflowSubmission(WorkflowCommandInput command)
{
    public WorkflowCommandInput Command { get; } = command;
    public bool Succeeded { get; set; }
    public int? Version { get; set; }
    public WorkflowContext? Context { get; set; }
}
