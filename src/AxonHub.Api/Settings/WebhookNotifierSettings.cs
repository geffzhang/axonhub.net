using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;

namespace AxonHub.Api.Settings;

/// <summary>
/// Port of biz.WebhookNotifierConfig (system.go:416-419): the JSON document
/// stored under <c>webhook_notifier_config</c>.
/// </summary>
public sealed record WebhookNotifierConfig
{
    public IReadOnlyList<WebhookTarget>? Targets { get; init; }

    public IReadOnlyList<WebhookSubscription>? Subscriptions { get; init; }
}

/// <summary>
/// Port of biz.WebhookTarget (system.go:421-429). A nil <see cref="Proxy"/>
/// is omitted from the stored document; a nil <see cref="Headers"/> is stored
/// as JSON <c>null</c>.
/// </summary>
public sealed record WebhookTarget
{
    public string Name { get; init; } = string.Empty;

    public bool Enabled { get; init; }

    public string Url { get; init; } = string.Empty;

    public ProxyConfig? Proxy { get; init; }

    public int TimeoutMs { get; init; }

    public IReadOnlyList<WebhookHeaderEntry>? Headers { get; init; }

    public string Body { get; init; } = string.Empty;
}

/// <summary>Port of objects.HeaderEntry (internal/objects/channel.go:44-47).</summary>
public sealed record WebhookHeaderEntry
{
    public string Key { get; init; } = string.Empty;

    public string Value { get; init; } = string.Empty;
}

/// <summary>Port of biz.WebhookSubscription (system.go:431-434).</summary>
public sealed record WebhookSubscription
{
    public string Event { get; init; } = string.Empty;

    public IReadOnlyList<string>? TargetNames { get; init; }
}

/// <summary>
/// JSON codec for biz.WebhookNotifierConfig (system.go:1174-1234). Decoding
/// follows Go encoding/json: nil lists stay nil until
/// <see cref="Normalize"/>, a nil slice element becomes the zero value and a
/// JSON null into a scalar field leaves the zero value.
/// </summary>
public static class WebhookNotifierConfigCodec
{
    /// <summary>
    /// Port of normalizeWebhookNotifierConfig (system.go:1174-1186): only the
    /// two nil lists are replaced with empty slices.
    /// </summary>
    public static WebhookNotifierConfig Normalize(WebhookNotifierConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return config with
        {
            Targets = config.Targets ?? [],
            Subscriptions = config.Subscriptions ?? [],
        };
    }

    /// <summary>
    /// The ent.IsNotFound branch of SystemService.WebhookNotifierConfig
    /// (system.go:1189-1198): a zero config with both lists normalized.
    /// </summary>
    public static WebhookNotifierConfig Default { get; } = new()
    {
        Targets = [],
        Subscriptions = [],
    };

    /// <summary>
    /// Port of <c>json.Unmarshal(value, &amp;cfg)</c> followed by
    /// <c>normalizeWebhookNotifierConfig</c> (system.go:1200-1209).
    /// </summary>
    /// <exception cref="JsonException">The stored value is not valid JSON.</exception>
    public static WebhookNotifierConfig Deserialize(string value)
    {
        var stored = JsonSerializer.Deserialize<ConfigJson>(value, LlmJson.Options);
        if (stored is null)
        {
            // A stored JSON null leaves the zero config.
            return Default;
        }

        return Normalize(new WebhookNotifierConfig
        {
            Targets = stored.Targets?.Select(ToTarget).ToList(),
            Subscriptions = stored.Subscriptions?.Select(ToSubscription).ToList(),
        });
    }

    /// <summary>Go <c>json.Marshal(cfg)</c> after normalization (system.go:1225-1234).</summary>
    public static string Serialize(WebhookNotifierConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   buffer,
                   new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("targets");
            WriteTargets(writer, config.Targets);
            writer.WritePropertyName("subscriptions");
            WriteSubscriptions(writer, config.Subscriptions);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(GoJson.Escape(buffer.ToArray()));
    }

    private static WebhookTarget ToTarget(TargetJson? stored)
    {
        if (stored is null)
        {
            // Go value-slice element: a JSON null leaves the zero target.
            return new WebhookTarget();
        }

        return new WebhookTarget
        {
            Name = stored.Name ?? string.Empty,
            Enabled = stored.Enabled ?? false,
            Url = stored.Url ?? string.Empty,
            Proxy = ToProxy(stored.Proxy),
            TimeoutMs = stored.TimeoutMs ?? 0,
            Headers = stored.Headers?.Select(ToHeaderEntry).ToList(),
            Body = stored.Body ?? string.Empty,
        };
    }

    private static ProxyConfig? ToProxy(ProxyJson? stored)
        => stored is null
            ? null
            : new ProxyConfig
            {
                Type = stored.Type ?? string.Empty,
                Url = stored.Url ?? string.Empty,
                Username = stored.Username ?? string.Empty,
                Password = stored.Password ?? string.Empty,
                DisableConnectionReuse = stored.DisableConnectionReuse ?? false,
            };

    private static WebhookHeaderEntry ToHeaderEntry(HeaderEntryJson? stored)
        => stored is null
            ? new WebhookHeaderEntry()
            : new WebhookHeaderEntry
            {
                Key = stored.Key ?? string.Empty,
                Value = stored.Value ?? string.Empty,
            };

    private static WebhookSubscription ToSubscription(SubscriptionJson? stored)
    {
        if (stored is null)
        {
            return new WebhookSubscription();
        }

        return new WebhookSubscription
        {
            Event = stored.Event ?? string.Empty,

            // []string elements: a JSON null leaves "".
            TargetNames = stored.TargetNames?.Select(name => name ?? string.Empty).ToList(),
        };
    }

    private static void WriteTargets(Utf8JsonWriter writer, IReadOnlyList<WebhookTarget>? targets)
    {
        if (targets is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        foreach (var target in targets)
        {
            writer.WriteStartObject();
            writer.WriteString("name", target.Name);
            writer.WriteBoolean("enabled", target.Enabled);
            writer.WriteString("url", target.Url);
            if (target.Proxy is { } proxy)
            {
                writer.WritePropertyName("proxy");
                WriteProxy(writer, proxy);
            }

            writer.WriteNumber("timeout_ms", target.TimeoutMs);
            writer.WritePropertyName("headers");
            WriteHeaders(writer, target.Headers);
            writer.WriteString("body", target.Body);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteProxy(Utf8JsonWriter writer, ProxyConfig proxy)
    {
        // Port of httpclient.ProxyConfig tags (llm/httpclient/proxy.go:11-17):
        // the type is always emitted, the other strings and the boolean carry
        // omitempty.
        writer.WriteStartObject();
        writer.WriteString("type", proxy.Type);
        if (!string.IsNullOrEmpty(proxy.Url))
        {
            writer.WriteString("url", proxy.Url);
        }

        if (!string.IsNullOrEmpty(proxy.Username))
        {
            writer.WriteString("username", proxy.Username);
        }

        if (!string.IsNullOrEmpty(proxy.Password))
        {
            writer.WriteString("password", proxy.Password);
        }

        if (proxy.DisableConnectionReuse)
        {
            writer.WriteBoolean("disableConnectionReuse", true);
        }

        writer.WriteEndObject();
    }

    private static void WriteHeaders(
        Utf8JsonWriter writer,
        IReadOnlyList<WebhookHeaderEntry>? headers)
    {
        if (headers is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        foreach (var header in headers)
        {
            writer.WriteStartObject();
            writer.WriteString("key", header.Key);
            writer.WriteString("value", header.Value);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteSubscriptions(
        Utf8JsonWriter writer,
        IReadOnlyList<WebhookSubscription>? subscriptions)
    {
        if (subscriptions is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        foreach (var subscription in subscriptions)
        {
            writer.WriteStartObject();
            writer.WriteString("event", subscription.Event);
            writer.WritePropertyName("target_names");
            if (subscription.TargetNames is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStartArray();
                foreach (var targetName in subscription.TargetNames)
                {
                    writer.WriteStringValue(targetName);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private sealed class ConfigJson
    {
        [JsonPropertyName("targets")]
        public List<TargetJson?>? Targets { get; set; }

        [JsonPropertyName("subscriptions")]
        public List<SubscriptionJson?>? Subscriptions { get; set; }
    }

    private sealed class TargetJson
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("enabled")]
        public bool? Enabled { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("proxy")]
        public ProxyJson? Proxy { get; set; }

        [JsonPropertyName("timeout_ms")]
        public int? TimeoutMs { get; set; }

        [JsonPropertyName("headers")]
        public List<HeaderEntryJson?>? Headers { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }
    }

    private sealed class ProxyJson
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("username")]
        public string? Username { get; set; }

        [JsonPropertyName("password")]
        public string? Password { get; set; }

        [JsonPropertyName("disableConnectionReuse")]
        public bool? DisableConnectionReuse { get; set; }
    }

    private sealed class HeaderEntryJson
    {
        [JsonPropertyName("key")]
        public string? Key { get; set; }

        [JsonPropertyName("value")]
        public string? Value { get; set; }
    }

    private sealed class SubscriptionJson
    {
        [JsonPropertyName("event")]
        public string? Event { get; set; }

        [JsonPropertyName("target_names")]
        public List<string?>? TargetNames { get; set; }
    }
}
