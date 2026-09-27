using System.ClientModel;
using KanbanFlow.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace KanbanFlow.Tests;

/// <summary>
/// Real-behavior tests for <see cref="EmbeddingService"/> (#219). The collaborator is a hand-written
/// fake <c>IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt;</c> (no Moq) so the wrapper's
/// actual control flow runs: vector/dimension mapping, the content-hash cache (a repeat issues zero
/// provider calls), batch dedup + provider-safe chunking, the timeout + single-retry policy, the
/// fail-soft mapping of provider errors to a typed <see cref="EmbedResult"/>, honored caller
/// cancellation, and bounded-cache eviction. Plus pure tests for the cache-key + config binding.
/// </summary>
public sealed class EmbeddingServiceTests
{
    private const string FakeModel = "fake-embed-model";

    // Generous hang-guard for the rendezvous waits below. This is NOT a race budget — the timeout
    // under test is driven by a FakeTimeProvider, so it fires when the test says so. This only stops
    // a genuine deadlock from hanging the suite, and it lives in the TEST BODY (not inside the fake)
    // so a real bug surfaces as "the retry never started" rather than corrupting the asserted string.
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private static EmbeddingService NewService(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        EmbeddingOptions? config = null,
        int? capacity = null,
        TimeProvider? timeProvider = null)
    {
        config ??= new EmbeddingOptions { Model = "test-embed-model", TimeoutMs = 5_000 };
        // capacity 0 ⇒ the service falls back to its own DefaultCacheCapacity (EmbeddingService.cs:87),
        // so the default path stays identical without duplicating the constant here.
        return new EmbeddingService(
            generator, config, NullLogger<EmbeddingService>.Instance,
            capacity ?? 0, timeProvider);
    }

    // Canned, content-derived 3-dim vector: identical text ⇒ identical vector; different text ⇒
    // different vector. Lets tests assert both the mapping and that dedup/caching short-circuits.
    private static Embedding<float> Canned(string text) =>
        new(new[] { (float)text.Length, text.Length > 0 ? text[0] : 0f, 3.5f }) { ModelId = FakeModel };

    private static Task<GeneratedEmbeddings<Embedding<float>>> CannedBatch(IReadOnlyList<string> inputs) =>
        Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(inputs.Select(Canned)));

    // The shape a refused connect REALLY takes (#549), copied from the app log of a run with nothing
    // on :5256: System.ClientModel's HttpClientPipelineTransport wraps the HttpRequestException in a
    // ClientResultException that has no response (Status 0). The SDK never surfaces the bare
    // HttpRequestException the older transport tests throw.
    private const string RefusedMessage =
        "No connection could be made because the target machine actively refused it. (localhost:5256)";

    private static ClientResultException SdkTransportFailure() =>
        new(RefusedMessage, response: null, innerException: new HttpRequestException(RefusedMessage));

    // ── success + mapping ──────────────────────────────────────────────────────────
    [Fact]
    public async Task EmbedAsync_Success_MapsVectorDimensionsAndModel()
    {
        var fake = new FakeEmbeddingGenerator((inputs, _, _) => CannedBatch(inputs));
        var service = NewService(fake);

        var result = await service.EmbedAsync("hello", CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(new[] { 5f, 'h', 3.5f }, result.Vector); // len=5, 'h'=104
        Assert.Equal(3, result.Dimensions);
        Assert.Equal(FakeModel, result.Model);
        Assert.Null(result.Error);
        Assert.Equal(1, fake.CallCount);
    }

    // ── cache ──────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task EmbedAsync_CacheHit_SecondCall_DoesNotCallProvider()
    {
        var fake = new FakeEmbeddingGenerator((inputs, _, _) => CannedBatch(inputs));
        var service = NewService(fake);

        var first = await service.EmbedAsync("same text", CancellationToken.None);
        var second = await service.EmbedAsync("same text", CancellationToken.None);

        Assert.True(first.Ok);
        Assert.True(second.Ok);
        Assert.Equal(first.Vector, second.Vector);
        Assert.Equal(1, fake.CallCount); // second call served from cache ⇒ no 2nd provider call
    }

    [Fact]
    public async Task EmbedAsync_DistinctText_EachCallsProvider()
    {
        var fake = new FakeEmbeddingGenerator((inputs, _, _) => CannedBatch(inputs));
        var service = NewService(fake);

        await service.EmbedAsync("alpha", CancellationToken.None);
        await service.EmbedAsync("beta", CancellationToken.None);

        Assert.Equal(2, fake.CallCount); // different content ⇒ different cache key ⇒ embedded separately
    }

    [Fact]
    public async Task EmbedAsync_EvictsOldest_WhenOverCapacity()
    {
        var fake = new FakeEmbeddingGenerator((inputs, _, _) => CannedBatch(inputs));
        var service = NewService(fake, capacity: 2); // bounded to 2 vectors

        await service.EmbedAsync("a", CancellationToken.None);
        await service.EmbedAsync("b", CancellationToken.None);
        await service.EmbedAsync("c", CancellationToken.None); // overflow ⇒ oldest ("a") evicted
        Assert.Equal(3, fake.CallCount);

        await service.EmbedAsync("a", CancellationToken.None); // "a" was evicted ⇒ re-embedded
        Assert.Equal(4, fake.CallCount);

        await service.EmbedAsync("c", CancellationToken.None); // "c" still cached ⇒ no provider call
        Assert.Equal(4, fake.CallCount);
    }

    [Fact]
    public async Task EmbedAsync_CacheHit_ReturnsProviderReportedModel_NotConfiguredModel()
    {
        // The provider reports a model id ("m-reported") that differs from the configured/requested
        // model. A fresh result already surfaces the reported model; #251 F2: a cache HIT must too,
        // not fall back to _config.Model.
        const string reported = "m-reported";
        var fake = new FakeEmbeddingGenerator((inputs, _, _) => Task.FromResult(
            new GeneratedEmbeddings<Embedding<float>>(
                inputs.Select(t => new Embedding<float>(new[] { (float)t.Length }) { ModelId = reported }))));
        var service = NewService(fake, new EmbeddingOptions { Model = "configured-model", TimeoutMs = 5_000 });

        var miss = await service.EmbedAsync("text", CancellationToken.None); // fresh ⇒ provider model
        var hit = await service.EmbedAsync("text", CancellationToken.None);  // served from cache

        Assert.Equal(1, fake.CallCount);          // second call hit the cache (no provider call)
        Assert.Equal(reported, miss.Model);       // fresh result reports the provider's model
        Assert.Equal(reported, hit.Model);        // F2: cache hit is consistent with the fresh result
        Assert.NotEqual("configured-model", hit.Model);
    }

    [Fact]
    public async Task EmbedAsync_CacheHit_ReturnsDefensiveCopy_NotSharedWithCache()
    {
        // #251 F4: a cache hit must hand back a COPY of the cached vector. Two hits return distinct
        // array instances, and mutating one must not poison the cached value seen by the next hit.
        var fake = new FakeEmbeddingGenerator((inputs, _, _) => CannedBatch(inputs));
        var service = NewService(fake);

        await service.EmbedAsync("dup", CancellationToken.None);            // prime (miss ⇒ store)
        var hit1 = await service.EmbedAsync("dup", CancellationToken.None); // cache hit #1
        var hit2 = await service.EmbedAsync("dup", CancellationToken.None); // cache hit #2

        Assert.True(hit1.Ok);
        Assert.True(hit2.Ok);
        Assert.NotSame(hit1.Vector, hit2.Vector); // each hit gets its own array instance...
        Assert.Equal(hit1.Vector, hit2.Vector);   // ...equal by value

        hit1.Vector[0] = -999f;                    // a caller poisoning the array it received

        var hit3 = await service.EmbedAsync("dup", CancellationToken.None);
        Assert.Equal(new[] { 3f, 'd', 3.5f }, hit3.Vector); // "dup".Length=3, 'd'=100 — cache intact
        Assert.Equal(1, fake.CallCount);           // every repeat served from cache (one provider call)
    }

    // ── batch: dedup + cache + order ─────────────────────────────────────────────────
    [Fact]
    public async Task EmbedBatchAsync_DedupesAndEmbedsOnlyDistinctMisses()
    {
        var fake = new FakeEmbeddingGenerator((inputs, _, _) => CannedBatch(inputs));
        var service = NewService(fake);

        var results = await service.EmbedBatchAsync(new[] { "a", "b", "a" }, CancellationToken.None);

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.True(r.Ok));
        Assert.Equal(results[0].Vector, results[2].Vector);     // both "a" ⇒ same vector
        Assert.NotEqual(results[0].Vector, results[1].Vector);  // "a" vs "b"
        Assert.Equal(1, fake.CallCount);                        // single batched provider call
        Assert.Equal(new[] { "a", "b" }, fake.AllRequested);    // distinct, first-seen order preserved
    }

    [Fact]
    public async Task EmbedBatchAsync_PreservesInputOrderAndCount()
    {
        var fake = new FakeEmbeddingGenerator((inputs, _, _) => CannedBatch(inputs));
        var service = NewService(fake);

        var results = await service.EmbedBatchAsync(new[] { "x", "yy", "zzz" }, CancellationToken.None);

        Assert.Equal(3, results.Count);
        Assert.Equal(1f, results[0].Vector[0]); // "x".Length
        Assert.Equal(2f, results[1].Vector[0]); // "yy".Length
        Assert.Equal(3f, results[2].Vector[0]); // "zzz".Length
    }

    [Fact]
    public async Task EmbedBatchAsync_UsesCache_ForAlreadyEmbeddedText()
    {
        var fake = new FakeEmbeddingGenerator((inputs, _, _) => CannedBatch(inputs));
        var service = NewService(fake);

        await service.EmbedAsync("a", CancellationToken.None);          // pre-cache "a"
        var results = await service.EmbedBatchAsync(new[] { "a", "b" }, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.Ok));
        Assert.Equal(2, fake.CallCount);                         // single EmbedAsync + one batch call
        Assert.Equal(new[] { "b" }, fake.LastRequested);         // only the miss "b" went to the provider
    }

    [Fact]
    public async Task EmbedBatchAsync_EmptyList_ReturnsEmpty_WithoutCallingProvider()
    {
        var fake = new FakeEmbeddingGenerator((inputs, _, _) => CannedBatch(inputs));
        var service = NewService(fake);

        var results = await service.EmbedBatchAsync(Array.Empty<string>(), CancellationToken.None);

        Assert.Empty(results);
        Assert.Equal(0, fake.CallCount);
    }

    [Fact]
    public async Task EmbedBatchAsync_ChunksByMaxBatchSize()
    {
        var fake = new FakeEmbeddingGenerator((inputs, _, _) => CannedBatch(inputs));
        var service = NewService(fake, new EmbeddingOptions { Model = "m", TimeoutMs = 5_000, MaxBatchSize = 2 });

        var results = await service.EmbedBatchAsync(new[] { "a", "b", "c", "d", "e" }, CancellationToken.None);

        Assert.Equal(5, results.Count);
        Assert.All(results, r => Assert.True(r.Ok));
        Assert.Equal(3, fake.CallCount); // 5 misses / batch size 2 ⇒ chunks of 2,2,1
    }

    // ── fail-soft (provider errors return typed results, never throw) ────────────────
    [Fact]
    public async Task EmbedAsync_NonTransientError_DoesNotRetry_AndFailsSoft()
    {
        var fake = new FakeEmbeddingGenerator((_, _, _) => throw new InvalidOperationException("bad embed shape"));
        var service = NewService(fake);

        var result = await service.EmbedAsync("ping", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Empty(result.Vector);
        Assert.Equal(0, result.Dimensions);
        Assert.Contains("bad embed shape", result.Error);
        Assert.Equal(1, fake.CallCount); // non-transient ⇒ no retry
    }

    [Fact]
    public async Task EmbedAsync_TransientTransportError_RetriesOnce_ThenFailsSoft()
    {
        // A BARE HttpRequestException is still transient. The SDK never throws this shape directly (it
        // wraps it, see SdkTransportFailure), so the production-shape guard is
        // EmbedAsync_SdkWrappedTransportFailure_FailsSoftWithoutRetry, not this test (#549).
        var fake = new FakeEmbeddingGenerator((_, _, _) => throw new HttpRequestException("connection refused"));
        var service = NewService(fake);

        var result = await service.EmbedAsync("ping", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Equal("test-embed-model", result.Model);
        Assert.Equal(2, fake.CallCount); // 1 attempt + 1 retry on a transient error
    }

    [Fact]
    public async Task EmbedAsync_SdkWrappedTransportFailure_FailsSoftWithoutRetry()
    {
        // #549: a dead router, in the shape production really throws. A retry would double the ~4 s
        // refused-connect cost of every memory search, so this is classified as provider-unavailable:
        // one attempt, then the failure cooldown, never a second attempt inside the same call.
        var fake = new FakeEmbeddingGenerator((_, _, _) => throw SdkTransportFailure());
        var service = NewService(fake);

        var result = await service.EmbedAsync("ping", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("actively refused", result.Error); // the provider's own failure, not a cached one
        Assert.Equal(1, fake.CallCount);                   // provider-unavailable ⇒ not retried
    }

    [Fact]
    public async Task EmbedAsync_Timeout_IsTreatedAsTransient_RetriesThenFailsSoft()
    {
        // #262 — this test used to be flaky, and the flake was self-inflicted. The old version raced a
        // REAL 30ms per-attempt timeout against a 2-second watchdog inside the fake. Under full-suite
        // parallelism the thread pool starved the 30ms CTS, the watchdog won, and it threw a
        // TimeoutException — which IsTransient (EmbeddingService.cs:398-411, `_ => false`) classifies
        // as NON-transient. So the watchdog short-circuited the very retry this test exists to verify,
        // and the assertion failed on the watchdog's message instead of "timed out".
        //
        // Now the per-attempt timeout is driven by a FakeTimeProvider: the test ADVANCES the clock
        // rather than waiting on it. No race, no watchdog, no wall-clock dependency.
        var time = new FakeTimeProvider();
        using var enteredGenerator = new SemaphoreSlim(0);

        var fake = new FakeEmbeddingGenerator(async (_, _, token) =>
        {
            // We are inside the generator, so the service has already armed its timeout CTS.
            enteredGenerator.Release();
            // Block until the SERVICE cancels us — never on a timer of our own.
            await Task.Delay(Timeout.Infinite, token);
            return new GeneratedEmbeddings<Embedding<float>>(new[] { Canned("x") });
        });
        var service = NewService(
            fake,
            new EmbeddingOptions { Model = "test-embed-model", TimeoutMs = 30 },
            timeProvider: time);

        var call = service.EmbedAsync("ping", CancellationToken.None);

        // Attempt 1: wait until the generator is running, then fire its timeout.
        Assert.True(await enteredGenerator.WaitAsync(HangGuard), "attempt 1 never reached the generator");
        time.Advance(TimeSpan.FromMilliseconds(31));

        // Attempt 2 (the retry — the actual thing under test): same again.
        Assert.True(await enteredGenerator.WaitAsync(HangGuard), "the retry never started — timeout was not treated as transient");
        time.Advance(TimeSpan.FromMilliseconds(31));

        var result = await call;

        Assert.False(result.Ok);
        Assert.Contains("timed out", result.Error);
        Assert.Equal(2, fake.CallCount); // timeout is transient ⇒ retried once
    }

    [Fact]
    public async Task EmbedAsync_CallerCancellation_Throws_NotSwallowedIntoResult()
    {
        var fake = new FakeEmbeddingGenerator((inputs, _, _) => CannedBatch(inputs));
        var service = NewService(fake);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.EmbedAsync("ping", cts.Token));
        Assert.Equal(0, fake.CallCount); // cancelled before the first attempt starts
    }

    [Fact]
    public async Task EmbedBatchAsync_ProviderDown_AllItemsFailSoft_NoThrow()
    {
        var fake = new FakeEmbeddingGenerator((_, _, _) => throw new HttpRequestException("connection refused"));
        var service = NewService(fake);

        var results = await service.EmbedBatchAsync(new[] { "a", "b" }, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.All(results, r =>
        {
            Assert.False(r.Ok);
            Assert.False(string.IsNullOrWhiteSpace(r.Error));
        });
    }

    [Fact]
    public async Task EmbedBatchAsync_PartialChunkFailure_OtherChunkStillSucceeds()
    {
        // Batch size 1 ⇒ "a" and "b" are separate chunks; the "b" chunk throws (non-transient) and
        // fails soft, while the "a" chunk succeeds. Proves a chunk failure doesn't lose the batch.
        var fake = new FakeEmbeddingGenerator((inputs, _, _) =>
            inputs.Contains("b")
                ? throw new InvalidOperationException("boom for b")
                : CannedBatch(inputs));
        var service = NewService(fake, new EmbeddingOptions { Model = "m", TimeoutMs = 5_000, MaxBatchSize = 1 });

        var results = await service.EmbedBatchAsync(new[] { "a", "b" }, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.True(results[0].Ok);            // "a" succeeded
        Assert.Equal(3, results[0].Dimensions);
        Assert.False(results[1].Ok);           // "b" failed soft
        Assert.Contains("boom for b", results[1].Error);
    }

    [Fact]
    public async Task EmbedBatchAsync_CacheHitCapturedEarly_SurvivesConcurrentEviction()
    {
        // #251 F1: an item that is a cache HIT when the batch starts must still return its real
        // vector even if the batch's own stores evict it (TOCTOU). Capacity 1 ⇒ embedding the two
        // misses evicts the primed "a" before results are assembled; the early capture must hold.
        var fake = new FakeEmbeddingGenerator((inputs, _, _) => CannedBatch(inputs));
        var service = NewService(fake, capacity: 1);

        var primed = await service.EmbedAsync("a", CancellationToken.None); // "a" is the sole cache entry
        Assert.True(primed.Ok);

        var results = await service.EmbedBatchAsync(new[] { "a", "b", "c" }, CancellationToken.None);

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.True(r.Ok));      // none degraded into a spurious failure
        Assert.Equal(primed.Vector, results[0].Vector);   // "a" returned its real (early-captured) vector
        Assert.Equal(new[] { 1f, 'a', 3.5f }, results[0].Vector); // "a".Length=1, 'a'=97
    }

    [Fact]
    public async Task EmbedBatchAsync_BlankEntry_FailsSoftLocally_OnlyValidsGoToProvider()
    {
        // #251 F3: a blank/whitespace entry must NOT be sent to the provider (a 4xx on it would sink
        // the whole chunk). It fails soft locally with a clear error (not a zero vector), while the
        // valid entries embed normally in a single provider call.
        var fake = new FakeEmbeddingGenerator((inputs, _, _) => CannedBatch(inputs));
        var service = NewService(fake);

        var results = await service.EmbedBatchAsync(new[] { "alpha", "   ", "beta" }, CancellationToken.None);

        Assert.Equal(3, results.Count);
        Assert.True(results[0].Ok);                  // valid ⇒ embedded
        Assert.True(results[2].Ok);                  // valid ⇒ embedded
        Assert.False(results[1].Ok);                 // blank ⇒ fail soft, no provider call
        Assert.Empty(results[1].Vector);             // NOT a (semantically wrong) zero vector
        Assert.Equal(0, results[1].Dimensions);
        Assert.False(string.IsNullOrWhiteSpace(results[1].Error));
        Assert.Equal(1, fake.CallCount);             // a single provider call...
        Assert.Equal(new[] { "alpha", "beta" }, fake.LastRequested); // ...for only the two valid texts
    }

    // ── failure cooldown (negative cache, #549) ─────────────────────────────────────────
    [Fact]
    public async Task EmbedAsync_WithinCooldown_DoesNotCallProvider()
    {
        // After a provider-unavailable failure, every call inside the window fails soft at once with
        // no provider call, so a dead router costs one refused connect per window, not one per search.
        // The window is provider-wide (a DIFFERENT text is skipped too), and a cache hit is still
        // served because the cooldown is checked after the cache lookup.
        var time = new FakeTimeProvider();
        var providerUp = true;
        var fake = new FakeEmbeddingGenerator((inputs, _, _) =>
            providerUp ? CannedBatch(inputs) : throw SdkTransportFailure());
        var service = NewService(fake, timeProvider: time);

        var cached = await service.EmbedAsync("cached", CancellationToken.None); // provider up ⇒ cached
        providerUp = false;
        var tripped = await service.EmbedAsync("ping", CancellationToken.None);  // refused ⇒ cooldown
        var skipped = await service.EmbedAsync("pong", CancellationToken.None);  // inside the window
        var hit = await service.EmbedAsync("cached", CancellationToken.None);    // inside the window

        Assert.True(cached.Ok);
        Assert.Contains("actively refused", tripped.Error);
        Assert.False(skipped.Ok);
        Assert.Contains("cooling down", skipped.Error);
        Assert.Equal("test-embed-model", skipped.Model);
        Assert.True(hit.Ok);                     // a cache hit never needed the provider
        Assert.Equal(cached.Vector, hit.Vector);
        Assert.Equal(2, fake.CallCount);         // "cached" + "ping": "pong" never reached the provider
    }

    [Fact]
    public async Task EmbedAsync_AfterCooldown_CallsProviderAgain()
    {
        // The window is exactly FailureCooldownMs long, pinned from both sides: 1 ms before it ends the
        // provider is still skipped, 1 ms after it ends the next call tries the provider again. A
        // non-default length proves the configured value is the one honoured. That retry failing opens
        // a NEW window, so a router that stays down keeps costing one wait per window rather than going
        // back to one wait per call once the first window has run out.
        var time = new FakeTimeProvider();
        var fake = new FakeEmbeddingGenerator((_, _, _) => throw SdkTransportFailure());
        var service = NewService(
            fake,
            new EmbeddingOptions { Model = "test-embed-model", TimeoutMs = 5_000, FailureCooldownMs = 10_000 },
            timeProvider: time);

        await service.EmbedAsync("ping", CancellationToken.None);  // refused ⇒ cooldown starts
        time.Advance(TimeSpan.FromMilliseconds(9_999));
        var inside = await service.EmbedAsync("ping", CancellationToken.None);
        Assert.Contains("cooling down", inside.Error);
        Assert.Equal(1, fake.CallCount);                           // still inside the window

        time.Advance(TimeSpan.FromMilliseconds(2));                // FailureCooldownMs + 1
        var after = await service.EmbedAsync("ping", CancellationToken.None);

        Assert.False(after.Ok);
        Assert.Contains("actively refused", after.Error);          // a real attempt, not the cooldown
        Assert.Equal(2, fake.CallCount);                           // the provider was tried again

        var rearmed = await service.EmbedAsync("ping", CancellationToken.None);
        Assert.Contains("cooling down", rearmed.Error);            // the failed retry opened a new window
        Assert.Equal(2, fake.CallCount);
    }

    [Fact]
    public async Task EmbedAsync_Cooldown_WallClockStepBack_DoesNotStretchIt()
    {
        // The window is timed on the TimeProvider's monotonic timestamp, not its wall clock, so a wall
        // clock stepped back by NTP or by hand does not add the step to the window: once
        // FailureCooldownMs has really passed, the provider is tried again.
        var clock = new SteppableClock();
        var fake = new FakeEmbeddingGenerator((_, _, _) => throw SdkTransportFailure());
        var service = NewService(
            fake,
            new EmbeddingOptions { Model = "test-embed-model", TimeoutMs = 5_000, FailureCooldownMs = 10_000 },
            timeProvider: clock);

        await service.EmbedAsync("ping", CancellationToken.None);  // refused ⇒ cooldown starts
        clock.StepWallClock(TimeSpan.FromHours(-1));               // the wall clock jumps an hour back...
        clock.Advance(TimeSpan.FromMilliseconds(10_001));          // ...then FailureCooldownMs + 1 really passes
        var next = await service.EmbedAsync("ping", CancellationToken.None);

        Assert.Contains("actively refused", next.Error);           // a real attempt: the window ended on time
        Assert.Equal(2, fake.CallCount);
    }

    [Fact]
    public async Task EmbedAsync_Cooldown_WallClockStepAhead_DoesNotEndIt()
    {
        // The mirror: a wall clock stepped ahead does not end the window early. No real time passes
        // here, so the provider is still skipped.
        var clock = new SteppableClock();
        var fake = new FakeEmbeddingGenerator((_, _, _) => throw SdkTransportFailure());
        var service = NewService(
            fake,
            new EmbeddingOptions { Model = "test-embed-model", TimeoutMs = 5_000, FailureCooldownMs = 10_000 },
            timeProvider: clock);

        await service.EmbedAsync("ping", CancellationToken.None);  // refused ⇒ cooldown starts
        clock.StepWallClock(TimeSpan.FromHours(1));                // the wall clock jumps an hour ahead
        var next = await service.EmbedAsync("ping", CancellationToken.None);

        Assert.Contains("cooling down", next.Error);               // still inside the window
        Assert.Equal(1, fake.CallCount);
    }

    [Fact]
    public async Task Probe_BypassesCooldown_AndSuccessClearsIt()
    {
        // embed-ping must report LIVE provider state: inside the window the probe still makes a real
        // provider call, and a successful one ends the cooldown at once, so recovery shows up
        // immediately instead of up to FailureCooldownMs later.
        var time = new FakeTimeProvider();
        var providerUp = false;
        var fake = new FakeEmbeddingGenerator((inputs, _, _) =>
            providerUp ? CannedBatch(inputs) : throw SdkTransportFailure());
        var service = NewService(fake, timeProvider: time);

        await service.EmbedAsync("ping", CancellationToken.None);             // refused ⇒ cooldown starts
        var probeDown = await service.ProbeAsync("probe 1", CancellationToken.None);
        Assert.Contains("actively refused", probeDown.Error);                 // a real attempt, not "cooling down"
        Assert.Equal(2, fake.CallCount);

        providerUp = true;                                                    // the router comes back
        var probeUp = await service.ProbeAsync("probe 2", CancellationToken.None);
        var next = await service.EmbedAsync("next", CancellationToken.None);  // no time has passed

        Assert.True(probeUp.Ok);
        Assert.True(next.Ok);                                                 // the probe's success ended the cooldown
        Assert.Equal(4, fake.CallCount);
    }

    [Fact]
    public async Task EmbedAsync_CooldownZero_KeepsTodaysBehaviour()
    {
        // FailureCooldownMs = 0 disables the cooldown: every call goes to the provider, as before #549.
        var time = new FakeTimeProvider();
        var fake = new FakeEmbeddingGenerator((_, _, _) => throw SdkTransportFailure());
        var service = NewService(
            fake,
            new EmbeddingOptions { Model = "test-embed-model", TimeoutMs = 5_000, FailureCooldownMs = 0 },
            timeProvider: time);

        var first = await service.EmbedAsync("ping", CancellationToken.None);
        var second = await service.EmbedAsync("pong", CancellationToken.None);

        Assert.False(first.Ok);
        Assert.Contains("actively refused", second.Error); // a real attempt, not "cooling down"
        Assert.Equal(2, fake.CallCount);
    }

    [Fact]
    public async Task EmbedAsync_Timeout_StartsCooldown()
    {
        // A hung router is the worst case (two per-attempt timeouts per call), so a timeout is
        // provider-unavailable too: once both attempts time out, the next call fails soft at once.
        // The clock is a FakeTimeProvider, advanced only once each attempt is inside the generator.
        // Only "ping" hangs: if a regression let "pong" reach the provider it would come straight
        // back and fail the asserts, rather than hang on a clock nobody advances.
        var time = new FakeTimeProvider();
        using var enteredGenerator = new SemaphoreSlim(0);
        var fake = new FakeEmbeddingGenerator(async (inputs, _, token) =>
        {
            if (inputs[0] != "ping")
                return new GeneratedEmbeddings<Embedding<float>>(inputs.Select(Canned));
            enteredGenerator.Release();
            await Task.Delay(Timeout.Infinite, token); // hang until the service's timeout cancels us
            return new GeneratedEmbeddings<Embedding<float>>(new[] { Canned("x") });
        });
        var service = NewService(
            fake, new EmbeddingOptions { Model = "test-embed-model", TimeoutMs = 30 }, timeProvider: time);

        var call = service.EmbedAsync("ping", CancellationToken.None);
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            Assert.True(await enteredGenerator.WaitAsync(HangGuard), $"attempt {attempt} never reached the generator");
            time.Advance(TimeSpan.FromMilliseconds(31));
        }
        Assert.Contains("timed out", (await call).Error);

        var next = await service.EmbedAsync("pong", CancellationToken.None);

        Assert.Contains("cooling down", next.Error);
        Assert.Equal(2, fake.CallCount); // the hung provider was not waited on a third time
    }

    [Fact]
    public async Task EmbedAsync_NonProviderError_DoesNotStartCooldown()
    {
        // The pair to the tests above: an error the provider is not to blame for (here a local one; a
        // 4xx is classified the same way) says nothing about its availability, so it never starts the
        // cooldown and the next call still reaches the provider.
        var fake = new FakeEmbeddingGenerator((_, _, _) => throw new InvalidOperationException("bad embed shape"));
        var service = NewService(fake);

        await service.EmbedAsync("ping", CancellationToken.None);
        var second = await service.EmbedAsync("pong", CancellationToken.None);

        Assert.Contains("bad embed shape", second.Error); // a real attempt, not "cooling down"
        Assert.Equal(2, fake.CallCount);
    }

    [Fact]
    public async Task EmbedAsync_CallerCancelMidAttempt_DoesNotStartCooldown()
    {
        // A caller that gives up says nothing about the provider either: the cancel propagates and
        // starts no cooldown, so the next call still reaches the provider. The cancel lands while the
        // attempt is blocked inside the generator; a token cancelled before the call never reaches the
        // provider at all (EmbedAsync_CallerCancellation_Throws_NotSwallowedIntoResult). Nobody
        // advances the FakeTimeProvider, so the per-attempt timeout cannot fire: the only cancel here
        // is the caller's.
        using var enteredGenerator = new SemaphoreSlim(0);
        var fake = new FakeEmbeddingGenerator(async (inputs, _, token) =>
        {
            if (inputs[0] != "ping")
                return new GeneratedEmbeddings<Embedding<float>>(inputs.Select(Canned));
            enteredGenerator.Release();
            await Task.Delay(Timeout.Infinite, token); // blocked until the caller's cancel reaches us
            return new GeneratedEmbeddings<Embedding<float>>(new[] { Canned("x") });
        });
        var service = NewService(fake, timeProvider: new FakeTimeProvider());
        using var cts = new CancellationTokenSource();

        var call = service.EmbedAsync("ping", cts.Token);
        Assert.True(await enteredGenerator.WaitAsync(HangGuard), "the attempt never reached the generator");
        Assert.Equal(1, fake.CallCount); // the attempt had started before the cancel
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);

        var next = await service.EmbedAsync("pong", CancellationToken.None);

        Assert.True(next.Ok);            // reached the provider: the cancel started no cooldown
        Assert.Equal(2, fake.CallCount);
    }

    [Fact]
    public async Task EmbedBatchAsync_WithinCooldown_ServesCacheHits_AndSparesLaterChunks()
    {
        // EmbedBatchAsync honours the same window, checked per chunk: the chunk that trips the cooldown
        // spares the chunks after it the same wait, while a cache hit captured up front is still served.
        var time = new FakeTimeProvider();
        var providerUp = true;
        var fake = new FakeEmbeddingGenerator((inputs, _, _) =>
            providerUp ? CannedBatch(inputs) : throw SdkTransportFailure());
        // Batch size 1 ⇒ one chunk per miss, so the window can act BETWEEN chunks.
        var service = NewService(
            fake, new EmbeddingOptions { Model = "m", TimeoutMs = 5_000, MaxBatchSize = 1 }, timeProvider: time);

        await service.EmbedAsync("cached", CancellationToken.None); // provider up ⇒ cached
        providerUp = false;

        var results = await service.EmbedBatchAsync(new[] { "cached", "a", "b" }, CancellationToken.None);

        Assert.True(results[0].Ok);                            // cache hit still served
        Assert.Contains("actively refused", results[1].Error); // chunk "a" reached the provider and tripped it...
        Assert.Contains("cooling down", results[2].Error);     // ...so chunk "b" skipped the provider
        Assert.Equal(2, fake.CallCount);                       // "cached" + chunk "a" only
    }

    // ── input guards ─────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmbedAsync_BlankText_Throws(string? text)
    {
        var fake = new FakeEmbeddingGenerator((inputs, _, _) => CannedBatch(inputs));
        var service = NewService(fake);

        await Assert.ThrowsAsync<ArgumentException>(() => service.EmbedAsync(text!, CancellationToken.None));
        Assert.Equal(0, fake.CallCount); // guarded locally ⇒ provider never called
    }

    [Fact]
    public async Task EmbedBatchAsync_NullList_Throws()
    {
        var service = NewService(new FakeEmbeddingGenerator((inputs, _, _) => CannedBatch(inputs)));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.EmbedBatchAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task EmbedBatchAsync_NullElement_Throws()
    {
        var fake = new FakeEmbeddingGenerator((inputs, _, _) => CannedBatch(inputs));
        var service = NewService(fake);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.EmbedBatchAsync(new[] { "a", null! }, CancellationToken.None));
        Assert.Equal(0, fake.CallCount); // validated before any provider call
    }

    // ── dimensionality passthrough ───────────────────────────────────────────────────
    [Fact]
    public async Task EmbedAsync_ConfiguredDimensions_AreRequestedFromProvider()
    {
        var fake = new FakeEmbeddingGenerator((inputs, _, _) => CannedBatch(inputs));
        var service = NewService(fake, new EmbeddingOptions { Model = "m", TimeoutMs = 5_000, Dimensions = 16 });

        await service.EmbedAsync("x", CancellationToken.None);

        Assert.NotNull(fake.LastOptions);
        Assert.Equal(16, fake.LastOptions!.Dimensions);
    }

    [Fact]
    public async Task EmbedAsync_NoConfiguredDimensions_SendsNoGenerationOptions()
    {
        var fake = new FakeEmbeddingGenerator((inputs, _, _) => CannedBatch(inputs));
        var service = NewService(fake); // no Dimensions configured

        await service.EmbedAsync("x", CancellationToken.None);

        Assert.Null(fake.LastOptions); // null ⇒ provider-native dimensionality
    }

    // ── cache-key (pure function) ────────────────────────────────────────────────────
    [Fact]
    public void ComputeCacheKey_SameModelAndText_IsStable() =>
        Assert.Equal(
            EmbeddingService.ComputeCacheKey("model-a", "the text"),
            EmbeddingService.ComputeCacheKey("model-a", "the text"));

    [Fact]
    public void ComputeCacheKey_DifferentText_DiffersKey() =>
        Assert.NotEqual(
            EmbeddingService.ComputeCacheKey("model-a", "text one"),
            EmbeddingService.ComputeCacheKey("model-a", "text two"));

    [Fact]
    public void ComputeCacheKey_DifferentModel_DiffersKey() =>
        // Model is part of the key ⇒ swapping the embed model never reuses a stale vector.
        Assert.NotEqual(
            EmbeddingService.ComputeCacheKey("model-a", "same text"),
            EmbeddingService.ComputeCacheKey("model-b", "same text"));

    // ── constructor validation ───────────────────────────────────────────────────────
    [Fact]
    public void Constructor_NullGenerator_Throws() =>
        Assert.Throws<ArgumentNullException>(
            () => new EmbeddingService(null!, new EmbeddingOptions(), NullLogger<EmbeddingService>.Instance));

    [Fact]
    public void Constructor_NullConfig_Throws()
    {
        var fake = new FakeEmbeddingGenerator((inputs, _, _) => CannedBatch(inputs));
        Assert.Throws<ArgumentNullException>(
            () => new EmbeddingService(fake, null!, NullLogger<EmbeddingService>.Instance));
    }

    [Fact]
    public void Constructor_NonPositiveTimeout_Throws()
    {
        var fake = new FakeEmbeddingGenerator((inputs, _, _) => CannedBatch(inputs));
        Assert.Throws<ArgumentException>(
            () => new EmbeddingService(fake, new EmbeddingOptions { TimeoutMs = 0 }, NullLogger<EmbeddingService>.Instance));
    }

    [Fact]
    public void Constructor_NonPositiveBatchSize_Throws()
    {
        var fake = new FakeEmbeddingGenerator((inputs, _, _) => CannedBatch(inputs));
        Assert.Throws<ArgumentException>(
            () => new EmbeddingService(fake, new EmbeddingOptions { MaxBatchSize = 0 }, NullLogger<EmbeddingService>.Instance));
    }

    [Fact]
    public void Constructor_NegativeCooldown_Throws()
    {
        // 0 is valid (it disables the cooldown); only a negative window is a config error.
        var fake = new FakeEmbeddingGenerator((inputs, _, _) => CannedBatch(inputs));
        Assert.Throws<ArgumentException>(
            () => new EmbeddingService(fake, new EmbeddingOptions { FailureCooldownMs = -1 }, NullLogger<EmbeddingService>.Instance));
    }

    // ── EmbeddingOptions.FromEnvironment binding ─────────────────────────────────────
    [Fact]
    public void FromEnvironment_NoVarsSet_UsesDefaults()
    {
        var options = EmbeddingOptions.FromEnvironment(_ => null);

        Assert.Equal(EmbeddingOptions.DefaultModel, options.Model);
        Assert.Equal(EmbeddingOptions.DefaultMaxBatchSize, options.MaxBatchSize);
        Assert.Equal(EmbeddingOptions.DefaultFailureCooldownMs, options.FailureCooldownMs);
        Assert.Null(options.Dimensions);
        // URL/key/timeout are inherited from the shared LlmOptions binding.
        Assert.Equal(LlmOptions.DefaultBaseUrl, options.BaseUrl);
        Assert.Equal(LlmOptions.DefaultApiKey, options.ApiKey);
        Assert.Equal(LlmOptions.DefaultTimeoutMs, options.TimeoutMs);
        Assert.EndsWith("/v1", options.BaseUrl);
    }

    [Fact]
    public void FromEnvironment_EmbedModelOverride_IsHonored()
    {
        var env = new Dictionary<string, string?> { ["KANBANFLOW_EMBED_MODEL"] = "nomic-embed-text" };
        var options = EmbeddingOptions.FromEnvironment(k => env.GetValueOrDefault(k));
        Assert.Equal("nomic-embed-text", options.Model);
    }

    [Fact]
    public void FromEnvironment_ReusesSharedUrlAndKeyPrecedence()
    {
        // Proves the URL/key come from the SAME binder as chat (#218): ZEROROUTER_* wins, /v1 kept.
        var env = new Dictionary<string, string?>
        {
            ["ZEROROUTER_URL"] = "http://zero:5256/v1",
            ["KANBANFLOW_LLM_URL"] = "http://alias:9000/v1",
            ["ZEROROUTER_API_KEY"] = "sk-zero",
            ["KANBANFLOW_LLM_TIMEOUT_MS"] = "4321",
        };
        var options = EmbeddingOptions.FromEnvironment(k => env.GetValueOrDefault(k));

        Assert.Equal("http://zero:5256/v1", options.BaseUrl);
        Assert.Equal("sk-zero", options.ApiKey);
        Assert.Equal(4321, options.TimeoutMs);
    }

    [Fact]
    public void FromEnvironment_UrlMissingV1Suffix_AppendsV1_ViaSharedNormalization()
    {
        var env = new Dictionary<string, string?> { ["ZEROROUTER_URL"] = "http://host:1234" };
        var options = EmbeddingOptions.FromEnvironment(k => env.GetValueOrDefault(k));
        Assert.Equal("http://host:1234/v1", options.BaseUrl);
    }

    [Theory]
    [InlineData("768", 768)]
    [InlineData("1536", 1536)]
    public void FromEnvironment_ValidDimensions_AreParsed(string raw, int expected)
    {
        var env = new Dictionary<string, string?> { ["KANBANFLOW_EMBED_DIMENSIONS"] = raw };
        var options = EmbeddingOptions.FromEnvironment(k => env.GetValueOrDefault(k));
        Assert.Equal(expected, options.Dimensions);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("not-a-number")]
    public void FromEnvironment_InvalidDimensions_StayNull(string raw)
    {
        var env = new Dictionary<string, string?> { ["KANBANFLOW_EMBED_DIMENSIONS"] = raw };
        var options = EmbeddingOptions.FromEnvironment(k => env.GetValueOrDefault(k));
        Assert.Null(options.Dimensions);
    }

    [Fact]
    public void FromEnvironment_ValidBatchSize_IsParsed_InvalidKeepsDefault()
    {
        var good = EmbeddingOptions.FromEnvironment(
            k => new Dictionary<string, string?> { ["KANBANFLOW_EMBED_BATCH_SIZE"] = "32" }.GetValueOrDefault(k));
        Assert.Equal(32, good.MaxBatchSize);

        var bad = EmbeddingOptions.FromEnvironment(
            k => new Dictionary<string, string?> { ["KANBANFLOW_EMBED_BATCH_SIZE"] = "0" }.GetValueOrDefault(k));
        Assert.Equal(EmbeddingOptions.DefaultMaxBatchSize, bad.MaxBatchSize);
    }

    [Theory]
    [InlineData("5000", 5_000)]
    [InlineData("0", 0)] // 0 is a real setting, not an invalid one: it disables the cooldown
    [InlineData("-1", EmbeddingOptions.DefaultFailureCooldownMs)]
    [InlineData("not-a-number", EmbeddingOptions.DefaultFailureCooldownMs)]
    public void FromEnvironment_CooldownMs_IsParsed_InvalidKeepsDefault(string raw, int expected)
    {
        var env = new Dictionary<string, string?> { ["KANBANFLOW_EMBED_COOLDOWN_MS"] = raw };
        var options = EmbeddingOptions.FromEnvironment(k => env.GetValueOrDefault(k));
        Assert.Equal(expected, options.FailureCooldownMs);
    }

    [Fact]
    public void FromEnvironment_NullGetEnv_Throws() =>
        Assert.Throws<ArgumentNullException>(() => EmbeddingOptions.FromEnvironment(null!));

    /// <summary>
    /// Minimal hand-written <c>IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt;</c> double:
    /// routes <see cref="GenerateAsync"/> to a supplied handler and records call count + the inputs
    /// and options it last saw (and every input across calls). No Moq — the real wrapper logic runs.
    /// </summary>
    private sealed class FakeEmbeddingGenerator(
        Func<IReadOnlyList<string>, EmbeddingGenerationOptions?, CancellationToken, Task<GeneratedEmbeddings<Embedding<float>>>> handler)
        : IEmbeddingGenerator<string, Embedding<float>>
    {
        public int CallCount { get; private set; }
        public List<string> AllRequested { get; } = new();
        public IReadOnlyList<string>? LastRequested { get; private set; }
        public EmbeddingGenerationOptions? LastOptions { get; private set; }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = values.ToList();
            CallCount++;
            AllRequested.AddRange(list);
            LastRequested = list;
            LastOptions = options;
            return handler(list, options, cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>
    /// A <see cref="TimeProvider"/> whose wall clock can be stepped without moving its monotonic
    /// timestamp, the way NTP or a manual change steps a real clock; <see cref="Advance"/> moves both,
    /// like real time passing. Its timers are the base class's real ones, which no test here waits on.
    /// </summary>
    private sealed class SteppableClock : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private long _timestamp = TimeSpan.TicksPerDay; // any positive start, as a real one has

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override long GetTimestamp() => _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan by)
        {
            _utcNow += by;
            _timestamp += by.Ticks;
        }

        public void StepWallClock(TimeSpan by) => _utcNow += by;
    }
}
