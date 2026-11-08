using System.Text;
using System.Text.Json;
using AxonHub.Abstractions;
using AxonHub.Api.Management;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer.Shared;
using AxonHub.Persistence;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of the Charm Hyper quota checker
/// (internal/server/biz/provider_quota/charm_hyper_checker.go): an independent
/// credits checker (it does not share the ZhiPu-family collector). A GET
/// /v1/credits on the channel's host reports a single balance, mapped onto a
/// 100-credit baseline: 0 is exhausted and at most 20 is a warning.
/// </summary>
public sealed class CharmHyperQuotaChecker : IQuotaChecker
{
    internal const string ProviderType = "charm_hyper";

    /// <summary>The host used when the channel has no base URL (charm_hyper_checker.go:44-46).</summary>
    internal const string DefaultBaseUrl = "https://hyper.charm.land";

    /// <summary>The fixed path that replaces the base URL's own path (charm_hyper_checker.go:135).</summary>
    internal const string QuotaPath = "/v1/credits";

    /// <summary>The fallback returned when Go's url.Parse rejects the base URL.</summary>
    internal const string DefaultQuotaUrl = DefaultBaseUrl + QuotaPath;

    /// <summary>charmHyperBaseline (charm_hyper_checker.go:17).</summary>
    internal const double Baseline = 100.0;

    /// <summary>charmHyperWarningThreshold (charm_hyper_checker.go:20).</summary>
    internal const double WarningThreshold = 20;

    private const string MissingBalanceError = "Charm Hyper quota response missing balance field";

    private const string ParseResponseErrorPrefix = "parsing Charm Hyper quota response: ";

    private readonly IExecutor _executor;
    private readonly IProxyExecutorFactory _proxyExecutors;
    private readonly TimeProvider _timeProvider;

    public CharmHyperQuotaChecker(
        IExecutor executor,
        IProxyExecutorFactory proxyExecutors,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(proxyExecutors);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _executor = executor;
        _proxyExecutors = proxyExecutors;
        _timeProvider = timeProvider;
    }

    /// <summary>Port of (*CharmHyperQuotaChecker).CheckQuota (charm_hyper_checker.go:37-71).</summary>
    public Task<QuotaData> CheckQuotaAsync(ChannelRecord channel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var apiKey = ExtractApiKey(channel);
        if (apiKey.Length == 0)
        {
            throw new InvalidOperationException("missing API key for Charm Hyper channel");
        }

        var baseUrl = channel.BaseUrl ?? string.Empty;
        if (baseUrl.Length == 0)
        {
            baseUrl = DefaultBaseUrl;
        }

        // The reference sets the Accept header explicitly; the executor's
        // default happens to be the same value. No Content-Type is sent.
        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Accept", "application/json");

        var request = new HttpRequestData
        {
            Method = "GET",
            Url = BuildQuotaUrl(baseUrl),
            Headers = headers,
            Auth = new AuthConfig
            {
                Type = AuthTypes.Bearer,
                ApiKey = apiKey,
            },
        };

        // provider_quota.go:46-48: a channel proxy replaces the shared client.
        var executor = ResolveExecutor(channel);

        HttpResponseData response;
        try
        {
            response = executor.Do(cancellationToken, request);
        }
        catch (Exception ex)
        {
            throw GoErrors.Wrap(ex, "executing Charm Hyper quota request: ");
        }

        // The shared executor already raises for >=400 (Go's httpclient.Do
        // does the same), so this only sees 1xx/3xx and non-200 2xx, exactly
        // like the reference's explicit check.
        if (response.StatusCode != 200)
        {
            throw new InvalidOperationException(
                "Charm Hyper quota API returned status "
                + response.StatusCode
                + ": "
                + Encoding.UTF8.GetString(response.Body ?? []));
        }

        return Task.FromResult(ParseResponse(response.Body ?? [], _timeProvider.GetUtcNow()));
    }

    /// <summary>Port of (*CharmHyperQuotaChecker).SupportsChannel (charm_hyper_checker.go:111-116).</summary>
    public bool SupportsChannel(ChannelRecord channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        if (channel.Type != ChannelOutboundFactory.OpenAiChannelType
            && channel.Type != ChannelOutboundFactory.OpenaiResponsesChannelType)
        {
            return false;
        }

        return ProviderQuotaUrlDetection.DetectProviderFromUrl(channel.BaseUrl) == ProviderType;
    }

    /// <summary>Port of (*CharmHyperQuotaChecker).extractAPIKey (charm_hyper_checker.go:118-127).</summary>
    internal static string ExtractApiKey(ChannelRecord channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var apiKey = GoStrings.TrimSpace(ChannelCredentials.GetLegacyApiKey(channel.Credentials));
        if (apiKey.Length != 0)
        {
            return apiKey;
        }

        var apiKeys = ChannelCredentials.GetApiKeys(channel.Credentials);
        if (apiKeys.Count > 0)
        {
            // Unlike most checkers, the reference trims this element and does
            // not scan further when it is blank.
            return GoStrings.TrimSpace(apiKeys[0]);
        }

        return string.Empty;
    }

    /// <summary>
    /// Port of buildCharmHyperQuotaURL (charm_hyper_checker.go:129-140): trim,
    /// parse with Go's net/url, then replace the path with /v1/credits and
    /// clear the query, userinfo and fragment. A parse error falls back to the
    /// default endpoint; unlike other checker builders, a successfully parsed
    /// relative URL stays relative (Go's URL.String() output).
    /// </summary>
    internal static string BuildQuotaUrl(string? baseUrl)
    {
        var trimmed = GoStrings.TrimSpace(baseUrl ?? string.Empty);
        if (!GoUrl.TryParse(trimmed, out var scheme, out var host, out _))
        {
            return DefaultQuotaUrl;
        }

        var forceQuery = false;
        var opaque = false;
        var opaquePart = string.Empty;
        if (scheme.Length > 0)
        {
            // Go cuts the fragment before parsing; the remainder after the
            // scheme decides ForceQuery and whether the URL is opaque
            // (rootless after a scheme). URL.String() keeps both.
            var afterScheme = trimmed[(scheme.Length + 1)..];
            var hash = afterScheme.IndexOf('#');
            if (hash >= 0)
            {
                afterScheme = afterScheme[..hash];
            }

            var question = afterScheme.IndexOf('?');
            forceQuery = question >= 0 && question == afterScheme.Length - 1;
            opaquePart = question >= 0 ? afterScheme[..question] : afterScheme;
            opaque = opaquePart.Length > 0 && !opaquePart.StartsWith("/", StringComparison.Ordinal);
        }

        if (opaque)
        {
            // Opaque URLs keep their text: setting Path has no effect on
            // URL.String() and the cleared RawQuery leaves ForceQuery alone.
            return scheme + ":" + opaquePart + (forceQuery ? "?" : string.Empty);
        }

        var prefix = scheme.Length > 0 ? scheme + "://" : host.Length > 0 ? "//" : string.Empty;
        return prefix + host + QuotaPath + (forceQuery ? "?" : string.Empty);
    }

    /// <summary>Port of (*CharmHyperQuotaChecker).parseResponse (charm_hyper_checker.go:73-94).</summary>
    internal static QuotaData ParseResponse(byte[] body, DateTimeOffset now)
    {
        CharmHyperCreditsResponse response;
        try
        {
            response = ParseCreditsResponse(body);
        }
        catch (JsonException ex)
        {
            throw GoErrors.Wrap(ex, ParseResponseErrorPrefix);
        }

        if (response.Balance is not { } balance)
        {
            throw new InvalidOperationException(MissingBalanceError);
        }

        var (status, ready, usageRatio) = ComputeStatus(balance);

        var limit = ProviderQuotaData.NewTokenLimitStatus(status, usageRatio, null);
        // The reference uses a zero window length; with no reset instant the
        // period start stays nil.
        ProviderQuotaData.WithWindow(limit, QuotaWindows.Credits, TimeSpan.Zero);

        return ProviderQuotaData.Normalize(
            new QuotaData
            {
                Status = status,
                ProviderType = ProviderType,
                RawData = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["balance"] = balance,
                },
                NextResetAt = null,
                Ready = ready,
                Limits = [limit],
            },
            now);
    }

    /// <summary>Port of (*CharmHyperQuotaChecker).computeStatus (charm_hyper_checker.go:96-109).</summary>
    internal static (string Status, bool Ready, double UsageRatio) ComputeStatus(double balance)
    {
        var usageRatio = 1.0 - (balance / Baseline);
        if (usageRatio < 0)
        {
            usageRatio = 0;
        }

        if (balance == 0)
        {
            return (ProviderQuotaStatuses.Exhausted, false, 1.0);
        }

        if (balance <= WarningThreshold)
        {
            return (ProviderQuotaStatuses.Warning, true, usageRatio);
        }

        return (ProviderQuotaStatuses.Available, true, usageRatio);
    }

    /// <summary>Port of the json.Unmarshal call into CharmHyperCreditsResponse (charm_hyper_checker.go:74-77).</summary>
    /// <exception cref="JsonException">
    /// The payload is not a JSON object or the balance field has the wrong
    /// JSON type (the decoder's message text differs from Go — C003/C219).
    /// </exception>
    internal static CharmHyperCreditsResponse ParseCreditsResponse(byte[] data)
    {
        using var document = JsonDocument.Parse(GoJsonUtf8.SanitizeStringLiterals(data) ?? data);
        var root = document.RootElement;

        // Go: unmarshaling JSON null into a struct is a no-op.
        if (root.ValueKind == JsonValueKind.Null)
        {
            return new CharmHyperCreditsResponse();
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException(
                "json: cannot unmarshal "
                + GoKindName(root.ValueKind)
                + " into Go value of type provider_quota.CharmHyperCreditsResponse");
        }

        var response = new CharmHyperCreditsResponse();
        foreach (var property in root.EnumerateObject())
        {
            // Go's field lookup folds ASCII letters; the last duplicate wins.
            if (FoldAscii(property.Name) == "balance")
            {
                response.Balance = ParseBalance(property.Value);
            }
        }

        return response;
    }

    private static double? ParseBalance(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number)
        {
            throw StructTypeMismatch(
                GoKindName(value.ValueKind),
                "CharmHyperCreditsResponse",
                "balance",
                "float64");
        }

        var raw = value.GetRawText();
        if (!GoCast.TryParseFloat(raw, out var parsed))
        {
            throw StructTypeMismatch("number " + raw, "CharmHyperCreditsResponse", "balance", "float64");
        }

        return parsed;
    }

    private static JsonException StructTypeMismatch(
        string value,
        string structName,
        string fieldPath,
        string type)
        => new(
            "json: cannot unmarshal "
            + value
            + " into Go struct field "
            + structName
            + "."
            + fieldPath
            + " of type "
            + type);

    private static string GoKindName(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "bool",
        _ => "null",
    };

    /// <summary>Go's encoding/json field lookup folds ASCII letters only.</summary>
    private static string FoldAscii(string name)
    {
        var chars = name.ToCharArray();
        for (var index = 0; index < chars.Length; index++)
        {
            if (chars[index] is >= 'A' and <= 'Z')
            {
                chars[index] = (char)(chars[index] + ('a' - 'A'));
            }
        }

        return new string(chars);
    }

    private IExecutor ResolveExecutor(ChannelRecord channel)
        => ParseSettingsLenient(channel.Settings)?.Proxy is { } proxy
            ? _proxyExecutors.Create(ToProxyConfig(proxy))
            : _executor;

    private static ChannelSettingsDraft? ParseSettingsLenient(string? raw)
    {
        try
        {
            return ChannelWriteSerialization.ParseSettings(raw);
        }
        catch (JsonException)
        {
            // The reference decodes settings with Ent before reaching the
            // checker; malformed JSON is treated as "no settings" (C013).
            return null;
        }
    }

    private static ProxyConfig ToProxyConfig(ProxyConfigDraft proxy) => new()
    {
        Type = proxy.Type,
        Url = proxy.Url,
        Username = proxy.Username,
        Password = proxy.Password,
        DisableConnectionReuse = proxy.DisableConnectionReuse ?? false,
    };
}

/// <summary>
/// Port of <c>CharmHyperCreditsResponse</c> (charm_hyper_checker.go:22-25):
/// one optional balance pointer. JSON null resets it, objects are irrelevant
/// (the struct has a single scalar field) and duplicate keys let the last
/// value win, as Go's decoder does.
/// </summary>
internal sealed class CharmHyperCreditsResponse
{
    public double? Balance { get; set; }
}
