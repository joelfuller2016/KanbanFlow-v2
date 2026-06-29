using System.Globalization;

namespace KanbanFlow.Services;

/// <summary>
/// Connection + model settings for <see cref="IEmbeddingService"/> (#219). Embeddings are routed
/// through the SAME ZeroRouter OpenAI-compatible endpoint as chat (#218): this type reuses
/// <see cref="LlmOptions.FromEnvironment"/> for the URL / key / timeout (so the
/// <c>ZEROROUTER_*</c> → <c>KANBANFLOW_LLM_*</c> precedence and the <c>/v1</c> URL-normalization
/// are single-sourced, never duplicated), and layers on the embedding-specific model +
/// dimensionality.
///
/// <para><b>Open access.</b> Same charter as <see cref="LlmOptions"/>: <see cref="ApiKey"/> is a
/// non-secret placeholder by default and is never masked in any UI/log/response; no auth or
/// rate-limit is added.</para>
/// </summary>
public sealed record EmbeddingOptions
{
    /// <summary>
    /// Default embedding model id. ZeroRouter maps it to a concrete provider via its routing rules;
    /// override via <c>KANBANFLOW_EMBED_MODEL</c>. The actual vector length is whatever the provider
    /// returns and is recorded per-vector in <see cref="EmbedResult.Dimensions"/> for index
    /// compatibility (#220).
    /// </summary>
    public const string DefaultModel = "text-embedding-3-small";

    /// <summary>
    /// Default per-request batch cap. Batches larger than this are split into multiple provider
    /// calls so a single oversized request can't blow the provider's token/size limits; override via
    /// <c>KANBANFLOW_EMBED_BATCH_SIZE</c>.
    /// </summary>
    public const int DefaultMaxBatchSize = 256;

    /// <summary>OpenAI-compatible base URL (includes <c>/v1</c>); shared with <see cref="LlmOptions"/>.</summary>
    public string BaseUrl { get; init; } = LlmOptions.DefaultBaseUrl;

    /// <summary>Credential passed to the OpenAI client (placeholder by default; never masked).</summary>
    public string ApiKey { get; init; } = LlmOptions.DefaultApiKey;

    /// <summary>Embedding model id sent to the provider.</summary>
    public string Model { get; init; } = DefaultModel;

    /// <summary>Per-attempt request timeout, in milliseconds.</summary>
    public int TimeoutMs { get; init; } = LlmOptions.DefaultTimeoutMs;

    /// <summary>
    /// Optional requested output dimensionality. When <c>&gt; 0</c> it is sent to the provider
    /// (OpenAI <c>text-embedding-3-*</c> can reduce dimensions on request); when <c>null</c> the
    /// provider's native size is used. A model/dim change is what makes previously stored vectors +
    /// the ANN index (#220) incompatible, so the value is carried with every result.
    /// </summary>
    public int? Dimensions { get; init; }

    /// <summary>Per-request batch cap (see <see cref="DefaultMaxBatchSize"/>).</summary>
    public int MaxBatchSize { get; init; } = DefaultMaxBatchSize;

    /// <summary>
    /// Build options from environment variables, falling back to the <c>Default*</c> constants.
    /// URL / key / timeout are bound by <see cref="LlmOptions.FromEnvironment"/> (so embeddings and
    /// chat always agree on the endpoint), then these embedding-specific vars are layered on:
    /// <list type="bullet">
    ///   <item><description>Model: <c>KANBANFLOW_EMBED_MODEL</c></description></item>
    ///   <item><description>Dimensions (optional, &gt; 0): <c>KANBANFLOW_EMBED_DIMENSIONS</c></description></item>
    ///   <item><description>Batch cap (optional, &gt; 0): <c>KANBANFLOW_EMBED_BATCH_SIZE</c></description></item>
    /// </list>
    /// <paramref name="getEnv"/> is injected (rather than reading <see cref="Environment"/> directly)
    /// so the binding is unit-testable without mutating shared process state.
    /// </summary>
    public static EmbeddingOptions FromEnvironment(Func<string, string?> getEnv)
    {
        ArgumentNullException.ThrowIfNull(getEnv);

        // Reuse the chat binder for URL/key/timeout: it already applies the ZEROROUTER_* →
        // KANBANFLOW_LLM_* precedence and the /v1 URL-normalization, so embeddings inherit exactly
        // the same endpoint resolution without re-implementing (or drifting from) that logic.
        var shared = LlmOptions.FromEnvironment(getEnv);

        string? Read(string name)
        {
            var value = getEnv(name);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        var options = new EmbeddingOptions
        {
            BaseUrl = shared.BaseUrl,
            ApiKey = shared.ApiKey,
            TimeoutMs = shared.TimeoutMs,
            Model = Read("KANBANFLOW_EMBED_MODEL") ?? DefaultModel,
        };

        var rawDimensions = Read("KANBANFLOW_EMBED_DIMENSIONS");
        if (rawDimensions is not null
            && int.TryParse(rawDimensions, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dimensions)
            && dimensions > 0)
        {
            options = options with { Dimensions = dimensions };
        }

        var rawBatchSize = Read("KANBANFLOW_EMBED_BATCH_SIZE");
        if (rawBatchSize is not null
            && int.TryParse(rawBatchSize, NumberStyles.Integer, CultureInfo.InvariantCulture, out var batchSize)
            && batchSize > 0)
        {
            options = options with { MaxBatchSize = batchSize };
        }

        return options;
    }
}
