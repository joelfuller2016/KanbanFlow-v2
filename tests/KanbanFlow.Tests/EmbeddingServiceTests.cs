using KanbanFlow.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

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

    private static EmbeddingService NewService(
        IEmbeddingGenerator<string, Embedding<float>> generator, EmbeddingOptions? config = null, int? capacity = null)
    {
        config ??= new EmbeddingOptions { Model = "test-embed-model", TimeoutMs = 5_000 };
        return capacity is { } c
            ? new EmbeddingService(generator, config, NullLogger<EmbeddingService>.Instance, c)
            : new EmbeddingService(generator, config, NullLogger<EmbeddingService>.Instance);
    }

    // Canned, content-derived 3-dim vector: identical text ⇒ identical vector; different text ⇒
    // different vector. Lets tests assert both the mapping and that dedup/caching short-circuits.
    private static Embedding<float> Canned(string text) =>
        new(new[] { (float)text.Length, text.Length > 0 ? text[0] : 0f, 3.5f }) { ModelId = FakeModel };

    private static Task<GeneratedEmbeddings<Embedding<float>>> CannedBatch(IReadOnlyList<string> inputs) =>
        Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(inputs.Select(Canned)));

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
        var fake = new FakeEmbeddingGenerator((_, _, _) => throw new HttpRequestException("connection refused"));
        var service = NewService(fake);

        var result = await service.EmbedAsync("ping", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Equal("test-embed-model", result.Model);
        Assert.Equal(2, fake.CallCount); // 1 attempt + 1 retry on a transient error
    }

    [Fact]
    public async Task EmbedAsync_Timeout_IsTreatedAsTransient_RetriesThenFailsSoft()
    {
        // The fake blocks until its (linked) token is cancelled; the tiny per-attempt timeout fires
        // and surfaces as a cancellation the wrapper classifies as transient — NOT a caller cancel,
        // since the caller token below is never cancelled.
        var fake = new FakeEmbeddingGenerator(async (_, _, token) =>
        {
            var wait = Task.Delay(Timeout.Infinite, token);
            var watchdog = Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None);
            if (await Task.WhenAny(wait, watchdog) == watchdog)
                throw new TimeoutException("Test watchdog: EmbeddingService did not cancel the attempt token.");
            await wait;
            return new GeneratedEmbeddings<Embedding<float>>(new[] { Canned("x") });
        });
        var service = NewService(fake, new EmbeddingOptions { Model = "test-embed-model", TimeoutMs = 30 });

        var result = await service.EmbedAsync("ping", CancellationToken.None);

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

    // ── EmbeddingOptions.FromEnvironment binding ─────────────────────────────────────
    [Fact]
    public void FromEnvironment_NoVarsSet_UsesDefaults()
    {
        var options = EmbeddingOptions.FromEnvironment(_ => null);

        Assert.Equal(EmbeddingOptions.DefaultModel, options.Model);
        Assert.Equal(EmbeddingOptions.DefaultMaxBatchSize, options.MaxBatchSize);
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
}
