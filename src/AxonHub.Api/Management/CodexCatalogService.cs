using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using AxonHub.Abstractions;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm.Http;
using AxonHub.Llm.OAuth;
using AxonHub.Llm.Transformer.OpenAI.Codex;
using AxonHub.Llm.Transformer.Shared;
using AxonHub.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Orleans;

namespace AxonHub.Api.Management;

/// <summary>
/// Port of biz.CodexCatalogError (internal/server/biz/codex_catalog.go:24-30):
/// the catalog fetch never throws into the transport; every failure is a
/// status-carrying error. <see cref="Status"/> is the HTTP status the runtime
/// route answers with; the management surface only reads the message and
/// <see cref="UpstreamStatus"/>.
/// </summary>
public sealed class CodexCatalogException : Exception
{
    public CodexCatalogException(int status, string message, int upstreamStatus = 0)
        : base(message)
    {
        Status = status;
        UpstreamStatus = upstreamStatus;
    }

    public int Status { get; }

    /// <summary>The upstream status when the failure came from an HTTP response.</summary>
    public int UpstreamStatus { get; }
}

/// <summary>
/// Port of biz.CodexCatalog (codex_catalog.go:32-36). The management surface
/// only reads <see cref="Count"/>; the raw fragments <c>Intersect</c> returns
/// belong to the not-yet-ported <c>/codex/*</c> runtime routes (C244).
/// </summary>
public sealed class CodexCatalog
{
    public CodexCatalog(int count)
    {
        Count = count;
    }

    /// <summary>Port of <c>(*CodexCatalog).Count</c>.</summary>
    public int Count { get; }
}

/// <summary>
/// Port of <c>ChannelService.FetchCodexCatalog</c> (codex_catalog.go:84-173):
/// fetches and strictly validates an upstream model catalog for one Codex
/// channel. Concurrent callers for the same channel and version share one
/// flight, which keeps running if the caller gives up.
/// </summary>
public interface ICodexCatalogService
{
    /// <summary>
    /// <paramref name="version"/> is the runtime <c>client_version</c>; the
    /// managed <c>testCodexCatalog</c> passes the empty string.
    /// </summary>
    /// <exception cref="CodexCatalogException">Every reference failure shape.</exception>
    Task<CodexCatalog> FetchAsync(
        long channelId,
        string version,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The reference's flight is detach-safe because it hands the closure a
/// <c>context.WithoutCancel</c> and re-attaches the ent client; the port's
/// equivalent is a fresh async <see cref="IServiceScope"/> inside the flight,
/// so a disposed request scope can never be touched. The 30s budget belongs to
/// the flight, not to the caller.
/// </summary>
public sealed class CodexCatalogService(
    IServiceScopeFactory scopeFactory,
    IExecutor executor,
    IProxyExecutorFactory proxyExecutors,
    IGrainFactory grainFactory,
    TimeProvider timeProvider) : ICodexCatalogService
{
    /// <summary>codex_catalog.go:86: <c>30*time.Second</c> detached budget.</summary>
    public static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(30);

    /// <summary>channel.TypeCodex.</summary>
    public const string CodexChannelType = "codex";

    private readonly ConcurrentDictionary<string, Lazy<Task<CodexCatalog>>> _flights =
        new(StringComparer.Ordinal);

    public async Task<CodexCatalog> FetchAsync(
        long channelId,
        string version,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(version);

        // singleflight key: fmt.Sprintf("%d:%s", channelID, version).
        var key = channelId.ToString(CultureInfo.InvariantCulture) + ":" + version;

        Lazy<Task<CodexCatalog>>? lazy = null;
        lazy = new Lazy<Task<CodexCatalog>>(
            () => RunFlightAsync(key, channelId, version, lazy!),
            LazyThreadSafetyMode.ExecutionAndPublication);

        var flight = _flights.GetOrAdd(key, lazy);
        try
        {
            return await flight.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // codex_catalog.go:91-93: the caller stopped waiting; the flight
            // keeps running for the next caller.
            throw new CodexCatalogException(
                502,
                "Codex catalog request was cancelled");
        }
    }

    private async Task<CodexCatalog> RunFlightAsync(
        string key,
        long channelId,
        string version,
        Lazy<Task<CodexCatalog>> self)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            using var timeout = new CancellationTokenSource(FetchTimeout);
            return await FetchCoreAsync(
                    scope.ServiceProvider,
                    channelId,
                    version,
                    timeout.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            _flights.TryRemove(new KeyValuePair<string, Lazy<Task<CodexCatalog>>>(key, self));
        }
    }

    /// <summary>Port of <c>fetchCodexCatalog</c> (codex_catalog.go:106-173).</summary>
    private async Task<CodexCatalog> FetchCoreAsync(
        IServiceProvider services,
        long channelId,
        string version,
        CancellationToken cancellationToken)
    {
        var channels = services.GetRequiredService<IChannelStore>();

        // The live row is authoritative for the type check and the base URL
        // (codex_catalog.go:107-127).
        ChannelRecord? entity;
        try
        {
            entity = await channels.GetByIdAsync(channelId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // ent Channel.Get error and not-found share the 503 branch.
            entity = null;
        }

        if (entity is null
            || !string.Equals(entity.Type, CodexChannelType, StringComparison.Ordinal))
        {
            throw new CodexCatalogException(503, "Codex catalog source is unavailable");
        }

        // Port of GetEnabledChannel: the reference reads its in-process cache,
        // which cannot fail; a failing snapshot read is treated as a miss so the
        // live-row branch below builds the credentials instead.
        ChannelDescriptor? source = null;
        try
        {
            var enabledProvider = services.GetRequiredService<IEnabledChannelProvider>();
            var enabled = await enabledProvider.ListEnabledAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var descriptor in enabled)
            {
                if (descriptor.Id == channelId)
                {
                    source = descriptor;
                    break;
                }
            }
        }
        catch (Exception)
        {
            source = null;
        }

        ITokenGetter tokens;
        try
        {
            tokens = source is not null
                ? CreateCachedTokenProvider(source)
                : CreateLiveTokenProvider(entity);
        }
        catch (CodexCatalogException)
        {
            throw;
        }
        catch (Exception)
        {
            // Cached branch: the outbound type check / token provider of the
            // cached channel. Live branch: codexCatalogOutbound's build errors.
            throw source is not null
                ? new CodexCatalogException(502, "Codex catalog provider is unavailable")
                : new CodexCatalogException(502, "Codex catalog credentials are unavailable");
        }

        // wss:// -> https://, ws:// -> http:// (codex_catalog.go:128-133).
        var baseUrl = entity.BaseUrl ?? string.Empty;
        if (baseUrl.StartsWith("wss://", StringComparison.Ordinal))
        {
            baseUrl = "https://" + baseUrl["wss://".Length..];
        }
        else if (baseUrl.StartsWith("ws://", StringComparison.Ordinal))
        {
            baseUrl = "http://" + baseUrl["ws://".Length..];
        }

        HttpRequestData request;
        try
        {
            // codex.ModelsRequest resolves the OAuth token lazily; a token
            // failure is the reference's "authentication failed" branch.
            request = CodexModels.ModelsRequest(cancellationToken, tokens, baseUrl);
        }
        catch (Exception)
        {
            throw new CodexCatalogException(502, "Codex catalog authentication failed");
        }

        // url.Parse(req.URL) (codex_catalog.go:138-141).
        if (!GoUrl.TryParse(request.Url, out _, out _, out _))
        {
            throw new CodexCatalogException(502, "invalid Codex catalog URL");
        }

        // query.Set("client_version", version) when version != "", then
        // url.Values.Encode() (codex_catalog.go:142-147).
        request.Url = RewriteCatalogUrl(request.Url, version);

        // The cached branch reuses the enabled channel's HTTP client, which was
        // built from that snapshot's proxy; the live branch builds from the row
        // (codex_catalog.go:114-122, 183).
        var catalogExecutor = ResolveExecutor(source?.Settings ?? entity.Settings)
            .WithSameOriginRedirects();

        HttpResponseData response;
        try
        {
            response = catalogExecutor.Do(cancellationToken, request);
        }
        catch (HttpClientError ex)
        {
            throw new CodexCatalogException(
                502,
                "Codex catalog upstream HTTP "
                + ex.StatusCode.ToString(CultureInfo.InvariantCulture),
                ex.StatusCode);
        }
        catch (Exception)
        {
            throw new CodexCatalogException(502, "Codex catalog transport failed");
        }

        if (response.StatusCode < 200 || response.StatusCode >= 300)
        {
            throw new CodexCatalogException(
                502,
                "Codex catalog upstream HTTP "
                + response.StatusCode.ToString(CultureInfo.InvariantCulture),
                response.StatusCode);
        }

        return ParseCodexCatalog(response.Body ?? []);
    }

    /// <summary>
    /// The token provider half of <c>buildCodexOutbound</c> over the enabled
    /// cache's snapshot: OAuth channels read through the per-channel token
    /// grain (§146); key channels reuse the port's selection provider.
    /// </summary>
    private ITokenGetter CreateCachedTokenProvider(ChannelDescriptor source)
    {
        // buildChannelWithOutbounds gives both codex and fenno channels a
        // *codex.OutboundTransformer (channel_llm.go:1263-1272); the reference
        // type-asserts the cached outbound, so both pass here.
        if (source.Type is not ("codex" or "fenno"))
        {
            // The cached outbound is not a codex outbound.
            throw new CodexCatalogException(502, "Codex catalog provider is unavailable");
        }

        return CreateTokenProvider(
            source.Id,
            source.Type,
            source.Credentials,
            source.DisabledApiKeys,
            source.UpdatedAt);
    }

    /// <summary>
    /// Port of <c>codexCatalogOutbound</c>'s non-cached half
    /// (codex_catalog.go:182-194): credentials come from the live row.
    /// </summary>
    private ITokenGetter CreateLiveTokenProvider(ChannelRecord entity)
        => CreateTokenProvider(
            entity.Id,
            entity.Type,
            entity.Credentials,
            entity.DisabledApiKeys,
            entity.UpdatedAt);

    private ITokenGetter CreateTokenProvider(
        long channelId,
        string channelType,
        string? credentialsJson,
        string? disabledApiKeysJson,
        DateTime updatedAt)
    {
        if (!ChannelCredentials.IsOAuth(credentialsJson))
        {
            var allKeys = ChannelCredentials.GetAllApiKeys(credentialsJson);
            var enabledKeys = ChannelCredentials.GetEnabledApiKeys(
                credentialsJson,
                disabledApiKeysJson,
                timeProvider.GetUtcNow());

            if (enabledKeys.Count == 0 && allKeys.Count <= 1)
            {
                // The reference's getAPIKeyProvider panics here and the
                // singleflight re-panics, killing the process (C246). The port
                // reports the failure as a normal credential error instead.
                throw new InvalidOperationException(
                    "no enabled api key configured for channel " + channelId);
            }

            var provider = new ChannelApiKeyProvider(enabledKeys, allKeys);
            return new ApiKeyTokenProvider(_ => provider.Get());
        }

        var credentials = OAuthCredentialsJson.ResolveChannelCredentials(credentialsJson);
        return new GrainTokenGetter(
            grainFactory,
            new ChannelTokenSeed
            {
                ChannelId = channelId,
                ChannelType = channelType,
                Credentials = ChannelOAuthTokenMapping.ToToken(credentials),
                CredentialsJson = credentialsJson ?? "{}",
                UpdatedAt = updatedAt,
            });
    }

    private IExecutor ResolveExecutor(string? settingsRaw)
        => ParseSettingsLenient(settingsRaw)?.Proxy is { } proxy
            ? proxyExecutors.Create(new ProxyConfig
            {
                Type = proxy.Type,
                Url = proxy.Url,
                Username = proxy.Username,
                Password = proxy.Password,
                DisableConnectionReuse = proxy.DisableConnectionReuse ?? false,
            })
            : executor;

    private static ChannelSettingsDraft? ParseSettingsLenient(string? raw)
    {
        try
        {
            return ChannelWriteSerialization.ParseSettings(raw);
        }
        catch (JsonException)
        {
            // The reference decodes settings with Ent before reaching this
            // path; malformed JSON is treated as "no settings" (C013).
            return null;
        }
    }

    /// <summary>
    /// Port of the <c>url.Parse</c> → <c>Query()</c> → <c>query.Set</c> →
    /// <c>Encode()</c> → <c>String()</c> round trip (codex_catalog.go:138-147).
    /// The fragment is split first, so a '?' inside it stays part of the
    /// fragment; the scheme, authority and path are left byte-for-byte alone.
    /// </summary>
    private static string RewriteCatalogUrl(string url, string version)
    {
        var hash = url.IndexOf('#', StringComparison.Ordinal);
        var fragment = hash >= 0 ? url[hash..] : string.Empty;
        var withoutFragment = hash >= 0 ? url[..hash] : url;

        var question = withoutFragment.IndexOf('?', StringComparison.Ordinal);
        var head = question >= 0 ? withoutFragment[..question] : withoutFragment;
        var queryString = question >= 0 ? withoutFragment[(question + 1)..] : string.Empty;

        var query = HttpClientUtils.ParseQuery(queryString);
        if (version.Length > 0)
        {
            query["client_version"] = [version];
        }

        var encoded = HttpClientUtils.EncodeQuery(query);
        return encoded.Length > 0 ? head + "?" + encoded + fragment : head + fragment;
    }

    /// <summary>
    /// Port of <c>ParseCodexCatalog</c> (codex_catalog.go:38-59): the root must
    /// be an object with an exact <c>models</c> array, every element must carry
    /// a non-empty <c>slug</c>, and every deviation is the same 502 error.
    /// </summary>
    /// <exception cref="CodexCatalogException">invalid Codex model catalog.</exception>
    public static CodexCatalog ParseCodexCatalog(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw InvalidCatalog();
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                // A top-level null leaves Go's map nil, which is the same error.
                throw InvalidCatalog();
            }

            JsonElement models = default;
            var found = false;
            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, "models", StringComparison.Ordinal))
                {
                    // Go's map decode keeps the last duplicate key.
                    models = property.Value;
                    found = true;
                }
            }

            if (!found
                || models.ValueKind != JsonValueKind.Array)
            {
                // Missing models decodes nil RawMessage (syntax error); null
                // leaves the slice nil; both are invalid.
                throw InvalidCatalog();
            }

            var count = 0;
            foreach (var element in models.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    // null decodes to the zero struct, whose empty slug is
                    // invalid; every other non-object is a decode error.
                    throw InvalidCatalog();
                }

                var slug = string.Empty;
                foreach (var property in element.EnumerateObject())
                {
                    if (!string.Equals(property.Name, "slug", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (property.Value.ValueKind == JsonValueKind.Null)
                    {
                        // null into the string field is a no-op.
                        continue;
                    }

                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        throw InvalidCatalog();
                    }

                    slug = property.Value.GetString() ?? string.Empty;
                }

                if (slug.Length == 0)
                {
                    throw InvalidCatalog();
                }

                count++;
            }

            return new CodexCatalog(count);
        }
    }

    private static CodexCatalogException InvalidCatalog()
        => new(502, "invalid Codex model catalog");
}
