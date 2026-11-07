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
/// Port of the NeuralWatt quota checker
/// (internal/server/biz/provider_quota/neuralwatt_checker.go): the fixed
/// /v1/quota endpoint reports a kWh subscription window plus a USD balance, and
/// the kWh pool drives the channel status.
/// </summary>
public sealed class NeuralWattQuotaChecker : IQuotaChecker
{
    private const string ProviderType = "neuralwatt";

    /// <summary>The fallback used by buildNeuralWattQuotaURL (neuralwatt_checker.go:168-185).</summary>
    internal const string DefaultQuotaUrl = "https://api.neuralwatt.com/v1/quota";

    private const string QuotaPath = "/v1/quota";

    /// <summary>The kWh window label stamped on the single token limit.</summary>
    internal const string KwhWindow = "kwh";

    /// <summary>warningRemainingFraction (neuralwatt_checker.go:17).</summary>
    internal const double WarningRemainingFraction = 0.2;

    private const string ParseResponseErrorPrefix = "failed to parse neuralwatt usage response: ";

    private readonly IExecutor _executor;
    private readonly IProxyExecutorFactory _proxyExecutors;
    private readonly TimeProvider _timeProvider;

    public NeuralWattQuotaChecker(
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

    /// <summary>Port of (*NeuralWattQuotaChecker).CheckQuota (neuralwatt_checker.go:50-84).</summary>
    public Task<QuotaData> CheckQuotaAsync(ChannelRecord channel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var apiKey = ResolveApiKey(channel);
        if (apiKey.Length == 0)
        {
            throw new InvalidOperationException("channel has no API key");
        }

        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Content-Type", "application/json");

        var request = new HttpRequestData
        {
            Method = "GET",
            Url = BuildQuotaUrl(channel.BaseUrl),
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
            throw GoErrors.Wrap(ex, "quota request failed: ");
        }

        // The shared executor already raises for >=400, so this only sees
        // 1xx/3xx and non-200 2xx, mirroring the reference's explicit check.
        if (response.StatusCode != 200)
        {
            throw new InvalidOperationException(
                "HTTP "
                + response.StatusCode
                + ": "
                + Encoding.UTF8.GetString(response.Body ?? []));
        }

        return Task.FromResult(ParseResponse(response.Body ?? [], _timeProvider.GetUtcNow()));
    }

    /// <summary>
    /// Port of (*NeuralWattQuotaChecker).SupportsChannel
    /// (neuralwatt_checker.go:152-158).
    /// </summary>
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

    /// <summary>Port of the API-key extraction at the top of CheckQuota (neuralwatt_checker.go:51-58).</summary>
    internal static string ResolveApiKey(ChannelRecord channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var apiKey = GoStrings.TrimSpace(ChannelCredentials.GetLegacyApiKey(channel.Credentials));
        if (apiKey.Length == 0)
        {
            var apiKeys = ChannelCredentials.GetApiKeys(channel.Credentials);
            if (apiKeys.Count > 0)
            {
                // The reference takes the first entry verbatim (no trim).
                apiKey = apiKeys[0];
            }
        }

        return apiKey;
    }

    /// <summary>Port of buildNeuralWattQuotaURL (neuralwatt_checker.go:168-185).</summary>
    internal static string BuildQuotaUrl(string? baseUrl)
    {
        var trimmed = GoStrings.TrimSpace(baseUrl ?? string.Empty);
        if (trimmed.Length == 0)
        {
            return DefaultQuotaUrl;
        }

        if (!GoUrl.TryParse(trimmed, out var scheme, out var host, out _)
            || (scheme.Length == 0 && host.Length == 0))
        {
            return DefaultQuotaUrl;
        }

        if (scheme == "http")
        {
            scheme = "https";
        }

        return scheme + "://" + host + QuotaPath;
    }

    /// <summary>Port of parseResponse (neuralwatt_checker.go:86-150).</summary>
    internal static QuotaData ParseResponse(byte[] body, DateTimeOffset now)
    {
        NeuralWattUsageResponse response;
        try
        {
            response = ParseUsageResponse(body);
        }
        catch (JsonException ex)
        {
            throw GoErrors.Wrap(ex, ParseResponseErrorPrefix);
        }

        var subscription = response.Subscription;

        var normalizedStatus = ProviderQuotaStatuses.Unknown;
        if (subscription is not null)
        {
            if (subscription.InOverage is true)
            {
                normalizedStatus = ProviderQuotaStatuses.Exhausted;
            }
            else if (subscription.KwhRemaining is { } remaining && remaining <= 0)
            {
                normalizedStatus = ProviderQuotaStatuses.Exhausted;
            }
            else if (IsLowRemaining(subscription))
            {
                normalizedStatus = ProviderQuotaStatuses.Warning;
            }
            else if (subscription.KwhRemaining is not null)
            {
                normalizedStatus = ProviderQuotaStatuses.Available;
            }
        }

        DateTimeOffset? nextResetAt = null;
        if (subscription?.KwhResetDate is { } resetDate
            && GoTimeParsing.TryParseRfc3339(resetDate) is { } parsedReset)
        {
            nextResetAt = parsedReset;
        }

        var usageRatio = 0.0;
        if (subscription?.KwhIncluded is { } kwhIncluded && kwhIncluded > 0)
        {
            if (subscription.KwhUsed is { } kwhUsed)
            {
                usageRatio = kwhUsed / kwhIncluded;
            }
            else if (subscription.KwhRemaining is { } kwhRemaining)
            {
                usageRatio = 1.0 - (kwhRemaining / kwhIncluded);
            }
        }

        var limit = ProviderQuotaData.NewTokenLimitStatus(
            normalizedStatus,
            usageRatio,
            nextResetAt);
        limit.Window = KwhWindow;
        // The kWh window resets monthly at kwh_reset_date; stamping the period
        // start enables the on-track marker and period estimates (types.go:212).
        limit.PeriodStart = ProviderQuotaData.PeriodStartFromMonthlyReset(nextResetAt);

        return ProviderQuotaData.Normalize(
            new QuotaData
            {
                Status = normalizedStatus,
                ProviderType = ProviderType,
                RawData = PrepareRawData(response),
                NextResetAt = nextResetAt,
                Ready = ProviderQuotaStatuses.IsReadyStatus(normalizedStatus),
                Limits = [limit],
            },
            now);
    }

    /// <summary>Port of isNeuralWattLowRemaining (neuralwatt_checker.go:160-166).</summary>
    internal static bool IsLowRemaining(NeuralWattSubscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        if (subscription.KwhRemaining is not { } remaining
            || subscription.KwhIncluded is not { } included
            || included <= 0)
        {
            return false;
        }

        return remaining < included * WarningRemainingFraction;
    }

    /// <summary>Port of the rawData construction in parseResponse (neuralwatt_checker.go:114-122).</summary>
    internal static Dictionary<string, object?> PrepareRawData(NeuralWattUsageResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var rawData = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (response.Balance is { } balance)
        {
            rawData["balance"] = ConvertBalanceToMap(balance);
        }

        if (response.Subscription is { } subscription)
        {
            rawData["subscription"] = ConvertSubscriptionToMap(subscription);
        }

        return rawData;
    }

    /// <summary>Port of convertNeuralWattBalanceToMap (neuralwatt_checker.go:187-203).</summary>
    internal static Dictionary<string, object?> ConvertBalanceToMap(NeuralWattBalance balance)
    {
        ArgumentNullException.ThrowIfNull(balance);

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (balance.CreditsRemainingUsd is { } creditsRemainingUsd)
        {
            result["credits_remaining_usd"] = creditsRemainingUsd;
        }

        if (balance.TotalCreditsUsd is { } totalCreditsUsd)
        {
            result["total_credits_usd"] = totalCreditsUsd;
        }

        if (balance.AccountingMethod is { } accountingMethod)
        {
            result["accounting_method"] = accountingMethod;
        }

        return result;
    }

    /// <summary>Port of convertNeuralWattSubscriptionToMap (neuralwatt_checker.go:205-237).</summary>
    internal static Dictionary<string, object?> ConvertSubscriptionToMap(
        NeuralWattSubscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (subscription.Plan is { } plan)
        {
            result["plan"] = plan;
        }

        if (subscription.Status is { } status)
        {
            result["status"] = status;
        }

        if (subscription.KwhIncluded is { } kwhIncluded)
        {
            result["kwh_included"] = kwhIncluded;
        }

        if (subscription.KwhUsed is { } kwhUsed)
        {
            result["kwh_used"] = kwhUsed;
        }

        if (subscription.KwhRemaining is { } kwhRemaining)
        {
            result["kwh_remaining"] = kwhRemaining;
        }

        if (subscription.InOverage is { } inOverage)
        {
            result["in_overage"] = inOverage;
        }

        if (subscription.KwhResetDate is { } kwhResetDate)
        {
            result["kwh_reset_date"] = kwhResetDate;
        }

        return result;
    }

    /// <summary>Port of the json.Unmarshal call into NeuralWattUsageResponse (neuralwatt_checker.go:89).</summary>
    /// <exception cref="JsonException">
    /// The payload is not a JSON object or a field has the wrong JSON type
    /// (the decoder's message text differs from Go — C003/C093).
    /// </exception>
    internal static NeuralWattUsageResponse ParseUsageResponse(byte[] data)
    {
        using var document = JsonDocument.Parse(GoJsonUtf8.SanitizeStringLiterals(data) ?? data);
        var root = document.RootElement;

        // Go: unmarshaling JSON null into a struct is a no-op.
        if (root.ValueKind == JsonValueKind.Null)
        {
            return new NeuralWattUsageResponse();
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException(
                "json: cannot unmarshal "
                + GoKindName(root.ValueKind)
                + " into Go value of type provider_quota.NeuralWattUsageResponse");
        }

        var response = new NeuralWattUsageResponse();
        foreach (var property in root.EnumerateObject())
        {
            switch (FoldAscii(property.Name))
            {
                case "balance":
                    response.Balance = ParseBalance(property.Value, response.Balance);
                    break;
                case "subscription":
                    response.Subscription = ParseSubscription(property.Value, response.Subscription);
                    break;
            }
        }

        return response;
    }

    private static NeuralWattBalance? ParseBalance(JsonElement value, NeuralWattBalance? existing)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw StructTypeMismatch(
                GoKindName(value.ValueKind),
                "NeuralWattUsageResponse",
                "balance",
                "provider_quota.NeuralWattBalance");
        }

        var balance = existing ?? new NeuralWattBalance();
        foreach (var property in value.EnumerateObject())
        {
            switch (FoldAscii(property.Name))
            {
                case "credits_remaining_usd":
                    balance.CreditsRemainingUsd = NullableFloat64(
                        property.Value,
                        "NeuralWattBalance",
                        "balance.credits_remaining_usd");
                    break;
                case "total_credits_usd":
                    balance.TotalCreditsUsd = NullableFloat64(
                        property.Value,
                        "NeuralWattBalance",
                        "balance.total_credits_usd");
                    break;
                case "accounting_method":
                    balance.AccountingMethod = NullableString(
                        property.Value,
                        "NeuralWattBalance",
                        "balance.accounting_method");
                    break;
            }
        }

        return balance;
    }

    private static NeuralWattSubscription? ParseSubscription(
        JsonElement value,
        NeuralWattSubscription? existing)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw StructTypeMismatch(
                GoKindName(value.ValueKind),
                "NeuralWattUsageResponse",
                "subscription",
                "provider_quota.NeuralWattSubscription");
        }

        var subscription = existing ?? new NeuralWattSubscription();
        foreach (var property in value.EnumerateObject())
        {
            switch (FoldAscii(property.Name))
            {
                case "plan":
                    subscription.Plan = NullableString(
                        property.Value,
                        "NeuralWattSubscription",
                        "subscription.plan");
                    break;
                case "status":
                    subscription.Status = NullableString(
                        property.Value,
                        "NeuralWattSubscription",
                        "subscription.status");
                    break;
                case "kwh_included":
                    subscription.KwhIncluded = NullableFloat64(
                        property.Value,
                        "NeuralWattSubscription",
                        "subscription.kwh_included");
                    break;
                case "kwh_used":
                    subscription.KwhUsed = NullableFloat64(
                        property.Value,
                        "NeuralWattSubscription",
                        "subscription.kwh_used");
                    break;
                case "kwh_remaining":
                    subscription.KwhRemaining = NullableFloat64(
                        property.Value,
                        "NeuralWattSubscription",
                        "subscription.kwh_remaining");
                    break;
                case "in_overage":
                    subscription.InOverage = NullableBool(
                        property.Value,
                        "NeuralWattSubscription",
                        "subscription.in_overage");
                    break;
                case "kwh_reset_date":
                    subscription.KwhResetDate = NullableString(
                        property.Value,
                        "NeuralWattSubscription",
                        "subscription.kwh_reset_date");
                    break;
            }
        }

        return subscription;
    }

    private static bool? NullableBool(JsonElement value, string structName, string fieldPath)
        => value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw StructTypeMismatch(
                GoKindName(value.ValueKind),
                structName,
                fieldPath,
                "bool"),
        };

    private static string? NullableString(JsonElement value, string structName, string fieldPath)
        => value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString() ?? string.Empty,
            _ => throw StructTypeMismatch(
                GoKindName(value.ValueKind),
                structName,
                fieldPath,
                "string"),
        };

    private static double? NullableFloat64(JsonElement value, string structName, string fieldPath)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number)
        {
            throw StructTypeMismatch(GoKindName(value.ValueKind), structName, fieldPath, "float64");
        }

        var raw = value.GetRawText();
        if (!GoCast.TryParseFloat(raw, out var parsed))
        {
            throw StructTypeMismatch("number " + raw, structName, fieldPath, "float64");
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
/// Port of <c>NeuralWattUsageResponse</c> (neuralwatt_checker.go:35-38). Every
/// field is optional in the reference, so each maps to a nullable value; the
/// nested objects keep Go's "null resets the pointer, objects merge" semantics.
/// </summary>
internal sealed class NeuralWattUsageResponse
{
    public NeuralWattBalance? Balance { get; set; }

    public NeuralWattSubscription? Subscription { get; set; }
}

/// <summary>Port of <c>NeuralWattBalance</c> (neuralwatt_checker.go:19-23).</summary>
internal sealed class NeuralWattBalance
{
    public double? CreditsRemainingUsd { get; set; }

    public double? TotalCreditsUsd { get; set; }

    public string? AccountingMethod { get; set; }
}

/// <summary>Port of <c>NeuralWattSubscription</c> (neuralwatt_checker.go:25-33).</summary>
internal sealed class NeuralWattSubscription
{
    public string? Plan { get; set; }

    public string? Status { get; set; }

    public double? KwhIncluded { get; set; }

    public double? KwhUsed { get; set; }

    public double? KwhRemaining { get; set; }

    public bool? InOverage { get; set; }

    public string? KwhResetDate { get; set; }
}
