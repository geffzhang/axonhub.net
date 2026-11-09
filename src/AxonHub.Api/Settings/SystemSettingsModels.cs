using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Api.Orchestration;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Api.Settings;

/// <summary>Port of biz.ProbeFrequency (system.go:576-583).</summary>
public static class ProbeFrequencies
{
    public const string OneMinute = "1m";

    public const string FiveMinutes = "5m";

    public const string ThirtyMinutes = "30m";

    public const string OneHour = "1h";
}

/// <summary>
/// Port of biz.AutoSyncFrequency (system.go:507-513). The stored values are the
/// lowercase wire strings; the enum type also decodes the GraphQL names.
/// </summary>
public static class AutoSyncFrequencies
{
    public const string OneHour = "1h";

    public const string SixHours = "6h";

    public const string OneDay = "1d";
}

/// <summary>Port of biz.ChannelProbeSetting (system.go:586-591).</summary>
public sealed record ChannelProbeSetting
{
    public bool Enabled { get; init; }

    public string Frequency { get; init; } = string.Empty;
}

/// <summary>Port of biz.ChannelModelAutoSyncSetting (system.go:503-505).</summary>
public sealed record ChannelModelAutoSyncSetting
{
    public string Frequency { get; init; } = string.Empty;
}

/// <summary>Port of biz.SystemChannelSettings (system.go:487-492).</summary>
public sealed record SystemChannelSettings
{
    public ChannelProbeSetting Probe { get; init; } = new();

    public ChannelModelAutoSyncSetting AutoSync { get; init; } = new();

    public string TestSystemPrompt { get; init; } = string.Empty;

    public string TestUserPrompt { get; init; } = string.Empty;
}

/// <summary>
/// Port of biz.UpdateSystemChannelSettings (system.go:496-501): omitted fields
/// are left unchanged; an empty or whitespace-only prompt restores the default.
/// </summary>
public sealed record SystemChannelSettingsPatch
{
    public ChannelProbeSetting? Probe { get; init; }

    public ChannelModelAutoSyncSetting? AutoSync { get; init; }

    public string? TestSystemPrompt { get; init; }

    public string? TestUserPrompt { get; init; }
}

/// <summary>Port of biz.SystemGeneralSettings (system.go:138-142).</summary>
public sealed record SystemGeneralSettings
{
    public string CurrencyCode { get; init; } = string.Empty;

    public string Timezone { get; init; } = string.Empty;
}

/// <summary>Port of the <c>brandSettings</c> payload (system.resolvers.go:491-512).</summary>
public sealed record BrandSettings
{
    public string BrandName { get; init; } = string.Empty;

    public string BrandLogo { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;
}

/// <summary>
/// Port of the defaults in system_default.go:6-8 and 101-116. Records are
/// immutable, so every read can share the same instances, like Go's package
/// variables.
/// </summary>
public static class SystemSettingsDefaults
{
    /// <summary>maxChannelTestPromptRunes (system_default.go:8).</summary>
    public const int MaxChannelTestPromptRunes = 4096;

    /// <summary>defaultChannelTestSystemPrompt (system_default.go:6).</summary>
    public const string DefaultChannelTestSystemPrompt =
        ChannelTestPrompts.DefaultSystemPrompt;

    /// <summary>defaultChannelTestUserPrompt (system_default.go:7).</summary>
    public const string DefaultChannelTestUserPrompt =
        ChannelTestPrompts.DefaultUserPrompt;

    /// <summary>defaultGeneralSettings (system_default.go:113-116).</summary>
    public static SystemGeneralSettings General { get; } = new()
    {
        CurrencyCode = "USD",
        Timezone = "UTC",
    };

    /// <summary>defaultChannelSetting (system_default.go:101-111).</summary>
    public static SystemChannelSettings Channel { get; } = new()
    {
        Probe = new ChannelProbeSetting
        {
            Enabled = true,
            Frequency = ProbeFrequencies.FiveMinutes,
        },
        AutoSync = new ChannelModelAutoSyncSetting
        {
            Frequency = AutoSyncFrequencies.OneHour,
        },
        TestSystemPrompt = DefaultChannelTestSystemPrompt,
        TestUserPrompt = DefaultChannelTestUserPrompt,
    };
}

/// <summary>
/// JSON codec for biz.SystemGeneralSettings / biz.SystemChannelSettings
/// (Go encoding/json, fields in declaration order, every field emitted).
/// </summary>
public static class SystemSettingsCodec
{
    /// <exception cref="JsonException">The stored value is not valid JSON.</exception>
    public static SystemGeneralSettings DeserializeGeneral(string value)
    {
        var stored = JsonSerializer.Deserialize<GeneralSettingsJson>(value, LlmJson.Options)
            ?? new GeneralSettingsJson();

        return new SystemGeneralSettings
        {
            CurrencyCode = stored.CurrencyCode ?? string.Empty,
            Timezone = stored.Timezone ?? string.Empty,
        };
    }

    public static string SerializeGeneral(SystemGeneralSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   buffer,
                   new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("currency_code", settings.CurrencyCode);
            writer.WriteString("timezone", settings.Timezone);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(GoJson.Escape(buffer.ToArray()));
    }

    /// <exception cref="JsonException">The stored value is not valid JSON.</exception>
    public static SystemChannelSettings DeserializeChannel(string value)
    {
        var stored = JsonSerializer.Deserialize<ChannelSettingsJson>(value, LlmJson.Options)
            ?? new ChannelSettingsJson();

        return new SystemChannelSettings
        {
            Probe = stored.Probe is null
                ? new ChannelProbeSetting()
                : new ChannelProbeSetting
                {
                    Enabled = stored.Probe.Enabled,
                    Frequency = stored.Probe.Frequency ?? string.Empty,
                },
            AutoSync = new ChannelModelAutoSyncSetting
            {
                // AutoSyncFrequency.UnmarshalJSON (system.go:552-573) never
                // fails: a non-string value, an unknown string and the legacy
                // probe intervals all become "1h".
                Frequency = DecodeAutoSyncFrequency(stored.AutoSync?.Frequency),
            },
            TestSystemPrompt = stored.TestSystemPrompt ?? string.Empty,
            TestUserPrompt = stored.TestUserPrompt ?? string.Empty,
        };
    }

    public static string SerializeChannel(SystemChannelSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   buffer,
                   new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("probe");
            writer.WriteStartObject();
            writer.WriteBoolean("enabled", settings.Probe.Enabled);
            writer.WriteString("frequency", settings.Probe.Frequency);
            writer.WriteEndObject();
            writer.WritePropertyName("auto_sync");
            writer.WriteStartObject();
            writer.WriteString("frequency", settings.AutoSync.Frequency);
            writer.WriteEndObject();
            writer.WriteString("test_system_prompt", settings.TestSystemPrompt);
            writer.WriteString("test_user_prompt", settings.TestUserPrompt);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(GoJson.Escape(buffer.ToArray()));
    }

    /// <summary>
    /// Port of normalizeSystemChannelSettings (system.go:1293-1306): an unknown
    /// auto-sync interval falls back to the default, and a blank prompt restores
    /// the default prompt without trimming a non-blank value.
    /// </summary>
    public static SystemChannelSettings Normalize(SystemChannelSettings setting)
    {
        ArgumentNullException.ThrowIfNull(setting);

        var frequency = setting.AutoSync.Frequency
            is AutoSyncFrequencies.OneHour
            or AutoSyncFrequencies.SixHours
            or AutoSyncFrequencies.OneDay
            ? setting.AutoSync.Frequency
            : SystemSettingsDefaults.Channel.AutoSync.Frequency;

        var testSystemPrompt =
            GoStrings.TrimSpace(setting.TestSystemPrompt).Length == 0
                ? SystemSettingsDefaults.Channel.TestSystemPrompt
                : setting.TestSystemPrompt;

        var testUserPrompt =
            GoStrings.TrimSpace(setting.TestUserPrompt).Length == 0
                ? SystemSettingsDefaults.Channel.TestUserPrompt
                : setting.TestUserPrompt;

        return setting with
        {
            AutoSync = setting.AutoSync with { Frequency = frequency },
            TestSystemPrompt = testSystemPrompt,
            TestUserPrompt = testUserPrompt,
        };
    }

    /// <summary>
    /// Port of validateSystemChannelSettings (system.go:1308-1317): the system
    /// prompt is validated before the user prompt, and both use Go runes.
    /// </summary>
    public static void Validate(SystemChannelSettings setting)
    {
        ArgumentNullException.ThrowIfNull(setting);

        if (RuneCount(setting.TestSystemPrompt) > SystemSettingsDefaults.MaxChannelTestPromptRunes)
        {
            throw new InvalidOperationException(
                "test system prompt must not exceed "
                + SystemSettingsDefaults.MaxChannelTestPromptRunes
                + " characters");
        }

        if (RuneCount(setting.TestUserPrompt) > SystemSettingsDefaults.MaxChannelTestPromptRunes)
        {
            throw new InvalidOperationException(
                "test user prompt must not exceed "
                + SystemSettingsDefaults.MaxChannelTestPromptRunes
                + " characters");
        }
    }

    /// <summary>Port of applySystemChannelSettingsPatch (system.go:1464-1477).</summary>
    public static SystemChannelSettings ApplyPatch(
        SystemChannelSettings setting,
        SystemChannelSettingsPatch patch)
    {
        ArgumentNullException.ThrowIfNull(setting);
        ArgumentNullException.ThrowIfNull(patch);

        return setting with
        {
            Probe = patch.Probe ?? setting.Probe,
            AutoSync = patch.AutoSync ?? setting.AutoSync,
            TestSystemPrompt = patch.TestSystemPrompt ?? setting.TestSystemPrompt,
            TestUserPrompt = patch.TestUserPrompt ?? setting.TestUserPrompt,
        };
    }

    private static long RuneCount(string value)
    {
        long count = 0;
        foreach (var _ in value.EnumerateRunes())
        {
            count++;
        }

        return count;
    }

    private static string DecodeAutoSyncFrequency(JsonElement? element)
    {
        if (element is not { } value || value.ValueKind != JsonValueKind.String)
        {
            return AutoSyncFrequencies.OneHour;
        }

        return value.GetString() switch
        {
            AutoSyncFrequencies.OneHour or "ONE_HOUR" => AutoSyncFrequencies.OneHour,
            AutoSyncFrequencies.SixHours or "SIX_HOURS" => AutoSyncFrequencies.SixHours,
            AutoSyncFrequencies.OneDay or "ONE_DAY" => AutoSyncFrequencies.OneDay,
            _ => AutoSyncFrequencies.OneHour,
        };
    }

    private sealed class GeneralSettingsJson
    {
        [JsonPropertyName("currency_code")]
        public string? CurrencyCode { get; set; }

        [JsonPropertyName("timezone")]
        public string? Timezone { get; set; }
    }

    private sealed class ChannelSettingsJson
    {
        [JsonPropertyName("probe")]
        public ChannelProbeSettingJson? Probe { get; set; }

        [JsonPropertyName("auto_sync")]
        public ChannelModelAutoSyncSettingJson? AutoSync { get; set; }

        [JsonPropertyName("test_system_prompt")]
        public string? TestSystemPrompt { get; set; }

        [JsonPropertyName("test_user_prompt")]
        public string? TestUserPrompt { get; set; }
    }

    private sealed class ChannelProbeSettingJson
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        [JsonPropertyName("frequency")]
        public string? Frequency { get; set; }
    }

    private sealed class ChannelModelAutoSyncSettingJson
    {
        [JsonPropertyName("frequency")]
        public JsonElement Frequency { get; set; }
    }
}
