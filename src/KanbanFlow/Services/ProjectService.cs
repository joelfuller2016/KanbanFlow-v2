using System.Text.Json;
using KanbanFlow.Data;
using KanbanFlow.Models;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Services;

/// <summary>
/// Default <see cref="IProjectService"/>. Uses <see cref="IDbContextFactory{AppDbContext}"/> so each
/// call gets its own short-lived context (safe for Blazor circuits + Minimal API requests alike).
/// </summary>
public sealed class ProjectService(IDbContextFactory<AppDbContext> dbFactory) : IProjectService
{
    private static readonly JsonSerializerOptions ActivityJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<bool> DeleteAsync(int projectId, string? actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null)
            return false;

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Audit BEFORE removal. ActivityEvent has no FK to Project (it's an immutable trail), so the
        // ProjectDeleted row survives the delete (#149/#85).
        db.ActivityEvents.Add(new ActivityEvent
        {
            ProjectId = projectId,
            EntityType = "Project",
            EntityId = projectId,
            Actor = string.IsNullOrWhiteSpace(actor) ? null : actor.Trim(),
            EventType = "ProjectDeleted",
            BeforeJson = JsonSerializer.Serialize(new { project.Id, project.Name, project.Description }, ActivityJsonOptions),
            AfterJson = null,
            Message = $"Project '{project.Name}' deleted"
        });

        // Leaf-first child cleanup: Feature->ChildFeatures and Subtask->ChildSubtasks are RESTRICT,
        // so a plain DB cascade aborts when a parent still has children. Reuse the proven REST
        // deletion so REST + UI share one path (#155). All removes are staged, then persisted with
        // the project removal in a single SaveChanges inside this transaction.
        await ApiEndpoints.DeleteProjectChildrenAsync(db, projectId);
        db.Projects.Remove(project);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }
}
