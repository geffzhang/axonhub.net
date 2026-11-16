using System.Text.Json;
using AxonHub.Api.Settings;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace AxonHub.Api.Catalog;

/// <summary>
/// The catalog payload served to the admin UI (biz.CatalogSnapshot,
/// catalog.go:35-41).
/// </summary>
public sealed record CatalogSnapshot(
    JsonElement Data,
    DateTime? FetchedAt,
    string Source,
    bool Filtered);

/// <summary>Port of <c>biz.CatalogService</c>'s GraphQL-facing surface.</summary>
public interface ICatalogService
{
    /// <summary><c>CatalogService.Snapshot(ctx, filtered)</c> (catalog.go:117-119).</summary>
    Task<CatalogSnapshot> SnapshotAsync(
        bool filtered,
        CancellationToken cancellationToken = default);

    /// <summary><c>CatalogService.Refresh(ctx)</c> (catalog.go:121-133).</summary>
    Task<CatalogSnapshot> RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary><c>CatalogService.Invalidate()</c> (catalog.go:298-303).</summary>
    void Invalidate();
}

/// <summary>
/// Port of <c>internal/server/biz/catalog.go</c>: fetches the public model
/// catalog, caches it for the configured refresh interval, falls back to the
/// last good (or bundled) snapshot when the upstream is unreachable, and
/// applies the AxonHub developer filters on read.
/// </summary>
/// <remarks>
/// The reference runs as a process-local singleton whose scheduler registration
/// arms a FixRate task; the port keeps one process-wide instance (C238 covers
/// the startup timing and cluster scope), the same RWMutex shape, and a single
/// shared in-flight fetch so concurrent cold readers issue one upstream
/// request, like <c>singleflight.Group</c>.
/// </remarks>
public sealed class CatalogService : ICatalogService
{
    /// <summary>biz.catalogSourceUpstream.</summary>
    public const string SourceUpstream = "upstream";

    /// <summary>biz.catalogSourceFallback.</summary>
    public const string SourceFallback = "fallback";

    /// <summary>biz.catalogFetchTimeout.</summary>
    public static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(20);

    private const string FallbackResource = "AxonHub.Api.Catalog.Data.providers.json";
    private const string ExtraModelsResource = "AxonHub.Api.Catalog.Data.models.json";

    private static readonly Lazy<byte[]> FallbackJson = new(
        () => ReadResource(FallbackResource),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<byte[]> ExtraModelsJson = new(
        () => ReadResource(ExtraModelsResource),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IExecutor? _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();

    private CatalogCache? _cache;
    private Task<CatalogCache>? _refreshInFlight;

    public CatalogService(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        IExecutor? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _httpClient = httpClient;
    }

    public async Task<CatalogSnapshot> SnapshotAsync(
        bool filtered,
        CancellationToken cancellationToken = default)
        => SnapshotFromCache(
            await EnsureCacheAsync(cancellationToken).ConfigureAwait(false),
            filtered);

    public async Task<CatalogSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var settings = await ReadSettingsOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(FetchTimeout);

        var raw = FetchUpstream(
            timeout.Token,
            CatalogSettingsRules.EffectiveUpstreamUrl(settings));

        return SnapshotFromCache(StoreCache(raw, SourceUpstream), filtered: true);
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _cache = null;
        }
    }

    private async Task<CatalogCache> EnsureCacheAsync(CancellationToken cancellationToken)
    {
        var settings = await ReadSettingsOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (FreshCache(settings) is { } fresh)
        {
            return fresh;
        }

        // singleflight.Group.Do("catalog-refresh"): concurrent callers share the
        // leader's fetch and result.
        Task<CatalogCache> task;
        lock (_gate)
        {
            task = _refreshInFlight ??= RefreshCacheAsync(cancellationToken);
        }

        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The reference swallows the singleflight error and serves the last
            // good (or bundled) snapshot.
            return Cached() ?? FallbackCache();
        }
        catch (Exception)
        {
            return Cached() ?? FallbackCache();
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_refreshInFlight, task))
                {
                    _refreshInFlight = null;
                }
            }
        }
    }

    private async Task<CatalogCache> RefreshCacheAsync(CancellationToken cancellationToken)
    {
        var settings = await ReadSettingsOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (FreshCache(settings) is { } fresh)
        {
            return fresh;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(FetchTimeout);

        try
        {
            var raw = FetchUpstream(
                timeout.Token,
                CatalogSettingsRules.EffectiveUpstreamUrl(settings));

            return StoreCache(raw, SourceUpstream);
        }
        catch
        {
            // Logged in the reference; the caller keeps serving the last good
            // snapshot, or the bundled fallback for one refresh window.
            return Cached() ?? StoreCache(FallbackCache().Raw, SourceFallback);
        }
    }

    private CatalogCache? FreshCache(CatalogSettings settings)
    {
        var cached = Cached();
        if (cached is null)
        {
            return null;
        }

        // time.Since(cached.fetchedAt) >= refreshSeconds means stale.
        if (_timeProvider.GetUtcNow() - cached.FetchedAt
            >= TimeSpan.FromSeconds(settings.RefreshSeconds))
        {
            return null;
        }

        return cached;
    }

    private CatalogCache? Cached()
    {
        lock (_gate)
        {
            return _cache;
        }
    }

    private CatalogCache StoreCache(CatalogFile raw, string source)
    {
        var cache = new CatalogCache(
            raw,
            _timeProvider.GetUtcNow().UtcDateTime,
            source);

        lock (_gate)
        {
            _cache = cache;
        }

        return cache;
    }

    private CatalogFile FetchUpstream(CancellationToken cancellationToken, string upstreamUrl)
    {
        if (_httpClient is null)
        {
            throw new InvalidOperationException("catalog http client is not configured");
        }

        HttpResponseData response;
        try
        {
            response = _httpClient.Do(cancellationToken, new HttpRequestData
            {
                Method = "GET",
                Url = upstreamUrl,
                Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
                {
                    ["Accept"] = ["application/json"],
                },
            });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("fetch catalog: " + ex.Message, ex);
        }

        if (response.StatusCode != 200)
        {
            throw new InvalidOperationException(
                "fetch catalog: unexpected status "
                + response.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        CatalogFile data;
        try
        {
            data = CatalogParser.ParseFile(response.Body ?? []);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("parse catalog: " + ex.Message, ex);
        }

        if (data.Providers is null)
        {
            throw new InvalidOperationException("catalog is missing providers");
        }

        return data;
    }

    private CatalogSnapshot SnapshotFromCache(CatalogCache cache, bool filtered)
    {
        var data = cache.Raw;

        if (filtered)
        {
            var prepared = CatalogFilter.FilterProviders(
                data,
                CatalogFilter.DefaultDeveloperIds);
            try
            {
                CatalogFilter.MergeExtraModels(prepared, ExtraModelsJson.Value);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    "merge extra catalog models: " + ex.Message,
                    ex);
            }

            CatalogFilter.SortCatalogModels(prepared);
            data = prepared;
        }

        var payload = data.ToUtf8Bytes();

        var source = cache.Source.Length == 0 ? SourceFallback : cache.Source;
        return new CatalogSnapshot(
            JsonDocument.Parse(payload).RootElement.Clone(),
            cache.FetchedAt,
            source,
            filtered);
    }

    /// <summary>
    /// Port of <c>fallbackCache()</c>: decode the bundled snapshot, substituting
    /// an empty provider map when it does not decode or carries no providers.
    /// Each call produces a fresh cache entry; whether it is stored depends on
    /// the caller (the fetch-error path stores it, the error path does not).
    /// </summary>
    private CatalogCache FallbackCache()
        => new(FallbackRaw(), _timeProvider.GetUtcNow().UtcDateTime, SourceFallback);

    private static CatalogFile FallbackRaw()
    {
        try
        {
            var data = CatalogParser.ParseFile(FallbackJson.Value);
            if (data.Providers is not null)
            {
                return data;
            }
        }
        catch (JsonException)
        {
            // The bundled JSON is valid; the reference still guards the decode.
        }

        return new CatalogFile
        {
            Providers = new Dictionary<string, CatalogProvider>(StringComparer.Ordinal),
        };
    }

    /// <summary>
    /// Port of <c>CatalogSettingsOrDefault</c>: the reference swallows every
    /// read failure (including a cancelled context) and falls back to the
    /// defaults.
    /// </summary>
    private async Task<CatalogSettings> ReadSettingsOrDefaultAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var settings = scope.ServiceProvider
                .GetRequiredService<ISystemSettingsAdminService>();
            return await settings.GetCatalogSettingsOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return CatalogSettingsRules.Default;
        }
    }

    private static byte[] ReadResource(string name)
    {
        using var stream = typeof(CatalogService).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                "missing embedded catalog resource: " + name);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private sealed record CatalogCache(CatalogFile Raw, DateTime FetchedAt, string Source);
}
