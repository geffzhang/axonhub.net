using System.Text;
using AxonHub.Api.Catalog;
using AxonHub.Api.Settings;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AxonHub.Api.Tests;

/// <summary>
/// Ports <c>internal/server/biz/catalog_test.go</c>: the bundled fallback, the
/// upstream refresh, the single-flight cold read, the request shape and the
/// fetch/parse error texts.
/// </summary>
public sealed class CatalogServiceTests
{
    [Fact]
    public async Task FallsBackAndRefreshes()
    {
        var store = new FakeSystemStore();
        using var provider = BuildProvider(store, out var scopeFactory);

        var withoutClient = new CatalogService(scopeFactory, TimeProvider.System);
        var fallback = await withoutClient.SnapshotAsync(true);

        Assert.Equal(CatalogService.SourceFallback, fallback.Source);
        Assert.True(fallback.Filtered);
        Assert.Contains("\"providers\"", fallback.Data.GetRawText(), StringComparison.Ordinal);

        const string upstreamUrl = "https://catalog.example/all.json";
        await new SystemSettingsAdminService(store).SetCatalogSettingsAsync(
            new CatalogSettings(upstreamUrl, 60));

        var executor = new RecordingExecutor(
            body:
            """{"providers":{"openai":{"id":"openai","models":[{"id":"gpt-hot"}]}}}""");
        var service = new CatalogService(scopeFactory, TimeProvider.System, executor);

        var refreshed = await service.RefreshAsync();

        Assert.Equal(CatalogService.SourceUpstream, refreshed.Source);
        Assert.Contains("gpt-hot", refreshed.Data.GetRawText(), StringComparison.Ordinal);
        Assert.NotNull(refreshed.FetchedAt);
        Assert.InRange(
            refreshed.FetchedAt!.Value,
            DateTime.UtcNow.AddSeconds(-5),
            DateTime.UtcNow.AddSeconds(5));

        service.Invalidate();
        var afterInvalidate = await service.SnapshotAsync(true);

        Assert.Equal(CatalogService.SourceUpstream, afterInvalidate.Source);
        Assert.Equal(2, executor.Hits);
    }

    [Fact]
    public async Task CachesFallbackAndDeduplicatesFetch()
    {
        var store = new FakeSystemStore();
        using var provider = BuildProvider(store, out var scopeFactory);

        await new SystemSettingsAdminService(store).SetCatalogSettingsAsync(
            new CatalogSettings("https://catalog.example/all.json", 3600));

        var executor = new RecordingExecutor(
            statusCode: 502,
            body: "unavailable",
            delay: TimeSpan.FromMilliseconds(150));
        var service = new CatalogService(scopeFactory, TimeProvider.System, executor);

        var sources = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => Task.Run(
                async () => (await service.SnapshotAsync(true)).Source)));

        Assert.Equal(8, sources.Length);
        Assert.All(sources, source => Assert.Equal(CatalogService.SourceFallback, source));
        Assert.Equal(1, executor.Hits);

        var second = await service.SnapshotAsync(true);

        Assert.Equal(CatalogService.SourceFallback, second.Source);
        Assert.Equal(1, executor.Hits);
    }

    [Fact]
    public async Task SnapshotFetchesUpstreamOncePerRefreshWindowAndUsesTheRequestShape()
    {
        var store = new FakeSystemStore();
        using var provider = BuildProvider(store, out var scopeFactory);

        const string upstreamUrl = "https://catalog.example/all.json";
        await new SystemSettingsAdminService(store).SetCatalogSettingsAsync(
            new CatalogSettings(upstreamUrl, 3600));

        var executor = new RecordingExecutor(
            body:
            """
            {"providers":{
              "openai":{"id":"openai","models":[{"id":"gpt-hot"}]},
              "unknown-lab":{"id":"unknown-lab","models":[{"id":"secret-model"}]}
            }}
            """);
        var service = new CatalogService(scopeFactory, TimeProvider.System, executor);

        var snapshot = await service.SnapshotAsync(true);

        Assert.Equal(CatalogService.SourceUpstream, snapshot.Source);
        Assert.Equal(
            "gpt-hot",
            snapshot.Data.GetProperty("providers").GetProperty("openai")
                .GetProperty("models")[0].GetProperty("id").GetString());
        Assert.False(snapshot.Data.GetProperty("providers").TryGetProperty(
            "unknown-lab",
            out _));

        var request = Assert.Single(executor.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal(upstreamUrl, request.Url);
        Assert.NotNull(request.Headers);
        Assert.Contains("application/json", request.Headers!["Accept"]);

        var second = await service.SnapshotAsync(true);

        Assert.Equal(CatalogService.SourceUpstream, second.Source);
        Assert.Equal(1, executor.Hits);

        service.Invalidate();
        await service.SnapshotAsync(true);

        Assert.Equal(2, executor.Hits);
    }

    [Theory]
    [InlineData(502, "unavailable", "fetch catalog: unexpected status 502")]
    [InlineData(200, "{}", "catalog is missing providers")]
    [InlineData(200, """{"providers":null}""", "catalog is missing providers")]
    public async Task RefreshSurfacesTheFetchAndShapeErrors(
        int statusCode,
        string body,
        string expected)
    {
        var store = new FakeSystemStore();
        using var provider = BuildProvider(store, out var scopeFactory);

        await new SystemSettingsAdminService(store).SetCatalogSettingsAsync(
            new CatalogSettings("https://catalog.example/all.json", 3600));

        var service = new CatalogService(
            scopeFactory,
            TimeProvider.System,
            new RecordingExecutor(statusCode, body));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RefreshAsync());

        Assert.Equal(expected, error.Message);
    }

    [Fact]
    public async Task RefreshSurfacesTheParseError()
    {
        var store = new FakeSystemStore();
        using var provider = BuildProvider(store, out var scopeFactory);

        var service = new CatalogService(
            scopeFactory,
            TimeProvider.System,
            new RecordingExecutor(body: "not json"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RefreshAsync());

        Assert.StartsWith("parse catalog: ", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshSurfacesTransportFailures()
    {
        var store = new FakeSystemStore();
        using var provider = BuildProvider(store, out var scopeFactory);

        var service = new CatalogService(
            scopeFactory,
            TimeProvider.System,
            new RecordingExecutor(
                failure: new InvalidOperationException("HTTP request failed: boom")));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RefreshAsync());

        Assert.Equal("fetch catalog: HTTP request failed: boom", error.Message);
    }

    [Fact]
    public async Task RefreshWithoutAClientReportsNotConfigured()
    {
        var store = new FakeSystemStore();
        using var provider = BuildProvider(store, out var scopeFactory);

        var service = new CatalogService(scopeFactory, TimeProvider.System);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RefreshAsync());

        Assert.Equal("catalog http client is not configured", error.Message);
    }

    private static ServiceProvider BuildProvider(
        FakeSystemStore store,
        out IServiceScopeFactory scopeFactory)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISystemStore>(store);
        services.AddScoped<ISystemSettingsAdminService>(
            _ => new SystemSettingsAdminService(store));
        var provider = services.BuildServiceProvider();
        scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        return provider;
    }

    private sealed class RecordingExecutor(
        int statusCode = 200,
        string body = "",
        TimeSpan? delay = null,
        Exception? failure = null) : IExecutor
    {
        private readonly List<HttpRequestData> _requests = [];
        private int _hits;

        public IReadOnlyList<HttpRequestData> Requests
        {
            get
            {
                lock (_requests)
                {
                    return [.. _requests];
                }
            }
        }

        public int Hits => Volatile.Read(ref _hits);

        public HttpResponseData Do(CancellationToken cancellationToken, HttpRequestData request)
        {
            _ = cancellationToken;
            lock (_requests)
            {
                _requests.Add(request);
            }

            Interlocked.Increment(ref _hits);

            if (delay is { } wait)
            {
                Thread.Sleep(wait);
            }

            if (failure is not null)
            {
                throw failure;
            }

            return new HttpResponseData
            {
                StatusCode = statusCode,
                Body = Encoding.UTF8.GetBytes(body),
            };
        }

        public IStream<StreamEvent> DoStream(
            CancellationToken cancellationToken,
            HttpRequestData request)
            => throw new NotSupportedException();
    }
}
