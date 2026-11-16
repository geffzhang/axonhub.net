using AxonHub.Api.Settings;
using AxonHub.Llm.Http;
using HotChocolate;

namespace AxonHub.Api.GraphQL;

/// <summary>Port of <c>HeaderEntryInput</c> (axonhub.graphql:16-19).</summary>
[GraphQLName("HeaderEntryInput")]
public sealed class HeaderEntryInput
{
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}

/// <summary>Port of the <c>WebhookTarget</c> type (system.graphql:76-84).</summary>
[GraphQLName("WebhookTarget")]
public sealed class WebhookTargetType
{
    public string Name { get; init; } = string.Empty;

    public bool Enabled { get; init; }

    public string Url { get; init; } = string.Empty;

    public ProxyConfigType? Proxy { get; init; }

    public int TimeoutMs { get; init; }

    public IReadOnlyList<HeaderEntryType> Headers { get; init; } = [];

    public string Body { get; init; } = string.Empty;
}

/// <summary>Port of <c>WebhookTargetInput</c> (system.graphql:86-94).</summary>
public sealed class WebhookTargetInput
{
    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    public string Url { get; set; } = string.Empty;

    public ProxyConfigInput? Proxy { get; set; }

    public int TimeoutMs { get; set; }

    public IReadOnlyList<HeaderEntryInput>? Headers { get; set; }

    public string Body { get; set; } = string.Empty;
}

/// <summary>Port of the <c>WebhookSubscription</c> type (system.graphql:96-99).</summary>
[GraphQLName("WebhookSubscription")]
public sealed class WebhookSubscriptionType
{
    public string Event { get; init; } = string.Empty;

    public IReadOnlyList<string> TargetNames { get; init; } = [];
}

/// <summary>Port of <c>WebhookSubscriptionInput</c> (system.graphql:101-104).</summary>
public sealed class WebhookSubscriptionInput
{
    public string Event { get; set; } = string.Empty;

    public IReadOnlyList<string> TargetNames { get; set; } = [];
}

/// <summary>Port of the <c>WebhookNotifierConfig</c> type (system.graphql:106-109).</summary>
[GraphQLName("WebhookNotifierConfig")]
public sealed class WebhookNotifierConfigType
{
    public IReadOnlyList<WebhookTargetType> Targets { get; init; } = [];

    public IReadOnlyList<WebhookSubscriptionType> Subscriptions { get; init; } = [];
}

/// <summary>Port of <c>WebhookNotifierConfigInput</c> (system.graphql:111-114).</summary>
public sealed class WebhookNotifierConfigInput
{
    public IReadOnlyList<WebhookTargetInput>? Targets { get; set; }

    public IReadOnlyList<WebhookSubscriptionInput>? Subscriptions { get; set; }
}

/// <summary>
/// Domain ↔ GraphQL mapping for the webhook notifier configuration. gqlgen's
/// generated non-null list marshalers turn a nil slice (including the nil
/// <c>headers</c> and <c>target_names</c> stored as JSON null) into an empty
/// array, so the port coalesces null lists the same way.
/// </summary>
public static class WebhookNotifierMapping
{
    public static WebhookNotifierConfigType ToGraphQL(WebhookNotifierConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return new WebhookNotifierConfigType
        {
            Targets = (config.Targets ?? []).Select(ToGraphQL).ToList(),
            Subscriptions = (config.Subscriptions ?? []).Select(ToGraphQL).ToList(),
        };
    }

    public static WebhookTargetType ToGraphQL(WebhookTarget target) => new()
    {
        Name = target.Name,
        Enabled = target.Enabled,
        Url = target.Url,
        Proxy = target.Proxy is null ? null : ToGraphQL(target.Proxy),
        TimeoutMs = target.TimeoutMs,
        Headers = (target.Headers ?? [])
            .Select(static header => new HeaderEntryType
            {
                Key = header.Key,
                Value = header.Value,
            })
            .ToList(),
        Body = target.Body,
    };

    public static WebhookSubscriptionType ToGraphQL(WebhookSubscription subscription) => new()
    {
        Event = subscription.Event,
        TargetNames = subscription.TargetNames ?? [],
    };

    /// <summary>
    /// The stored proxy type uses the lowercase httpclient.ProxyType wire
    /// values; an unknown value is a schema violation (C185 family).
    /// </summary>
    private static ProxyConfigType ToGraphQL(ProxyConfig proxy) => new()
    {
        Type = proxy.Type switch
        {
            ProxyType.Disabled => ProxyTypeValue.Disabled,
            ProxyType.Environment => ProxyTypeValue.Environment,
            ProxyType.Url => ProxyTypeValue.Url,
            _ => throw new GraphQLException(
                "Enum \"ProxyType\" cannot represent value: \""
                + proxy.Type
                + "\""),
        },
        Url = proxy.Url ?? string.Empty,
        Username = proxy.Username ?? string.Empty,
        Password = proxy.Password ?? string.Empty,
        DisableConnectionReuse = proxy.DisableConnectionReuse,
    };

    /// <summary>
    /// gqlgen decodes WebhookNotifierConfigInput straight into
    /// biz.WebhookNotifierConfig: an absent list stays nil until the service
    /// normalizes it, and an absent proxy field is nil.
    /// </summary>
    public static WebhookNotifierConfig ToDomain(WebhookNotifierConfigInput input) => new()
    {
        Targets = input.Targets?.Select(ToDomain).ToList(),
        Subscriptions = input.Subscriptions?.Select(ToDomain).ToList(),
    };

    private static WebhookTarget ToDomain(WebhookTargetInput input) => new()
    {
        Name = input.Name,
        Enabled = input.Enabled,
        Url = input.Url,
        Proxy = input.Proxy is null ? null : ToDomain(input.Proxy),
        TimeoutMs = input.TimeoutMs,
        Headers = input.Headers?
            .Select(static header => new WebhookHeaderEntry
            {
                Key = header.Key,
                Value = header.Value,
            })
            .ToList(),
        Body = input.Body,
    };

    private static ProxyConfig ToDomain(ProxyConfigInput input) => new()
    {
        Type = input.Type switch
        {
            ProxyTypeValue.Environment => ProxyType.Environment,
            ProxyTypeValue.Url => ProxyType.Url,
            _ => ProxyType.Disabled,
        },
        Url = input.Url ?? string.Empty,
        Username = input.Username ?? string.Empty,
        Password = input.Password ?? string.Empty,
        DisableConnectionReuse = input.DisableConnectionReuse ?? false,
    };

    private static WebhookSubscription ToDomain(WebhookSubscriptionInput input) => new()
    {
        Event = input.Event,
        TargetNames = input.TargetNames.ToList(),
    };
}
