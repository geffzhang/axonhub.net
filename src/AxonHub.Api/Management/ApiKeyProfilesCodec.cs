using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Api.Billing;
using AxonHub.Llm.Json;

namespace AxonHub.Api.Management;

/// <summary>Port of objects.APIKeyProfiles (internal/objects/apikey.go:9-12).</summary>
public sealed record ApiKeyProfilesModel
{
    public string ActiveProfile { get; init; } = string.Empty;

    /// <summary>Nil (JSON null) and empty (JSON []) are distinct on the wire.</summary>
    public IReadOnlyList<ApiKeyProfileModel>? Profiles { get; init; }
}

/// <summary>
/// Port of objects.APIKeyProfile (internal/objects/apikey.go:14-30). The
/// field order of the type declaration is the encoding/json marshaling order.
/// </summary>
public sealed record ApiKeyProfileModel
{
    public string Name { get; init; } = string.Empty;

    /// <summary>JSON omitempty on a pointer: null is dropped, 0 is kept.</summary>
    public long? TemplateId { get; init; }

    /// <summary>JSON omitempty on a string: the empty string is dropped.</summary>
    public string TemplateName { get; init; } = string.Empty;

    /// <summary>
    /// No omitempty: null marshals as JSON null and an empty non-nil list as
    /// []. An omitted GraphQL input field stays nil in the reference.
    /// </summary>
    public IReadOnlyList<ApiKeyModelMapping>? ModelMappings { get; init; }

    /// <summary>JSON omitempty on a pointer: null is dropped, a zero quota is kept.</summary>
    public ApiKeyQuotaModel? Quota { get; init; }

    /// <summary>JSON omitempty on a pointer to string: "" is kept, null is dropped.</summary>
    public string? LoadBalanceStrategy { get; init; }

    /// <summary>JSON omitempty on a pointer to string: "" is kept, null is dropped.</summary>
    public string? TraceStickyMode { get; init; }

    public IReadOnlyList<long>? ChannelIDs { get; init; }

    public IReadOnlyList<string>? ChannelTags { get; init; }

    /// <summary>JSON omitempty on a string: the empty string is dropped.</summary>
    public string ChannelTagsMatchMode { get; init; } = string.Empty;

    public IReadOnlyList<string>? ModelIDs { get; init; }
}

/// <summary>Port of objects.ModelMapping (internal/objects/channel.go:36-42).</summary>
public sealed record ApiKeyModelMapping
{
    public string From { get; init; } = string.Empty;

    public string To { get; init; } = string.Empty;
}

/// <summary>Port of objects.APIKeyQuota (internal/objects/apikey.go:165-170).</summary>
public sealed record ApiKeyQuotaModel
{
    public long? Requests { get; init; }

    public long? TotalTokens { get; init; }

    /// <summary>
    /// shopspring/decimal marshals as a quoted, trailing-zero-trimmed string.
    /// The .NET decimal domain is narrower than big.Int; see the ledger. The
    /// converter is required for the restore decode path
    /// (<c>LlmJson.Deserialize&lt;BackupData&gt;</c>), where the stored profile
    /// JSON is decoded by System.Text.Json rather than
    /// <see cref="ApiKeyProfilesCodec"/>.
    /// </summary>
    [JsonConverter(typeof(GoNullableDecimalConverter))]
    public decimal? Cost { get; init; }

    /// <summary>No omitempty: the period always appears, zero-valued if absent.</summary>
    public ApiKeyQuotaPeriodModel Period { get; init; } = new();
}

/// <summary>Port of objects.APIKeyQuotaPeriod (internal/objects/apikey.go:172-176).</summary>
public sealed record ApiKeyQuotaPeriodModel
{
    public string Type { get; init; } = string.Empty;

    public ApiKeyQuotaPastDurationModel? PastDuration { get; init; }

    public ApiKeyQuotaCalendarDurationModel? CalendarDuration { get; init; }
}

/// <summary>Port of objects.APIKeyQuotaPastDuration (internal/objects/apikey.go:186-189).</summary>
public sealed record ApiKeyQuotaPastDurationModel
{
    public long Value { get; init; }

    public string Unit { get; init; } = string.Empty;
}

/// <summary>Port of objects.APIKeyQuotaCalendarDuration (internal/objects/apikey.go:199-201).</summary>
public sealed record ApiKeyQuotaCalendarDurationModel
{
    public string Unit { get; init; } = string.Empty;
}

/// <summary>
/// Port of objects.RoutingPolicyDefault and friends
/// (internal/objects/routing.go:8-51).
/// </summary>
public static class RoutingPolicy
{
    public const string Default = "default";

    public const string Adaptive = "adaptive";

    public const string Failover = "failover";

    public const string CircuitBreaker = "circuit-breaker";

    public const string RoundRobin = "round-robin";

    public const string TraceStickyDisabled = "disabled";

    public const string TraceStickyPreferPreviousChannel = "prefer_previous_channel";

    public static string NormalizeValue(string value)
        => value is "" or "system_default" ? Default : value;

    public static bool IsValidLoadBalancerStrategy(string value)
        => NormalizeValue(value) is Default or Adaptive or Failover or CircuitBreaker or RoundRobin;

    public static bool IsValidTraceStickyMode(string value)
        => NormalizeValue(value) is Default or TraceStickyDisabled or TraceStickyPreferPreviousChannel;
}

/// <summary>
/// Go-compatible codec for the <c>api_keys.profiles</c> jsonb column and the
/// APIKey profile GraphQL types. Go marshals objects.APIKeyProfiles with
/// encoding/json: declaration order, omitempty exactly as tagged, HTML escaping
/// and lowercase control escapes.
/// </summary>
public static class ApiKeyProfilesCodec
{
    /// <summary>Map of the reference's ent create default (&amp;objects.APIKeyProfiles{}).</summary>
    public const string DefaultJson = "{\"activeProfile\":\"\",\"profiles\":null}";

    public static string Serialize(ApiKeyProfilesModel profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   buffer,
                   new JsonWriterOptions
                   {
                       Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                   }))
        {
            writer.WriteStartObject();
            writer.WriteString("activeProfile", profiles.ActiveProfile);

            writer.WritePropertyName("profiles");
            if (profiles.Profiles is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStartArray();
                foreach (var profile in profiles.Profiles)
                {
                    WriteProfile(writer, profile);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(GoJson.Escape(buffer.ToArray()));
    }

    public static ApiKeyProfilesModel Deserialize(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return new ApiKeyProfilesModel();
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new ApiKeyProfilesModel();
        }

        var activeProfile = ReadString(root, "activeProfile") ?? string.Empty;

        IReadOnlyList<ApiKeyProfileModel>? profiles = null;
        var profilesElement = FindProperty(root, "profiles");
        if (profilesElement is { ValueKind: JsonValueKind.Array } array)
        {
            profiles = [.. array.EnumerateArray().Select(ReadProfile)];
        }

        return new ApiKeyProfilesModel
        {
            ActiveProfile = activeProfile,
            Profiles = profiles,
        };
    }

    /// <summary>
    /// Port of APIKey.GetActiveProfile (internal/ent/extra.go:30-42): resolve the
    /// named active profile, or null when no profile is active or the name does
    /// not match.
    /// </summary>
    public static ApiKeyProfileModel? ActiveProfile(string? profilesJson)
    {
        var profiles = Deserialize(profilesJson);
        if (profiles.ActiveProfile.Length == 0)
        {
            return null;
        }

        foreach (var profile in profiles.Profiles ?? [])
        {
            if (string.Equals(profile.Name, profiles.ActiveProfile, StringComparison.Ordinal))
            {
                return profile;
            }
        }

        return null;
    }

    /// <summary>
    /// Port of UpdateAPIKeyProfiles' validation sequence
    /// (biz/api_key.go:644-664): names, active profile, filters, routing
    /// policies (normalizing the stored values), quota.
    /// </summary>
    public static ApiKeyProfilesModel ValidateAndNormalize(ApiKeyProfilesModel profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        ValidateProfileNames(profiles.Profiles);
        ValidateActiveProfile(profiles.ActiveProfile, profiles.Profiles);
        ValidateProfileFilters(profiles.Profiles);
        var normalized = NormalizeProfileRoutingPolicies(profiles);
        ValidateProfileQuota(normalized.Profiles);
        return normalized;
    }

    /// <summary>
    /// Port of detachModifiedTemplateProfiles (biz/api_key.go:685-705): a
    /// profile stays linked only while its template-managed contents are
    /// unchanged, otherwise the caller has customized it and the link is
    /// cleared.
    /// </summary>
    public static ApiKeyProfilesModel DetachModifiedTemplateProfiles(
        ApiKeyProfilesModel? existing,
        ApiKeyProfilesModel next)
    {
        ArgumentNullException.ThrowIfNull(next);

        if (next.Profiles is null)
        {
            return next;
        }

        var updated = new List<ApiKeyProfileModel>(next.Profiles.Count);
        foreach (var profile in next.Profiles)
        {
            if (profile.TemplateId is null)
            {
                updated.Add(profile with { TemplateName = string.Empty });
                continue;
            }

            var linked = FindLinkedProfile(existing, profile.TemplateId.Value, profile.Name);
            if (linked is null || !SameProfileIgnoringTemplate(linked, profile))
            {
                updated.Add(profile with { TemplateId = null, TemplateName = string.Empty });
            }
            else
            {
                updated.Add(profile with { TemplateName = linked.TemplateName });
            }
        }

        return next with { Profiles = updated };
    }

    /// <summary>Port of validateProfileNames (biz/api_key.go:763-780).</summary>
    public static void ValidateProfileNames(IReadOnlyList<ApiKeyProfileModel>? profiles)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in profiles ?? [])
        {
            var nameLower = profile.Name.Trim().ToLowerInvariant();
            if (nameLower.Length == 0)
            {
                throw new InvalidOperationException("profile name cannot be empty");
            }

            if (!seen.Add(nameLower))
            {
                throw new InvalidOperationException("duplicate profile name: " + profile.Name);
            }
        }
    }

    /// <summary>Port of validateActiveProfile (biz/api_key.go:783-791).</summary>
    public static void ValidateActiveProfile(
        string activeProfile,
        IReadOnlyList<ApiKeyProfileModel>? profiles)
    {
        foreach (var profile in profiles ?? [])
        {
            if (string.Equals(profile.Name, activeProfile, StringComparison.Ordinal))
            {
                return;
            }
        }

        throw new InvalidOperationException(
            "active profile '" + activeProfile + "' does not exist in the profiles list");
    }

    /// <summary>Port of validateProfileFilters (biz/api_key.go:793-801).</summary>
    public static void ValidateProfileFilters(IReadOnlyList<ApiKeyProfileModel>? profiles)
    {
        foreach (var profile in profiles ?? [])
        {
            if (!IsValidChannelTagsMatchMode(profile.ChannelTagsMatchMode))
            {
                throw new InvalidOperationException(
                    "profile '" + profile.Name + "' channelTagsMatchMode is invalid");
            }
        }
    }

    /// <summary>Port of validateProfileQuota (biz/api_key.go:841-896).</summary>
    public static void ValidateProfileQuota(IReadOnlyList<ApiKeyProfileModel>? profiles)
    {
        foreach (var profile in profiles ?? [])
        {
            var quota = profile.Quota;
            if (quota is null)
            {
                continue;
            }

            if (quota.Requests is null && quota.TotalTokens is null && quota.Cost is null)
            {
                throw new InvalidOperationException(
                    "profile '" + profile.Name + "' quota must set at least one limit");
            }

            if (quota.Requests is <= 0)
            {
                throw new InvalidOperationException(
                    "profile '" + profile.Name + "' quota.requests must be positive");
            }

            if (quota.TotalTokens is <= 0)
            {
                throw new InvalidOperationException(
                    "profile '" + profile.Name + "' quota.totalTokens must be positive");
            }

            if (quota.Cost is < 0)
            {
                throw new InvalidOperationException(
                    "profile '" + profile.Name + "' quota.cost must be non-negative");
            }

            switch (quota.Period.Type)
            {
                case "all_time":
                    break;

                case "past_duration":
                    if (quota.Period.PastDuration is null)
                    {
                        throw new InvalidOperationException(
                            "profile '" + profile.Name + "' quota.period.pastDuration is required");
                    }

                    if (quota.Period.PastDuration.Value <= 0)
                    {
                        throw new InvalidOperationException(
                            "profile '" + profile.Name
                            + "' quota.period.pastDuration.value must be positive");
                    }

                    if (quota.Period.PastDuration.Unit is not ("minute" or "hour" or "day"))
                    {
                        throw new InvalidOperationException(
                            "profile '" + profile.Name
                            + "' quota.period.pastDuration.unit is invalid");
                    }

                    break;

                case "calendar_duration":
                    if (quota.Period.CalendarDuration is null)
                    {
                        throw new InvalidOperationException(
                            "profile '" + profile.Name
                            + "' quota.period.calendarDuration is required");
                    }

                    if (quota.Period.CalendarDuration.Unit is not ("day" or "month"))
                    {
                        throw new InvalidOperationException(
                            "profile '" + profile.Name
                            + "' quota.period.calendarDuration.unit is invalid");
                    }

                    break;

                default:
                    throw new InvalidOperationException(
                        "profile '" + profile.Name + "' quota.period.type is invalid");
            }
        }
    }

    /// <summary>Port of validateAllowedIPs (biz/api_key.go:898-917).</summary>
    public static void ValidateAllowedIps(IReadOnlyList<string>? ips)
    {
        if (ips is null)
        {
            return;
        }

        foreach (var raw in ips)
        {
            var ip = raw.Trim();
            if (ip.Length == 0)
            {
                continue;
            }

            if (ip.Contains('/', StringComparison.Ordinal))
            {
                if (!GoNetip.TryParsePrefix(ip, out var error))
                {
                    throw new InvalidOperationException("invalid CIDR \"" + ip + "\": " + error);
                }
            }
            else if (!GoNetip.TryParseAddr(ip, out var error))
            {
                throw new InvalidOperationException("invalid IP \"" + ip + "\": " + error);
            }
        }
    }

    public static bool IsValidChannelTagsMatchMode(string mode)
        => mode is "" or "any" or "all" or "none";

    public static string OrDefaultChannelTagsMatchMode(string mode)
        => mode is "all" or "none" ? mode : "any";

    private static ApiKeyProfilesModel NormalizeProfileRoutingPolicies(ApiKeyProfilesModel profiles)
    {
        if (profiles.Profiles is null)
        {
            return profiles;
        }

        var normalized = new List<ApiKeyProfileModel>(profiles.Profiles.Count);
        foreach (var profile in profiles.Profiles)
        {
            // Port of normalizeAndValidateProfileRoutingPolicy
            // (biz/api_key.go:813-839).
            var loadBalanceStrategy = RoutingPolicy.NormalizeValue(
                profile.LoadBalanceStrategy ?? RoutingPolicy.Default);
            if (!RoutingPolicy.IsValidLoadBalancerStrategy(loadBalanceStrategy))
            {
                throw new InvalidOperationException(
                    "profile '" + profile.Name + "' loadBalanceStrategy is invalid");
            }

            var traceStickyMode = RoutingPolicy.NormalizeValue(
                profile.TraceStickyMode ?? RoutingPolicy.Default);
            if (!RoutingPolicy.IsValidTraceStickyMode(traceStickyMode))
            {
                throw new InvalidOperationException(
                    "profile '" + profile.Name + "' traceStickyMode is invalid");
            }

            normalized.Add(profile with
            {
                LoadBalanceStrategy = loadBalanceStrategy,
                TraceStickyMode = traceStickyMode,
            });
        }

        return profiles with { Profiles = normalized };
    }

    private static ApiKeyProfileModel? FindLinkedProfile(
        ApiKeyProfilesModel? profiles,
        long templateId,
        string name)
    {
        if (profiles?.Profiles is null)
        {
            return null;
        }

        foreach (var profile in profiles.Profiles)
        {
            if (profile.TemplateId == templateId
                && string.Equals(profile.Name, name, StringComparison.Ordinal))
            {
                return profile;
            }
        }

        return null;
    }

    /// <summary>
    /// Port of sameProfileIgnoringTemplate (biz/api_key.go:722-731): normalize
    /// both sides, clear the template link fields, then compare. Go's
    /// reflect.DeepEqual is structural; list contents and decimal scale are
    /// significant here.
    /// </summary>
    private static bool SameProfileIgnoringTemplate(
        ApiKeyProfileModel left,
        ApiKeyProfileModel right)
    {
        var a = NormalizeProfileForComparison(left);
        var b = NormalizeProfileForComparison(right);

        return string.Equals(a.Name, b.Name, StringComparison.Ordinal)
            && SequenceEqual(a.ModelMappings, b.ModelMappings)
            && QuotaEqual(a.Quota, b.Quota)
            && string.Equals(a.LoadBalanceStrategy, b.LoadBalanceStrategy, StringComparison.Ordinal)
            && string.Equals(a.TraceStickyMode, b.TraceStickyMode, StringComparison.Ordinal)
            && SequenceEqual(a.ChannelIDs, b.ChannelIDs)
            && SequenceEqual(a.ChannelTags, b.ChannelTags)
            && string.Equals(
                a.ChannelTagsMatchMode,
                b.ChannelTagsMatchMode,
                StringComparison.Ordinal)
            && SequenceEqual(a.ModelIDs, b.ModelIDs);
    }

    private static ApiKeyProfileModel NormalizeProfileForComparison(ApiKeyProfileModel profile)
        => profile with
        {
            TemplateId = null,
            TemplateName = string.Empty,
            ModelMappings = profile.ModelMappings ?? [],
            ChannelIDs = profile.ChannelIDs ?? [],
            ChannelTags = profile.ChannelTags ?? [],
            ModelIDs = profile.ModelIDs ?? [],
            ChannelTagsMatchMode = OrDefaultChannelTagsMatchMode(profile.ChannelTagsMatchMode),
            LoadBalanceStrategy = RoutingPolicy.NormalizeValue(
                profile.LoadBalanceStrategy ?? RoutingPolicy.Default),
            TraceStickyMode = RoutingPolicy.NormalizeValue(
                profile.TraceStickyMode ?? RoutingPolicy.Default),
        };

    private static bool SequenceEqual<T>(IReadOnlyList<T>? left, IReadOnlyList<T>? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!EqualityComparer<T>.Default.Equals(left[i], right[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool QuotaEqual(ApiKeyQuotaModel? left, ApiKeyQuotaModel? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return left.Requests == right.Requests
            && left.TotalTokens == right.TotalTokens
            && DecimalShapeEqual(left.Cost, right.Cost)
            && PeriodEqual(left.Period, right.Period);
    }

    private static bool PeriodEqual(
        ApiKeyQuotaPeriodModel left,
        ApiKeyQuotaPeriodModel right)
        => string.Equals(left.Type, right.Type, StringComparison.Ordinal)
            && DurationEqual(left.PastDuration, right.PastDuration)
            && DurationEqual(left.CalendarDuration, right.CalendarDuration);

    private static bool DurationEqual(
        ApiKeyQuotaPastDurationModel? left,
        ApiKeyQuotaPastDurationModel? right)
        => left is null || right is null
            ? left is null && right is null
            : left.Value == right.Value
                && string.Equals(left.Unit, right.Unit, StringComparison.Ordinal);

    private static bool DurationEqual(
        ApiKeyQuotaCalendarDurationModel? left,
        ApiKeyQuotaCalendarDurationModel? right)
        => left is null || right is null
            ? left is null && right is null
            : string.Equals(left.Unit, right.Unit, StringComparison.Ordinal);

    /// <summary>
    /// shopspring stores a big.Int coefficient plus an exponent, so
    /// reflect.DeepEqual distinguishes 1.0 from 1.00. The decimal bit layout
    /// keeps the same distinction; decimal.Equals would not.
    /// </summary>
    private static bool DecimalShapeEqual(decimal? left, decimal? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        var a = decimal.GetBits(left.Value);
        var b = decimal.GetBits(right.Value);
        return a[0] == b[0] && a[1] == b[1] && a[2] == b[2] && a[3] == b[3];
    }

    private static void WriteProfile(Utf8JsonWriter writer, ApiKeyProfileModel profile)
    {
        writer.WriteStartObject();
        writer.WriteString("name", profile.Name);

        if (profile.TemplateId is not null)
        {
            writer.WriteNumber("templateID", profile.TemplateId.Value);
        }

        if (!string.IsNullOrEmpty(profile.TemplateName))
        {
            writer.WriteString("templateName", profile.TemplateName);
        }

        writer.WritePropertyName("modelMappings");
        if (profile.ModelMappings is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStartArray();
            foreach (var mapping in profile.ModelMappings)
            {
                writer.WriteStartObject();
                writer.WriteString("from", mapping.From);
                writer.WriteString("to", mapping.To);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        if (profile.Quota is not null)
        {
            writer.WritePropertyName("quota");
            WriteQuota(writer, profile.Quota);
        }

        if (profile.LoadBalanceStrategy is not null)
        {
            writer.WriteString("loadBalanceStrategy", profile.LoadBalanceStrategy);
        }

        if (profile.TraceStickyMode is not null)
        {
            writer.WriteString("traceStickyMode", profile.TraceStickyMode);
        }

        if (profile.ChannelIDs is { Count: > 0 })
        {
            writer.WritePropertyName("channelIDs");
            writer.WriteStartArray();
            foreach (var id in profile.ChannelIDs)
            {
                writer.WriteNumberValue(id);
            }

            writer.WriteEndArray();
        }

        if (profile.ChannelTags is { Count: > 0 })
        {
            writer.WritePropertyName("channelTags");
            writer.WriteStartArray();
            foreach (var tag in profile.ChannelTags)
            {
                writer.WriteStringValue(tag);
            }

            writer.WriteEndArray();
        }

        if (!string.IsNullOrEmpty(profile.ChannelTagsMatchMode))
        {
            writer.WriteString("channelTagsMatchMode", profile.ChannelTagsMatchMode);
        }

        if (profile.ModelIDs is { Count: > 0 })
        {
            writer.WritePropertyName("modelIDs");
            writer.WriteStartArray();
            foreach (var id in profile.ModelIDs)
            {
                writer.WriteStringValue(id);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private static void WriteQuota(Utf8JsonWriter writer, ApiKeyQuotaModel quota)
    {
        writer.WriteStartObject();
        if (quota.Requests is not null)
        {
            writer.WriteNumber("requests", quota.Requests.Value);
        }

        if (quota.TotalTokens is not null)
        {
            writer.WriteNumber("totalTokens", quota.TotalTokens.Value);
        }

        if (quota.Cost is not null)
        {
            writer.WriteString("cost", GoDecimal.Format(quota.Cost.Value));
        }

        writer.WritePropertyName("period");
        writer.WriteStartObject();
        writer.WriteString("type", quota.Period.Type);
        if (quota.Period.PastDuration is not null)
        {
            writer.WritePropertyName("pastDuration");
            writer.WriteStartObject();
            writer.WriteNumber("value", quota.Period.PastDuration.Value);
            writer.WriteString("unit", quota.Period.PastDuration.Unit);
            writer.WriteEndObject();
        }

        if (quota.Period.CalendarDuration is not null)
        {
            writer.WritePropertyName("calendarDuration");
            writer.WriteStartObject();
            writer.WriteString("unit", quota.Period.CalendarDuration.Unit);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static ApiKeyProfileModel ReadProfile(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return new ApiKeyProfileModel();
        }

        IReadOnlyList<ApiKeyModelMapping>? modelMappings = null;
        if (FindProperty(element, "modelMappings") is { ValueKind: JsonValueKind.Array } mappings)
        {
            modelMappings = [.. mappings.EnumerateArray().Select(ReadMapping)];
        }

        ApiKeyQuotaModel? quota = null;
        if (FindProperty(element, "quota") is { ValueKind: JsonValueKind.Object } quotaElement)
        {
            quota = ReadQuota(quotaElement);
        }

        IReadOnlyList<long>? channelIds = null;
        if (FindProperty(element, "channelIDs") is { ValueKind: JsonValueKind.Array } channels)
        {
            channelIds = [.. channels.EnumerateArray().Select(static item => item.GetInt64())];
        }

        IReadOnlyList<string>? channelTags = null;
        if (FindProperty(element, "channelTags") is { ValueKind: JsonValueKind.Array } tags)
        {
            channelTags = [.. tags.EnumerateArray().Select(static item => item.GetString() ?? string.Empty)];
        }

        IReadOnlyList<string>? modelIds = null;
        if (FindProperty(element, "modelIDs") is { ValueKind: JsonValueKind.Array } models)
        {
            modelIds = [.. models.EnumerateArray().Select(static item => item.GetString() ?? string.Empty)];
        }

        long? templateId = null;
        if (FindProperty(element, "templateID") is { ValueKind: JsonValueKind.Number } template)
        {
            templateId = template.GetInt64();
        }

        var loadBalanceStrategy = ReadString(element, "loadBalanceStrategy");
        var traceStickyMode = ReadString(element, "traceStickyMode");

        return new ApiKeyProfileModel
        {
            Name = ReadString(element, "name") ?? string.Empty,
            TemplateId = templateId,
            TemplateName = ReadString(element, "templateName") ?? string.Empty,
            ModelMappings = modelMappings,
            Quota = quota,
            LoadBalanceStrategy = loadBalanceStrategy,
            TraceStickyMode = traceStickyMode,
            ChannelIDs = channelIds,
            ChannelTags = channelTags,
            ChannelTagsMatchMode = ReadString(element, "channelTagsMatchMode") ?? string.Empty,
            ModelIDs = modelIds,
        };
    }

    private static ApiKeyModelMapping ReadMapping(JsonElement element)
        => new()
        {
            From = ReadString(element, "from") ?? string.Empty,
            To = ReadString(element, "to") ?? string.Empty,
        };

    private static ApiKeyQuotaModel ReadQuota(JsonElement element)
    {
        long? requests = null;
        if (FindProperty(element, "requests") is { ValueKind: JsonValueKind.Number } requestsElement)
        {
            requests = requestsElement.GetInt64();
        }

        long? totalTokens = null;
        if (FindProperty(element, "totalTokens") is { ValueKind: JsonValueKind.Number } tokensElement)
        {
            totalTokens = tokensElement.GetInt64();
        }

        decimal? cost = null;
        if (FindProperty(element, "cost") is { } costElement)
        {
            cost = ReadDecimal(costElement);
        }

        var period = new ApiKeyQuotaPeriodModel();
        if (FindProperty(element, "period") is { ValueKind: JsonValueKind.Object } periodElement)
        {
            ApiKeyQuotaPastDurationModel? pastDuration = null;
            if (FindProperty(periodElement, "pastDuration") is { ValueKind: JsonValueKind.Object } past)
            {
                pastDuration = new ApiKeyQuotaPastDurationModel
                {
                    Value = FindProperty(past, "value") is { ValueKind: JsonValueKind.Number } value
                        ? value.GetInt64()
                        : 0,
                    Unit = ReadString(past, "unit") ?? string.Empty,
                };
            }

            ApiKeyQuotaCalendarDurationModel? calendarDuration = null;
            if (FindProperty(periodElement, "calendarDuration") is { ValueKind: JsonValueKind.Object } calendar)
            {
                calendarDuration = new ApiKeyQuotaCalendarDurationModel
                {
                    Unit = ReadString(calendar, "unit") ?? string.Empty,
                };
            }

            period = new ApiKeyQuotaPeriodModel
            {
                Type = ReadString(periodElement, "type") ?? string.Empty,
                PastDuration = pastDuration,
                CalendarDuration = calendarDuration,
            };
        }

        return new ApiKeyQuotaModel
        {
            Requests = requests,
            TotalTokens = totalTokens,
            Cost = cost,
            Period = period,
        };
    }

    private static decimal ReadDecimal(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.String => GoDecimal.Parse(element.GetString()),
            JsonValueKind.Number => element.TryGetDecimal(out var value)
                ? value
                : throw new JsonException("invalid decimal value"),
            _ => throw new JsonException("unexpected token for decimal: " + element.ValueKind),
        };

    private static JsonElement? FindProperty(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (element.TryGetProperty(name, out var exact))
        {
            return exact;
        }

        // Go's encoding/json matches object keys case-insensitively after an
        // exact match fails.
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static string? ReadString(JsonElement element, string name)
        => FindProperty(element, name) is { ValueKind: JsonValueKind.String } value
            ? value.GetString()
            : null;
}
