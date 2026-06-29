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
/// on transient transport failures, and maps any provider failure to a typed
/// <see cref="EmbedResult"/> rather than throwing (#219). Stateless apart from the cache and
/// thread-safe, so it is registered as a singleton.
/// </summary>
public sealed class EmbeddingService : IEmbeddingService
{
    // 1 initial attempt + 1 retry. The retry only fires for transient failures (see IsTransient).
    // Mirrors LlmClient's policy (#218) so chat + embeddings behave identically against a flaky
    // provider; the helper set (IsTransient/Describe/Unwrap) is duplicated rather than shared to
    // keep #219 from refactoring the merged, tested #218 surface.
    private const int MaxAttempts = 2;

    /// <summary>Default cap on the number of cached vectors before oldest-first eviction kicks in.</summary>
    private const int DefaultCacheCapacity = 8192;

    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;
    private readonly EmbeddingOptions _config;
    private readonly ILogger<EmbeddingService> _logger;
    private readonly EmbeddingGenerationOptions? _generationOptions;

    // Bounded, thread-safe content-hash → vector cache. The dictionary keeps reads lock-free on the
    // hot path; the parallel queue records insertion order so we can trim oldest-first once the
    // dictionary exceeds capacity, giving a hard memory bound under a stream of distinct text. An
    // approximate FIFO (not strict LRU) is plenty for this single-user local tool: a key re-added
    // after eviction may be dropped a little early, which only costs one recompute, never correctness.
    private readonly ConcurrentDictionary<string, float[]> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _insertionOrder = new();
    private readonly int _capacity;

    /// <summary>
    /// Production constructor used by DI.
    /// </summary>
    /// <param name="generator">Underlying embedding generator (the OpenAI connector pointed at ZeroRouter).</param>
    /// <param name="config">Connection + model settings; <see cref="EmbeddingOptions.TimeoutMs"/> and <see cref="EmbeddingOptions.MaxBatchSize"/> must be &gt; 0.</param>
    /// <param name="logger">Logger for transient-retry and fail-soft diagnostics.</param>
    public EmbeddingService(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        EmbeddingOptions config,
        ILogger<EmbeddingService> logger)
        : this(generator, config, logger, DefaultCacheCapacity)
    {
    }

    // Test seam (InternalsVisibleTo = KanbanFlow.Tests): lets a test pick a tiny cache capacity to
    // prove eviction is real without embedding thousands of strings.
    internal EmbeddingService(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        EmbeddingOptions config,
        ILogger<EmbeddingService> logger,
        int cacheCapacity)
    {
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
        _config = ValidateConfig(config);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _capacity = cacheCapacity > 0 ? cacheCapacity : DefaultCacheCapacity;

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
        return config;
    }

    /// <inheritdoc />
    public async Task<EmbedResult> EmbedAsync(string text, CancellationToken ct = default)
    {
        // Reject blank input locally (fail fast) rather than wasting a remote no-op on empty text —
        // mirrors LlmClient's user-prompt guard.
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(text));

        var key = CacheKey(text);
        if (_cache.TryGetValue(key, out var cached))
            return EmbedResult.Success(cached, _config.Model);

        var outcome = await GenerateAsync(new[] { text }, ct).ConfigureAwait(false);
        if (!outcome.Ok)
            return EmbedResult.Failure(outcome.Error!, outcome.Model);

        var vector = outcome.Vectors![0];
        CacheStore(key, vector);
        return EmbedResult.Success(vector, outcome.Model);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EmbedResult>> EmbedBatchAsync(
        IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count == 0)
            return Array.Empty<EmbedResult>();

        // Per-input cache keys. Validate up-front so a null element fails fast (a null in a typed
        // string batch is a caller bug); blank/whitespace entries are passed through to the provider
        // unchanged (one bad entry must not nuke the whole batch — the batch path favors resilience).
        var keys = new string[texts.Count];
        for (var i = 0; i < texts.Count; i++)
        {
            var text = texts[i];
            if (text is null)
                throw new ArgumentException($"Batch element [{i}] is null.", nameof(texts));
            keys[i] = CacheKey(text);
        }

        // Collect distinct cache misses, preserving first-seen order and deduped by key, so identical
        // text is embedded exactly once and already-cached text triggers no provider call.
        var missTextByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        var missOrder = new List<string>();
        for (var i = 0; i < texts.Count; i++)
        {
            var key = keys[i];
            if (_cache.ContainsKey(key) || missTextByKey.ContainsKey(key))
                continue;
            missTextByKey[key] = texts[i];
            missOrder.Add(key);
        }

        // Embed the misses in provider-safe chunks. A chunk that fails (after its retry) fails soft:
        // its keys get an error and are NOT cached, while successful chunks + cache hits are unaffected.
        var errorByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        var modelByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var start = 0; start < missOrder.Count; start += _config.MaxBatchSize)
        {
            var chunkKeys = missOrder.GetRange(start, Math.Min(_config.MaxBatchSize, missOrder.Count - start));
            var chunkTexts = new string[chunkKeys.Count];
            for (var j = 0; j < chunkKeys.Count; j++)
                chunkTexts[j] = missTextByKey[chunkKeys[j]];

            var outcome = await GenerateAsync(chunkTexts, ct).ConfigureAwait(false);
            if (outcome.Ok)
            {
                for (var j = 0; j < chunkKeys.Count; j++)
                {
                    CacheStore(chunkKeys[j], outcome.Vectors![j]);
                    modelByKey[chunkKeys[j]] = outcome.Model;
                }
            }
            else
            {
                foreach (var chunkKey in chunkKeys)
                    errorByKey[chunkKey] = outcome.Error!;
            }
        }

        // Assemble results in input order. Every input is now a cache hit (pre-existing or just
        // stored) or belongs to a failed chunk.
        var results = new EmbedResult[texts.Count];
        for (var i = 0; i < texts.Count; i++)
        {
            var key = keys[i];
            if (_cache.TryGetValue(key, out var vector))
                results[i] = EmbedResult.Success(vector, modelByKey.GetValueOrDefault(key) ?? _config.Model);
            else
                results[i] = EmbedResult.Failure(errorByKey.GetValueOrDefault(key) ?? "Embedding failed.", _config.Model);
        }
        return results;
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
            using var timeoutCts = new CancellationTokenSource(_config.TimeoutMs);
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

    private void CacheStore(string key, float[] vector)
    {
        // First writer wins; a concurrent duplicate add is a no-op (the vector is identical for the
        // same key anyway). Only a genuinely new key advances the eviction queue + count.
        if (!_cache.TryAdd(key, vector))
            return;
        _insertionOrder.Enqueue(key);
        while (_cache.Count > _capacity && _insertionOrder.TryDequeue(out var oldest))
            _cache.TryRemove(oldest, out _);
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
        ClientResultException { Status: >= 500 } => true,
        ClientResultException => false,
        _ => false,
    };

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

    /// <summary>Internal outcome of a single provider call: aligned vectors on success, or a typed error.</summary>
    private readonly record struct GenerateOutcome(bool Ok, float[][]? Vectors, string Model, string? Error)
    {
        public static GenerateOutcome Succeed(float[][] vectors, string model) => new(true, vectors, model, null);
        public static GenerateOutcome Fail(string error, string model) => new(false, null, model, error);
    }
}
