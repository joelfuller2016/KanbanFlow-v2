using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Net.Sockets;
using System.Text;
using KanbanFlow.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OpenAI;

namespace KanbanFlow.Tests;

/// <summary>
/// <see cref="EmbeddingService"/> over the REAL OpenAI SDK pipeline (#549): the same
/// <c>OpenAIClient → GetEmbeddingClient → AsIEmbeddingGenerator</c> chain as <c>Program.cs</c>, with
/// only the HTTP transport swapped for a stub <see cref="HttpMessageHandler"/>, so every exception the
/// service classifies is one the SDK itself made. <see cref="EmbeddingServiceTests"/> builds the SDK's
/// transport-failure shape by hand; an SDK update that changed that shape would leave those tests
/// green over input production no longer throws (the trap #312 recorded). These go red instead.
/// </summary>
public sealed class EmbeddingServiceSdkShapeTests
{
    // One embedding in the OpenAI wire format, for a call that reaches a healthy provider.
    private const string OkBody = """
        {"object":"list","data":[{"object":"embedding","index":0,"embedding":[0.1,0.2,0.3]}],"model":"stub-model","usage":{"prompt_tokens":1,"total_tokens":1}}
        """;

    [Fact]
    public async Task RefusedConnect_IsNotRetried_AndStartsCooldown()
    {
        // What SocketsHttpHandler throws when nothing listens on the port (10061 = WSAECONNREFUSED).
        var (service, handler, generator) = Build();
        handler.Respond = () => throw new HttpRequestException(
            "No connection could be made because the target machine actively refused it.",
            new SocketException(10061));

        var first = await service.EmbedAsync("first", CancellationToken.None);
        var requestsAfterFirst = handler.Requests;
        handler.Respond = Ok; // if the next call reached the provider it would now succeed
        var second = await service.EmbedAsync("second", CancellationToken.None);

        // The shape EmbeddingServiceTests.SdkTransportFailure builds by hand: the SDK wraps the
        // transport's HttpRequestException in a ClientResultException that has no response.
        var thrown = Assert.IsAssignableFrom<ClientResultException>(Assert.Single(generator.Thrown));
        Assert.Equal(0, thrown.Status);
        Assert.IsType<HttpRequestException>(thrown.InnerException);

        Assert.False(first.Ok);
        Assert.Equal(1, requestsAfterFirst);           // provider-unavailable ⇒ not retried
        Assert.Contains("cooling down", second.Error); // ...and the window opened
        Assert.Equal(1, handler.Requests);             // so the next call never reached the provider
    }

    [Fact]
    public async Task ServerError_IsRetriedOnce_AndStartsCooldown()
    {
        var (service, handler, generator) = Build();
        handler.Respond = () => Error(HttpStatusCode.InternalServerError);

        var first = await service.EmbedAsync("first", CancellationToken.None);
        var requestsAfterFirst = handler.Requests;
        handler.Respond = Ok;
        var second = await service.EmbedAsync("second", CancellationToken.None);

        Assert.Equal(2, generator.Thrown.Count);
        Assert.All(generator.Thrown, ex => Assert.Equal(500, Assert.IsAssignableFrom<ClientResultException>(ex).Status));
        Assert.Contains("HTTP 500", first.Error);
        Assert.Equal(2, requestsAfterFirst);           // a 5xx is transient ⇒ retried once
        Assert.Contains("cooling down", second.Error); // ...and the window opened
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task ClientError_IsNotRetried_AndStartsNoCooldown()
    {
        // The provider answered: a 4xx says nothing about its availability, and asking again is cheap.
        var (service, handler, generator) = Build();
        handler.Respond = () => Error(HttpStatusCode.BadRequest);

        var first = await service.EmbedAsync("first", CancellationToken.None);
        var requestsAfterFirst = handler.Requests;
        handler.Respond = Ok;
        var second = await service.EmbedAsync("second", CancellationToken.None);

        Assert.Equal(400, Assert.IsAssignableFrom<ClientResultException>(Assert.Single(generator.Thrown)).Status);
        Assert.Contains("HTTP 400", first.Error);
        Assert.Equal(1, requestsAfterFirst); // a 4xx is not retried
        Assert.True(second.Ok);              // no window: the next call reached the provider...
        Assert.Equal(3, second.Dimensions);  // ...and the SDK parsed its real response
        Assert.Equal(2, handler.Requests);
    }

    // Program.cs's embedding wiring with only the transport swapped. RetryPolicy maxRetries 0 mirrors
    // Program.cs as well: the SDK makes one HTTP request per attempt, so the request counts above are
    // EmbeddingService's own retry policy and nothing else. The .invalid host (RFC 2606) never
    // resolves, so a request that bypassed the stub fails and never reaches a real network.
    private static (EmbeddingService Service, StubHandler Handler, RecordingGenerator Generator) Build()
    {
        var handler = new StubHandler();
        var client = new OpenAIClient(
            new ApiKeyCredential("stub-key"),
            new OpenAIClientOptions
            {
                Endpoint = new Uri("http://stub.invalid/v1"),
                RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
                Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
            });
        var generator = new RecordingGenerator(client.GetEmbeddingClient("stub-model").AsIEmbeddingGenerator());
        var service = new EmbeddingService(
            generator,
            new EmbeddingOptions { Model = "stub-model", TimeoutMs = 5_000 },
            NullLogger<EmbeddingService>.Instance,
            cacheCapacity: 0,
            timeProvider: new FakeTimeProvider());
        return (service, handler, generator);
    }

    private static HttpResponseMessage Ok() => Json(HttpStatusCode.OK, OkBody);

    private static HttpResponseMessage Error(HttpStatusCode status) =>
        Json(status, $$$"""{"error":{"message":"stub {{{(int)status}}}","type":"stub_error"}}""");

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>
    /// The network under the SDK's own <see cref="HttpClientPipelineTransport"/>: counts the requests
    /// that reach it and answers each with <see cref="Respond"/>, which may throw like a failed connect.
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpResponseMessage> Respond { get; set; } = Ok;
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(Respond());
        }
    }

    /// <summary>
    /// Passes every call through to the SDK's generator and records each exception it throws into
    /// <see cref="EmbeddingService"/>, so a test can pin the exact shape the service classifies.
    /// </summary>
    private sealed class RecordingGenerator(IEmbeddingGenerator<string, Embedding<float>> inner)
        : DelegatingEmbeddingGenerator<string, Embedding<float>>(inner)
    {
        public List<Exception> Thrown { get; } = new();

        public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            try
            {
                return await base.GenerateAsync(values, options, cancellationToken);
            }
            catch (Exception ex)
            {
                Thrown.Add(ex);
                throw;
            }
        }
    }
}
