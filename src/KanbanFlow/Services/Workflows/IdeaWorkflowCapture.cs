using KanbanFlow.Data;
using KanbanFlow.Models;
using KanbanFlow.Models.Workflows;

namespace KanbanFlow.Services.Workflows;

/// <summary>Captures the request inside the existing idea transaction, before normalization loses its wording.</summary>
public static class IdeaWorkflowCapture
{
    public static WorkRequest Add(AppDbContext db, Idea idea, string original, string? actor, string source, bool exact = true)
    {
        var request = new WorkRequest
        {
            Idea = idea, IdeaId = idea.Id, ProjectId = idea.PromotedProjectId, Title = idea.Title,
            Archived = idea.Status is IdeaStatus.Archived or IdeaStatus.Rejected,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        request.Messages.Add(new RequestMessage
        {
            RequestId = request.Id, Kind = "original", Text = original,
            Author = string.IsNullOrWhiteSpace(actor) ? "System" : actor.Trim(),
            Source = exact ? source : "legacy-snapshot: exact original unavailable", CreatedAt = request.CreatedAt
        });
        db.WorkRequests.Add(request);
        return request;
    }

    public static void RecordSummaryEdit(WorkRequest request, string? actor, string field, string? value)
    {
        request.Version++;
        request.UpdatedAt = DateTime.UtcNow;
        request.Messages.Add(new RequestMessage
        {
            RequestId = request.Id, Kind = "clarification", Text = $"Idea {field} updated:\n\n{value ?? "(cleared)"}",
            Author = string.IsNullOrWhiteSpace(actor) ? "System" : actor.Trim(), Source = "idea-edit", CreatedAt = request.UpdatedAt
        });
    }
}
