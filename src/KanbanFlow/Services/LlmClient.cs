using System.ClientModel;
using Microsoft.Extensions.AI;

namespace KanbanFlow.Services;

/// <summary>
/// Default <see cref="ILlmClient"/>: a thin, fail-soft wrapper over an injected
/// <see cref="IChatClient"/> (built from the OpenAI connector pointed at ZeroRouter — see
/// <c>Program.cs</c>). It adds a per-attempt timeout, a single retry on transient transport
/// failures, and maps any provider failure to a typed <see cref="LlmResult"/> rather than
/// throwing (#218). Stateless and thread-safe, so it is registered as a singleton.
/// </summary>
/// <param name="chatClient">Underlying chat client (the OpenAI connector pointed at ZeroRouter).</param>
/// <param name="config">Connection + per-attempt timeout settings; <see cref="LlmOptions.TimeoutMs"/> must be &gt; 0.</param>
/// <param name="logger">Logger for transient-retry and fail-soft diagnostics.</param>
public sealed class LlmClient(IChatClient chatClient, LlmOptions config, ILogger<LlmClient> logger) : ILlmClient
{
    // Validate the primary-constructor args once, at construction — fail fast on a misconfigured
    // registration instead of NRE-ing on first use (null config) or throwing deep inside the
    // per-attempt CancellationTokenSource (TimeoutMs <= 0). Primary-constructor syntax is preserved:
    // each captured dependency flows through its field initializer, and the non-null + positive-timeout
    // invariants are enforced by the static ValidateConfig helper below.
    private readonly IChatClient _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
    private readonly LlmOptions _config = ValidateConfig(config);
    private readonly ILogger<LlmClient> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    // TimeProvider.System in production; a FakeTimeProvider in tests so the per-attempt timeout is
    // deterministic rather than a wall-clock race the thread pool can lose under load (#262).
    private readonly TimeProvider _timeProvider = TimeProvider.System;

    // Test seam (InternalsVisibleTo = KanbanFlow.Tests). Chains through the primary constructor so
    // the DI-visible public ctor keeps its 3-parameter shape — the container never has to resolve a
    // TimeProvider, and production behaviour is byte-identical. The parameter is deliberately
    // NULLABLE: a non-nullable one would make the `?? TimeProvider.System` fallback below
    // unreachable — a guard that advertises a behaviour it can never provide — and would diverge
    // from EmbeddingService, whose seam already takes `TimeProvider?`. Don't tighten it back.
    internal LlmClient(IChatClient chatClient, LlmOptions config, ILogger<LlmClient> logger, TimeProvider? timeProvider)
        : this(chatClient, config, logger)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    // 1 initial attempt + 1 retry. The retry only fires for transient failures (see IsTransient).
    private const int MaxAttempts = 2;

    private static LlmOptions ValidateConfig(LlmOptions config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.TimeoutMs <= 0)
            throw new ArgumentException("TimeoutMs must be greater than zero.", nameof(config));
        return config;
    }

    /// <inheritdoc />
    public Task<LlmResult> CompleteAsync(string? systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        // Reject invalid input locally (fail fast) rather than coercing to an empty user turn, which
        // would hide caller bugs and turn bad input into a wasted remote no-op.
        if (string.IsNullOrWhiteSpace(userPrompt))
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(userPrompt));

        var messages = new List<ChatMessage>(2);
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            messages.Add(new ChatMessage(ChatRole.System, systemPrompt));
        messages.Add(new ChatMessage(ChatRole.User, userPrompt));
        return CompleteAsync(messages, options: null, ct);
    }

    /// <inheritdoc />
    public async Task<LlmResult> CompleteAsync(
        IReadOnlyList<ChatMessage> messages, LlmCompleteOptions? options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var model = options?.Model ?? _config.Model;
        if (messages.Count == 0)
            return LlmResult.Failure("No messages to send.", model);

        var chatOptions = new ChatOptions
        {
            ModelId = model,
            Temperature = options?.Temperature,
            MaxOutputTokens = options?.MaxOutputTokens,
        };

        Exception? lastError = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            // Honor caller cancellation up-front so a cancelled token never starts an attempt.
            ct.ThrowIfCancellationRequested();

            // Per-attempt timeout, linked to the caller's token so caller-cancel still wins the race.
            // Driven by _timeProvider so a test can advance the clock and fire this deterministically
            // instead of waiting on a real timeout the thread pool can delay under load (#262).
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(_config.TimeoutMs), _timeProvider);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            try
            {
                var response = await _chatClient
                    .GetResponseAsync(messages, chatOptions, linkedCts.Token)
                    .ConfigureAwait(false);
                return LlmResult.Success(response.Text ?? string.Empty, response.ModelId ?? model);
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
                    ex, "LLM completion attempt {Attempt}/{Max} failed (transient); retrying.", attempt, MaxAttempts);
            }
            catch (Exception ex)
            {
                // Non-transient, or the final attempt failed: fail soft with a typed result.
                _logger.LogWarning(ex, "LLM completion failed (model {Model}); returning fail-soft result.", model);
                return LlmResult.Failure(Describe(ex), model);
            }
        }

        // Compiler-required fallback; unreachable in practice — the final-attempt catch always
        // returns before the loop exits normally.
        return LlmResult.Failure(Describe(lastError), model);
    }

    /// <summary>
    /// Transient = worth a single retry: a request timeout, a raw transport exception, or a 5xx
    /// from the provider. NOT transient (fail fast): a 4xx (bad model / malformed request), and a
    /// <see cref="ClientResultException"/> with <c>Status == 0</c> — that means no HTTP response
    /// was received (connection refused / DNS / TLS), where an immediate retry almost never helps,
    /// so we fail fast rather than double the latency on a down provider.
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

    /// <summary>Human-readable, log-safe description of a provider failure for <see cref="LlmResult.Error"/>.</summary>
    private static string Describe(Exception? ex) => (ex is null ? null : Unwrap(ex)) switch
    {
        null => "Unknown LLM error.",
        ClientResultException cre => $"LLM provider returned HTTP {cre.Status}: {cre.Message}",
        OperationCanceledException => "LLM request timed out.",
        HttpRequestException hre => $"LLM provider unreachable: {hre.Message}",
        var e => $"{e.GetType().Name}: {e.Message}",
    };

    /// <summary>
    /// Flatten an <see cref="AggregateException"/> to its most meaningful inner exception so
    /// transport failures classify and read cleanly (the pipeline can wrap a transport error in
    /// an <see cref="AggregateException"/>); other exceptions pass through unchanged.
    /// </summary>
    private static Exception Unwrap(Exception ex) =>
        ex is AggregateException ae ? ae.Flatten().InnerException ?? ae : ex;
}
