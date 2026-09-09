using KanbanFlow.Models;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Services;

public sealed partial class ExternalWorkItemService
{
    public async Task<ExternalWorkItemProjectionResult> ProjectInboundGitHubIssueAsync(
        ProjectInboundGitHubIssueRequest request,
        string? actor,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Task);
        TaskService.ValidateCreate(request.Task);
        if (request.Task.FeatureId <= 0)
            throw new ValidationException("FeatureId must be positive.");
        // TaskId is deliberately absent until task construction; every identity rule is shared
        // with verified registration. This boundary never performs a GitHub write or transfer.
        var identity = NormalizeVerifiedLink(new RegisterExternalWorkItemLinkRequest(
            0, ExternalProvider.GitHub, request.RepositoryExternalId, request.RepositoryOwner,
            request.RepositoryName, request.ExternalNodeId, request.ExternalNumber, request.Url),
            requireTaskId: false);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // BEGIN IMMEDIATE precedes all ownership reads, including the global immutable-node
        // recheck. Distinct app/sync processes serialize here, not in a process-local lock.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var projectId = await db.Features.Where(f => f.Id == request.Task.FeatureId)
            .Select(f => (int?)f.ProjectId).SingleOrDefaultAsync(ct)
            ?? throw new FeatureNotFoundException(request.Task.FeatureId);
        var existing = await db.ExternalWorkItemLinks
            .Include(l => l.Task).ThenInclude(t => t.Feature)
            .FirstOrDefaultAsync(l => l.Provider == ExternalProvider.GitHub &&
                l.ExternalNodeId == identity.ExternalNodeId, ct);
        if (existing is not null)
        {
            if (existing.Task.Feature.ProjectId != projectId)
                throw Conflict("external-node-cross-project",
                    $"GitHub node '{identity.ExternalNodeId}' belongs to task {existing.TaskId} in another project.",
                    existing.TaskId, ExternalProvider.GitHub);
            if (existing.State != ExternalLinkState.Active)
                throw Conflict("quarantined-link-exists",
                    $"Task {existing.TaskId} has quarantined GitHub identity; resolve it before projection.",
                    existing.TaskId, ExternalProvider.GitHub);
            if (existing.RepositoryExternalId != identity.RepositoryExternalId)
                throw new ValidationException("RepositoryExternalId does not match the existing immutable issue identity. Reconcile the verified link before projecting.");
            if (existing.ExternalNumber != identity.ExternalNumber)
                throw Conflict("external-node-number-mismatch",
                    $"GitHub node '{identity.ExternalNodeId}' has another issue number; reconcile its verified link first.",
                    existing.TaskId, ExternalProvider.GitHub);
            if (!VerifiedLinkMatches(existing, identity))
                throw new ValidationException("Repository name or canonical URL does not match the existing issue identity. Reconcile the verified link before projecting.");

            // A retry cannot overwrite a human edit, move the card, or emit another create audit.
            await tx.CommitAsync(ct);
            return new(existing.Task, existing, Created: false);
        }

        await EnsureExternalIdentityAvailableAsync(db, 0, identity.Provider,
            identity.RepositoryExternalId, identity.ExternalNodeId, identity.ExternalNumber, ct);
        var quarantined = await db.ExternalWorkItemLinks.AsNoTracking().FirstOrDefaultAsync(
            l => l.Provider == identity.Provider && l.State == ExternalLinkState.Quarantined &&
                l.RepositoryExternalId == identity.RepositoryExternalId &&
                l.ExternalNumber == identity.ExternalNumber, ct);
        if (quarantined is not null)
            throw Conflict("quarantined-link-exists",
                $"Task {quarantined.TaskId} has an unresolved quarantine for this repository issue number.",
                quarantined.TaskId, identity.Provider);
        var task = await TaskService.CreateInContextAsync(db, request.Task, actor, ct);
        var now = UtcNow();
        var link = new ExternalWorkItemLink
        {
            TaskId = task.Id,
            Provider = identity.Provider,
            State = ExternalLinkState.Active,
            RepositoryExternalId = identity.RepositoryExternalId,
            RepositoryOwner = identity.RepositoryOwner,
            RepositoryName = identity.RepositoryName,
            ExternalNodeId = identity.ExternalNodeId,
            ExternalNumber = identity.ExternalNumber,
            Url = identity.Url,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.ExternalWorkItemLinks.Add(link);
        AddActivityEvent(db, projectId, task.Id, actor, "ExternalWorkItemLinked",
            new
            {
                provider = identity.Provider.ToString(), identity.RepositoryExternalId,
                identity.RepositoryOwner, identity.RepositoryName, identity.ExternalNodeId,
                identity.ExternalNumber, identity.Url, source = "inbound-projection"
            }, $"Task {task.Id} linked to GitHub #{identity.ExternalNumber}.", now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(task, link, Created: true);
    }
}
