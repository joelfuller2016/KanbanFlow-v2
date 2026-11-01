using System.Text.Json;
using System.Text.RegularExpressions;
using KanbanFlow.Models;
using KanbanFlow.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;

namespace KanbanFlow.Tests;

public sealed class ExternalWorkItemServiceTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 8, 30, 20, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan LeaseTtl = TimeSpan.FromMinutes(2);

    [Theory]
    [InlineData("Markdown hard break  ")]
    [InlineData("    code block\r\n\t")]
    [InlineData("Unicode 漢字🙂\n\n")]
    public async Task Prepare_PreservesCompleteAuthoredBodyBeforeOperationMarker(string body)
    {
        using var factory = new SqliteTestFactory();
        var seeded = await SeedTaskAsync(factory);
        var lease = await Service(factory).PrepareAsync(Request(seeded, body));

        Assert.Equal($"{body}\n\n<!-- kanbanflow-push:{lease.Operation.OperationId:D} -->", lease.Operation.Body);
        await using var db = await factory.CreateDbContextAsync();
        Assert.Equal(lease.Operation.Body, (await db.ExternalWriteOperations.SingleAsync()).Body);
    }

    [Fact]
    public async Task Prepare_FreezesPayload_AddsOneMarker_AndAuditsWithoutBody()
    {
        using var factory = new SqliteTestFactory();
        var seeded = await SeedTaskAsync(factory, "Original task");
        var clock = new FakeTimeProvider(Start);
        var service = new ExternalWorkItemService(factory, clock, LeaseTtl);
        const string sensitiveBody = "Exact outbound body; do not copy this into activity.";

        var lease = await service.PrepareAsync(Request(seeded, sensitiveBody));

        Assert.True(lease.SafeToCreate);
        Assert.Equal(ExternalWriteState.Prepared, lease.Operation.State);
        Assert.Equal(1, lease.Operation.AttemptCount);
        Assert.Equal(Start.UtcDateTime, lease.Operation.CreatedAt);
        Assert.Equal(seeded.UpdatedAt, lease.Operation.ExpectedTaskUpdatedAt);
        Assert.Matches("^[0-9a-f]{64}$", lease.Operation.RequestHash);
        var expectedMarker = $"<!-- kanbanflow-push:{lease.Operation.OperationId:D} -->";
        Assert.Equal($"{sensitiveBody}\n\n{expectedMarker}", lease.Operation.Body);
        Assert.Single(Regex.Matches(lease.Operation.Body, "kanbanflow-push:").Cast<Match>());

        await using var db = await factory.CreateDbContextAsync();
        var audit = Assert.Single(await db.ActivityEvents
            .Where(e => e.EventType == "ExternalWritePrepared")
            .ToListAsync());
        Assert.Equal(Start.UtcDateTime, audit.CreatedAt);
        Assert.Equal("System", audit.Actor);
        Assert.DoesNotContain(sensitiveBody, audit.AfterJson ?? string.Empty);
        Assert.DoesNotContain(sensitiveBody, audit.Message ?? string.Empty);
        Assert.Contains(lease.Operation.RequestHash, audit.AfterJson);
    }

    [Fact]
    public async Task Prepare_RejectsCallerMarker_WithoutPersistingOperationOrAudit()
    {
        using var factory = new SqliteTestFactory();
        var seeded = await SeedTaskAsync(factory);
        var service = Service(factory);
        var request = Request(seeded, "hello\n<!-- kanbanflow-push:caller-value -->");

        var error = await Assert.ThrowsAsync<ValidationException>(() => service.PrepareAsync(request));

        Assert.Equal("Body must not contain a kanbanflow-push marker.", error.Message);
        await using var db = await factory.CreateDbContextAsync();
        Assert.Empty(await db.ExternalWriteOperations.ToListAsync());
        Assert.Empty(await db.ActivityEvents.Where(e => e.EventType == "ExternalWritePrepared").ToListAsync());
    }

    [Fact]
    public async Task Configuration_CanShortenLeaseForCrashHarness()
    {
        using var factory = new SqliteTestFactory();
        var seeded = await SeedTaskAsync(factory);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ExternalWorkItemService.LeaseSecondsConfigurationKey] = "2"
            })
            .Build();
        var service = new ExternalWorkItemService(factory, configuration);

        var prepared = await service.PrepareAsync(Request(seeded));

        Assert.NotNull(prepared.Operation.LeaseExpiresAt);
        Assert.NotNull(prepared.Operation.LastAttemptAt);
        Assert.Equal(
            TimeSpan.FromSeconds(2),
            prepared.Operation.LeaseExpiresAt!.Value - prepared.Operation.LastAttemptAt!.Value);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("not-a-number")]
    public void Configuration_RejectsInvalidLeaseSeconds(string value)
    {
        using var factory = new SqliteTestFactory();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ExternalWorkItemService.LeaseSecondsConfigurationKey] = value
            })
            .Build();

        var error = Assert.Throws<InvalidOperationException>(
            () => new ExternalWorkItemService(factory, configuration));

        Assert.Contains(ExternalWorkItemService.LeaseSecondsConfigurationKey, error.Message);
    }

    [Fact]
    public async Task Resume_RequiresExpiredLease_RotatesToken_AndRejectsStaleToken()
    {
        using var factory = new SqliteTestFactory();
        var seeded = await SeedTaskAsync(factory);
        var clock = new FakeTimeProvider(Start);
        var service = new ExternalWorkItemService(factory, clock, LeaseTtl);
        var first = await service.PrepareAsync(Request(seeded));

        var fresh = await Assert.ThrowsAsync<ExternalWorkItemConflictException>(
            () => service.ResumeAsync(first.Operation.OperationId));
        Assert.Equal("operation-lease-active", fresh.ConflictCode);

        clock.Advance(LeaseTtl + TimeSpan.FromSeconds(1));
        var resumed = await service.ResumeAsync(first.Operation.OperationId);

        Assert.True(resumed.SafeToCreate);
        Assert.Equal(ExternalWriteState.Prepared, resumed.Operation.State);
        Assert.Equal(2, resumed.Operation.AttemptCount);
        Assert.NotEqual(first.LeaseToken, resumed.LeaseToken);
        var stale = await Assert.ThrowsAsync<ExternalWorkItemConflictException>(
            () => service.MarkDispatchedAsync(first.Operation.OperationId, first.LeaseToken));
        Assert.Equal("stale-lease-token", stale.ConflictCode);

        var dispatched = await service.MarkDispatchedAsync(
            first.Operation.OperationId,
            resumed.LeaseToken);
        Assert.Equal(ExternalWriteState.Dispatched, dispatched.State);
    }

    [Fact]
    public async Task Resume_NeverTurnsDispatchedOrNeedsReviewIntoCreatePermission()
    {
        using var factory = new SqliteTestFactory();
        var seeded = await SeedTaskAsync(factory);
        var clock = new FakeTimeProvider(Start);
        var service = new ExternalWorkItemService(factory, clock, LeaseTtl);
        const string sensitiveBody = "Lifecycle body must stay out of the audit journal.";
        const string ambiguousError = "repository enumeration was incomplete";
        const string definitiveError = "complete reconciliation proved no request landed";
        var prepared = await service.PrepareAsync(Request(seeded, sensitiveBody));
        await service.MarkDispatchedAsync(prepared.Operation.OperationId, prepared.LeaseToken);

        clock.Advance(LeaseTtl + TimeSpan.FromSeconds(1));
        var dispatchedResume = await service.ResumeAsync(prepared.Operation.OperationId);
        Assert.False(dispatchedResume.SafeToCreate);
        Assert.Equal(ExternalWriteState.Dispatched, dispatchedResume.Operation.State);

        await service.RecordFailureAsync(
            prepared.Operation.OperationId,
            dispatchedResume.LeaseToken,
            ExternalWriteFailureKind.AmbiguousOrIncomplete,
            ambiguousError);
        var reviewResume = await service.ResumeAsync(prepared.Operation.OperationId);
        Assert.False(reviewResume.SafeToCreate);
        Assert.Equal(ExternalWriteState.NeedsReview, reviewResume.Operation.State);

        // A caller cannot relabel an operation that may already have reached GitHub as a safe
        // pre-create failure. That would turn the next resume into a duplicate-create permit.
        var unsafeDowngrade = await Assert.ThrowsAsync<ExternalWorkItemConflictException>(() =>
            service.RecordFailureAsync(
                prepared.Operation.OperationId,
                reviewResume.LeaseToken,
                ExternalWriteFailureKind.DefinitivePreCreate,
                definitiveError));
        Assert.Equal("definitive-failure-after-dispatch", unsafeDowngrade.ConflictCode);

        await using var db = await factory.CreateDbContextAsync();
        Assert.Equal(2, await db.ActivityEvents.CountAsync(e => e.EventType == "ExternalWriteResumed"));
        Assert.Single(await db.ActivityEvents.Where(e => e.EventType == "ExternalWriteFailureRecorded").ToListAsync());
        Assert.Single(await db.ActivityEvents.Where(e => e.EventType == "ExternalWriteDispatched").ToListAsync());
        var lifecycleAudits = await db.ActivityEvents
            .Where(e => e.EventType.StartsWith("ExternalWrite"))
            .ToListAsync();
        Assert.All(lifecycleAudits, audit =>
        {
            Assert.Equal("System", audit.Actor);
            Assert.DoesNotContain(sensitiveBody, audit.AfterJson ?? string.Empty);
            Assert.DoesNotContain(sensitiveBody, audit.Message ?? string.Empty);
            Assert.DoesNotContain(ambiguousError, audit.AfterJson ?? string.Empty);
            Assert.DoesNotContain(ambiguousError, audit.Message ?? string.Empty);
            Assert.DoesNotContain(definitiveError, audit.AfterJson ?? string.Empty);
            Assert.DoesNotContain(definitiveError, audit.Message ?? string.Empty);
        });
        var persisted = await db.ExternalWriteOperations.SingleAsync();
        Assert.Equal(ExternalWriteState.NeedsReview, persisted.State);
        Assert.Equal(ambiguousError, persisted.LastError);
        Assert.Equal(reviewResume.LeaseToken, persisted.LeaseToken);
    }

    [Fact]
    public async Task RecordFailure_PreservesCompleteEvidenceAndWhitespace()
    {
        using var factory = new SqliteTestFactory();
        var seeded = await SeedTaskAsync(factory);
        var service = Service(factory);
        var prepared = await service.PrepareAsync(Request(seeded));
        await service.MarkDispatchedAsync(prepared.Operation.OperationId, prepared.LeaseToken);
        var evidence = "\r\n    Trace begins 漢字 🧪\r\n" + new string('x', 4_001) + "\r\n    Final cause must survive  \r\n\t";

        var result = await service.RecordFailureAsync(
            prepared.Operation.OperationId,
            prepared.LeaseToken,
            ExternalWriteFailureKind.AmbiguousOrIncomplete,
            evidence);

        Assert.Equal(evidence, result.LastError);
        var reread = await service.GetOperationAsync(prepared.Operation.OperationId);
        Assert.NotNull(reread);
        Assert.Equal(evidence, reread.LastError);
        Assert.Equal(ExternalWriteState.NeedsReview, reread.State);
        await using var db = await factory.CreateDbContextAsync();
        Assert.Single(await db.ActivityEvents.Where(e => e.EventType == "ExternalWriteFailureRecorded").ToListAsync());
    }

    [Fact]
    public async Task DefinitiveFailure_BeforeDispatch_CanBeExplicitlyResumedForCreate()
    {
        using var factory = new SqliteTestFactory();
        var seeded = await SeedTaskAsync(factory);
        var clock = new FakeTimeProvider(Start);
        var service = new ExternalWorkItemService(factory, clock, LeaseTtl);
        var prepared = await service.PrepareAsync(Request(seeded));

        var failed = await service.RecordFailureAsync(
            prepared.Operation.OperationId,
            prepared.LeaseToken,
            ExternalWriteFailureKind.DefinitivePreCreate,
            "local payload validation failed before dispatch");
        Assert.Equal(ExternalWriteState.DefinitiveFailure, failed.State);
        Assert.Null(failed.LeaseToken);

        var resumed = await service.ResumeAsync(prepared.Operation.OperationId);
        Assert.True(resumed.SafeToCreate);
        Assert.Equal(ExternalWriteState.Prepared, resumed.Operation.State);
        Assert.NotEqual(prepared.LeaseToken, resumed.LeaseToken);

        await using var db = await factory.CreateDbContextAsync();
        Assert.Single(await db.ActivityEvents.Where(e => e.EventType == "ExternalWriteFailureRecorded").ToListAsync());
        Assert.Single(await db.ActivityEvents.Where(e => e.EventType == "ExternalWriteResumed").ToListAsync());
    }

    [Fact]
    public async Task DefinitiveFailure_CannotResumeAfterItsTaskWasDeleted()
    {
        using var factory = new SqliteTestFactory();
        var seeded = await SeedTaskAsync(factory);
        var clock = new FakeTimeProvider(Start);
        var service = new ExternalWorkItemService(factory, clock, LeaseTtl);
        var prepared = await service.PrepareAsync(Request(seeded));
        await service.RecordFailureAsync(
            prepared.Operation.OperationId,
            prepared.LeaseToken,
            ExternalWriteFailureKind.DefinitivePreCreate,
            "local validation failed before dispatch");

        await using (var deleteDb = await factory.CreateDbContextAsync())
        {
            deleteDb.Tasks.Remove(await deleteDb.Tasks.SingleAsync(t => t.Id == seeded.TaskId));
            await deleteDb.SaveChangesAsync();
        }

        var error = await Assert.ThrowsAsync<TaskNotFoundException>(
            () => service.ResumeAsync(prepared.Operation.OperationId));
        Assert.Equal(seeded.TaskId, error.TaskId);

        await using var verifyDb = await factory.CreateDbContextAsync();
        var persisted = await verifyDb.ExternalWriteOperations.SingleAsync();
        Assert.Equal(ExternalWriteState.DefinitiveFailure, persisted.State);
        Assert.Null(persisted.LeaseToken);
        Assert.DoesNotContain(
            await verifyDb.ActivityEvents.ToListAsync(),
            e => e.EventType == "ExternalWriteResumed");
    }

    [Fact]
    public async Task ManualRetryAuthorization_IsAudited_Idempotent_AndNeedsSeparateResume()
    {
        using var factory = new SqliteTestFactory();
        var seeded = await SeedTaskAsync(factory);
        var clock = new FakeTimeProvider(Start);
        var service = new ExternalWorkItemService(factory, clock, LeaseTtl);
        var prepared = await service.PrepareAsync(Request(seeded, "frozen payload"));
        await service.MarkDispatchedAsync(prepared.Operation.OperationId, prepared.LeaseToken);
        clock.Advance(LeaseTtl + TimeSpan.FromSeconds(1));
        var recoveryLease = await service.ResumeAsync(prepared.Operation.OperationId);
        await service.RecordFailureAsync(
            prepared.Operation.OperationId,
            recoveryLease.LeaseToken,
            ExternalWriteFailureKind.AmbiguousOrIncomplete,
            "complete scan observed no exact marker");
        var request = new AuthorizeAmbiguousExternalWriteRetryRequest(
            prepared.Operation.OperationId,
            prepared.Operation.OperationId,
            recoveryLease.Operation.AttemptCount,
            ScanComplete: true,
            ObservedMatchCount: 0,
            ObservedRawTargetOccurrenceCount: 0,
            ScanCompletedAt: clock.GetUtcNow(),
            Actor: "operator@example",
            Reason: "\r\n    prior process is stopped; accept residual provider visibility risk  \r\n\t");

        var authorized = await service.AuthorizeAmbiguousRetryAsync(request);
        var duplicate = await service.AuthorizeAmbiguousRetryAsync(request);

        Assert.Equal(ExternalWriteState.ManualRetryAuthorized, authorized.State);
        Assert.Equal(authorized.OperationId, duplicate.OperationId);
        Assert.Null(authorized.LeaseToken);
        Assert.False(authorized.State == ExternalWriteState.Prepared);
        Assert.Equal(prepared.Operation.Body, authorized.Body);
        Assert.Equal(prepared.Operation.RequestHash, authorized.RequestHash);

        await using (var beforeResume = await factory.CreateDbContextAsync())
        {
            var audit = Assert.Single(await beforeResume.ActivityEvents
                .Where(e => e.EventType == "ExternalWriteManualRetryAuthorized")
                .ToListAsync());
            Assert.Equal("operator@example", audit.Actor);
            using var after = JsonDocument.Parse(audit.AfterJson!);
            Assert.Equal(request.Reason, after.RootElement.GetProperty("reason").GetString());
            Assert.DoesNotContain("frozen payload", audit.AfterJson);
            Assert.DoesNotContain(prepared.LeaseToken, audit.AfterJson);
        }

        var resumed = await service.ResumeAsync(prepared.Operation.OperationId);
        Assert.True(resumed.SafeToCreate);
        Assert.Equal(ExternalWriteState.ManualRetryAuthorized, resumed.Operation.State);
        Assert.Equal(recoveryLease.Operation.AttemptCount + 1, resumed.Operation.AttemptCount);

        // If the old request becomes visible after the second scan/resume but before a new
        // dispatch, proof still wins: finalize the late result instead of creating again.
        var lateLink = await service.FinalizeAsync(
            new FinalizeExternalWriteRequest(
                prepared.Operation.OperationId,
                null,
                "R_repo",
                "acme",
                "widgets",
                "I_visible_after_resume",
                92,
                "https://github.com/acme/widgets/issues/92"),
            actor: "sync");
        Assert.Equal("I_visible_after_resume", lateLink.ExternalNodeId);
        Assert.Equal(
            ExternalWriteState.Completed,
            (await service.GetOperationAsync(prepared.Operation.OperationId))!.State);
    }

    [Fact]
    public async Task ManualRetryDispatch_RequiresFreshEvidence_AndSurvivesPreDispatchCrash()
    {
        using var factory = new SqliteTestFactory();
        var seeded = await SeedTaskAsync(factory);
        var clock = new FakeTimeProvider(Start);
        var service = new ExternalWorkItemService(factory, clock, LeaseTtl);
        var prepared = await service.PrepareAsync(Request(seeded));
        await service.MarkDispatchedAsync(prepared.Operation.OperationId, prepared.LeaseToken);
        clock.Advance(LeaseTtl + TimeSpan.FromSeconds(1));
        var recovery = await service.ResumeAsync(prepared.Operation.OperationId);
        await service.RecordFailureAsync(
            prepared.Operation.OperationId,
            recovery.LeaseToken,
            ExternalWriteFailureKind.AmbiguousOrIncomplete,
            "first exhaustive scan observed no target marker");
        await service.AuthorizeAmbiguousRetryAsync(new(
            prepared.Operation.OperationId,
            prepared.Operation.OperationId,
            recovery.Operation.AttemptCount,
            true,
            0,
            0,
            clock.GetUtcNow(),
            "operator",
            "accept delayed provider visibility risk"));

        var firstManualLease = await service.ResumeAsync(prepared.Operation.OperationId);
        Assert.True(firstManualLease.SafeToCreate);
        Assert.Equal(ExternalWriteState.ManualRetryAuthorized, firstManualLease.Operation.State);
        await Assert.ThrowsAsync<ValidationException>(() => service.MarkDispatchedAsync(
            prepared.Operation.OperationId,
            firstManualLease.LeaseToken));
        await Assert.ThrowsAsync<ExternalWorkItemConflictException>(() => service.RecordFailureAsync(
            prepared.Operation.OperationId,
            firstManualLease.LeaseToken,
            ExternalWriteFailureKind.DefinitivePreCreate,
            "local failure must not erase prior ambiguity"));

        // Simulate a crash after resume but before dispatch. The expired retry lease must retain
        // ManualRetryAuthorized, so another fresh scan is required at the next dispatch boundary.
        clock.Advance(LeaseTtl + TimeSpan.FromSeconds(1));
        var preResumeScanCompletedAt = clock.GetUtcNow();
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var secondManualLease = await service.ResumeAsync(prepared.Operation.OperationId);
        Assert.Equal(ExternalWriteState.ManualRetryAuthorized, secondManualLease.Operation.State);
        Assert.Equal(firstManualLease.Operation.AttemptCount + 1, secondManualLease.Operation.AttemptCount);

        var rawOccurrence = new ManualRetryDispatchEvidence(
            prepared.Operation.OperationId,
            secondManualLease.Operation.AttemptCount,
            true,
            0,
            1,
            clock.GetUtcNow());
        await Assert.ThrowsAsync<ValidationException>(() => service.MarkDispatchedAsync(
            prepared.Operation.OperationId,
            secondManualLease.LeaseToken,
            rawOccurrence with
            {
                ObservedRawTargetOccurrenceCount = 0,
                ScanCompletedAt = preResumeScanCompletedAt
            }));
        Assert.Equal(
            ExternalWriteState.ManualRetryAuthorized,
            (await service.GetOperationAsync(prepared.Operation.OperationId))!.State);
        await using (var beforeValidDispatch = await factory.CreateDbContextAsync())
        {
            Assert.Single(await beforeValidDispatch.ActivityEvents
                .Where(e => e.EventType == "ExternalWriteDispatched")
                .ToListAsync());
        }
        await Assert.ThrowsAsync<ValidationException>(() => service.MarkDispatchedAsync(
            prepared.Operation.OperationId,
            secondManualLease.LeaseToken,
            rawOccurrence));

        var dispatched = await service.MarkDispatchedAsync(
            prepared.Operation.OperationId,
            secondManualLease.LeaseToken,
            rawOccurrence with { ObservedRawTargetOccurrenceCount = 0 });
        Assert.Equal(ExternalWriteState.Dispatched, dispatched.State);

        await using var db = await factory.CreateDbContextAsync();
        var dispatches = await db.ActivityEvents
            .Where(e => e.EventType == "ExternalWriteDispatched")
            .OrderBy(e => e.Id)
            .ToListAsync();
        Assert.Equal(2, dispatches.Count);
        Assert.Contains("\"manualRetryEvidenceVerified\":true", dispatches[^1].AfterJson);
        Assert.Contains("\"manualRetryEvidenceVerified\":false", dispatches[0].AfterJson);
        Assert.Contains("manualRetryScanCompletedAt", dispatches[^1].AfterJson);
        Assert.Contains("\"manualRetryObservedMatchCount\":0", dispatches[^1].AfterJson);
        Assert.Contains(
            "\"manualRetryObservedRawTargetOccurrenceCount\":0",
            dispatches[^1].AfterJson);
    }

    [Fact]
    public async Task ManualRetryAuthorization_RejectsWrongStateFreshLeaseOrInvalidEvidence()
    {
        using var factory = new SqliteTestFactory();
        var seeded = await SeedTaskAsync(factory);
        var clock = new FakeTimeProvider(Start);
        var service = new ExternalWorkItemService(factory, clock, LeaseTtl);
        var prepared = await service.PrepareAsync(Request(seeded));
        var valid = new AuthorizeAmbiguousExternalWriteRetryRequest(
            prepared.Operation.OperationId,
            prepared.Operation.OperationId,
            prepared.Operation.AttemptCount,
            true,
            0,
            0,
            clock.GetUtcNow(),
            "operator",
            "explicit risk acceptance");

        var wrongState = await Assert.ThrowsAsync<ExternalWorkItemConflictException>(
            () => service.AuthorizeAmbiguousRetryAsync(valid));
        Assert.Equal("operation-not-awaiting-manual-retry", wrongState.ConflictCode);
        await Assert.ThrowsAsync<ValidationException>(() => service.AuthorizeAmbiguousRetryAsync(
            valid with { ConfirmationOperationId = Guid.NewGuid() }));
        await Assert.ThrowsAsync<ValidationException>(() => service.AuthorizeAmbiguousRetryAsync(
            valid with { ScanComplete = false }));
        await Assert.ThrowsAsync<ValidationException>(() => service.AuthorizeAmbiguousRetryAsync(
            valid with { ObservedMatchCount = 1 }));
        await Assert.ThrowsAsync<ValidationException>(() => service.AuthorizeAmbiguousRetryAsync(
            valid with { ObservedRawTargetOccurrenceCount = 1 }));

        await service.MarkDispatchedAsync(prepared.Operation.OperationId, prepared.LeaseToken);
        clock.Advance(LeaseTtl + TimeSpan.FromSeconds(1));
        var recovery = await service.ResumeAsync(prepared.Operation.OperationId);
        await service.RecordFailureAsync(
            prepared.Operation.OperationId,
            recovery.LeaseToken,
            ExternalWriteFailureKind.AmbiguousOrIncomplete,
            "zero matches");
        var competingRecovery = await service.ResumeAsync(prepared.Operation.OperationId);
        var activeLease = await Assert.ThrowsAsync<ExternalWorkItemConflictException>(() =>
            service.AuthorizeAmbiguousRetryAsync(valid with
            {
                ExpectedAttemptCount = competingRecovery.Operation.AttemptCount,
                ScanCompletedAt = clock.GetUtcNow()
            }));
        Assert.Equal("operation-lease-active", activeLease.ConflictCode);

        await using var db = await factory.CreateDbContextAsync();
        Assert.Equal(ExternalWriteState.NeedsReview, (await db.ExternalWriteOperations.SingleAsync()).State);
        Assert.Empty(await db.ActivityEvents
            .Where(e => e.EventType == "ExternalWriteManualRetryAuthorized")
            .ToListAsync());
    }

    [Fact]
    public async Task RecordFailure_AmbiguousBeforeDispatch_IsRecordedAsDefinitive()
    {
        using var factory = new SqliteTestFactory();
        var seeded = await SeedTaskAsync(factory);
        var clock = new FakeTimeProvider(Start);
        var service = new ExternalWorkItemService(factory, clock, LeaseTtl);
        var prepared = await service.PrepareAsync(Request(seeded));
        Assert.Null(prepared.Operation.DispatchedAt);

        var recorded = await service.RecordFailureAsync(
            prepared.Operation.OperationId,
            prepared.LeaseToken,
            ExternalWriteFailureKind.AmbiguousOrIncomplete,
            "frozen body did not contain exactly one exact push marker");

        // Nothing was ever dispatched, so "the provider may have seen this" is not a possible
        // reading of the facts. NeedsReview here is a lie that no route can undo.
        Assert.Equal(ExternalWriteState.DefinitiveFailure, recorded.State);

        await using var db = await factory.CreateDbContextAsync();
        var audit = Assert.Single(await db.ActivityEvents
            .Where(e => e.EventType == "ExternalWriteFailureRecorded")
            .ToListAsync());
        Assert.Contains("\"failureKind\":\"DefinitivePreCreate\"", audit.AfterJson);
        // The caller's own claim survives beside the correction rather than being overwritten.
        Assert.Contains("\"reportedKind\":\"AmbiguousOrIncomplete\"", audit.AfterJson);
        Assert.Contains("DefinitivePreCreate", audit.Message);
    }

    [Fact]
    public async Task RecordFailure_AmbiguousAfterDispatch_StillNeedsReview()
    {
        using var factory = new SqliteTestFactory();
        var seeded = await SeedTaskAsync(factory);
        var clock = new FakeTimeProvider(Start);
        var service = new ExternalWorkItemService(factory, clock, LeaseTtl);
        var prepared = await service.PrepareAsync(Request(seeded));
        await service.MarkDispatchedAsync(prepared.Operation.OperationId, prepared.LeaseToken);
        clock.Advance(LeaseTtl + TimeSpan.FromSeconds(1));
        var recovery = await service.ResumeAsync(prepared.Operation.OperationId);

        var recorded = await service.RecordFailureAsync(
            prepared.Operation.OperationId,
            recovery.LeaseToken,
            ExternalWriteFailureKind.AmbiguousOrIncomplete,
            "exhaustive scan was incomplete");

        // The discriminator. A guard that reclassified every ambiguous failure would erase the
        // one fact that stops a resume from minting a duplicate issue.
        Assert.Equal(ExternalWriteState.NeedsReview, recorded.State);
        Assert.NotNull(recorded.DispatchedAt);

        await using var db = await factory.CreateDbContextAsync();
        var audit = Assert.Single(await db.ActivityEvents
            .Where(e => e.EventType == "ExternalWriteFailureRecorded")
            .ToListAsync());
        Assert.Contains("\"failureKind\":\"AmbiguousOrIncomplete\"", audit.AfterJson);
        Assert.Contains("\"reportedKind\":null", audit.AfterJson);
    }

    [Fact]
    public async Task PreparedNeverDispatched_DoesNotBecomeUnreachable()
    {
        using var factory = new SqliteTestFactory();
        var seeded = await SeedTaskAsync(factory);
        var clock = new FakeTimeProvider(Start);
        var service = new ExternalWorkItemService(factory, clock, LeaseTtl);

        // The exact ordinary path: portfolio-sync's pre-dispatch self-check fails and the script
        // reports AmbiguousOrIncomplete because every failure it can see MIGHT be post-dispatch.
        var prepared = await service.PrepareAsync(Request(seeded));
        await service.RecordFailureAsync(
            prepared.Operation.OperationId,
            prepared.LeaseToken,
            ExternalWriteFailureKind.AmbiguousOrIncomplete,
            "pre-dispatch self-check failed");

        clock.Advance(LeaseTtl + TimeSpan.FromSeconds(1));
        var resumed = await service.ResumeAsync(prepared.Operation.OperationId);

        // The whole point: a routine failed prepare is recoverable. Before the guard this row was
        // NeedsReview with DispatchedAt == null -- Resume refuses it create permission and
        // AuthorizeAmbiguousRetryAsync refuses it for never having dispatched, so the task was
        // wedged for good with no board-side exit.
        Assert.True(resumed.SafeToCreate);
        Assert.Equal(ExternalWriteState.Prepared, resumed.Operation.State);
        Assert.Null(resumed.Operation.DispatchedAt);

        // And it really can go on to dispatch and finalize, not merely report a hopeful state.
        var dispatched = await service.MarkDispatchedAsync(
            prepared.Operation.OperationId,
            resumed.LeaseToken);
        Assert.Equal(ExternalWriteState.Dispatched, dispatched.State);
    }

    [Fact]
    public async Task ManualRetryAuthorization_RejectsNeedsReviewWithoutDurableDispatch()
    {
        using var factory = new SqliteTestFactory();
        var seeded = await SeedTaskAsync(factory);
        var clock = new FakeTimeProvider(Start);
        var service = new ExternalWorkItemService(factory, clock, LeaseTtl);
        var prepared = await service.PrepareAsync(Request(seeded));

        // RecordFailureAsync now reclassifies an ambiguous failure on a never-dispatched
        // Prepared operation as DefinitivePreCreate, so the service API can no longer MAKE a
        // NeedsReview row with DispatchedAt == null (see
        // RecordFailure_AmbiguousBeforeDispatch_IsRecordedAsDefinitive). The guard below is
        // still load-bearing for such a row from any other source -- an older build, a manual
        // repair, a future caller -- so the row is forged directly here rather than dropping
        // the coverage. Every assertion in this test is unchanged.
        await using (var seedDb = await factory.CreateDbContextAsync())
        {
            var forged = await seedDb.ExternalWriteOperations
                .SingleAsync(o => o.OperationId == prepared.Operation.OperationId);
            forged.State = ExternalWriteState.NeedsReview;
            forged.LeaseToken = null;
            forged.LeaseExpiresAt = null;
            Assert.Null(forged.DispatchedAt);
            await seedDb.SaveChangesAsync();
        }

        var error = await Assert.ThrowsAsync<ExternalWorkItemConflictException>(() =>
            service.AuthorizeAmbiguousRetryAsync(new(
                prepared.Operation.OperationId,
                prepared.Operation.OperationId,
                prepared.Operation.AttemptCount,
                true,
                0,
                0,
                clock.GetUtcNow(),
                "operator",
                "risk acceptance must not repair a broken dispatch protocol")));

        Assert.Equal("operation-never-dispatched", error.ConflictCode);
        Assert.Equal(
            ExternalWriteState.NeedsReview,
            (await service.GetOperationAsync(prepared.Operation.OperationId))!.State);
    }

    [Fact]
    public async Task ManualRetryAuthorized_CanFinalizeLateExactResultWithoutCreateResume()
    {
        using var factory = new SqliteTestFactory();
        var seeded = await SeedTaskAsync(factory);
        var clock = new FakeTimeProvider(Start);
        var service = new ExternalWorkItemService(factory, clock, LeaseTtl);
        var prepared = await service.PrepareAsync(Request(seeded));
        await service.MarkDispatchedAsync(prepared.Operation.OperationId, prepared.LeaseToken);
        clock.Advance(LeaseTtl + TimeSpan.FromSeconds(1));
        var recovery = await service.ResumeAsync(prepared.Operation.OperationId);
        await service.RecordFailureAsync(
            prepared.Operation.OperationId,
            recovery.LeaseToken,
            ExternalWriteFailureKind.AmbiguousOrIncomplete,
            "zero matches");
        await service.AuthorizeAmbiguousRetryAsync(new(
            prepared.Operation.OperationId,
            prepared.Operation.OperationId,
            recovery.Operation.AttemptCount,
            true,
            0,
            0,
            clock.GetUtcNow(),
            "operator",
            "accept delayed visibility risk"));

        var link = await service.FinalizeAsync(
            new FinalizeExternalWriteRequest(
                prepared.Operation.OperationId,
                null,
                "R_repo",
                "acme",
                "widgets",
                "I_late_visible",
                91,
                "https://github.com/acme/widgets/issues/91"),
            actor: "portfolio-sync");

        Assert.Equal("I_late_visible", link.ExternalNodeId);
        Assert.Equal(
            ExternalWriteState.Completed,
            (await service.GetOperationAsync(prepared.Operation.OperationId))!.State);
    }

    [Fact]
    public async Task Finalize_WithoutLease_IsAtomic_AndIdenticalRetryAddsNoAudit()
    {
        using var factory = new SqliteTestFactory();
        var seeded = await SeedTaskAsync(factory);
        var clock = new FakeTimeProvider(Start);
        var service = new ExternalWorkItemService(factory, clock, LeaseTtl);
        var prepared = await service.PrepareAsync(Request(seeded));
        await service.MarkDispatchedAsync(prepared.Operation.OperationId, prepared.LeaseToken);
        var request = new FinalizeExternalWriteRequest(
            prepared.Operation.OperationId,
            LeaseToken: null,
            RepositoryExternalId: "R_repo",
            RepositoryOwner: "acme",
            RepositoryName: "widgets",
            ExternalNodeId: "I_kwDO-test-42",
            ExternalNumber: 42,
            Url: "https://github.com/acme/widgets/issues/42");

        var link = await service.FinalizeAsync(request, actor: "sync-agent");
        var retry = await service.FinalizeAsync(request, actor: "other-agent");

        Assert.Equal(link.Id, retry.Id);
        await using (var db = await factory.CreateDbContextAsync())
        {
            var operation = await db.ExternalWriteOperations.SingleAsync();
            Assert.Equal(ExternalWriteState.Completed, operation.State);
            Assert.Equal(link.ExternalNodeId, operation.ResultingExternalNodeId);
            Assert.Equal(link.ExternalNumber, operation.ResultingExternalNumber);
            Assert.Equal(link.Url, operation.ResultingUrl);
            Assert.NotNull(operation.CompletedAt);
            Assert.Null(operation.LeaseToken);
            Assert.Null(operation.LeaseExpiresAt);
            Assert.Single(await db.ExternalWorkItemLinks.ToListAsync());
            var linkedAudit = Assert.Single(await db.ActivityEvents
                .Where(e => e.EventType == "ExternalWorkItemLinked")
                .ToListAsync());
            Assert.Equal("sync-agent", linkedAudit.Actor);
        }

        // Later verified sync may refresh mutable display metadata while the journal preserves
        // the exact original create result. Retrying that original result remains idempotent.
        await service.RegisterVerifiedLinkAsync(
            new RegisterExternalWorkItemLinkRequest(
                seeded.TaskId,
                ExternalProvider.GitHub,
                "R_repo",
                "new-owner",
                "renamed-widgets",
                "I_kwDO-test-42",
                84,
                "https://github.com/new-owner/renamed-widgets/issues/84"),
            actor: "sync-agent");
        var retryAfterMetadataRefresh = await service.FinalizeAsync(request, actor: "retry-agent");
        Assert.Equal(84, retryAfterMetadataRefresh.ExternalNumber);
        Assert.Equal("new-owner", retryAfterMetadataRefresh.RepositoryOwner);

        var mismatch = request with { ExternalNumber = 43, Url = "https://github.com/acme/widgets/issues/43" };
        var conflict = await Assert.ThrowsAsync<ExternalWorkItemConflictException>(
            () => service.FinalizeAsync(mismatch, actor: null));
        Assert.Equal("finalize-result-mismatch", conflict.ConflictCode);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Single(await verify.ActivityEvents.Where(e => e.EventType == "ExternalWorkItemLinked").ToListAsync());
        Assert.Single(await verify.ActivityEvents.Where(e => e.EventType == "ExternalWorkItemLinkRefreshed").ToListAsync());
    }

    [Fact]
    public async Task Finalize_RejectsDifferentRepositoryIdentityAndMismatchedUrlWithoutMutation()
    {
        using var factory = new SqliteTestFactory();
        var seeded = await SeedTaskAsync(factory);
        var service = Service(factory);
        var prepared = await service.PrepareAsync(Request(seeded));
        await service.MarkDispatchedAsync(prepared.Operation.OperationId, prepared.LeaseToken);

        var repositoryConflict = await Assert.ThrowsAsync<ExternalWorkItemConflictException>(() =>
            service.FinalizeAsync(
                new FinalizeExternalWriteRequest(
                    prepared.Operation.OperationId,
                    LeaseToken: null,
                    RepositoryExternalId: "R_other",
                    RepositoryOwner: "other-owner",
                    RepositoryName: "other-repository",
                    ExternalNodeId: "I_wrong_repository",
                    ExternalNumber: 17,
                    Url: "https://github.com/other-owner/other-repository/issues/17"),
                actor: "sync"));
        Assert.Equal("operation-repository-mismatch", repositoryConflict.ConflictCode);

        await Assert.ThrowsAsync<ValidationException>(() => service.FinalizeAsync(
            new FinalizeExternalWriteRequest(
                prepared.Operation.OperationId,
                LeaseToken: null,
                RepositoryExternalId: "R_repo",
                RepositoryOwner: "acme",
                RepositoryName: "widgets",
                ExternalNodeId: "I_bad_url",
                ExternalNumber: 18,
                Url: "https://github.com/acme/different-repository/issues/18"),
            actor: "sync"));

        await using var verify = await factory.CreateDbContextAsync();
        var operation = await verify.ExternalWriteOperations.SingleAsync();
        Assert.Equal(ExternalWriteState.Dispatched, operation.State);
        Assert.Null(operation.ResultingExternalNodeId);
        Assert.Null(operation.CompletedAt);
        Assert.Empty(await verify.ExternalWorkItemLinks.ToListAsync());
        Assert.Empty(await verify.ActivityEvents
            .Where(e => e.EventType == "ExternalWorkItemLinked")
            .ToListAsync());
    }

    [Fact]
    public async Task Finalize_AllowsRepositoryRenameWhenImmutableRepositoryIdentityMatches()
    {
        using var factory = new SqliteTestFactory();
        var seeded = await SeedTaskAsync(factory);
        var service = Service(factory);
        var prepared = await service.PrepareAsync(Request(seeded));
        await service.MarkDispatchedAsync(prepared.Operation.OperationId, prepared.LeaseToken);

        var link = await service.FinalizeAsync(
            new FinalizeExternalWriteRequest(
                prepared.Operation.OperationId,
                LeaseToken: null,
                RepositoryExternalId: "R_repo",
                RepositoryOwner: "renamed-owner",
                RepositoryName: "renamed-repository",
                ExternalNodeId: "I_same_repository_after_rename",
                ExternalNumber: 23,
                Url: "https://github.com/renamed-owner/renamed-repository/issues/23"),
            actor: "sync");

        Assert.Equal("R_repo", link.RepositoryExternalId);
        Assert.Equal("renamed-owner", link.RepositoryOwner);
        Assert.Equal("renamed-repository", link.RepositoryName);
        Assert.Equal("https://github.com/renamed-owner/renamed-repository/issues/23", link.Url);
        Assert.Equal(
            ExternalWriteState.Completed,
            (await service.GetOperationAsync(prepared.Operation.OperationId))!.State);
    }

    [Fact]
    public async Task Finalize_RollsBackOperationAndAudit_WhenLinkInsertFails()
    {
        using var factory = new SqliteTestFactory();
        var seeded = await SeedTaskAsync(factory);
        var service = Service(factory);
        var prepared = await service.PrepareAsync(Request(seeded));
        await service.MarkDispatchedAsync(prepared.Operation.OperationId, prepared.LeaseToken);

        // Simulate corruption outside the guarded service paths: the journal intentionally has no
        // task FK, while the new link does. This forces SaveChanges to fail after Finalize has
        // staged the operation transition, link, and audit in one transaction.
        await using (var db = await factory.CreateDbContextAsync())
        {
            var task = await db.Tasks.SingleAsync(t => t.Id == seeded.TaskId);
            db.Tasks.Remove(task);
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<DbUpdateException>(() => service.FinalizeAsync(
            new FinalizeExternalWriteRequest(
                prepared.Operation.OperationId,
                LeaseToken: null,
                RepositoryExternalId: "R_repo",
                RepositoryOwner: "acme",
                RepositoryName: "widgets",
                ExternalNodeId: "I_atomic_failure",
                ExternalNumber: 51,
                Url: "https://github.com/acme/widgets/issues/51"),
            actor: "sync"));

        await using var verify = await factory.CreateDbContextAsync();
        var operation = await verify.ExternalWriteOperations.SingleAsync();
        Assert.Equal(ExternalWriteState.Dispatched, operation.State);
        Assert.Null(operation.ResultingExternalNodeId);
        Assert.Null(operation.ResultingExternalNumber);
        Assert.Null(operation.ResultingUrl);
        Assert.Null(operation.CompletedAt);
        Assert.Empty(await verify.ExternalWorkItemLinks.ToListAsync());
        Assert.Empty(await verify.ActivityEvents
            .Where(e => e.EventType == "ExternalWorkItemLinked")
            .ToListAsync());
    }

    [Fact]
    public async Task PreparedSnapshotAndLinkSurviveTaskRename()
    {
        using var factory = new SqliteTestFactory();
        var seeded = await SeedTaskAsync(factory, "Before rename");
        var clock = new FakeTimeProvider(Start);
        var service = new ExternalWorkItemService(factory, clock, LeaseTtl);
        var prepared = await service.PrepareAsync(Request(seeded, body: "frozen body"));
        var originalHash = prepared.Operation.RequestHash;
        var originalBody = prepared.Operation.Body;

        await using (var db = await factory.CreateDbContextAsync())
        {
            var task = await db.Tasks.SingleAsync(t => t.Id == seeded.TaskId);
            task.Name = "After arbitrary rename";
            task.Description = "new local description";
            task.UpdatedAt = task.UpdatedAt.AddHours(1);
            await db.SaveChangesAsync();
        }

        var dispatched = await service.MarkDispatchedAsync(
            prepared.Operation.OperationId,
            prepared.LeaseToken);
        Assert.Equal("Snapshot title", dispatched.Title);
        Assert.Equal(originalBody, dispatched.Body);
        Assert.Equal(originalHash, dispatched.RequestHash);
        Assert.Equal(seeded.UpdatedAt, dispatched.ExpectedTaskUpdatedAt);

        var link = await service.FinalizeAsync(
            new FinalizeExternalWriteRequest(
                prepared.Operation.OperationId,
                null,
                "R_repo",
                "acme",
                "widgets",
                "I_rename-safe",
                77,
                "https://github.com/acme/widgets/issues/77"),
            actor: "sync");

        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal("After arbitrary rename", (await verify.Tasks.FindAsync(seeded.TaskId))!.Name);
        Assert.Equal("I_rename-safe", link.ExternalNodeId);
        Assert.Equal(link.Id, (await service.GetActiveLinkAsync(seeded.TaskId, ExternalProvider.GitHub))!.Id);
    }

    [Fact]
    public async Task RegistrationAndQuarantine_RejectUrlsOutsideSuppliedGitHubIdentity()
    {
        using var factory = new SqliteTestFactory();
        var seeded = await SeedTaskAsync(factory);
        var service = Service(factory);

        await Assert.ThrowsAsync<ValidationException>(() =>
            service.RegisterVerifiedLinkAsync(
                Verified(seeded.TaskId) with
                {
                    Url = "https://attacker.example/acme/widgets/issues/42"
                },
                actor: "sync"));

        await Assert.ThrowsAsync<ValidationException>(() =>
            service.QuarantineAsync(
                new QuarantineExternalWorkItemRequest(
                    seeded.TaskId,
                    ExternalProvider.GitHub,
                    "R_repo",
                    "acme",
                    "widgets",
                    CandidateNumber: 42,
                    CandidateUrl: "https://github.com/acme/other-repository/issues/42",
                    Reason: "untrusted candidate"),
                actor: "backfill"));

        await using var verify = await factory.CreateDbContextAsync();
        Assert.Empty(await verify.ExternalWorkItemLinks.ToListAsync());
        Assert.Empty(await verify.ActivityEvents.ToListAsync());
    }

    [Fact]
    public async Task RegisterVerifiedLink_IsNoOpForIdentical_AndRefreshesMutableRepoMetadata()
    {
        using var factory = new SqliteTestFactory();
        var seeded = await SeedTaskAsync(factory, "Issue title one");
        var clock = new FakeTimeProvider(Start);
        var service = new ExternalWorkItemService(factory, clock, LeaseTtl);
        var initial = Verified(seeded.TaskId);

        var link = await service.RegisterVerifiedLinkAsync(initial, actor: "backfill");
        var identical = await service.RegisterVerifiedLinkAsync(initial, actor: "duplicate");
        Assert.Equal(link.Id, identical.Id);

        await using (var db = await factory.CreateDbContextAsync())
        {
            var task = await db.Tasks.SingleAsync(t => t.Id == seeded.TaskId);
            task.Name = "Issue title independently renamed";
            task.UpdatedAt = task.UpdatedAt.AddMinutes(1);
            await db.SaveChangesAsync();
        }

        clock.Advance(TimeSpan.FromMinutes(1));
        var renamedRepo = initial with
        {
            RepositoryOwner = "new-owner",
            RepositoryName = "renamed-widgets",
            ExternalNumber = 84,
            Url = "https://github.com/new-owner/renamed-widgets/issues/84"
        };
        var refreshed = await service.RegisterVerifiedLinkAsync(renamedRepo, actor: "sync");

        Assert.Equal(link.Id, refreshed.Id);
        Assert.Equal("R_immutable", refreshed.RepositoryExternalId);
        Assert.Equal("I_immutable", refreshed.ExternalNodeId);
        Assert.Equal("new-owner", refreshed.RepositoryOwner);
        Assert.Equal("renamed-widgets", refreshed.RepositoryName);
        Assert.Equal(84, refreshed.ExternalNumber);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal("Issue title independently renamed", (await verify.Tasks.FindAsync(seeded.TaskId))!.Name);
        Assert.Equal(1, await verify.ActivityEvents.CountAsync(e => e.EventType == "ExternalWorkItemLinked"));
        Assert.Equal(1, await verify.ActivityEvents.CountAsync(e => e.EventType == "ExternalWorkItemLinkRefreshed"));

        var mismatch = renamedRepo with { ExternalNodeId = "I_different" };
        var conflict = await Assert.ThrowsAsync<ExternalWorkItemConflictException>(
            () => service.RegisterVerifiedLinkAsync(mismatch, actor: null));
        Assert.Equal("external-link-mismatch", conflict.ConflictCode);
    }

    [Fact]
    public async Task RegisterVerifiedLink_TransfersSameNode_AndOldFinalizeReplayConverges()
    {
        using var factory = new SqliteTestFactory();
        var seeded = await SeedTaskAsync(factory);
        var service = Service(factory);
        var prepared = await service.PrepareAsync(Request(seeded));
        await service.MarkDispatchedAsync(prepared.Operation.OperationId, prepared.LeaseToken);
        var originalResult = new FinalizeExternalWriteRequest(
            prepared.Operation.OperationId,
            null,
            "R_repo",
            "acme",
            "widgets",
            "I_transfer_stable",
            71,
            "https://github.com/acme/widgets/issues/71");
        var original = await service.FinalizeAsync(originalResult, actor: "creator");

        var transferredRequest = new RegisterExternalWorkItemLinkRequest(
            seeded.TaskId,
            ExternalProvider.GitHub,
            "R_new_repository",
            "new-owner",
            "new-repository",
            "I_transfer_stable",
            9,
            "https://github.com/new-owner/new-repository/issues/9");
        var transferred = await service.RegisterVerifiedLinkAsync(transferredRequest, actor: "sync");
        var duplicate = await service.RegisterVerifiedLinkAsync(transferredRequest, actor: "repeat");

        Assert.Equal(original.Id, transferred.Id);
        Assert.Equal(transferred.Id, duplicate.Id);
        Assert.Equal("R_new_repository", transferred.RepositoryExternalId);
        Assert.Equal(9, transferred.ExternalNumber);

        // The completed journal remains an immutable record of the original create. Replaying its
        // original finalize result after a transfer still converges on the same active node/link.
        var finalizeReplay = await service.FinalizeAsync(originalResult, actor: "late-replay");
        Assert.Equal(transferred.Id, finalizeReplay.Id);
        Assert.Equal("R_new_repository", finalizeReplay.RepositoryExternalId);

        await using var db = await factory.CreateDbContextAsync();
        Assert.Single(await db.ActivityEvents
            .Where(e => e.EventType == "ExternalWorkItemTransferred")
            .ToListAsync());
        Assert.Single(await db.ActivityEvents
            .Where(e => e.EventType == "ExternalWorkItemLinked")
            .ToListAsync());
        Assert.Empty(await db.ActivityEvents
            .Where(e => e.EventType == "ExternalWorkItemLinkRefreshed")
            .ToListAsync());
    }

    [Fact]
    public async Task RegisterVerifiedLink_TransferToClaimedNumberConflictsWithoutMutation()
    {
        using var factory = new SqliteTestFactory();
        var first = await SeedTaskAsync(factory, "transfer source");
        var second = await SeedTaskAsync(factory, "target number owner");
        var service = Service(factory);
        var source = Verified(first.TaskId) with
        {
            RepositoryExternalId = "R_source",
            ExternalNodeId = "I_source",
            ExternalNumber = 31,
            Url = "https://github.com/acme/widgets/issues/31"
        };
        var targetOwner = Verified(second.TaskId) with
        {
            RepositoryExternalId = "R_target",
            RepositoryName = "target",
            ExternalNodeId = "I_target_owner",
            ExternalNumber = 7,
            Url = "https://github.com/acme/target/issues/7"
        };
        var sourceLink = await service.RegisterVerifiedLinkAsync(source, actor: "seed");
        await service.RegisterVerifiedLinkAsync(targetOwner, actor: "seed");

        var conflict = await Assert.ThrowsAsync<ExternalWorkItemConflictException>(() =>
            service.RegisterVerifiedLinkAsync(source with
            {
                RepositoryExternalId = "R_target",
                RepositoryName = "target",
                ExternalNumber = 7,
                Url = "https://github.com/acme/target/issues/7"
            }, actor: "sync"));

        Assert.Equal("external-number-already-linked", conflict.ConflictCode);
        await using var db = await factory.CreateDbContextAsync();
        var persisted = await db.ExternalWorkItemLinks.SingleAsync(link => link.Id == sourceLink.Id);
        Assert.Equal("R_source", persisted.RepositoryExternalId);
        Assert.Equal(31, persisted.ExternalNumber);
        Assert.Empty(await db.ActivityEvents
            .Where(e => e.EventType == "ExternalWorkItemTransferred")
            .ToListAsync());
    }

    [Fact]
    public async Task Quarantine_IsPersisted_Idempotent_NotActive_AndBlocksPrepare()
    {
        using var factory = new SqliteTestFactory();
        var seeded = await SeedTaskAsync(factory);
        var service = Service(factory);
        var request = new QuarantineExternalWorkItemRequest(
            seeded.TaskId,
            ExternalProvider.GitHub,
            "R_repo",
            "acme",
            "widgets",
            CandidateNumber: 123,
            CandidateUrl: "https://github.com/acme/widgets/issues/123",
            Reason: "title prefix has no verified sync provenance");

        var quarantined = await service.QuarantineAsync(request, actor: "backfill");
        var retry = await service.QuarantineAsync(request, actor: "duplicate");

        Assert.Equal(quarantined.Id, retry.Id);
        Assert.Equal(ExternalLinkState.Quarantined, quarantined.State);
        Assert.Null(quarantined.ExternalNodeId);
        Assert.Null(await service.GetActiveLinkAsync(seeded.TaskId, ExternalProvider.GitHub));
        var conflict = await Assert.ThrowsAsync<ExternalWorkItemConflictException>(
            () => service.PrepareAsync(Request(seeded)));
        Assert.Equal("quarantined-link-exists", conflict.ConflictCode);

        await using var db = await factory.CreateDbContextAsync();
        Assert.Single(await db.ExternalWorkItemLinks.ToListAsync());
        Assert.Single(await db.ActivityEvents.Where(e => e.EventType == "ExternalLinkQuarantined").ToListAsync());
    }

    [Fact]
    public async Task Quarantine_PreservesCompleteReasonAndWhitespaceInLinkAndAudit()
    {
        using var factory = new SqliteTestFactory();
        var seeded = await SeedTaskAsync(factory);
        var service = Service(factory);
        var reason = "\r\n    Human explanation 漢字 🧪\r\n" + new string('x', 2_001) + "\r\n    Final decision context  \r\n\t";
        var request = new QuarantineExternalWorkItemRequest(
            seeded.TaskId,
            ExternalProvider.GitHub,
            "R_repo",
            "acme",
            "widgets",
            CandidateNumber: 123,
            CandidateUrl: "https://github.com/acme/widgets/issues/123",
            Reason: reason);

        var result = await service.QuarantineAsync(request, actor: "human");
        var replay = await service.QuarantineAsync(request, actor: "retry");

        Assert.Equal(reason, result.QuarantineReason);
        Assert.Equal(result.Id, replay.Id);
        await using var db = await factory.CreateDbContextAsync();
        Assert.Equal(reason, (await db.ExternalWorkItemLinks.SingleAsync()).QuarantineReason);
        var audit = Assert.Single(await db.ActivityEvents.Where(e => e.EventType == "ExternalLinkQuarantined").ToListAsync());
        using var after = JsonDocument.Parse(audit.AfterJson!);
        Assert.Equal(reason, after.RootElement.GetProperty("reason").GetString());
        Assert.EndsWith(reason, audit.Message);
        Assert.Equal("human", audit.Actor);
    }

    [Fact]
    public async Task VerifiedRegistration_PromotesQuarantineInPlace_AndAuditsDecision()
    {
        using var factory = new SqliteTestFactory();
        var seeded = await SeedTaskAsync(factory);
        var service = Service(factory);
        var quarantine = await service.QuarantineAsync(
            new QuarantineExternalWorkItemRequest(
                seeded.TaskId,
                ExternalProvider.GitHub,
                "R_repo",
                "old-owner",
                "old-name",
                CandidateNumber: 123,
                CandidateUrl: "https://github.com/old-owner/old-name/issues/123",
                Reason: "candidate requires a human decision"),
            actor: "backfill");
        var verified = new RegisterExternalWorkItemLinkRequest(
            seeded.TaskId,
            ExternalProvider.GitHub,
            "R_repo",
            "acme",
            "widgets",
            "I_verified_123",
            123,
            "https://github.com/acme/widgets/issues/123");

        var active = await service.RegisterVerifiedLinkAsync(verified, actor: "operator");
        var identical = await service.RegisterVerifiedLinkAsync(verified, actor: "duplicate");

        Assert.Equal(quarantine.Id, active.Id);
        Assert.Equal(active.Id, identical.Id);
        Assert.Equal(ExternalLinkState.Active, active.State);
        Assert.Equal("I_verified_123", active.ExternalNodeId);
        Assert.Equal("acme", active.RepositoryOwner);
        Assert.Equal("widgets", active.RepositoryName);
        Assert.Null(active.QuarantineReason);
        await using var db = await factory.CreateDbContextAsync();
        Assert.Single(await db.ExternalWorkItemLinks.ToListAsync());
        var audit = Assert.Single(await db.ActivityEvents
            .Where(e => e.EventType == "ExternalLinkQuarantineResolved")
            .ToListAsync());
        Assert.Equal("operator", audit.Actor);
        Assert.Contains("candidate requires a human decision", audit.AfterJson);
        Assert.Single(await db.ActivityEvents.Where(e => e.EventType == "ExternalLinkQuarantined").ToListAsync());
        Assert.Empty(await db.ActivityEvents.Where(e => e.EventType == "ExternalWorkItemLinked").ToListAsync());
    }

    [Fact]
    public async Task VerifiedRegistration_CannotPromoteQuarantineToAlreadyLinkedIdentity()
    {
        using var factory = new SqliteTestFactory();
        var first = await SeedTaskAsync(factory, "first");
        var second = await SeedTaskAsync(factory, "second");
        var service = Service(factory);
        var winner = Verified(first.TaskId);
        await service.RegisterVerifiedLinkAsync(winner, actor: "backfill");
        var quarantined = await service.QuarantineAsync(
            new QuarantineExternalWorkItemRequest(
                second.TaskId,
                ExternalProvider.GitHub,
                winner.RepositoryExternalId,
                winner.RepositoryOwner,
                winner.RepositoryName,
                winner.ExternalNumber,
                winner.Url,
                "duplicate candidate"),
            actor: "backfill");

        var conflict = await Assert.ThrowsAsync<ExternalWorkItemConflictException>(() =>
            service.RegisterVerifiedLinkAsync(winner with { TaskId = second.TaskId }, actor: "operator"));

        Assert.Equal("external-node-already-linked", conflict.ConflictCode);
        await using var db = await factory.CreateDbContextAsync();
        var persisted = await db.ExternalWorkItemLinks.SingleAsync(l => l.Id == quarantined.Id);
        Assert.Equal(ExternalLinkState.Quarantined, persisted.State);
        Assert.Equal("duplicate candidate", persisted.QuarantineReason);
        Assert.Empty(await db.ActivityEvents
            .Where(e => e.EntityId == second.TaskId && e.EventType == "ExternalLinkQuarantineResolved")
            .ToListAsync());
    }

    [Fact]
    public async Task DatabaseConstraintsDirectlyRejectBothLinkAndOperationDuplicates()
    {
        using var factory = new SqliteTestFactory();
        var first = await SeedTaskAsync(factory, "first");
        var second = await SeedTaskAsync(factory, "second");

        // Provider + node id: a remote issue cannot belong to two tasks.
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.ExternalWorkItemLinks.AddRange(
                ActiveLink(first.TaskId, "I_same", 1),
                ActiveLink(second.TaskId, "I_same", 2));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        // Task + provider: a task cannot carry two GitHub identities, including quarantine rows.
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.ExternalWorkItemLinks.AddRange(
                ActiveLink(first.TaskId, "I_one", 10),
                ActiveLink(first.TaskId, "I_two", 11));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        // Provider + immutable repository id + issue number: the same active repository issue
        // cannot be linked twice even when the observed node ids differ.
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.ExternalWorkItemLinks.AddRange(
                ActiveLink(first.TaskId, "I_repo_one", 42),
                ActiveLink(second.TaskId, "I_repo_two", 42));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        // The repository/number uniqueness index is deliberately filtered to Active. Ambiguous
        // candidates for different tasks can therefore coexist in quarantine until reconciled.
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.ExternalWorkItemLinks.AddRange(
                QuarantinedLink(first.TaskId, 99),
                QuarantinedLink(second.TaskId, 99));
            await db.SaveChangesAsync();
            Assert.Equal(2, await db.ExternalWorkItemLinks.CountAsync());
        }

        // Journal uniqueness is lifetime uniqueness, not just an active-state convention.
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.ExternalWriteOperations.AddRange(
                Operation(first, Guid.NewGuid()),
                Operation(first, Guid.NewGuid()));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

    private static ExternalWorkItemService Service(SqliteTestFactory factory) =>
        new(factory, new FakeTimeProvider(Start), LeaseTtl);

    private static PrepareExternalWriteRequest Request(
        SeededTask seeded,
        string body = "Outbound Markdown") =>
        new(
            seeded.TaskId,
            ExternalProvider.GitHub,
            "R_repo",
            "acme",
            "widgets",
            "Snapshot title",
            body,
            "M1",
            "P1",
            seeded.UpdatedAt);

    private static RegisterExternalWorkItemLinkRequest Verified(int taskId) =>
        new(
            taskId,
            ExternalProvider.GitHub,
            "R_immutable",
            "acme",
            "widgets",
            "I_immutable",
            42,
            "https://github.com/acme/widgets/issues/42");

    private static ExternalWorkItemLink ActiveLink(int taskId, string nodeId, int number) =>
        new()
        {
            TaskId = taskId,
            Provider = ExternalProvider.GitHub,
            State = ExternalLinkState.Active,
            RepositoryExternalId = "R_repo",
            RepositoryOwner = "acme",
            RepositoryName = "widgets",
            ExternalNodeId = nodeId,
            ExternalNumber = number,
            Url = $"https://github.com/acme/widgets/issues/{number}"
        };

    private static ExternalWorkItemLink QuarantinedLink(int taskId, int number) =>
        new()
        {
            TaskId = taskId,
            Provider = ExternalProvider.GitHub,
            State = ExternalLinkState.Quarantined,
            RepositoryExternalId = "R_repo",
            RepositoryOwner = "acme",
            RepositoryName = "widgets",
            ExternalNumber = number,
            Url = $"https://github.com/acme/widgets/issues/{number}",
            QuarantineReason = "ambiguous test candidate"
        };

    private static ExternalWriteOperation Operation(SeededTask task, Guid operationId) =>
        new()
        {
            OperationId = operationId,
            TaskId = task.TaskId,
            ProjectId = task.ProjectId,
            Provider = ExternalProvider.GitHub,
            RepositoryExternalId = "R_repo",
            RepositoryOwner = "acme",
            RepositoryName = "widgets",
            Title = "title",
            Body = $"body\n\n<!-- kanbanflow-push:{operationId:D} -->",
            RequestHash = new string('a', 64),
            ExpectedTaskUpdatedAt = task.UpdatedAt,
            State = ExternalWriteState.Prepared
        };

    private static async Task<SeededTask> SeedTaskAsync(
        IDbContextFactory<KanbanFlow.Data.AppDbContext> factory,
        string name = "Task")
    {
        await using var db = await factory.CreateDbContextAsync();
        var task = new TaskItem
        {
            Name = name,
            UpdatedAt = Start.UtcDateTime,
            Feature = new Feature
            {
                Name = $"Feature {Guid.NewGuid():N}",
                Project = new Project { Name = $"Project {Guid.NewGuid():N}" }
            }
        };
        db.Tasks.Add(task);
        await db.SaveChangesAsync();
        return new SeededTask(task.Id, task.Feature.ProjectId, task.UpdatedAt);
    }

    private sealed record SeededTask(int TaskId, int ProjectId, DateTime UpdatedAt);
}
