namespace KanbanFlow.Services;

/// <summary>
/// Provider-agnostic text embeddings (#219), backed by ZeroRouter's OpenAI-compatible API through a
/// <c>Microsoft.Extensions.AI</c> <c>IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt;</c>
/// (the same connector + config as #218's <see cref="ILlmClient"/>). It vectorizes text for
/// semantic recall — the retrieval foundation for the memory store (#220) and duplicate/similar
/// detection — with a content-hash cache so identical text is embedded once, plus native batching.
///
/// <para><b>Fail-soft contract.</b> Exactly like <see cref="ILlmClient"/>: provider problems
/// (ZeroRouter down, request timeout, bad model, transport error) are returned as an
/// <see cref="EmbedResult"/> with <see cref="EmbedResult.Ok"/> = <c>false</c>; they are NEVER
/// thrown. An embedding outage must not crash the app or break the (audited, #88) write path. The
/// single exception is <em>caller</em> cancellation: a cancelled <see cref="CancellationToken"/>
/// propagates as <see cref="OperationCanceledException"/> (honored, not swallowed into a result).
/// A call that never reaches the provider is still returned for a cancelled token, as it was before
/// the failure cooldown (#549): a cache hit, and a batch in which every element is a cache hit or
/// blank, an empty batch included.</para>
/// </summary>
public interface IEmbeddingService
{
    /// <summary>
    /// Embed a single text. <paramref name="text"/> must be non-null and non-blank. Identical text
    /// (same configured model) served from cache without a provider call on a repeat. Inside the
    /// failure cooldown (#549), for up to <see cref="EmbeddingOptions.FailureCooldownMs"/> after the
    /// provider was unreachable, timed out or returned a 5xx, a cache miss fails soft without a
    /// provider call; use <see cref="ProbeAsync"/> for a live check.
    /// </summary>
    Task<EmbedResult> EmbedAsync(string text, CancellationToken ct = default);

    /// <summary>
    /// Embed a batch. The returned list aligns 1:1 with <paramref name="texts"/> (same order, same
    /// count). Duplicate texts within the batch are embedded once; already-cached texts issue no
    /// provider call; the remaining misses go out in provider-safe chunks. A provider failure fails
    /// only the affected items soft (<see cref="EmbedResult.Ok"/> = <c>false</c>) — cache hits and
    /// other chunks still succeed, except that inside the failure cooldown (#549) a chunk fails soft
    /// without a provider call, so the chunks after one that finds the provider unreachable, timed out
    /// or returning a 5xx fail soft too — and never throws. A <c>null</c> element throws (a typed-batch
    /// caller bug); a blank/whitespace element fails soft locally and is never sent to the provider,
    /// so one bad entry cannot sink its batch-mates.
    /// </summary>
    Task<IReadOnlyList<EmbedResult>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default);

    /// <summary>
    /// Embed a single text like <see cref="EmbedAsync"/>, but never short-circuited by the failure
    /// cooldown (#549): a cache miss always reaches the provider, even inside the window in which
    /// <see cref="EmbedAsync"/> fails fast after the provider was unreachable, and a success ends that
    /// window at once. For the <c>embed-ping</c> self-test, which must report live provider state.
    /// </summary>
    Task<EmbedResult> ProbeAsync(string text, CancellationToken ct = default);
}

/// <summary>
/// Outcome of an embedding. <see cref="Ok"/> distinguishes a real vector from a fail-soft provider
/// error. On success <see cref="Vector"/> holds the floats and <see cref="Dimensions"/> its length;
/// on failure <see cref="Vector"/> is empty, <see cref="Dimensions"/> is <c>0</c> and
/// <see cref="Error"/> is set. <see cref="Model"/> is always populated (the model the provider
/// reported on success, or the requested model on failure) so callers can store <c>(model, dim)</c>
/// alongside the vector for index compatibility (#220).
/// </summary>
/// <param name="Ok"><c>true</c> for a real embedding; <c>false</c> for a fail-soft provider error.</param>
/// <param name="Vector">The embedding floats on success; an empty array on failure.</param>
/// <param name="Dimensions">Vector length on success; <c>0</c> on failure.</param>
/// <param name="Model">Model id (provider-reported on success, requested on failure).</param>
/// <param name="Error">Fail-soft error description on failure; <c>null</c> on success.</param>
public sealed record EmbedResult(bool Ok, float[] Vector, int Dimensions, string Model, string? Error)
{
    /// <summary>A successful embedding: <paramref name="vector"/> produced by <paramref name="model"/>.</summary>
    public static EmbedResult Success(float[] vector, string model)
    {
        ArgumentNullException.ThrowIfNull(vector);
        return new EmbedResult(true, vector, vector.Length, model, null);
    }

    /// <summary>A fail-soft failure: <paramref name="error"/>, with no exception thrown to the caller.</summary>
    public static EmbedResult Failure(string error, string model) =>
        new(false, Array.Empty<float>(), 0, model, error);
}
