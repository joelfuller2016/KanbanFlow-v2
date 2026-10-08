using System.ClientModel;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;

namespace KanbanFlow.Services;

/// <summary>
/// Default <see cref="IEmbeddingService"/>: a thin, fail-soft wrapper over an injected
/// <c>IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt;</c> (built from the OpenAI connector
/// pointed at ZeroRouter — see <c>Program.cs</c>). It adds a content-hash cache (identical text is
/// embedded once), batch dedup + provider-safe chunking, a per-attempt timeout with a single retry
/// on transient transport failures, a failure cooldown so a down provider does not charge every call
/// its full failure wait (#549), and maps any provider failure to a typed
/// <see cref="EmbedResult"/> rather than throwing (#219). Stateless apart from the cache and the
/// cooldown timestamp, and thread-safe, so it is registered as a singleton.
/// </summary>
public sealed class EmbeddingService : IEmbeddingService
{
    // 1 initial attempt + 1 retry. The retry only fires for transient failures (see IsTransient).
    // Mirrors LlmClient's policy (#218) so chat + embeddings behave identically against a flaky
    // provider; the helper set (IsTransient/Describe/Unwrap) is duplicated rather than shared to
    // keep #219 from refactoring the merged, tested #218 surface. The failure cooldown (#549) is
    // embeddings-only; LlmClient keeps its own policy.
    private const int MaxAttempts = 2;

    /// <summary>Default cap on the number of cached vectors before oldest-first eviction kicks in.</summary>
    private const int DefaultCacheCapacity = 8192;

    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;
    private readonly EmbeddingOptions _config;
    private readonly ILogger<EmbeddingService> _logger;
    // TimeProvider.System in production; a FakeTimeProvider in tests so the per-attempt timeout is
    // deterministic rather than a wall-clock race the thread pool can lose under load (#262). Its
    // monotonic timestamp is also the failure cooldown's clock (#549).
    private readonly TimeProvider _timeProvider;
    private readonly EmbeddingGenerationOptions? _generationOptions;

    // Failure cooldown (#549): the _timeProvider.GetTimestamp() value until which provider calls are
    // skipped because the provider was just unreachable, timed out or returned a 5xx; 0 = none. A
    // monotonic timestamp rather than the wall clock, so a clock stepped by NTP or by hand can neither
    // stretch the window nor cut it short. One long, read and written with Interlocked, so the hot
    // path takes no lock.
    private long _unavailableUntilTimestamp;

    // Bounded, thread-safe content-hash → (vector + provider-reported model) cache. The dictionary
    // keeps reads lock-free on the hot path; the parallel queue records insertion order so we can
    // trim oldest-first once the dictionary exceeds capacity, giving a hard memory bound under a
    // stream of distinct text. An approximate FIFO (not strict LRU) is plenty for this single-user
    // local tool: a key re-added after eviction may be dropped a little early, which only costs one
    // recompute, never correctness. The cached array is owned by the cache and never handed out —
    // every read returns a defensive copy (see Clone), so a caller mutating EmbedResult.Vector can
    // never poison a future hit.
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _insertionOrder = new();
    private readonly int _capacity;

    /// <summary>
    /// Production constructor used by DI.
    /// </summary>
    /// <param name="generator">Underlying embedding generator (the OpenAI connector pointed at ZeroRouter).</param>
    /// <param name="config">Connection + model settings; <see cref="EmbeddingOptions.TimeoutMs"/> and <see cref="EmbeddingOptions.MaxBatchSize"/> must be &gt; 0, <see cref="EmbeddingOptions.FailureCooldownMs"/> &gt;= 0.</param>
    /// <param name="logger">Logger for transient-retry and fail-soft diagnostics.</param>
    public EmbeddingService(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        EmbeddingOptions config,
        ILogger<EmbeddingService> logger)
        : this(generator, config, logger, DefaultCacheCapacity)
    {
    }

    // Test seam (InternalsVisibleTo = KanbanFlow.Tests): lets a test pick a tiny cache capacity to
    // prove eviction is real without embedding thousands of strings, and supply a fake TimeProvider
    // so the per-attempt timeout fires deterministically instead of racing the wall clock (#262) and
    // the failure cooldown (#549) ends exactly when the test advances the clock past it.
    internal EmbeddingService(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        EmbeddingOptions config,
        ILogger<EmbeddingService> logger,
        int cacheCapacity,
        TimeProvider? timeProvider = null)
    {
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
        _config = ValidateConfig(config);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _capacity = cacheCapacity > 0 ? cacheCapacity : DefaultCacheCapacity;
        _timeProvider = timeProvider ?? TimeProvider.System;

        // Only send generation options when there is something to override. A configured (> 0)
        // dimensionality is requested from the provider; otherwise null ⇒ provider-native size.
        _generationOptions = _config.Dimensions is > 0
            ? new EmbeddingGenerationOptions { Dimensions = _config.Dimensions }
            : null;
    }

    private static EmbeddingOptions ValidateConfig(EmbeddingOptions config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.TimeoutMs <= 0)
            throw new ArgumentException("TimeoutMs must be greater than zero.", nameof(config));
        if (config.MaxBatchSize <= 0)
            throw new ArgumentException("MaxBatchSize must be greater than zero.", nameof(config));
        if (config.FailureCooldownMs < 0)
            throw new ArgumentException("FailureCooldownMs must not be negative.", nameof(config));
        return config;
    }

    /// <inheritdoc />
    public Task<EmbedResult> EmbedAsync(string text, CancellationToken ct = default) =>
        EmbedCoreAsync(text, bypassCooldown: false, ct);

    /// <inheritdoc />
    public Task<EmbedResult> ProbeAsync(string text, CancellationToken ct = default) =>
        EmbedCoreAsync(text, bypassCooldown: true, ct);

    private async Task<EmbedResult> EmbedCoreAsync(string text, bool bypassCooldown, CancellationToken ct)
    {
        // Reject blank input locally (fail fast) rather than wasting a remote no-op on empty text —
        // mirrors LlmClient's user-prompt guard.
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(text));

        var key = CacheKey(text);
        if (_cache.TryGetValue(key, out var cached))
            // Return the provider-reported model the hit was stored with (consistent with a fresh
            // result) and a defensive copy so the caller can't mutate the cached array.
            return EmbedResult.Success(Clone(cached.Vector), cached.Model);

        // Honour caller cancellation before the cooldown check below: its early return would turn a
        // cancelled call into a fail-soft result, and GenerateAsync's own check never runs on that path.
        ct.ThrowIfCancellationRequested();

        // Inside the failure cooldown the provider was just seen down: fail soft now rather than pay
        // the same refused-connect or timeout wait again (#549). The probe skips this check so
        // embed-ping always reports live provider state.
        if (!bypassCooldown && CooldownError() is { } coolingDown)
            return EmbedResult.Failure(coolingDown, _config.Model);

        var outcome = await GenerateAsync(new[] { text }, ct).ConfigureAwait(false);
        if (!outcome.Ok)
            return EmbedResult.Failure(outcome.Error!, outcome.Model);

        var vector = outcome.Vectors![0];
        CacheStore(key, vector, outcome.Model);
        return EmbedResult.Success(Clone(vector), outcome.Model);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EmbedResult>> EmbedBatchAsync(
        IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count == 0)
            return Array.Empty<EmbedResult>();

        // One result slot per input, filled deterministically so every index gets a defined result
        // regardless of concurrent cache eviction (#251 F1). Slots are populated three ways below: a
        // blank short-circuit, an early-captured cache hit, or this run's fresh/failed embeddings.
        var results = new EmbedResult?[texts.Count];
        var keys = new string[texts.Count];
        var missTextByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        var missOrder = new List<string>();

        // Classify every input up-front, before any provider call:
        //   • null element → caller bug (a null in a typed string batch) → fail fast.
        //   • blank/space  → fail soft locally and DO NOT send it to the provider (#251 F3): a 4xx on
        //                    one blank would otherwise sink the whole chunk and its batch-mates.
        //   • cache hit    → capture its vector + model NOW (defensive copy), so a later eviction
        //                    can't leave a hole or return a wrong value (#251 F1/F2/F4).
        //   • otherwise    → a miss, deduped by key in first-seen order (embedded exactly once).
        for (var i = 0; i < texts.Count; i++)
        {
            var text = texts[i];
            if (text is null)
                throw new ArgumentException($"Batch element [{i}] is null.", nameof(texts));

            if (string.IsNullOrWhiteSpace(text))
            {
                results[i] = EmbedResult.Failure("Cannot embed empty or whitespace input.", _config.Model);
                continue;
            }

            var key = CacheKey(text);
            keys[i] = key;
            if (_cache.TryGetValue(key, out var hit))
            {
                results[i] = EmbedResult.Success(Clone(hit.Vector), hit.Model);
                continue;
            }

            if (missTextByKey.TryAdd(key, text))
                missOrder.Add(key);
        }

        // Embed the misses in provider-safe chunks. A chunk that fails (after its retry) fails soft:
        // its keys get an error and are NOT cached, while successful chunks + captured hits are
        // unaffected. Capture each fresh (vector, model) here keyed by content hash; we assemble from
        // THIS run's results, never by re-reading the cache, so eviction can't fake a failure (F1).
        var freshByKey = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
        var errorByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var start = 0; start < missOrder.Count; start += _config.MaxBatchSize)
        {
            // Honour caller cancellation first, as GenerateAsync does: the cooldown check below would
            // otherwise fail a cancelled chunk soft instead of letting the cancellation propagate.
            ct.ThrowIfCancellationRequested();

            var chunkKeys = missOrder.GetRange(start, Math.Min(_config.MaxBatchSize, missOrder.Count - start));

            // Inside the failure cooldown (#549) the chunk fails soft without a provider call. Checked
            // per chunk, so a chunk whose failure starts the cooldown spares the chunks after it.
            if (CooldownError() is { } coolingDown)
            {
                foreach (var chunkKey in chunkKeys)
                    errorByKey[chunkKey] = coolingDown;
                continue;
            }

            var chunkTexts = new string[chunkKeys.Count];
            for (var j = 0; j < chunkKeys.Count; j++)
                chunkTexts[j] = missTextByKey[chunkKeys[j]];

            var outcome = await GenerateAsync(chunkTexts, ct).ConfigureAwait(false);
            if (outcome.Ok)
            {
                for (var j = 0; j < chunkKeys.Count; j++)
                {
                    var vector = outcome.Vectors![j];
                    CacheStore(chunkKeys[j], vector, outcome.Model);
                    freshByKey[chunkKeys[j]] = new CacheEntry(vector, outcome.Model);
                }
            }
            else
            {
                foreach (var chunkKey in chunkKeys)
                    errorByKey[chunkKey] = outcome.Error!;
            }
        }

        // Fill the remaining (miss) slots from this run's results — a fresh success (defensive copy)
        // or the typed chunk error. Blank short-circuits and captured hits are already populated.
        for (var i = 0; i < texts.Count; i++)
        {
            if (results[i] is not null)
                continue;

            var key = keys[i];
            results[i] = freshByKey.TryGetValue(key, out var fresh)
                ? EmbedResult.Success(Clone(fresh.Vector), fresh.Model)
                : EmbedResult.Failure(errorByKey.GetValueOrDefault(key) ?? "Embedding failed.", _config.Model);
        }
        return results!;
    }

    /// <summary>
    /// One provider call for a set of inputs, with the per-attempt timeout + single transient retry +
    /// fail-soft mapping (mirrors <see cref="LlmClient"/>). Returns vectors aligned 1:1 with
    /// <paramref name="inputs"/> on success, or a typed failure; never throws except on caller cancel.
    /// </summary>
    private async Task<GenerateOutcome> GenerateAsync(IReadOnlyList<string> inputs, CancellationToken ct)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            // Honor caller cancellation up-front so a cancelled token never starts an attempt.
            ct.ThrowIfCancellationRequested();

            // Per-attempt timeout, linked to the caller's token so caller-cancel still wins the race.
            // Driven by _timeProvider (TimeProvider.System in production) so a test can advance the
            // clock and fire this deterministically rather than waiting on a real 30ms that thread-pool
            // starvation can delay past the test's own patience (#262).
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(_config.TimeoutMs), _timeProvider);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            try
            {
                var generated = await _generator
                    .GenerateAsync(inputs, _generationOptions, linkedCts.Token)
                    .ConfigureAwait(false);

                // Guard the index alignment the batch path relies on: the provider must return one
                // vector per input. A mismatch is treated as a fail-soft error, not silently truncated.
                if (generated.Count != inputs.Count)
                {
                    return GenerateOutcome.Fail(
                        $"Provider returned {generated.Count} embeddings for {inputs.Count} inputs.", _config.Model);
                }

                var vectors = new float[inputs.Count][];
                string? model = null;
                for (var i = 0; i < generated.Count; i++)
                {
                    vectors[i] = generated[i].Vector.ToArray();
                    model ??= generated[i].ModelId;
                }

                // Any success proves the provider is back, so end a running failure cooldown now
                // rather than when it expires (#549); this is how a successful embed-ping clears it.
                Interlocked.Exchange(ref _unavailableUntilTimestamp, 0);
                return GenerateOutcome.Succeed(vectors, model ?? _config.Model);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The CALLER cancelled — propagate (cancellation is honored, not turned into a result).
                throw;
            }
            catch (Exception ex) when (attempt < MaxAttempts && IsTransient(ex))
            {
                // Transient (our timeout / transport blip / 5xx): record it and retry once.
                lastError = ex;
                _logger.LogWarning(
                    ex, "Embedding attempt {Attempt}/{Max} failed (transient); retrying.", attempt, MaxAttempts);
            }
            catch (Exception ex)
            {
                // Non-transient, or the final attempt failed: fail soft with a typed result.
                _logger.LogWarning(ex, "Embedding failed (model {Model}); returning fail-soft result.", _config.Model);
                // A provider that could not be reached or could not serve starts the failure cooldown,
                // so the calls behind this one fail fast instead of each paying the same wait (#549).
                if (IsProviderUnavailable(ex))
                    StartCooldown();
                return GenerateOutcome.Fail(Describe(ex), _config.Model);
            }
        }

        // Compiler-required fallback; unreachable in practice — the final-attempt catch always
        // returns before the loop exits normally.
        return GenerateOutcome.Fail(Describe(lastError), _config.Model);
    }

    private string CacheKey(string text) => ComputeCacheKey(_config.Model, text);

    /// <summary>
    /// Content-hash cache key: SHA-256 of <c>model + "\n" + text</c>, hex-encoded. The model is part
    /// of the key so swapping the embed model never reuses a stale vector of a different
    /// dimensionality. Pure + deterministic; <c>internal</c> so it is unit-testable directly.
    /// </summary>
    internal static string ComputeCacheKey(string model, string text)
    {
        var payload = Encoding.UTF8.GetBytes($"{model}\n{text}");
        return Convert.ToHexString(SHA256.HashData(payload));
    }

    private void CacheStore(string key, float[] vector, string model)
    {
        // First writer wins; a concurrent duplicate add is a no-op (the vector is identical for the
        // same key anyway). Only a genuinely new key advances the eviction queue + count. The cache
        // takes ownership of `vector` — callers always receive a Clone, never this instance.
        if (!_cache.TryAdd(key, new CacheEntry(vector, model)))
            return;
        _insertionOrder.Enqueue(key);
        while (_cache.Count > _capacity && _insertionOrder.TryDequeue(out var oldest))
            _cache.TryRemove(oldest, out _);
    }

    /// <summary>
    /// Defensive copy of a cached vector so a caller mutating <see cref="EmbedResult.Vector"/> can
    /// never poison the array the cache holds (and therefore the next hit) (#251 F4).
    /// </summary>
    private static float[] Clone(float[] vector) => (float[])vector.Clone();

    /// <summary>
    /// Start (or extend) the failure cooldown after a provider-unavailable failure (#549): until it
    /// ends, <see cref="EmbedAsync"/> and <see cref="EmbedBatchAsync"/> fail soft without calling the
    /// provider. Logged once per window, not per call: a failure that lands inside a window already
    /// running (a concurrent call, or the probe) extends it silently.
    /// </summary>
    private void StartCooldown()
    {
        if (_config.FailureCooldownMs == 0)
            return; // disabled: every call goes to the provider, as before #549

        // The deadline is on the monotonic timestamp; the wall clock only renders it for the log.
        var now = _timeProvider.GetTimestamp();
        var until = now + _config.FailureCooldownMs * _timeProvider.TimestampFrequency / 1000;
        var previous = Interlocked.Exchange(ref _unavailableUntilTimestamp, until);
        if (previous <= now)
        {
            _logger.LogWarning(
                "Embedding provider unavailable; cooling down until {Until:O}. Embedding calls fail soft without calling the provider for {CooldownMs} ms.",
                _timeProvider.GetUtcNow().AddMilliseconds(_config.FailureCooldownMs), _config.FailureCooldownMs);
        }
    }

    /// <summary>
    /// The fail-soft error for a call made inside the failure cooldown, which must not call the
    /// provider, or <c>null</c> when no cooldown is running (#549). A lock-free read of one long.
    /// </summary>
    private string? CooldownError()
    {
        var until = Interlocked.Read(ref _unavailableUntilTimestamp);
        var now = _timeProvider.GetTimestamp();
        if (now >= until)
            return null;

        // Decided on the monotonic timestamp above; the wall clock only renders the end for the reader.
        var endsAt = _timeProvider.GetUtcNow() + _timeProvider.GetElapsedTime(now, until);
        return $"Embedding provider unavailable; cooling down until {endsAt:O}.";
    }

    /// <summary>
    /// Transient = worth a single retry: a request timeout, a raw transport exception, or a 5xx from
    /// the provider. NOT transient (fail fast): a 4xx (bad model / malformed request) and a
    /// <see cref="ClientResultException"/> with <c>Status == 0</c> (no HTTP response — connection
    /// refused / DNS / TLS), where an immediate retry almost never helps. Mirrors <see cref="LlmClient"/>.
    /// </summary>
    private static bool IsTransient(Exception ex) => Unwrap(ex) switch
    {
        HttpRequestException => true,
        // Our per-attempt timeout surfaces as a cancellation here (caller-cancel is caught earlier).
        OperationCanceledException => true,
        // The SDK's own transport failure: a request that got no HTTP response, its
        // HttpRequestException wrapped by the pipeline. It is what a refused connect to a down router
        // really throws. Deliberately not retried: a retry doubles a refused-connect cost (~4 s on
        // Windows) that the failure cooldown already bounds, so IsProviderUnavailable sends it there (#549).
        ClientResultException { InnerException: HttpRequestException } => false,
        ClientResultException { Status: >= 500 } => true,
        ClientResultException => false,
        _ => false,
    };

    /// <summary>
    /// Provider-unavailable = the provider could not be reached or could not serve the request: every
    /// transient failure (a transport failure, our timeout, a 5xx) plus the SDK's wrapped transport
    /// failure, which <see cref="IsTransient"/> deliberately does not retry. Starts the failure
    /// cooldown (#549). NOT unavailable: a 4xx or a local error, where the provider answered (or was
    /// never the problem) and asking again is cheap.
    /// </summary>
    private static bool IsProviderUnavailable(Exception ex) =>
        IsTransient(ex) || Unwrap(ex) is ClientResultException { InnerException: HttpRequestException };

    /// <summary>Human-readable, log-safe description of a provider failure for <see cref="EmbedResult.Error"/>.</summary>
    private static string Describe(Exception? ex) => (ex is null ? null : Unwrap(ex)) switch
    {
        null => "Unknown embedding error.",
        ClientResultException cre => $"Embedding provider returned HTTP {cre.Status}: {cre.Message}",
        OperationCanceledException => "Embedding request timed out.",
        HttpRequestException hre => $"Embedding provider unreachable: {hre.Message}",
        var e => $"{e.GetType().Name}: {e.Message}",
    };

    /// <summary>
    /// Flatten an <see cref="AggregateException"/> to its most meaningful inner exception so transport
    /// failures classify and read cleanly; other exceptions pass through unchanged.
    /// </summary>
    private static Exception Unwrap(Exception ex) =>
        ex is AggregateException ae ? ae.Flatten().InnerException ?? ae : ex;

    /// <summary>
    /// A cached embedding: the vector plus the model the provider reported when it was produced, so a
    /// cache hit returns the same model a fresh result would (#251 F2). The vector is owned by the
    /// cache; reads hand out a <see cref="Clone(float[])"/>, never this instance (#251 F4).
    /// </summary>
    private readonly record struct CacheEntry(float[] Vector, string Model);

    /// <summary>Internal outcome of a single provider call: aligned vectors on success, or a typed error.</summary>
    private readonly record struct GenerateOutcome(bool Ok, float[][]? Vectors, string Model, string? Error)
    {
        public static GenerateOutcome Succeed(float[][] vectors, string model) => new(true, vectors, model, null);
        public static GenerateOutcome Fail(string error, string model) => new(false, null, model, error);
    }
}
