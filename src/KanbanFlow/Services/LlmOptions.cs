using System.Globalization;

namespace KanbanFlow.Services;

/// <summary>
/// Connection settings for <see cref="ILlmClient"/> (#218), read from the environment the
/// same way <c>KANBANFLOW_DB_PATH</c> is in <c>Program.cs</c>.
///
/// <para><b>Open access.</b> ZeroRouter is open on the local network, so <see cref="ApiKey"/>
/// is a non-secret placeholder by default — the OpenAI client merely requires a non-empty
/// credential. Per the open-access charter the key is never masked in any UI/log/response and
/// no auth/rate-limit is added.</para>
/// </summary>
public sealed record LlmOptions
{
    /// <summary>
    /// Base URL of the OpenAI-compatible endpoint. MUST include the <c>/v1</c> suffix: the
    /// OpenAI client appends <c>chat/completions</c> to this without inserting <c>/v1</c>, so a
    /// missing suffix is the most likely first-run 404.
    /// </summary>
    public const string DefaultBaseUrl = "http://localhost:5256/v1";

    /// <summary>
    /// Default model id. ZeroRouter maps it to a concrete provider via its routing rules; fully
    /// overridable via <c>KANBANFLOW_LLM_MODEL</c> or a per-call <see cref="LlmCompleteOptions.Model"/>.
    /// </summary>
    public const string DefaultModel = "gpt-4o-mini";

    /// <summary>
    /// Non-empty placeholder credential. ZeroRouter needs no key, but the OpenAI client throws on
    /// a null/empty credential at construction, so we always supply <em>something</em> non-blank.
    /// </summary>
    public const string DefaultApiKey = "zerorouter";

    /// <summary>Per-attempt request timeout, in milliseconds.</summary>
    public const int DefaultTimeoutMs = 30_000;

    /// <summary>OpenAI-compatible base URL (includes <c>/v1</c>).</summary>
    public string BaseUrl { get; init; } = DefaultBaseUrl;

    /// <summary>Default model id sent to the provider.</summary>
    public string Model { get; init; } = DefaultModel;

    /// <summary>Credential passed to the OpenAI client (placeholder by default; never masked).</summary>
    public string ApiKey { get; init; } = DefaultApiKey;

    /// <summary>Per-attempt request timeout, in milliseconds.</summary>
    public int TimeoutMs { get; init; } = DefaultTimeoutMs;

    /// <summary>
    /// Build options from environment variables, falling back to the <c>Default*</c> constants.
    /// Both naming conventions are accepted (first non-blank value wins) so either works:
    /// <list type="bullet">
    ///   <item><description>URL: <c>ZEROROUTER_URL</c> or <c>KANBANFLOW_LLM_URL</c></description></item>
    ///   <item><description>Model: <c>KANBANFLOW_LLM_MODEL</c></description></item>
    ///   <item><description>Key: <c>ZEROROUTER_API_KEY</c> or <c>KANBANFLOW_LLM_KEY</c></description></item>
    ///   <item><description>Timeout (ms): <c>KANBANFLOW_LLM_TIMEOUT_MS</c></description></item>
    /// </list>
    /// <paramref name="getEnv"/> is injected (rather than calling <see cref="Environment"/>
    /// directly) so the binding is unit-testable without mutating shared process state.
    /// </summary>
    public static LlmOptions FromEnvironment(Func<string, string?> getEnv)
    {
        ArgumentNullException.ThrowIfNull(getEnv);

        string? First(params string[] names)
        {
            foreach (var name in names)
            {
                var value = getEnv(name);
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }
            return null;
        }

        // Validate + normalize the base URL at bind time. Program.cs does `new Uri(BaseUrl)` when it
        // registers the OpenAI client, so a malformed value would otherwise crash startup — but #218
        // is optional and fail-soft, so an unparseable or non-HTTP(S) URL falls back to the default
        // rather than throwing. The /v1 suffix is required (the OpenAI client appends
        // `chat/completions` without inserting /v1), so a missing suffix is appended here to head off
        // the most common first-run 404.
        static string NormalizeBaseUrl(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return DefaultBaseUrl;
            }

            var path = uri.AbsolutePath.TrimEnd('/');
            if (!path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                path = string.IsNullOrEmpty(path) ? "/v1" : $"{path}/v1";

            return new UriBuilder(uri) { Path = path }.Uri.ToString().TrimEnd('/');
        }

        var options = new LlmOptions
        {
            BaseUrl = NormalizeBaseUrl(First("ZEROROUTER_URL", "KANBANFLOW_LLM_URL") ?? DefaultBaseUrl),
            Model = First("KANBANFLOW_LLM_MODEL") ?? DefaultModel,
            ApiKey = First("ZEROROUTER_API_KEY", "KANBANFLOW_LLM_KEY") ?? DefaultApiKey,
        };

        var rawTimeout = First("KANBANFLOW_LLM_TIMEOUT_MS");
        if (rawTimeout is not null
            && int.TryParse(rawTimeout, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)
            && ms > 0)
        {
            options = options with { TimeoutMs = ms };
        }

        return options;
    }
}
