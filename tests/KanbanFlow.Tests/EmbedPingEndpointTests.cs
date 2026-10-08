using System.ClientModel;
using System.Net;
using System.Text.Json;
using KanbanFlow.Data;
using KanbanFlow.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace KanbanFlow.Tests;

/// <summary>
/// <c>GET /api/admin/embed-ping</c> over REAL HTTP inside the failure cooldown (#549).
///
/// <para>The route has to reach the provider even while <see cref="IEmbeddingService.EmbedAsync"/> is
/// failing fast, or the probe would report the cooldown instead of live provider state. That is a
/// property of the ROUTE: <see cref="EmbeddingService.ProbeAsync"/>'s own tests stay green if the
/// route goes back to calling <c>EmbedAsync</c>. So this boots a Kestrel host on <c>127.0.0.1:0</c>,
/// maps the admin routes over a real <see cref="EmbeddingService"/>, and counts the provider calls
/// behind the response that comes back over the wire.</para>
/// </summary>
public sealed class EmbedPingEndpointTests
{
    private const string RefusedMessage =
        "No connection could be made because the target machine actively refused it. (localhost:5256)";

    [Fact]
    public async Task EmbedPing_InsideCooldown_ReachesTheProvider()
    {
        var generator = new RefusingGenerator();
        var options = new EmbeddingOptions { Model = "ping-model", TimeoutMs = 5_000 };
        // A FakeTimeProvider nobody advances, so the window cannot run out during the test.
        var service = new EmbeddingService(
            generator, options, NullLogger<EmbeddingService>.Instance, cacheCapacity: 0, timeProvider: new FakeTimeProvider());
        await using var host = await PingHost.StartAsync(service, options);

        await service.EmbedAsync("trip", CancellationToken.None);                  // refused ⇒ cooldown starts
        var skipped = await service.EmbedAsync("skipped", CancellationToken.None);
        Assert.Contains("cooling down", skipped.Error);                             // the window is open
        Assert.Equal(1, generator.Calls);

        var ping = await host.GetEmbedPingAsync();
        var error = ping.GetProperty("error").GetString();

        Assert.False(ping.GetProperty("ok").GetBoolean());
        Assert.DoesNotContain("cooling down", error);
        Assert.Contains("actively refused", error); // the provider's own failure...
        Assert.Equal(2, generator.Calls);           // ...from a real call made inside the window
    }

    /// <summary>
    /// A dead router: counts every call and refuses it in the shape the SDK really throws (a
    /// <see cref="ClientResultException"/> with no response around the transport's
    /// <see cref="HttpRequestException"/>; see <c>EmbeddingServiceSdkShapeTests</c>).
    /// </summary>
    private sealed class RefusingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        private int _calls;

        // The route runs on a Kestrel thread, so the count is read and written with Interlocked.
        public int Calls => Volatile.Read(ref _calls);

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            throw new ClientResultException(RefusedMessage, response: null, innerException: new HttpRequestException(RefusedMessage));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>
    /// A real Kestrel host on an ephemeral loopback port with the admin routes mapped over the given
    /// embedding service, built the way <c>AdminHealthEndpointTests</c> builds its host.
    /// </summary>
    private sealed class PingHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly HttpClient _client;

        private PingHost(WebApplication app, HttpClient client)
        {
            _app = app;
            _client = client;
        }

        public static async Task<PingHost> StartAsync(IEmbeddingService embeddings, EmbeddingOptions options)
        {
            // Development so a server-side exception comes back in the response body.
            var builder = WebApplication.CreateBuilder(
                new WebApplicationOptions { EnvironmentName = "Development" });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0"); // ephemeral port: tests can run in parallel
            builder.Services.AddSingleton(embeddings);
            builder.Services.AddSingleton(options);

            // ASP.NET Core builds the WHOLE endpoint table on the first request, so embed-ping answers
            // only once the other admin routes' dependencies are registered too (measured in
            // AdminHealthEndpointTests). They are unusable stubs, so straying onto one says so.
            foreach (var offRoute in new[]
                     {
                         typeof(IAppStartupInfo), typeof(DbPathResolution), typeof(IDbContextFactory<AppDbContext>),
                         typeof(ITestRunnerService), typeof(ILlmClient), typeof(LlmOptions), typeof(IMemoryService)
                     })
            {
                builder.Services.AddSingleton(offRoute, (Func<IServiceProvider, object>)(_ =>
                    throw new NotSupportedException("this host maps /api/admin/embed-ping only")));
            }

            var app = builder.Build();
            try
            {
                app.MapAdminEndpoints();
                await app.StartAsync();
                return new PingHost(app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) });
            }
            catch
            {
                // As in AdminHealthEndpointTests: a host that failed to start has no other owner to
                // dispose it, and a leaked listener would land on whatever test runs next.
                await app.DisposeAsync();
                throw;
            }
        }

        public async Task<JsonElement> GetEmbedPingAsync()
        {
            using var response = await _client.GetAsync("/api/admin/embed-ping");
            var json = await response.Content.ReadAsStringAsync();

            // embed-ping always answers 200, fail-soft; anything else means the host is broken, so the
            // body is reported verbatim rather than as a JSON parse failure.
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new Xunit.Sdk.XunitException(
                    $"/api/admin/embed-ping answered HTTP {(int)response.StatusCode}. Body: <{json}>");
            }

            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
