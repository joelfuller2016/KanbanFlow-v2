using KanbanFlow.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace KanbanFlow.Tests;

/// <summary>
/// Real-behavior tests for <see cref="LlmClient"/> (#218). The collaborator is a hand-written
/// fake <see cref="IChatClient"/> (no Moq) so the wrapper's actual control flow runs: success
/// mapping, the timeout + single-retry policy, the fail-soft mapping of provider errors to a
/// typed <see cref="LlmResult"/>, and honored caller cancellation. Plus pure config-binding
/// tests for <see cref="LlmOptions.FromEnvironment"/>.
/// </summary>
public sealed class LlmClientTests
{
    // Generous hang-guard for the rendezvous waits. NOT a race budget — the timeout under test is
    // driven by a FakeTimeProvider and fires when the test says so. This only stops a genuine deadlock
    // from hanging the suite, and it lives in the TEST BODY (not inside the fake) so a real bug reads
    // as "the retry never started" rather than corrupting the asserted string (#262).
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private static LlmClient NewClient(IChatClient chat, LlmOptions? config = null, TimeProvider? timeProvider = null)
    {
        var opts = config ?? new LlmOptions { Model = "test-default-model", TimeoutMs = 5_000 };
        return timeProvider is null
            ? new LlmClient(chat, opts, NullLogger<LlmClient>.Instance)
            : new LlmClient(chat, opts, NullLogger<LlmClient>.Instance, timeProvider);
    }

    private static ChatResponse Reply(string text, string? model) =>
        new(new ChatMessage(ChatRole.Assistant, text)) { ModelId = model };

    // ── success path ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task CompleteAsync_Success_MapsReplyTextAndModel()
    {
        var fake = new FakeChatClient((_, _, _) => Task.FromResult(Reply("pong", "router/model-x")));
        var client = NewClient(fake);

        var result = await client.CompleteAsync("you are a health check", "ping", CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("pong", result.Text);
        Assert.Equal("router/model-x", result.Model);
        Assert.Null(result.Error);
        Assert.Equal(1, fake.CallCount); // success ⇒ no retry
    }

    [Fact]
    public async Task CompleteAsync_StringOverload_SendsSystemThenUserMessage()
    {
        var fake = new FakeChatClient((_, _, _) => Task.FromResult(Reply("ok", "m")));
        var client = NewClient(fake);

        await client.CompleteAsync("the system prompt", "the user prompt", CancellationToken.None);

        Assert.NotNull(fake.LastMessages);
        Assert.Equal(2, fake.LastMessages!.Count);
        Assert.Equal(ChatRole.System, fake.LastMessages[0].Role);
        Assert.Equal("the system prompt", fake.LastMessages[0].Text);
        Assert.Equal(ChatRole.User, fake.LastMessages[1].Role);
        Assert.Equal("the user prompt", fake.LastMessages[1].Text);
    }

    [Fact]
    public async Task CompleteAsync_StringOverload_BlankSystemPrompt_SendsOnlyUserMessage()
    {
        var fake = new FakeChatClient((_, _, _) => Task.FromResult(Reply("ok", "m")));
        var client = NewClient(fake);

        await client.CompleteAsync("   ", "just the user", CancellationToken.None);

        Assert.NotNull(fake.LastMessages);
        var only = Assert.Single(fake.LastMessages!);
        Assert.Equal(ChatRole.User, only.Role);
        Assert.Equal("just the user", only.Text);
    }

    [Fact]
    public async Task CompleteAsync_UsesConfiguredModel_ByDefault()
    {
        var fake = new FakeChatClient((_, _, _) => Task.FromResult(Reply("ok", "m")));
        var client = NewClient(fake, new LlmOptions { Model = "configured-model", TimeoutMs = 5_000 });

        await client.CompleteAsync(null, "hi", CancellationToken.None);

        Assert.Equal("configured-model", fake.LastOptions?.ModelId);
    }

    [Fact]
    public async Task CompleteAsync_PerCallOptions_OverrideModelAndSamplingParams()
    {
        var fake = new FakeChatClient((_, _, _) => Task.FromResult(Reply("ok", "m")));
        var client = NewClient(fake, new LlmOptions { Model = "configured-model", TimeoutMs = 5_000 });

        await client.CompleteAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") },
            new LlmCompleteOptions(Model: "override-model", Temperature: 0.25f, MaxOutputTokens: 64),
            CancellationToken.None);

        Assert.Equal("override-model", fake.LastOptions?.ModelId);
        Assert.Equal(0.25f, fake.LastOptions?.Temperature);
        Assert.Equal(64, fake.LastOptions?.MaxOutputTokens);
    }

    // ── fail-soft paths (provider errors return a typed result, never throw) ───────
    [Fact]
    public async Task CompleteAsync_TransientTransportError_RetriesOnce_ThenFailsSoft()
    {
        var fake = new FakeChatClient((_, _, _) => throw new HttpRequestException("connection refused"));
        var client = NewClient(fake);

        var result = await client.CompleteAsync(null, "ping", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Null(result.Text);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Equal("test-default-model", result.Model);
        Assert.Equal(2, fake.CallCount); // 1 attempt + 1 retry on a transient error
    }

    [Fact]
    public async Task CompleteAsync_NonTransientError_DoesNotRetry_AndFailsSoft()
    {
        var fake = new FakeChatClient((_, _, _) => throw new InvalidOperationException("bad request shape"));
        var client = NewClient(fake);

        var result = await client.CompleteAsync(null, "ping", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("bad request shape", result.Error);
        Assert.Equal(1, fake.CallCount); // non-transient ⇒ no retry
    }

    [Fact]
    public async Task CompleteAsync_Timeout_IsTreatedAsTransient_RetriesThenFailsSoft()
    {
        // #262 (twin of the EmbeddingService flake). This test carried the identical defect: a REAL
        // 30ms per-attempt timeout raced against a 2-second watchdog inside the fake. It was passing
        // only by luck — under enough load the watchdog wins, throws a TimeoutException (which
        // IsTransient classifies as NON-transient), and short-circuits the retry under test.
        //
        // Now the timeout is driven by a FakeTimeProvider: the test advances the clock rather than
        // racing it. Deterministic, no watchdog, no wall-clock dependency.
        var time = new FakeTimeProvider();
        var enteredClient = new SemaphoreSlim(0);

        var fake = new FakeChatClient(async (_, _, token) =>
        {
            // Inside the chat client ⇒ the wrapper has already armed its timeout CTS.
            enteredClient.Release();
            // Block until the WRAPPER cancels us — never on a timer of our own.
            await Task.Delay(Timeout.Infinite, token);
            return Reply("unreachable", "m");
        });
        var client = NewClient(fake, new LlmOptions { Model = "test-default-model", TimeoutMs = 30 }, timeProvider: time);

        var call = client.CompleteAsync(null, "ping", CancellationToken.None);

        // Attempt 1: wait until we're in the client, then fire its timeout.
        Assert.True(await enteredClient.WaitAsync(HangGuard), "attempt 1 never reached the chat client");
        time.Advance(TimeSpan.FromMilliseconds(31));

        // Attempt 2 (the retry — the thing actually under test).
        Assert.True(await enteredClient.WaitAsync(HangGuard), "the retry never started — timeout was not treated as transient");
        time.Advance(TimeSpan.FromMilliseconds(31));

        var result = await call;

        Assert.False(result.Ok);
        Assert.Contains("timed out", result.Error);
        Assert.Equal(2, fake.CallCount); // timeout is transient ⇒ retried once
    }

    [Fact]
    public async Task CompleteAsync_CallerCancellation_Throws_NotSwallowedIntoResult()
    {
        var fake = new FakeChatClient((_, _, _) => Task.FromResult(Reply("nope", "m")));
        var client = NewClient(fake);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => client.CompleteAsync(null, "ping", cts.Token));
        Assert.Equal(0, fake.CallCount); // cancelled before the first attempt starts
    }

    // ── input guards ──────────────────────────────────────────────────────────────
    [Fact]
    public async Task CompleteAsync_NullMessages_Throws()
    {
        var client = NewClient(new FakeChatClient((_, _, _) => Task.FromResult(Reply("x", "m"))));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => client.CompleteAsync(null!, options: null, CancellationToken.None));
    }

    [Fact]
    public async Task CompleteAsync_EmptyMessages_FailsSoft_WithoutCallingProvider()
    {
        var fake = new FakeChatClient((_, _, _) => Task.FromResult(Reply("x", "m")));
        var client = NewClient(fake);

        var result = await client.CompleteAsync(Array.Empty<ChatMessage>(), options: null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(0, fake.CallCount);
    }

    // ── LlmOptions.FromEnvironment binding ─────────────────────────────────────────
    [Fact]
    public void FromEnvironment_NoVarsSet_UsesDefaults()
    {
        var options = LlmOptions.FromEnvironment(_ => null);

        Assert.Equal(LlmOptions.DefaultBaseUrl, options.BaseUrl);
        Assert.Equal(LlmOptions.DefaultModel, options.Model);
        Assert.Equal(LlmOptions.DefaultApiKey, options.ApiKey);
        Assert.Equal(LlmOptions.DefaultTimeoutMs, options.TimeoutMs);
        // The OpenAI client rejects an empty credential — the default must never be blank.
        Assert.False(string.IsNullOrWhiteSpace(options.ApiKey));
        // The base URL must carry the /v1 suffix or the OpenAI client 404s.
        Assert.EndsWith("/v1", options.BaseUrl);
    }

    [Fact]
    public void FromEnvironment_ZeroRouterNames_AreHonored()
    {
        var env = new Dictionary<string, string?>
        {
            ["ZEROROUTER_URL"] = "http://zero:5256/v1",
            ["ZEROROUTER_API_KEY"] = "sk-zero",
            ["KANBANFLOW_LLM_MODEL"] = "llama-3.3-70b",
            ["KANBANFLOW_LLM_TIMEOUT_MS"] = "1234",
        };

        var options = LlmOptions.FromEnvironment(k => env.GetValueOrDefault(k));

        Assert.Equal("http://zero:5256/v1", options.BaseUrl);
        Assert.Equal("sk-zero", options.ApiKey);
        Assert.Equal("llama-3.3-70b", options.Model);
        Assert.Equal(1234, options.TimeoutMs);
    }

    [Fact]
    public void FromEnvironment_KanbanFlowAliasNames_AreHonored()
    {
        var env = new Dictionary<string, string?>
        {
            ["KANBANFLOW_LLM_URL"] = "http://alias:9000/v1",
            ["KANBANFLOW_LLM_KEY"] = "sk-alias",
        };

        var options = LlmOptions.FromEnvironment(k => env.GetValueOrDefault(k));

        Assert.Equal("http://alias:9000/v1", options.BaseUrl);
        Assert.Equal("sk-alias", options.ApiKey);
    }

    [Fact]
    public void FromEnvironment_ZeroRouterNames_TakePrecedence_OverAliases()
    {
        var env = new Dictionary<string, string?>
        {
            ["ZEROROUTER_URL"] = "http://primary/v1",
            ["KANBANFLOW_LLM_URL"] = "http://alias/v1",
            ["ZEROROUTER_API_KEY"] = "sk-primary",
            ["KANBANFLOW_LLM_KEY"] = "sk-alias",
        };

        var options = LlmOptions.FromEnvironment(k => env.GetValueOrDefault(k));

        Assert.Equal("http://primary/v1", options.BaseUrl);
        Assert.Equal("sk-primary", options.ApiKey);
    }

    [Fact]
    public void FromEnvironment_BlankAndInvalidValues_FallBackToDefaults()
    {
        var env = new Dictionary<string, string?>
        {
            ["ZEROROUTER_URL"] = "   ",            // whitespace ⇒ ignored
            ["ZEROROUTER_API_KEY"] = "",            // blank ⇒ ignored (key must stay non-empty)
            ["KANBANFLOW_LLM_TIMEOUT_MS"] = "not-a-number", // unparseable ⇒ default kept
        };

        var options = LlmOptions.FromEnvironment(k => env.GetValueOrDefault(k));

        Assert.Equal(LlmOptions.DefaultBaseUrl, options.BaseUrl);
        Assert.Equal(LlmOptions.DefaultApiKey, options.ApiKey);
        Assert.Equal(LlmOptions.DefaultTimeoutMs, options.TimeoutMs);
    }

    [Fact]
    public void FromEnvironment_NonPositiveTimeout_IsIgnored()
    {
        var env = new Dictionary<string, string?> { ["KANBANFLOW_LLM_TIMEOUT_MS"] = "0" };
        var options = LlmOptions.FromEnvironment(k => env.GetValueOrDefault(k));
        Assert.Equal(LlmOptions.DefaultTimeoutMs, options.TimeoutMs);
    }

    [Fact]
    public void FromEnvironment_MalformedUrl_FallsBackToDefault()
    {
        var env = new Dictionary<string, string?> { ["ZEROROUTER_URL"] = "not-a-valid-url" };
        var options = LlmOptions.FromEnvironment(k => env.GetValueOrDefault(k));
        Assert.Equal(LlmOptions.DefaultBaseUrl, options.BaseUrl);
    }

    [Fact]
    public void FromEnvironment_NonHttpScheme_FallsBackToDefault()
    {
        // Valid URI but a non-HTTP(S) scheme ⇒ fall back to the default.
        var env = new Dictionary<string, string?> { ["ZEROROUTER_URL"] = "ftp://example.com/v1" };
        var options = LlmOptions.FromEnvironment(k => env.GetValueOrDefault(k));
        Assert.Equal(LlmOptions.DefaultBaseUrl, options.BaseUrl);
    }

    [Fact]
    public void FromEnvironment_UrlMissingV1Suffix_AppendsV1()
    {
        // Missing /v1 ⇒ appended (the OpenAI client appends `chat/completions` without inserting /v1).
        var env = new Dictionary<string, string?> { ["ZEROROUTER_URL"] = "http://host:1234" };
        var options = LlmOptions.FromEnvironment(k => env.GetValueOrDefault(k));
        Assert.Equal("http://host:1234/v1", options.BaseUrl);
    }

    [Fact]
    public void FromEnvironment_UrlWithV1Suffix_IsUnchanged()
    {
        // A valid absolute HTTP URL already ending in /v1 is preserved as-is.
        var env = new Dictionary<string, string?> { ["ZEROROUTER_URL"] = "http://host:1234/v1" };
        var options = LlmOptions.FromEnvironment(k => env.GetValueOrDefault(k));
        Assert.Equal("http://host:1234/v1", options.BaseUrl);
    }

    // ── constructor validation ─────────────────────────────────────────────────────
    [Fact]
    public void Constructor_NullChatClient_Throws() =>
        Assert.Throws<ArgumentNullException>(
            () => new LlmClient(null!, new LlmOptions(), NullLogger<LlmClient>.Instance));

    [Fact]
    public void Constructor_NonPositiveTimeout_Throws()
    {
        var fake = new FakeChatClient((_, _, _) => Task.FromResult(Reply("x", "m")));
        // TimeoutMs <= 0 would throw deep inside CancellationTokenSource at first use; reject at ctor.
        Assert.Throws<ArgumentException>(
            () => new LlmClient(fake, new LlmOptions { TimeoutMs = 0 }, NullLogger<LlmClient>.Instance));
    }

    // ── string-overload input guard ────────────────────────────────────────────────
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CompleteAsync_StringOverload_BlankUserPrompt_Throws(string? userPrompt)
    {
        var fake = new FakeChatClient((_, _, _) => Task.FromResult(Reply("x", "m")));
        var client = NewClient(fake);

        // null/blank user prompt fails fast locally instead of sending an empty user turn.
        await Assert.ThrowsAsync<ArgumentException>(
            () => client.CompleteAsync("a system prompt", userPrompt!, CancellationToken.None));
        Assert.Equal(0, fake.CallCount); // guarded locally ⇒ provider never called
    }

    /// <summary>
    /// Minimal hand-written <see cref="IChatClient"/> double: routes <see cref="GetResponseAsync"/>
    /// to a supplied handler and records call count / last arguments. Streaming and
    /// <see cref="GetService"/> are unused by <see cref="LlmClient"/>.
    /// </summary>
    private sealed class FakeChatClient(
        Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>> handler) : IChatClient
    {
        public int CallCount { get; private set; }
        public IReadOnlyList<ChatMessage>? LastMessages { get; private set; }
        public ChatOptions? LastOptions { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastMessages = messages.ToList();
            LastOptions = options;
            return handler(messages, options, cancellationToken);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("LlmClient does not use streaming.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
