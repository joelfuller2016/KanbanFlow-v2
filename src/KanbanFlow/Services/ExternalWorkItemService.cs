using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using KanbanFlow.Data;
using KanbanFlow.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace KanbanFlow.Services;

/// <summary>
/// SQLite-backed durable identity and create journal for external work items (#336).
/// Every check-then-write path begins an immediate SQLite transaction before reading, so two app
/// instances cannot both reserve or advance the same task/provider operation.
/// </summary>
public sealed partial class ExternalWorkItemService : IExternalWorkItemService
{
    public static readonly TimeSpan DefaultLeaseTtl = TimeSpan.FromMinutes(5);
    public const string LeaseSecondsConfigurationKey = "KANBANFLOW_EXTERNAL_WRITE_LEASE_SECONDS";

    private static readonly JsonSerializerOptions ActivityJsonOptions =
        new(JsonSerializerDefaults.Web);
    private static readonly Regex CallerPushMarker = new(
        @"<!--\s*kanbanflow-push\s*:",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly IDbContextFactory<AppDbContext> dbFactory;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan leaseTtl;

    public ExternalWorkItemService(IDbContextFactory<AppDbContext> dbFactory)
        : this(dbFactory, TimeProvider.System, DefaultLeaseTtl)
    {
    }

    /// <summary>
    /// Production DI constructor. The optional configuration value gives the destructive
    /// crash/restart harness a short lease without changing the five-minute production default.
    /// Environment variables flow through the standard ASP.NET configuration provider.
    /// </summary>
    public ExternalWorkItemService(
        IDbContextFactory<AppDbContext> dbFactory,
        IConfiguration configuration)
        : this(dbFactory, TimeProvider.System, LeaseTtlFromConfiguration(configuration))
    {
    }

    public ExternalWorkItemService(
        IDbContextFactory<AppDbContext> dbFactory,
        TimeProvider timeProvider,
        TimeSpan leaseTtl)
    {
        ArgumentNullException.ThrowIfNull(dbFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (leaseTtl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseTtl), "Lease TTL must be positive.");

        this.dbFactory = dbFactory;
        this.timeProvider = timeProvider;
        this.leaseTtl = leaseTtl;
    }

    public async Task<ExternalWriteLeaseResult> PrepareAsync(
        PrepareExternalWriteRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateProvider(request.Provider);
        if (request.TaskId <= 0)
            throw new ValidationException("TaskId must be positive.");

        var repositoryExternalId = Required(request.RepositoryExternalId, "RepositoryExternalId");
        var repositoryOwner = Required(request.RepositoryOwner, "RepositoryOwner");
        var repositoryName = Required(request.RepositoryName, "RepositoryName");
        var title = Required(request.Title, "Title");
        var milestoneTitle = Optional(request.MilestoneTitle);
        var label = Optional(request.Label);
        if (request.Body is null)
            throw new ValidationException("Body is required.");
        if (CallerPushMarker.IsMatch(request.Body))
            throw new ValidationException("Body must not contain a kanbanflow-push marker.");
        if (request.ExpectedTaskUpdatedAt == default)
            throw new ValidationException("ExpectedTaskUpdatedAt is required.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // Parameterless BeginTransactionAsync is BEGIN IMMEDIATE for Microsoft.Data.Sqlite. The
        // reservation is taken before all reads so concurrent prepares serialize deterministically.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var task = await db.Tasks
            .Include(t => t.Feature)
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == request.TaskId, ct)
            ?? throw new TaskNotFoundException(request.TaskId);
        var projectId = ProjectIdOf(task);

        if (task.UpdatedAt != request.ExpectedTaskUpdatedAt)
        {
            throw Conflict(
                "task-version-mismatch",
                $"Task {request.TaskId} changed after the outbound request was prepared.",
                request.TaskId,
                request.Provider);
        }

        var existingLink = await db.ExternalWorkItemLinks
            .AsNoTracking()
            .FirstOrDefaultAsync(
                l => l.TaskId == request.TaskId && l.Provider == request.Provider,
                ct);
        if (existingLink is not null)
        {
            var kind = existingLink.State == ExternalLinkState.Quarantined
                ? "quarantined-link-exists"
                : "external-link-exists";
            throw Conflict(
                kind,
                $"Task {request.TaskId} already has {existingLink.State.ToString().ToLowerInvariant()} " +
                $"{request.Provider} external-link state.",
                request.TaskId,
                request.Provider);
        }

        var existingOperation = await db.ExternalWriteOperations
            .AsNoTracking()
            .FirstOrDefaultAsync(
                o => o.TaskId == request.TaskId && o.Provider == request.Provider,
                ct);
        if (existingOperation is not null)
        {
            throw Conflict(
                "external-operation-exists",
                $"Task {request.TaskId} already has durable operation " +
                $"{existingOperation.OperationId:D} in state {existingOperation.State}.",
                request.TaskId,
                request.Provider,
                existingOperation);
        }

        var now = UtcNow();
        var operationId = Guid.NewGuid();
        var frozenBody = WithPushMarker(request.Body, operationId);
        var leaseToken = NewLeaseToken();
        var operation = new ExternalWriteOperation
        {
            OperationId = operationId,
            TaskId = request.TaskId,
            ProjectId = projectId,
            Provider = request.Provider,
            RepositoryExternalId = repositoryExternalId,
            RepositoryOwner = repositoryOwner,
            RepositoryName = repositoryName,
            Title = title,
            Body = frozenBody,
            MilestoneTitle = milestoneTitle,
            Label = label,
            ExpectedTaskUpdatedAt = task.UpdatedAt,
            RequestHash = RequestHash(
                request.TaskId,
                projectId,
                request.Provider,
                repositoryExternalId,
                repositoryOwner,
                repositoryName,
                title,
                frozenBody,
                milestoneTitle,
                label,
                task.UpdatedAt),
            State = ExternalWriteState.Prepared,
            LeaseToken = leaseToken,
            LeaseExpiresAt = now + leaseTtl,
            AttemptCount = 1,
            LastAttemptAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.ExternalWriteOperations.Add(operation);
        AddActivityEvent(
            db,
            operation.ProjectId,
            operation.TaskId,
            actor: "System",
            "ExternalWritePrepared",
            new
            {
                operationId,
                provider = operation.Provider.ToString(),
                operation.RepositoryExternalId,
                operation.RequestHash,
                operation.ExpectedTaskUpdatedAt,
                operation.State,
                operation.AttemptCount,
                operation.LeaseExpiresAt
            },
            $"External write operation {operationId:D} prepared for task {operation.TaskId}.",
            now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return new ExternalWriteLeaseResult(operation, leaseToken, SafeToCreate: true);
    }

    public async Task<ExternalWriteLeaseResult> ResumeAsync(
        Guid operationId,
        CancellationToken ct = default)
    {
        ValidateOperationId(operationId);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var operation = await db.ExternalWriteOperations
            .FirstOrDefaultAsync(o => o.OperationId == operationId, ct)
            ?? throw new ExternalWriteOperationNotFoundException(operationId);
        var now = UtcNow();

        if (operation.State == ExternalWriteState.Completed)
        {
            throw Conflict(
                "operation-completed",
                $"External write operation {operationId:D} is already completed.",
                operation.TaskId,
                operation.Provider,
                operation);
        }

        // Terminal pre-create failures may outlive a deliberately deleted task for audit. They
        // must never be revived into a creatable operation after that deletion: GitHub could be
        // mutated, but finalization could not satisfy the task foreign key. Check under the same
        // immediate transaction before rotating the lease or restoring Prepared.
        if (!await db.Tasks.AsNoTracking().AnyAsync(t => t.Id == operation.TaskId, ct))
            throw new TaskNotFoundException(operation.TaskId);

        if (HasFreshLease(operation, now))
        {
            throw Conflict(
                "operation-lease-active",
                $"External write operation {operationId:D} is leased until " +
                $"{operation.LeaseExpiresAt:O}.",
                operation.TaskId,
                operation.Provider,
                operation);
        }

        if (operation.LeaseToken is not null && operation.LeaseExpiresAt is null)
        {
            throw Conflict(
                "operation-lease-invalid",
                $"External write operation {operationId:D} has a token without an expiry.",
                operation.TaskId,
                operation.Provider,
                operation);
        }

        // This is the only retry transition that restores an ordinary initial create attempt. A
        // manual authorization deliberately remains visible in durable state until dispatch; if
        // this process crashes before dispatch, the next process must supply another fresh scan.
        // Dispatched and NeedsReview are never reset here: they must reconcile the marker first.
        var priorState = operation.State;
        if (operation.State == ExternalWriteState.DefinitiveFailure)
            operation.State = ExternalWriteState.Prepared;

        // Defensive compatibility for any row produced by an earlier implementation that erased
        // manual-retry lineage into Prepared while retaining dispatch history. Fail closed by
        // restoring review state instead of returning create permission.
        if (operation.State == ExternalWriteState.Prepared && operation.DispatchedAt is not null)
            operation.State = ExternalWriteState.NeedsReview;

        var leaseToken = NewLeaseToken();
        operation.LeaseToken = leaseToken;
        operation.LeaseExpiresAt = now + leaseTtl;
        operation.AttemptCount++;
        operation.LastAttemptAt = now;
        operation.UpdatedAt = now;

        AddActivityEvent(
            db,
            operation.ProjectId,
            operation.TaskId,
            actor: "System",
            "ExternalWriteResumed",
            new
            {
                operationId = operation.OperationId,
                priorState,
                operation.State,
                operation.AttemptCount,
                operation.LeaseExpiresAt,
                safeToCreate = operation.State is ExternalWriteState.Prepared or
                    ExternalWriteState.ManualRetryAuthorized
            },
            $"External write operation {operation.OperationId:D} resumed in state {operation.State}.",
            now);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return new ExternalWriteLeaseResult(
            operation,
            leaseToken,
            SafeToCreate: operation.State is ExternalWriteState.Prepared or
                ExternalWriteState.ManualRetryAuthorized);
    }

    public async Task<ExternalWriteOperation> MarkDispatchedAsync(
        Guid operationId,
        string leaseToken,
        ManualRetryDispatchEvidence? manualRetryEvidence = null,
        CancellationToken ct = default)
    {
        ValidateOperationId(operationId);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var operation = await FindOperationAsync(db, operationId, ct);
        var nowOffset = timeProvider.GetUtcNow();
        var now = nowOffset.UtcDateTime;
        ValidateLease(operation, leaseToken, now);
        if (operation.State == ExternalWriteState.Prepared && operation.DispatchedAt is not null)
        {
            throw Conflict(
                "prepared-operation-has-dispatch-history",
                $"Operation {operationId:D} is Prepared but retains prior dispatch history; it " +
                "must return to review instead of dispatching.",
                operation.TaskId,
                operation.Provider,
                operation);
        }

        if (operation.State == ExternalWriteState.ManualRetryAuthorized)
        {
            ValidateManualRetryDispatchEvidence(operation, manualRetryEvidence, nowOffset);
        }
        else if (operation.State == ExternalWriteState.Prepared)
        {
            if (manualRetryEvidence is not null)
                throw new ValidationException("Manual retry evidence is invalid for an initial dispatch.");
        }
        else
        {
            throw Conflict(
                "operation-not-prepared",
                $"Operation {operationId:D} is {operation.State}; only Prepared or an explicitly " +
                "authorized manual retry may dispatch.",
                operation.TaskId,
                operation.Provider,
                operation);
        }

        var priorState = operation.State;
        operation.State = ExternalWriteState.Dispatched;
        operation.DispatchedAt = now;
        operation.UpdatedAt = now;
        AddActivityEvent(
            db,
            operation.ProjectId,
            operation.TaskId,
            actor: "System",
            "ExternalWriteDispatched",
            new
            {
                operationId = operation.OperationId,
                priorState,
                operation.State,
                operation.AttemptCount,
                operation.DispatchedAt,
                manualRetryEvidenceVerified = priorState == ExternalWriteState.ManualRetryAuthorized,
                manualRetryConfirmationOperationId = manualRetryEvidence?.ConfirmationOperationId,
                manualRetryExpectedAttemptCount = manualRetryEvidence?.ExpectedAttemptCount,
                manualRetryScanCompletedAt = manualRetryEvidence?.ScanCompletedAt.ToUniversalTime(),
                manualRetryObservedMatchCount = manualRetryEvidence?.ObservedMatchCount,
                manualRetryObservedRawTargetOccurrenceCount =
                    manualRetryEvidence?.ObservedRawTargetOccurrenceCount
            },
            $"External write operation {operation.OperationId:D} marked dispatched.",
            now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return operation;
    }

    public async Task<ExternalWriteOperation> RecordFailureAsync(
        Guid operationId,
        string leaseToken,
        ExternalWriteFailureKind kind,
        string error,
        CancellationToken ct = default)
    {
        ValidateOperationId(operationId);
        if (!Enum.IsDefined(kind))
            throw new ValidationException($"Invalid external write failure kind '{kind}'.");
        var recordedError = Required(error, "Error", preserveWhitespace: true);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var operation = await FindOperationAsync(db, operationId, ct);
        var now = UtcNow();
        ValidateLease(operation, leaseToken, now);

        if (operation.State == ExternalWriteState.Completed)
        {
            throw Conflict(
                "operation-completed",
                $"Operation {operationId:D} is already completed.",
                operation.TaskId,
                operation.Provider,
                operation);
        }

        // Once dispatch is persisted, the provider may already have accepted the request.  No
        // caller-supplied classification may erase that fact: doing so would let a later resume
        // restore create permission and produce the duplicate this journal exists to prevent.
        // DefinitivePreCreate is therefore valid only for the initial Prepared state, before the
        // operation has any dispatch history. A manually authorized retry also becomes Prepared,
        // but its earlier ambiguous dispatch must remain impossible to erase.
        if (kind == ExternalWriteFailureKind.DefinitivePreCreate &&
            (operation.State != ExternalWriteState.Prepared || operation.DispatchedAt is not null))
        {
            throw Conflict(
                "definitive-failure-after-dispatch",
                $"Operation {operationId:D} is {operation.State} with durable dispatch history " +
                $"{(operation.DispatchedAt is null ? "absent" : "present")}; a definitively " +
                "pre-create failure can be recorded only before any dispatch.",
                operation.TaskId,
                operation.Provider,
                operation);
        }

        // The mirror of the guard twenty lines above, pointing the other way. That one refuses
        // to call a post-dispatch failure definitive; this one refuses to call a PRE-dispatch
        // failure ambiguous. Both rest on the same fact -- DispatchedAt is the only durable
        // evidence that the provider may have seen the request -- and a classification that
        // contradicts it is wrong in whichever direction it leans.
        //
        // Without this, an ordinary failed prepare (the frozen-body marker self-check at
        // portfolio-sync.mjs's create path, a lost API call, a caller that simply reports
        // uncertainty) produced a NeedsReview row with DispatchedAt == null: NeedsReview is not
        // resumable into create permission, and AuthorizeAmbiguousRetryAsync refuses a row that
        // was never dispatched, so no route could ever leave it. Classified truthfully as
        // DefinitivePreCreate, the same failure lands in DefinitiveFailure, which ResumeAsync
        // restores to Prepared. This is a classification guard, not a new state.
        var effectiveKind = kind == ExternalWriteFailureKind.AmbiguousOrIncomplete &&
            operation.State == ExternalWriteState.Prepared &&
            operation.DispatchedAt is null
            ? ExternalWriteFailureKind.DefinitivePreCreate
            : kind;

        operation.State = effectiveKind == ExternalWriteFailureKind.DefinitivePreCreate
            ? ExternalWriteState.DefinitiveFailure
            : ExternalWriteState.NeedsReview;
        operation.LastError = recordedError;
        operation.LeaseToken = null;
        operation.LeaseExpiresAt = null;
        operation.UpdatedAt = now;

        AddActivityEvent(
            db,
            operation.ProjectId,
            operation.TaskId,
            actor: "System",
            "ExternalWriteFailureRecorded",
            new
            {
                operationId = operation.OperationId,
                failureKind = effectiveKind.ToString(),
                // What the caller claimed, kept only when it differs from what was recorded, so
                // the audit trail shows the reclassification rather than silently overwriting it.
                reportedKind = effectiveKind == kind ? null : kind.ToString(),
                operation.State,
                operation.AttemptCount,
                hasError = true
            },
            $"External write operation {operation.OperationId:D} recorded {effectiveKind}.",
            now);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return operation;
    }

    public async Task<ExternalWriteOperation> AuthorizeAmbiguousRetryAsync(
        AuthorizeAmbiguousExternalWriteRetryRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateOperationId(request.OperationId);
        if (request.ConfirmationOperationId != request.OperationId)
            throw new ValidationException("ConfirmationOperationId must exactly repeat OperationId.");
        if (request.ExpectedAttemptCount <= 0)
            throw new ValidationException("ExpectedAttemptCount must be positive.");
        if (!request.ScanComplete)
            throw new ValidationException("An incomplete marker scan cannot authorize a retry.");
        if (request.ObservedMatchCount != 0)
            throw new ValidationException("Only a complete zero-match marker scan can authorize a retry.");
        if (request.ObservedRawTargetOccurrenceCount != 0)
        {
            throw new ValidationException(
                "Any raw target-marker occurrence blocks ambiguous retry authorization.");
        }
        if (request.ScanCompletedAt == default)
            throw new ValidationException("ScanCompletedAt is required.");

        var actor = Required(request.Actor, "Actor");
        if (actor.Length > 200)
            throw new ValidationException("Actor must be 200 characters or fewer.");
        var reason = Required(request.Reason, "Reason", preserveWhitespace: true);
        if (reason.Length > 1_000)
            throw new ValidationException("Reason must be 1000 characters or fewer.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var operation = await FindOperationAsync(db, request.OperationId, ct);
        var nowOffset = timeProvider.GetUtcNow();
        var now = nowOffset.UtcDateTime;
        var scanCompletedAt = request.ScanCompletedAt.ToUniversalTime();

        if (operation.AttemptCount != request.ExpectedAttemptCount)
        {
            throw Conflict(
                "operation-attempt-mismatch",
                $"Operation {operation.OperationId:D} is on attempt {operation.AttemptCount}, not " +
                $"expected attempt {request.ExpectedAttemptCount}.",
                operation.TaskId,
                operation.Provider,
                operation);
        }

        // An identical repeated confirmation converges without a second audit event. The durable
        // state itself is the authorization record; the original actor/reason remain in its audit.
        if (operation.State == ExternalWriteState.ManualRetryAuthorized)
        {
            await tx.CommitAsync(ct);
            return operation;
        }

        if (scanCompletedAt > nowOffset + TimeSpan.FromMinutes(1) ||
            scanCompletedAt < nowOffset - TimeSpan.FromMinutes(5))
        {
            throw new ValidationException(
                "ScanCompletedAt must describe a fresh scan completed within the last five minutes.");
        }
        if (scanCompletedAt.UtcDateTime < operation.UpdatedAt)
        {
            throw new ValidationException(
                "Retry authorization evidence must come from a scan completed after the " +
                "operation entered its current review state.");
        }

        // Force the ordinary recovery path to run first. It is the path that performs the initial
        // authoritative scan and records NeedsReview; an operator cannot skip straight from a
        // merely expired Dispatched lease to retry authorization.
        if (operation.State != ExternalWriteState.NeedsReview)
        {
            throw Conflict(
                "operation-not-awaiting-manual-retry",
                $"Operation {operation.OperationId:D} is {operation.State}; only NeedsReview may " +
                "receive an ambiguous retry authorization.",
                operation.TaskId,
                operation.Provider,
                operation);
        }

        // Manual authorization exists only for the uncertain post-dispatch boundary. If no
        // dispatch was ever persisted, either the request is still safely pre-create or the
        // caller violated the journal protocol; neither case may be converted into a policy
        // authorization that later restores create permission.
        if (operation.DispatchedAt is null)
        {
            throw Conflict(
                "operation-never-dispatched",
                $"Operation {operation.OperationId:D} has no durable dispatch record and cannot " +
                "receive an ambiguous retry authorization.",
                operation.TaskId,
                operation.Provider,
                operation);
        }

        if (operation.LeaseToken is not null && operation.LeaseExpiresAt is null)
        {
            throw Conflict(
                "operation-lease-invalid",
                $"Operation {operation.OperationId:D} has a lease token without an expiry.",
                operation.TaskId,
                operation.Provider,
                operation);
        }

        if (HasFreshLease(operation, now))
        {
            throw Conflict(
                "operation-lease-active",
                $"Operation {operation.OperationId:D} is still leased until " +
                $"{operation.LeaseExpiresAt:O}; stop the prior recovery process and wait for expiry.",
                operation.TaskId,
                operation.Provider,
                operation);
        }

        if (!await db.Tasks.AsNoTracking().AnyAsync(t => t.Id == operation.TaskId, ct))
            throw new TaskNotFoundException(operation.TaskId);

        var externalState = await db.ExternalWorkItemLinks
            .AsNoTracking()
            .FirstOrDefaultAsync(
                l => l.TaskId == operation.TaskId && l.Provider == operation.Provider,
                ct);
        if (externalState is not null)
        {
            throw Conflict(
                externalState.State == ExternalLinkState.Active
                    ? "external-link-exists"
                    : "quarantined-link-exists",
                $"Task {operation.TaskId} already has {externalState.State} " +
                $"{operation.Provider} external state.",
                operation.TaskId,
                operation.Provider,
                operation);
        }

        if (operation.ResultingExternalNodeId is not null ||
            operation.ResultingExternalNumber is not null ||
            operation.ResultingUrl is not null)
        {
            throw Conflict(
                "operation-result-already-recorded",
                $"Operation {operation.OperationId:D} already carries remote result metadata.",
                operation.TaskId,
                operation.Provider,
                operation);
        }

        var priorState = operation.State;
        operation.State = ExternalWriteState.ManualRetryAuthorized;
        operation.LeaseToken = null;
        operation.LeaseExpiresAt = null;
        operation.UpdatedAt = now;
        AddActivityEvent(
            db,
            operation.ProjectId,
            operation.TaskId,
            actor,
            "ExternalWriteManualRetryAuthorized",
            new
            {
                operationId = operation.OperationId,
                priorState,
                operation.State,
                operation.AttemptCount,
                scanComplete = true,
                observedMatchCount = 0,
                observedRawTargetOccurrenceCount = 0,
                scanCompletedAt,
                reason
            },
            $"Manual retry authorized for ambiguous external write operation " +
            $"{operation.OperationId:D}; a separate resume is required.",
            now);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return operation;
    }

    public async Task<ExternalWorkItemLink> FinalizeAsync(
        FinalizeExternalWriteRequest request,
        string? actor,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateOperationId(request.OperationId);
        var repositoryExternalId = Required(request.RepositoryExternalId, "RepositoryExternalId");
        var repositoryOwner = Required(request.RepositoryOwner, "RepositoryOwner");
        var repositoryName = Required(request.RepositoryName, "RepositoryName");
        var externalNodeId = Required(request.ExternalNodeId, "ExternalNodeId");
        ValidateExternalNumber(request.ExternalNumber);
        var url = ValidatedGitHubIssueUrl(
            request.Url,
            repositoryOwner,
            repositoryName,
            request.ExternalNumber);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var operation = await FindOperationAsync(db, request.OperationId, ct);

        // Owner/name may legitimately change during a repository rename, but the repository node
        // cannot. Binding finalization to that immutable identity prevents a direct API caller from
        // completing repo A's journal with an issue from repo B.
        if (!string.Equals(
                operation.RepositoryExternalId,
                repositoryExternalId,
                StringComparison.Ordinal))
        {
            throw Conflict(
                "operation-repository-mismatch",
                $"Operation {request.OperationId:D} targets repository node " +
                $"'{operation.RepositoryExternalId}', not '{repositoryExternalId}'.",
                operation.TaskId,
                operation.Provider,
                operation);
        }

        if (operation.State == ExternalWriteState.Completed)
        {
            var completedLink = await db.ExternalWorkItemLinks
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    l => l.TaskId == operation.TaskId && l.Provider == operation.Provider,
                    ct);
            if (completedLink is not null &&
                CompletedResultMatches(operation, completedLink, externalNodeId, request.ExternalNumber, url))
            {
                await tx.CommitAsync(ct);
                return completedLink;
            }

            throw Conflict(
                "finalize-result-mismatch",
                $"Operation {request.OperationId:D} already completed with a different result.",
                operation.TaskId,
                operation.Provider,
                operation);
        }

        var now = UtcNow();
        // Finalization is proof-driven, not lease-driven. Sync may observe the exact operation
        // marker during the crash window and must be able to link the originating task immediately,
        // even while the crashed creator's lease is still fresh. BEGIN IMMEDIATE serializes two
        // finalizers; identical results converge and a mismatch conflicts below.
        var resumedPriorDispatch = operation.State == ExternalWriteState.Prepared &&
            operation.DispatchedAt is not null && operation.AttemptCount > 1;
        if (operation.State is not ExternalWriteState.Dispatched and
            not ExternalWriteState.NeedsReview and
            not ExternalWriteState.ManualRetryAuthorized &&
            !resumedPriorDispatch)
        {
            throw Conflict(
                "operation-not-finalizable",
                $"Operation {request.OperationId:D} is {operation.State}; only Dispatched, " +
                "NeedsReview, ManualRetryAuthorized, or a Prepared retry of a prior dispatch " +
                "may finalize a verified remote result.",
                operation.TaskId,
                operation.Provider,
                operation);
        }

        var taskLink = await db.ExternalWorkItemLinks
            .FirstOrDefaultAsync(
                l => l.TaskId == operation.TaskId && l.Provider == operation.Provider,
                ct);
        if (taskLink is not null)
        {
            throw Conflict(
                taskLink.State == ExternalLinkState.Quarantined
                    ? "quarantined-link-exists"
                    : "external-link-exists",
                $"Task {operation.TaskId} already has {taskLink.State} {operation.Provider} state.",
                operation.TaskId,
                operation.Provider,
                operation);
        }

        await EnsureExternalIdentityAvailableAsync(
            db,
            operation.TaskId,
            operation.Provider,
            operation.RepositoryExternalId,
            externalNodeId,
            request.ExternalNumber,
            ct);

        var link = new ExternalWorkItemLink
        {
            TaskId = operation.TaskId,
            Provider = operation.Provider,
            State = ExternalLinkState.Active,
            RepositoryExternalId = operation.RepositoryExternalId,
            RepositoryOwner = repositoryOwner,
            RepositoryName = repositoryName,
            ExternalNodeId = externalNodeId,
            ExternalNumber = request.ExternalNumber,
            Url = url,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.ExternalWorkItemLinks.Add(link);

        operation.State = ExternalWriteState.Completed;
        operation.ResultingExternalNodeId = externalNodeId;
        operation.ResultingExternalNumber = request.ExternalNumber;
        operation.ResultingUrl = url;
        operation.CompletedAt = now;
        operation.LeaseToken = null;
        operation.LeaseExpiresAt = null;
        operation.LastError = null;
        operation.UpdatedAt = now;

        AddActivityEvent(
            db,
            operation.ProjectId,
            operation.TaskId,
            actor,
            "ExternalWorkItemLinked",
            new
            {
                operationId = operation.OperationId,
                provider = operation.Provider.ToString(),
                operation.RepositoryExternalId,
                repositoryOwner,
                repositoryName,
                externalNodeId,
                externalNumber = request.ExternalNumber,
                url
            },
            $"Task {operation.TaskId} linked to {operation.Provider} #{request.ExternalNumber}.",
            now);

        // The link, completed journal result, and exactly one audit row are one commit.
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return link;
    }

    public async Task<ExternalWorkItemLink> RegisterVerifiedLinkAsync(
        RegisterExternalWorkItemLinkRequest request,
        string? actor,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalized = NormalizeVerifiedLink(request);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var task = await db.Tasks
            .Include(t => t.Feature)
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == normalized.TaskId, ct)
            ?? throw new TaskNotFoundException(normalized.TaskId);

        var existing = await db.ExternalWorkItemLinks
            .FirstOrDefaultAsync(
                l => l.TaskId == normalized.TaskId && l.Provider == normalized.Provider,
                ct);
        if (existing is not null)
        {
            // Quarantine is a review state, not a dead end. An explicit verified-link PUT is the
            // supported human decision boundary: promote the same row atomically so the
            // (TaskId, Provider) constraint remains continuously true, while the remote identity
            // constraints arbitrate any competing claim.
            if (existing.State == ExternalLinkState.Quarantined)
            {
                await EnsureExternalIdentityAvailableAsync(
                    db,
                    normalized.TaskId,
                    normalized.Provider,
                    normalized.RepositoryExternalId,
                    normalized.ExternalNodeId,
                    normalized.ExternalNumber,
                    ct);

                var resolvedAt = UtcNow();
                var before = new
                {
                    existing.RepositoryExternalId,
                    existing.RepositoryOwner,
                    existing.RepositoryName,
                    candidateNumber = existing.ExternalNumber,
                    candidateUrl = existing.Url,
                    existing.QuarantineReason
                };
                existing.State = ExternalLinkState.Active;
                existing.RepositoryExternalId = normalized.RepositoryExternalId;
                existing.RepositoryOwner = normalized.RepositoryOwner;
                existing.RepositoryName = normalized.RepositoryName;
                existing.ExternalNodeId = normalized.ExternalNodeId;
                existing.ExternalNumber = normalized.ExternalNumber;
                existing.Url = normalized.Url;
                existing.QuarantineReason = null;
                existing.UpdatedAt = resolvedAt;
                AddActivityEvent(
                    db,
                    ProjectIdOf(task),
                    normalized.TaskId,
                    actor,
                    "ExternalLinkQuarantineResolved",
                    new
                    {
                        provider = normalized.Provider.ToString(),
                        before,
                        after = new
                        {
                            normalized.RepositoryExternalId,
                            normalized.RepositoryOwner,
                            normalized.RepositoryName,
                            normalized.ExternalNodeId,
                            normalized.ExternalNumber,
                            normalized.Url
                        }
                    },
                    $"Task {normalized.TaskId} quarantined {normalized.Provider} candidate " +
                    $"resolved to #{normalized.ExternalNumber}.",
                    resolvedAt);

                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return existing;
            }

            if (VerifiedLinkMatches(existing, normalized))
            {
                await tx.CommitAsync(ct);
                return existing;
            }

            if (SameExternalNodeIdentity(existing, normalized))
            {
                await EnsureExternalIdentityAvailableAsync(
                    db,
                    normalized.TaskId,
                    normalized.Provider,
                    normalized.RepositoryExternalId,
                    normalized.ExternalNodeId,
                    normalized.ExternalNumber,
                    ct);

                var refreshNow = UtcNow();
                var transferred = !string.Equals(
                    existing.RepositoryExternalId,
                    normalized.RepositoryExternalId,
                    StringComparison.Ordinal);
                var before = new
                {
                    existing.RepositoryExternalId,
                    existing.RepositoryOwner,
                    existing.RepositoryName,
                    existing.ExternalNumber,
                    existing.Url
                };
                existing.RepositoryExternalId = normalized.RepositoryExternalId;
                existing.RepositoryOwner = normalized.RepositoryOwner;
                existing.RepositoryName = normalized.RepositoryName;
                existing.ExternalNumber = normalized.ExternalNumber;
                existing.Url = normalized.Url;
                existing.UpdatedAt = refreshNow;
                AddActivityEvent(
                    db,
                    ProjectIdOf(task),
                    normalized.TaskId,
                    actor,
                    transferred ? "ExternalWorkItemTransferred" : "ExternalWorkItemLinkRefreshed",
                    new
                    {
                        provider = normalized.Provider.ToString(),
                        normalized.RepositoryExternalId,
                        normalized.ExternalNodeId,
                        before,
                        after = new
                        {
                            normalized.RepositoryOwner,
                            normalized.RepositoryName,
                            normalized.ExternalNumber,
                            normalized.Url
                        }
                    },
                    transferred
                        ? $"Task {normalized.TaskId} {normalized.Provider} issue transferred to " +
                          $"repository {normalized.RepositoryExternalId}."
                        : $"Task {normalized.TaskId} {normalized.Provider} link metadata refreshed.",
                    refreshNow);

                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return existing;
            }

            throw Conflict(
                existing.State == ExternalLinkState.Quarantined
                    ? "quarantined-link-exists"
                    : "external-link-mismatch",
                $"Task {normalized.TaskId} already has different {normalized.Provider} identity.",
                normalized.TaskId,
                normalized.Provider);
        }

        var operation = await db.ExternalWriteOperations
            .AsNoTracking()
            .FirstOrDefaultAsync(
                o => o.TaskId == normalized.TaskId && o.Provider == normalized.Provider,
                ct);
        if (operation is not null)
        {
            throw Conflict(
                "external-operation-exists",
                $"Task {normalized.TaskId} already has durable operation {operation.OperationId:D}.",
                normalized.TaskId,
                normalized.Provider,
                operation);
        }

        await EnsureExternalIdentityAvailableAsync(
            db,
            normalized.TaskId,
            normalized.Provider,
            normalized.RepositoryExternalId,
            normalized.ExternalNodeId,
            normalized.ExternalNumber,
            ct);

        var now = UtcNow();
        var link = new ExternalWorkItemLink
        {
            TaskId = normalized.TaskId,
            Provider = normalized.Provider,
            State = ExternalLinkState.Active,
            RepositoryExternalId = normalized.RepositoryExternalId,
            RepositoryOwner = normalized.RepositoryOwner,
            RepositoryName = normalized.RepositoryName,
            ExternalNodeId = normalized.ExternalNodeId,
            ExternalNumber = normalized.ExternalNumber,
            Url = normalized.Url,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.ExternalWorkItemLinks.Add(link);
        AddActivityEvent(
            db,
            ProjectIdOf(task),
            normalized.TaskId,
            actor,
            "ExternalWorkItemLinked",
            new
            {
                provider = normalized.Provider.ToString(),
                normalized.RepositoryExternalId,
                normalized.RepositoryOwner,
                normalized.RepositoryName,
                normalized.ExternalNodeId,
                normalized.ExternalNumber,
                normalized.Url,
                source = "verified-registration"
            },
            $"Task {normalized.TaskId} linked to {normalized.Provider} #{normalized.ExternalNumber}.",
            now);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return link;
    }

    public async Task<ExternalWorkItemLink> QuarantineAsync(
        QuarantineExternalWorkItemRequest request,
        string? actor,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateProvider(request.Provider);
        if (request.TaskId <= 0)
            throw new ValidationException("TaskId must be positive.");
        var repositoryExternalId = Required(request.RepositoryExternalId, "RepositoryExternalId");
        var repositoryOwner = Required(request.RepositoryOwner, "RepositoryOwner");
        var repositoryName = Required(request.RepositoryName, "RepositoryName");
        if (request.CandidateNumber is <= 0)
            throw new ValidationException("CandidateNumber must be positive when supplied.");
        var candidateUrl = request.CandidateUrl is null
            ? null
            : request.Provider == ExternalProvider.GitHub
                ? request.CandidateNumber is > 0
                    ? ValidatedGitHubIssueUrl(
                        request.CandidateUrl,
                        repositoryOwner,
                        repositoryName,
                        request.CandidateNumber.Value)
                    : throw new ValidationException(
                        "CandidateNumber is required when CandidateUrl is supplied.")
                : ValidatedUrl(request.CandidateUrl);
        var reason = Required(request.Reason, "Reason", preserveWhitespace: true);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var task = await db.Tasks
            .Include(t => t.Feature)
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == request.TaskId, ct)
            ?? throw new TaskNotFoundException(request.TaskId);

        var operation = await db.ExternalWriteOperations
            .AsNoTracking()
            .FirstOrDefaultAsync(
                o => o.TaskId == request.TaskId && o.Provider == request.Provider,
                ct);
        if (operation is not null)
        {
            throw Conflict(
                "external-operation-exists",
                $"Task {request.TaskId} already has durable operation {operation.OperationId:D}.",
                request.TaskId,
                request.Provider,
                operation);
        }

        var existing = await db.ExternalWorkItemLinks
            .FirstOrDefaultAsync(
                l => l.TaskId == request.TaskId && l.Provider == request.Provider,
                ct);
        if (existing is not null)
        {
            if (QuarantineMatches(
                    existing,
                    repositoryExternalId,
                    repositoryOwner,
                    repositoryName,
                    request.CandidateNumber,
                    candidateUrl,
                    reason))
            {
                await tx.CommitAsync(ct);
                return existing;
            }

            throw Conflict(
                existing.State == ExternalLinkState.Quarantined
                    ? "quarantine-mismatch"
                    : "external-link-exists",
                $"Task {request.TaskId} already has different {request.Provider} external state.",
                request.TaskId,
                request.Provider);
        }

        var now = UtcNow();
        var link = new ExternalWorkItemLink
        {
            TaskId = request.TaskId,
            Provider = request.Provider,
            State = ExternalLinkState.Quarantined,
            RepositoryExternalId = repositoryExternalId,
            RepositoryOwner = repositoryOwner,
            RepositoryName = repositoryName,
            ExternalNumber = request.CandidateNumber,
            Url = candidateUrl,
            QuarantineReason = reason,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.ExternalWorkItemLinks.Add(link);
        AddActivityEvent(
            db,
            ProjectIdOf(task),
            request.TaskId,
            actor,
            "ExternalLinkQuarantined",
            new
            {
                provider = request.Provider.ToString(),
                repositoryExternalId,
                repositoryOwner,
                repositoryName,
                candidateNumber = request.CandidateNumber,
                candidateUrl,
                reason
            },
            $"Task {request.TaskId} external-link candidate quarantined: {reason}",
            now);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return link;
    }

    public async Task<ExternalWriteOperation?> GetOperationAsync(
        Guid operationId,
        CancellationToken ct = default)
    {
        ValidateOperationId(operationId);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ExternalWriteOperations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.OperationId == operationId, ct);
    }

    public async Task<ExternalWorkItemLink?> GetActiveLinkAsync(
        int taskId,
        ExternalProvider provider,
        CancellationToken ct = default)
    {
        ValidateProvider(provider);
        if (taskId <= 0)
            throw new ValidationException("TaskId must be positive.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ExternalWorkItemLinks
            .AsNoTracking()
            .FirstOrDefaultAsync(
                l => l.TaskId == taskId && l.Provider == provider &&
                     l.State == ExternalLinkState.Active,
                ct);
    }

    public async Task<IReadOnlyList<ExternalWorkItemLink>> ListActiveLinksAsync(
        ExternalProvider provider,
        string repositoryExternalId,
        CancellationToken ct = default)
    {
        ValidateProvider(provider);
        var normalizedRepositoryId = Required(repositoryExternalId, "RepositoryExternalId");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ExternalWorkItemLinks
            .Where(l => l.Provider == provider &&
                        l.RepositoryExternalId == normalizedRepositoryId &&
                        l.State == ExternalLinkState.Active)
            .OrderBy(l => l.ExternalNumber)
            .ThenBy(l => l.Id)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    private static async Task<ExternalWriteOperation> FindOperationAsync(
        AppDbContext db,
        Guid operationId,
        CancellationToken ct) =>
        await db.ExternalWriteOperations
            .FirstOrDefaultAsync(o => o.OperationId == operationId, ct)
        ?? throw new ExternalWriteOperationNotFoundException(operationId);

    private static async Task EnsureExternalIdentityAvailableAsync(
        AppDbContext db,
        int taskId,
        ExternalProvider provider,
        string repositoryExternalId,
        string externalNodeId,
        int externalNumber,
        CancellationToken ct)
    {
        var nodeOwner = await db.ExternalWorkItemLinks
            .AsNoTracking()
            .FirstOrDefaultAsync(
                l => l.Provider == provider && l.ExternalNodeId == externalNodeId,
                ct);
        if (nodeOwner is not null && nodeOwner.TaskId != taskId)
        {
            throw Conflict(
                "external-node-already-linked",
                $"{provider} node '{externalNodeId}' is already linked to task {nodeOwner.TaskId}.",
                taskId > 0 ? taskId : nodeOwner.TaskId,
                provider);
        }

        var numberOwner = await db.ExternalWorkItemLinks
            .AsNoTracking()
            .FirstOrDefaultAsync(
                l => l.Provider == provider &&
                     l.State == ExternalLinkState.Active &&
                     l.RepositoryExternalId == repositoryExternalId &&
                     l.ExternalNumber == externalNumber,
                ct);
        if (numberOwner is not null && numberOwner.TaskId != taskId)
        {
            throw Conflict(
                "external-number-already-linked",
                $"{provider} repository item #{externalNumber} is already linked to task " +
                $"{numberOwner.TaskId}.",
                taskId > 0 ? taskId : numberOwner.TaskId,
                provider);
        }
    }

    private static bool CompletedResultMatches(
        ExternalWriteOperation operation,
        ExternalWorkItemLink link,
        string externalNodeId,
        int externalNumber,
        string url) =>
        // Idempotency is anchored to the immutable completed journal result. The active link's
        // owner/name/number/url may subsequently refresh after a repository rename or issue
        // transfer; that metadata drift must not turn an identical finalize retry into a conflict.
        operation.ResultingExternalNodeId == externalNodeId &&
        operation.ResultingExternalNumber == externalNumber &&
        operation.ResultingUrl == url &&
        link.State == ExternalLinkState.Active &&
        link.ExternalNodeId == externalNodeId;

    private static bool VerifiedLinkMatches(
        ExternalWorkItemLink existing,
        RegisterExternalWorkItemLinkRequest request) =>
        existing.State == ExternalLinkState.Active &&
        existing.RepositoryExternalId == request.RepositoryExternalId &&
        existing.RepositoryOwner == request.RepositoryOwner &&
        existing.RepositoryName == request.RepositoryName &&
        existing.ExternalNodeId == request.ExternalNodeId &&
        existing.ExternalNumber == request.ExternalNumber &&
        existing.Url == request.Url;

    private static bool SameExternalNodeIdentity(
        ExternalWorkItemLink existing,
        RegisterExternalWorkItemLinkRequest request) =>
        existing.State == ExternalLinkState.Active &&
        existing.ExternalNodeId == request.ExternalNodeId;

    private static bool QuarantineMatches(
        ExternalWorkItemLink existing,
        string repositoryExternalId,
        string repositoryOwner,
        string repositoryName,
        int? candidateNumber,
        string? candidateUrl,
        string reason) =>
        existing.State == ExternalLinkState.Quarantined &&
        existing.RepositoryExternalId == repositoryExternalId &&
        existing.RepositoryOwner == repositoryOwner &&
        existing.RepositoryName == repositoryName &&
        existing.ExternalNodeId is null &&
        existing.ExternalNumber == candidateNumber &&
        existing.Url == candidateUrl &&
        existing.QuarantineReason == reason;

    private static RegisterExternalWorkItemLinkRequest NormalizeVerifiedLink(
        RegisterExternalWorkItemLinkRequest request,
        bool requireTaskId = true)
    {
        ValidateProvider(request.Provider);
        if (requireTaskId && request.TaskId <= 0)
            throw new ValidationException("TaskId must be positive.");
        ValidateExternalNumber(request.ExternalNumber);
        var repositoryOwner = Required(request.RepositoryOwner, "RepositoryOwner");
        var repositoryName = Required(request.RepositoryName, "RepositoryName");
        return request with
        {
            RepositoryExternalId = Required(request.RepositoryExternalId, "RepositoryExternalId"),
            RepositoryOwner = repositoryOwner,
            RepositoryName = repositoryName,
            ExternalNodeId = Required(request.ExternalNodeId, "ExternalNodeId"),
            Url = request.Provider == ExternalProvider.GitHub
                ? ValidatedGitHubIssueUrl(
                    request.Url,
                    repositoryOwner,
                    repositoryName,
                    request.ExternalNumber)
                : ValidatedUrl(request.Url)
        };
    }

    private static void ValidateManualRetryDispatchEvidence(
        ExternalWriteOperation operation,
        ManualRetryDispatchEvidence? evidence,
        DateTimeOffset now)
    {
        if (evidence is null)
        {
            throw new ValidationException(
                "A manually authorized retry requires fresh marker-scan evidence at dispatch.");
        }
        if (evidence.ConfirmationOperationId != operation.OperationId)
            throw new ValidationException("ConfirmationOperationId must exactly repeat OperationId.");
        if (evidence.ExpectedAttemptCount != operation.AttemptCount)
        {
            throw Conflict(
                "operation-attempt-mismatch",
                $"Operation {operation.OperationId:D} is on attempt {operation.AttemptCount}, not " +
                $"expected attempt {evidence.ExpectedAttemptCount}.",
                operation.TaskId,
                operation.Provider,
                operation);
        }
        if (!evidence.ScanComplete)
            throw new ValidationException("An incomplete marker scan cannot dispatch a manual retry.");
        if (evidence.ObservedMatchCount != 0)
            throw new ValidationException("A clean marker match must finalize instead of dispatching.");
        if (evidence.ObservedRawTargetOccurrenceCount != 0)
        {
            throw new ValidationException(
                "Any raw target-marker occurrence blocks a manual retry dispatch.");
        }
        if (evidence.ScanCompletedAt == default)
            throw new ValidationException("ScanCompletedAt is required.");

        var completedAt = evidence.ScanCompletedAt.ToUniversalTime();
        if (completedAt > now + TimeSpan.FromMinutes(1) ||
            completedAt < now - TimeSpan.FromMinutes(5))
        {
            throw new ValidationException(
                "ScanCompletedAt must describe a fresh scan completed within the last five minutes.");
        }

        if (operation.LastAttemptAt is null || completedAt.UtcDateTime < operation.LastAttemptAt.Value)
        {
            throw new ValidationException(
                "Manual retry dispatch evidence must come from a scan completed after this " +
                "attempt acquired its resume lease.");
        }
    }

    private void ValidateLease(
        ExternalWriteOperation operation,
        string suppliedToken,
        DateTime now)
    {
        if (string.IsNullOrWhiteSpace(suppliedToken) ||
            string.IsNullOrWhiteSpace(operation.LeaseToken) ||
            !FixedTimeEquals(operation.LeaseToken, suppliedToken.Trim()))
        {
            throw Conflict(
                "stale-lease-token",
                $"Lease token for operation {operation.OperationId:D} is stale or invalid.",
                operation.TaskId,
                operation.Provider,
                operation);
        }

        if (operation.LeaseExpiresAt is null || operation.LeaseExpiresAt <= now)
        {
            throw Conflict(
                "operation-lease-expired",
                $"Lease for operation {operation.OperationId:D} expired at " +
                $"{operation.LeaseExpiresAt:O}.",
                operation.TaskId,
                operation.Provider,
                operation);
        }
    }

    private static bool HasFreshLease(ExternalWriteOperation operation, DateTime now) =>
        !string.IsNullOrWhiteSpace(operation.LeaseToken) &&
        operation.LeaseExpiresAt is not null &&
        operation.LeaseExpiresAt > now;

    private static bool FixedTimeEquals(string expected, string supplied)
    {
        var expectedBytes = System.Text.Encoding.UTF8.GetBytes(expected);
        var suppliedBytes = System.Text.Encoding.UTF8.GetBytes(supplied);
        return expectedBytes.Length == suppliedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }

    private static string RequestHash(
        int taskId,
        int projectId,
        ExternalProvider provider,
        string repositoryExternalId,
        string repositoryOwner,
        string repositoryName,
        string title,
        string body,
        string? milestoneTitle,
        string? label,
        DateTime expectedTaskUpdatedAt)
    {
        // An ordered JSON array distinguishes null, empty, and field boundaries without relying on
        // culture-sensitive delimiters. The resulting lower-case hex is stable across processes.
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new object?[]
        {
            taskId,
            projectId,
            (int)provider,
            repositoryExternalId,
            repositoryOwner,
            repositoryName,
            title,
            body,
            milestoneTitle,
            label,
            expectedTaskUpdatedAt.Ticks
        });
        return Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
    }

    private static string NewLeaseToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private static TimeSpan LeaseTtlFromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var raw = configuration[LeaseSecondsConfigurationKey];
        if (string.IsNullOrWhiteSpace(raw))
            return DefaultLeaseTtl;

        if (!int.TryParse(
                raw.Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var seconds) ||
            seconds <= 0)
        {
            throw new InvalidOperationException(
                $"{LeaseSecondsConfigurationKey} must be a positive whole number of seconds.");
        }

        return TimeSpan.FromSeconds(seconds);
    }

    private static string WithPushMarker(string body, Guid operationId)
    {
        var content = body.TrimEnd();
        var marker = $"<!-- kanbanflow-push:{operationId:D} -->";
        return content.Length == 0 ? marker : $"{content}\n\n{marker}";
    }

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;

    private static void AddActivityEvent(
        AppDbContext db,
        int projectId,
        int taskId,
        string? actor,
        string eventType,
        object after,
        string message,
        DateTime occurredAt)
    {
        db.ActivityEvents.Add(new ActivityEvent
        {
            ProjectId = projectId,
            EntityType = "Task",
            EntityId = taskId,
            Actor = Optional(actor),
            EventType = eventType,
            AfterJson = JsonSerializer.Serialize(after, ActivityJsonOptions),
            Message = message,
            CreatedAt = occurredAt
        });
    }

    private static ExternalWorkItemConflictException Conflict(
        string code,
        string message,
        int taskId,
        ExternalProvider provider,
        ExternalWriteOperation? operation = null) =>
        new(
            code,
            message,
            taskId,
            provider,
            operation?.OperationId,
            operation?.State,
            operation?.LeaseExpiresAt);

    private static string Required(string? value, string field, bool preserveWhitespace = false)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ValidationException($"{field} is required.");
        return preserveWhitespace ? value : value.Trim();
    }

    private static string? Optional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int ProjectIdOf(TaskItem task) =>
        task.Feature?.ProjectId
        ?? throw new InvalidOperationException(
            $"Task {task.Id} has no feature; cannot resolve project for audit.");

    private static string ValidatedUrl(string? value)
    {
        var normalized = Required(value, "Url");
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ValidationException("Url must be an absolute HTTPS URL.");
        }
        return uri.AbsoluteUri;
    }

    private static string ValidatedGitHubIssueUrl(
        string? value,
        string repositoryOwner,
        string repositoryName,
        int externalNumber)
    {
        var normalized = Required(value, "Url");
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ValidationException("Url must be an absolute https://github.com issue URL.");
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();
        if (segments.Length != 4
            || !string.Equals(segments[0], repositoryOwner, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(segments[1], repositoryName, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(segments[2], "issues", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(segments[3], NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            || number != externalNumber)
        {
            throw new ValidationException(
                "Url must match the supplied GitHub owner, repository, and issue number.");
        }
        return uri.AbsoluteUri;
    }

    private static void ValidateProvider(ExternalProvider provider)
    {
        if (!Enum.IsDefined(provider))
            throw new ValidationException($"Invalid external provider '{provider}'.");
    }

    private static void ValidateOperationId(Guid operationId)
    {
        if (operationId == Guid.Empty)
            throw new ValidationException("OperationId is required.");
    }

    private static void ValidateExternalNumber(int externalNumber)
    {
        if (externalNumber <= 0)
            throw new ValidationException("ExternalNumber must be positive.");
    }
}
