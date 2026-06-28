using Microsoft.Extensions.AI;

namespace KanbanFlow.Services;

/// <summary>
/// Provider-agnostic chat-completions client (#218), backed by ZeroRouter's
/// OpenAI-compatible API (<c>:5256</c>) through a <see cref="IChatClient"/>. This is the
/// LLM foundation later AI features consume (memory store #220, AI-PM brain #226;
/// embeddings #219 reuse the same connector + config).
///
/// <para><b>Fail-soft contract.</b> Provider problems — ZeroRouter down, request timeout,
/// bad model, transport error — are returned as a typed <see cref="LlmResult"/> with
/// <see cref="LlmResult.Ok"/> = <c>false</c>; they are NEVER thrown. A ZeroRouter outage
/// must not crash the app or any request path. The single exception is <em>caller</em>
/// cancellation: if the caller's <see cref="CancellationToken"/> is cancelled, the
/// resulting <see cref="OperationCanceledException"/> propagates (cancellation is honored,
/// not swallowed into a fake "error" result).</para>
/// </summary>
public interface ILlmClient
{
    /// <summary>
    /// Convenience overload for the common single-turn case. <paramref name="systemPrompt"/>
    /// may be <c>null</c>/blank to send only the user turn.
    /// </summary>
    Task<LlmResult> CompleteAsync(string? systemPrompt, string userPrompt, CancellationToken ct = default);

    /// <summary>
    /// Full overload: a prepared message list plus optional per-call overrides (model,
    /// temperature, max output tokens). Returns the assistant's text in a typed result.
    /// </summary>
    Task<LlmResult> CompleteAsync(
        IReadOnlyList<ChatMessage> messages, LlmCompleteOptions? options, CancellationToken ct = default);
}

/// <summary>
/// Outcome of an <see cref="ILlmClient"/> call. <see cref="Ok"/> distinguishes a real
/// completion (<see cref="Text"/> set) from a fail-soft provider error (<see cref="Error"/>
/// set). <see cref="Model"/> is the model id the provider reported (or the requested model
/// on failure), for diagnostics.
/// </summary>
public sealed record LlmResult(bool Ok, string? Text, string? Model, string? Error)
{
    /// <summary>A successful completion: <paramref name="text"/> produced by <paramref name="model"/>.</summary>
    public static LlmResult Success(string text, string? model) => new(true, text, model, null);

    /// <summary>A fail-soft failure: <paramref name="error"/>, with no exception thrown to the caller.</summary>
    public static LlmResult Failure(string error, string? model = null) => new(false, null, model, error);
}

/// <summary>
/// Optional per-call overrides for <see cref="ILlmClient.CompleteAsync(IReadOnlyList{ChatMessage}, LlmCompleteOptions?, CancellationToken)"/>.
/// Any <c>null</c> member falls back to the configured <see cref="LlmOptions"/> default.
/// </summary>
public sealed record LlmCompleteOptions(
    string? Model = null, float? Temperature = null, int? MaxOutputTokens = null);
