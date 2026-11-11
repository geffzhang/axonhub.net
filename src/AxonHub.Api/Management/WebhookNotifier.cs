using System.Globalization;
using System.Text;
using AxonHub.Api.Orchestration;
using AxonHub.Api.Settings;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Pipeline;
using AxonHub.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AxonHub.Api.Management;

/// <summary>
/// Port of biz.ChannelAutoDisabledEvent (webhook_notifier.go:27-38): the event
/// published when a channel is auto-disabled by a rule or by credential
/// exhaustion.
/// </summary>
public sealed record ChannelAutoDisabledEvent
{
    public long ChannelId { get; init; }

    public string ChannelName { get; init; } = string.Empty;

    public string ChannelProvider { get; init; } = string.Empty;

    public string ChannelBaseUrl { get; init; } = string.Empty;

    public string ChannelStatus { get; init; } = string.Empty;

    public int StatusCode { get; init; }

    public int Threshold { get; init; }

    public int ActualCount { get; init; }

    public string Reason { get; init; } = string.Empty;

    public DateTimeOffset OccurredAt { get; init; }
}

/// <summary>
/// Port of the anonymous <c>WebhookRenderContext.Channel</c> struct
/// (webhook_notifier.go:48-54). The field names are the Go exported names
/// because templates address them by name; the numeric fields are longs so the
/// template formatter prints them like Go's <c>int</c>.
/// </summary>
public sealed class WebhookChannelContext
{
    public long Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Provider { get; init; } = string.Empty;

    public string BaseUrl { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;
}

/// <summary>
/// Port of the anonymous <c>WebhookRenderContext.Trigger</c> struct
/// (webhook_notifier.go:56-62).
/// </summary>
public sealed class WebhookTriggerContext
{
    public string Type { get; init; } = string.Empty;

    public long StatusCode { get; init; }

    public long Threshold { get; init; }

    public long ActualCount { get; init; }

    public string Reason { get; init; } = string.Empty;
}

/// <summary>
/// Port of biz.WebhookRenderContext (webhook_notifier.go:40-63). The reference
/// renders this struct with text/template; <see cref="OccurredAt"/> is set by
/// <see cref="WebhookNotifier"/> after resolving the configured timezone.
/// </summary>
public sealed class WebhookRenderContext
{
    public string Event { get; init; } = string.Empty;

    public string Severity { get; init; } = string.Empty;

    /// <summary>RFC3339 in the configured system timezone.</summary>
    public string OccurredAt { get; set; } = string.Empty;

    public WebhookChannelContext Channel { get; init; } = new();

    public WebhookTriggerContext Trigger { get; init; } = new();
}

/// <summary>
/// Port of biz.WebhookNotifier (webhook_notifier.go:65-253): selects the
/// targets subscribed to an event, renders each body and header set, and POSTs
/// the result with the target's timeout and proxy. Every failure is logged and
/// only skips the affected target, exactly like the reference.
/// </summary>
public sealed class WebhookNotifier
{
    /// <summary>biz.EventChannelAutoDisabled.</summary>
    public const string EventChannelAutoDisabled = "channel.auto_disabled";

    /// <summary>biz.defaultWebhookMethod.</summary>
    internal const string DefaultWebhookMethod = "POST";

    /// <summary>biz.defaultWebhookTimeoutMs.</summary>
    internal const int DefaultWebhookTimeoutMs = 3000;

    private static readonly Action<ILogger, long, int, string, Exception?> LogNotifyChannelAutoDisabled =
        LoggerMessage.Define<long, int, string>(
            LogLevel.Information,
            new EventId(0, "NotifyChannelAutoDisabled"),
            "notify channel auto disabled channel_id={ChannelId} status_code={StatusCode} reason={Reason}");

    private static readonly Action<ILogger, Exception?> LogWebhookConfigReadFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(0, "WebhookNotifierConfigReadFailed"),
            "failed to get webhook notifier config");

    private static readonly Action<ILogger, string, int, Exception?> LogNotifyWebhook =
        LoggerMessage.Define<string, int>(
            LogLevel.Debug,
            new EventId(0, "NotifyWebhook"),
            "notify webhook event_name={EventName} target_count={TargetCount}");

    private static readonly Action<ILogger, string, string, Exception?> LogBodyTemplateRenderFailed =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(0, "WebhookBodyTemplateRenderFailed"),
            "failed to render webhook body template event={Event} target={Target}");

    private static readonly Action<ILogger, string, string, Exception?> LogHeadersRenderFailed =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(0, "WebhookHeadersRenderFailed"),
            "failed to render webhook headers event={Event} target={Target}");

    private static readonly Action<ILogger, string, string, Exception?> LogWebhookSendFailed =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(0, "WebhookSendFailed"),
            "failed to send webhook notification event={Event} target={Target}");

    private readonly ISystemSettingsAdminService _settings;
    private readonly ISystemStore _systemStore;
    private readonly IExecutor _httpClient;
    private readonly IProxyExecutorFactory _proxyExecutorFactory;
    private readonly ILogger<WebhookNotifier>? _logger;

    public WebhookNotifier(
        ISystemSettingsAdminService settings,
        ISystemStore systemStore,
        IExecutor httpClient,
        IProxyExecutorFactory proxyExecutorFactory,
        ILogger<WebhookNotifier>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(systemStore);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(proxyExecutorFactory);

        _settings = settings;
        _systemStore = systemStore;
        _httpClient = httpClient;
        _proxyExecutorFactory = proxyExecutorFactory;
        _logger = logger;
    }

    /// <summary>
    /// Port of WebhookNotifier.NotifyChannelAutoDisabled (webhook_notifier.go:77-96).
    /// </summary>
    public async Task NotifyChannelAutoDisabledAsync(
        ChannelAutoDisabledEvent notification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        if (_logger is { } logger)
        {
            LogNotifyChannelAutoDisabled(logger, notification.ChannelId, notification.StatusCode, notification.Reason, null);
        }

        var renderContext = new WebhookRenderContext
        {
            Event = EventChannelAutoDisabled,
            Severity = "warning",
            Channel = new WebhookChannelContext
            {
                Id = notification.ChannelId,
                Name = notification.ChannelName,
                Provider = notification.ChannelProvider,
                BaseUrl = notification.ChannelBaseUrl,
                Status = notification.ChannelStatus,
            },
            Trigger = new WebhookTriggerContext
            {
                Type = "error_status_rule",
                StatusCode = notification.StatusCode,
                Threshold = notification.Threshold,
                ActualCount = notification.ActualCount,
                Reason = notification.Reason,
            },
        };

        await NotifyAsync(EventChannelAutoDisabled, renderContext, notification.OccurredAt, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Port of WebhookNotifier.notify (webhook_notifier.go:104-156): the
    /// occurrence is formatted after the timezone read, since that read is the
    /// system-scoped operation the reference wraps in a bypass.
    /// </summary>
    public async Task NotifyAsync(
        string eventName,
        WebhookRenderContext renderContext,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventName);
        ArgumentNullException.ThrowIfNull(renderContext);

        var location = await SystemTimeLocations
            .ResolveAsync(_systemStore, cancellationToken)
            .ConfigureAwait(false);
        renderContext.OccurredAt = FormatRfc3339(occurredAt, location.Zone);

        WebhookNotifierConfig config;
        try
        {
            config = await _settings
                .GetWebhookNotifierConfigAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // WebhookNotifierConfigOrDefault logs and falls back to the
            // normalized zero configuration.
            if (_logger is { } configLogger)
            {
                LogWebhookConfigReadFailed(configLogger, ex);
            }

            config = WebhookNotifierConfigCodec.Default;
        }

        var targets = SelectTargets(config, eventName);

        if (_logger is { } notifyLogger)
        {
            LogNotifyWebhook(notifyLogger, eventName, targets.Count, null);
        }

        foreach (var target in targets)
        {
            if (!GoTextTemplate.TryRender(
                    target.Body,
                    renderContext,
                    GoTemplateFunctions.Webhook,
                    out var body))
            {
                if (_logger is { } bodyLogger)
                {
                    LogBodyTemplateRenderFailed(bodyLogger, eventName, target.Name, null);
                }

                continue;
            }

            Dictionary<string, List<string>> headers;
            try
            {
                headers = RenderWebhookHeaders(target.Headers, renderContext);
            }
            catch (InvalidOperationException ex)
            {
                if (_logger is { } headerLogger)
                {
                    LogHeadersRenderFailed(headerLogger, eventName, target.Name, ex);
                }

                continue;
            }

            try
            {
                Send(target, body, headers);
            }
            catch (InvalidOperationException ex)
            {
                if (_logger is { } sendLogger)
                {
                    LogWebhookSendFailed(sendLogger, eventName, target.Name, ex);
                }
            }
        }
    }

    /// <summary>
    /// Port of WebhookNotifier.selectTargets (webhook_notifier.go:158-184):
    /// only the first subscription for the event is consulted, names are
    /// matched against the first target with that name, and a duplicate name
    /// is sent more than once.
    /// </summary>
    internal static List<WebhookTarget> SelectTargets(WebhookNotifierConfig config, string eventName)
    {
        var subscription = config.Subscriptions?.FirstOrDefault(
            item => string.Equals(item.Event, eventName, StringComparison.Ordinal));
        if (subscription is null)
        {
            return [];
        }

        var names = subscription.TargetNames;
        if (names is null || names.Count == 0 || config.Targets is null || config.Targets.Count == 0)
        {
            return [];
        }

        var targets = new List<WebhookTarget>(names.Count);
        foreach (var name in names)
        {
            var target = config.Targets.FirstOrDefault(
                item => string.Equals(item.Name, name, StringComparison.Ordinal));
            if (target is null || !target.Enabled || string.IsNullOrWhiteSpace(target.Url))
            {
                continue;
            }

            targets.Add(target);
        }

        return targets;
    }

    /// <summary>
    /// Port of renderWebhookHeaders (webhook_notifier.go:218-235). An empty key
    /// is skipped; a failed value render fails the whole target with the
    /// reference's wrapping.
    /// </summary>
    internal static Dictionary<string, List<string>> RenderWebhookHeaders(
        IReadOnlyList<WebhookHeaderEntry>? headers,
        WebhookRenderContext renderContext)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (headers is null)
        {
            return result;
        }

        foreach (var header in headers)
        {
            var key = header.Key.Trim();
            if (key.Length == 0)
            {
                continue;
            }

            if (!GoTextTemplate.TryRender(
                    header.Value,
                    renderContext,
                    GoTemplateFunctions.Webhook,
                    out var value))
            {
                throw new InvalidOperationException(
                    "header " + GoCast.GoQuote(key) + ": template render failed");
            }

            // http.Header.Set canonicalizes the key and replaces the values.
            HttpClientUtils.HeaderSet(result, key, value);
        }

        return result;
    }

    /// <summary>
    /// Port of WebhookNotifier.send (webhook_notifier.go:186-216): POST with
    /// the target's timeout, JSON default content type, and the target proxy
    /// when configured.
    /// </summary>
    private void Send(
        WebhookTarget target,
        string body,
        Dictionary<string, List<string>> headers)
    {
        var timeoutMs = target.TimeoutMs <= 0 ? DefaultWebhookTimeoutMs : target.TimeoutMs;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));

        if (HttpClientUtils.HeaderGet(headers, "Content-Type").Length == 0)
        {
            HttpClientUtils.HeaderSet(headers, "Content-Type", "application/json");
        }

        var client = target.Proxy is null
            ? _httpClient
            : _proxyExecutorFactory.Create(target.Proxy);

        var request = new HttpRequestData
        {
            Method = DefaultWebhookMethod,
            Url = target.Url,
            Headers = headers,
            ContentType = HttpClientUtils.HeaderGet(headers, "Content-Type"),
            Body = Encoding.UTF8.GetBytes(body),
        };

        try
        {
            _ = client.Do(timeout.Token, request);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("do request: " + ex.Message, ex);
        }
    }

    /// <summary>
    /// Go's <c>time.RFC3339</c> layout: second precision, <c>Z</c> for a zero
    /// offset and <c>±hh:mm</c> otherwise.
    /// </summary>
    internal static string FormatRfc3339(DateTimeOffset instant, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(instant, zone);
        return local.Offset == TimeSpan.Zero
            ? local.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            : local.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// The background delivery seam for channel auto-disable notifications. The
/// reference fires a goroutine (asyncNotifyChannelAutoDisabled) detached from
/// the caller's context; the port hands the event to this sink so the request
/// path never waits for the webhook.
/// </summary>
public interface IWebhookNotificationSink
{
    void PublishChannelAutoDisabled(ChannelAutoDisabledEvent notification);
}

/// <summary>
/// The production sink: one detached task per event, with a fresh DI scope for
/// the scoped settings/store reads. A failure is logged like the reference's
/// goroutine recover (webhook_notifier.go:135-143).
/// </summary>
public sealed class BackgroundWebhookNotificationSink(
    IServiceScopeFactory scopeFactory,
    ILogger<BackgroundWebhookNotificationSink>? logger = null) : IWebhookNotificationSink
{
    private static readonly Action<ILogger, Exception?> LogNotificationPanicked =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(0, "WebhookNotificationPanicked"),
            "channel auto-disabled webhook notification panicked");

    public void PublishChannelAutoDisabled(ChannelAutoDisabledEvent notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var notifier = scope.ServiceProvider.GetRequiredService<WebhookNotifier>();
                await notifier
                    .NotifyChannelAutoDisabledAsync(notification, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (logger is { } sinkLogger)
                {
                    LogNotificationPanicked(sinkLogger, ex);
                }
            }
        });
    }
}
