using System.Text;
using AxonHub.Api.Management;
using AxonHub.Api.Settings;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AxonHub.Api.Tests;

/// <summary>
/// C236: port of webhook_notifier.go. Locks target selection, template
/// rendering (including the skip-the-target failure behavior), the RFC3339
/// occurrence in the system timezone, and the detached delivery sink.
/// </summary>
public sealed class WebhookNotifierTests
{
    private static readonly DateTimeOffset OccurredAt =
        new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public async Task NotifyChannelAutoDisabledPostsRenderedBodyAndHeaders()
    {
        using var harness = new Harness();
        harness.Systems.Rows.Add(new SystemRecord
        {
            Key = SystemTimeLocations.GeneralSettingsKey,
            Value = """{"timezone":"Asia/Shanghai","currency_code":"CNY"}""",
        });
        harness.UseTargets(new WebhookTarget
        {
            Name = "ops",
            Enabled = true,
            Url = "https://hooks.example.com/axon",
            Headers =
            [
                new WebhookHeaderEntry { Key = "X-Channel", Value = "{{.Channel.Name}}" },
                new WebhookHeaderEntry { Key = "", Value = "ignored" },
                new WebhookHeaderEntry { Key = "Authorization", Value = "Bearer {{.Trigger.StatusCode}}" },
            ],
            Body = """{"event":"{{.Event}}","severity":"{{.Severity}}","occurred_at":"{{.OccurredAt}}","channel":{"id":{{.Channel.ID}},"name":"{{.Channel.Name}}","provider":"{{.Channel.Provider}}","base_url":"{{.Channel.BaseURL}}","status":"{{.Channel.Status}}"},"trigger":{"type":"{{.Trigger.Type}}","status_code":{{.Trigger.StatusCode}},"threshold":{{.Trigger.Threshold}},"actual_count":{{.Trigger.ActualCount}},"reason":"{{.Trigger.Reason}}"}}""",
        });

        await harness.Notifier.NotifyChannelAutoDisabledAsync(Event());

        var request = Assert.Single(harness.Executor.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("https://hooks.example.com/axon", request.Url);
        Assert.Equal("application/json", request.ContentType);
        Assert.Equal(
            """{"event":"channel.auto_disabled","severity":"warning","occurred_at":"2026-01-02T11:04:05+08:00","channel":{"id":42,"name":"ops","provider":"openai","base_url":"https://api.example.com","status":"disabled"},"trigger":{"type":"error_status_rule","status_code":429,"threshold":3,"actual_count":3,"reason":"rate limited"}}""",
            Encoding.UTF8.GetString(request.Body!));

        var headers = Assert.IsType<Dictionary<string, List<string>>>(request.Headers);
        Assert.Equal(new[] { "ops" }, headers["X-Channel"]);
        Assert.Equal(new[] { "Bearer 429" }, headers["Authorization"]);
        Assert.Equal(new[] { "application/json" }, headers["Content-Type"]);
        Assert.Equal(3, headers.Count);
        Assert.Empty(harness.Proxies.Requests);
    }

    [Fact]
    public async Task BodiesWithoutTemplateDelimitersArePostedUnchanged()
    {
        using var harness = new Harness();
        harness.UseTargets(new WebhookTarget
        {
            Name = "ops",
            Enabled = true,
            Url = "https://hooks.example.com/axon",
            Body = "plain text",
        });

        await harness.Notifier.NotifyChannelAutoDisabledAsync(Event());

        var request = Assert.Single(harness.Executor.Requests);
        Assert.Equal("plain text", Encoding.UTF8.GetString(request.Body!));
    }

    [Fact]
    public async Task InvalidBodyTemplateSkipsOnlyThatTarget()
    {
        using var harness = new Harness();
        harness.UseTargets(
            new WebhookTarget
            {
                Name = "bad",
                Enabled = true,
                Url = "https://hooks.example.com/bad",
                Body = "{{.Channel.Missing}}",
            },
            new WebhookTarget
            {
                Name = "good",
                Enabled = true,
                Url = "https://hooks.example.com/good",
                Body = "ok",
            });

        await harness.Notifier.NotifyChannelAutoDisabledAsync(Event());

        var request = Assert.Single(harness.Executor.Requests);
        Assert.Equal("https://hooks.example.com/good", request.Url);
        Assert.Equal("ok", Encoding.UTF8.GetString(request.Body!));
    }

    [Fact]
    public async Task InvalidHeaderTemplateSkipsTheTarget()
    {
        using var harness = new Harness();
        harness.UseTargets(new WebhookTarget
        {
            Name = "ops",
            Enabled = true,
            Url = "https://hooks.example.com/axon",
            Headers = [new WebhookHeaderEntry { Key = "X-Bad", Value = "{{.Missing}}" }],
            Body = "ok",
        });

        await harness.Notifier.NotifyChannelAutoDisabledAsync(Event());

        Assert.Empty(harness.Executor.Requests);
    }

    [Fact]
    public void SelectTargetsUsesTheFirstSubscriptionAndSkipsUnusableTargets()
    {
        var config = new WebhookNotifierConfig
        {
            Targets =
            [
                new WebhookTarget { Name = "a", Enabled = true, Url = "https://a" },
                new WebhookTarget { Name = "b", Enabled = false, Url = "https://b" },
                new WebhookTarget { Name = "c", Enabled = true, Url = "   " },
                new WebhookTarget { Name = "d", Enabled = true, Url = "https://d" },
            ],
            Subscriptions =
            [
                new WebhookSubscription
                {
                    Event = WebhookNotifier.EventChannelAutoDisabled,
                    TargetNames = ["a", "b", "c", "missing", "a"],
                },
                new WebhookSubscription
                {
                    Event = WebhookNotifier.EventChannelAutoDisabled,
                    TargetNames = ["d"],
                },
            ],
        };

        var targets = WebhookNotifier.SelectTargets(
            config,
            WebhookNotifier.EventChannelAutoDisabled);

        // Only the first subscription is consulted; the duplicate name is kept
        // because the reference sends once per listed name.
        Assert.Equal(new[] { "a", "a" }, targets.Select(target => target.Name));
    }

    [Fact]
    public async Task ConfigReadFailureFallsBackToNoTargets()
    {
        using var harness = new Harness();
        harness.UseTargets(new WebhookTarget
        {
            Name = "ops",
            Enabled = true,
            Url = "https://hooks.example.com/axon",
            Body = "ok",
        });
        harness.Settings.WebhookNotifierFailure = new InvalidOperationException("settings down");

        await harness.Notifier.NotifyChannelAutoDisabledAsync(Event());

        Assert.Empty(harness.Executor.Requests);
    }

    [Fact]
    public async Task ProxiedTargetsUseTheProxyExecutor()
    {
        using var harness = new Harness();
        var proxyExecutor = new ScriptedExecutor
        {
            OnDo = (_, request, _) => new HttpResponseData { StatusCode = 200, Request = request },
        };
        harness.Proxies.Executor = proxyExecutor;
        harness.UseTargets(new WebhookTarget
        {
            Name = "ops",
            Enabled = true,
            Url = "https://hooks.example.com/axon",
            Proxy = new ProxyConfig { Type = ProxyType.Url, Url = "http://proxy.internal:8080" },
            Body = "ok",
        });

        await harness.Notifier.NotifyChannelAutoDisabledAsync(Event());

        var proxy = Assert.Single(harness.Proxies.Requests);
        Assert.Equal("http://proxy.internal:8080", proxy.Url);
        Assert.Single(proxyExecutor.Requests);
        Assert.Empty(harness.Executor.Requests);
    }

    [Fact]
    public async Task TargetsWithoutHeadersStillGetTheJsonContentType()
    {
        using var harness = new Harness();
        harness.UseTargets(new WebhookTarget
        {
            Name = "ops",
            Enabled = true,
            Url = "https://hooks.example.com/axon",
            Body = "ok",
        });

        await harness.Notifier.NotifyChannelAutoDisabledAsync(Event());

        var request = Assert.Single(harness.Executor.Requests);
        var headers = Assert.IsType<Dictionary<string, List<string>>>(request.Headers);
        Assert.Equal(new[] { "application/json" }, headers["Content-Type"]);
        Assert.Single(headers);
    }

    [Fact]
    public async Task SendFailureIsLoggedAndDoesNotEscape()
    {
        using var harness = new Harness();
        harness.Executor.OnDo = (_, _, _) => throw new InvalidOperationException("connection refused");
        harness.UseTargets(new WebhookTarget
        {
            Name = "ops",
            Enabled = true,
            Url = "https://hooks.example.com/axon",
            Body = "ok",
        });

        await harness.Notifier.NotifyChannelAutoDisabledAsync(Event());

        Assert.Equal(1, harness.Executor.Calls);
    }

    [Fact]
    public void FormatRfc3339UsesTheZuluSuffixOnlyForZeroOffsets()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
        var instant = new DateTimeOffset(2026, 1, 2, 3, 4, 5, 999, TimeSpan.Zero);

        Assert.Equal("2026-01-02T03:04:05Z", WebhookNotifier.FormatRfc3339(instant, TimeZoneInfo.Utc));
        Assert.Equal(
            "2026-01-02T11:04:05+08:00",
            WebhookNotifier.FormatRfc3339(instant, zone));
    }

    [Fact]
    public async Task BackgroundSinkDeliversThroughAFreshScope()
    {
        using var harness = new Harness();
        harness.UseTargets(new WebhookTarget
        {
            Name = "ops",
            Enabled = true,
            Url = "https://hooks.example.com/axon",
            Body = "{{.Event}}",
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISystemSettingsAdminService>(harness.Settings);
        services.AddSingleton<ISystemStore>(harness.Systems);
        services.AddSingleton<IExecutor>(harness.Executor);
        services.AddSingleton<IProxyExecutorFactory>(harness.Proxies);
        services.AddScoped<WebhookNotifier>();

        await using var provider = services.BuildServiceProvider();
        var sink = new BackgroundWebhookNotificationSink(
            provider.GetRequiredService<IServiceScopeFactory>());

        sink.PublishChannelAutoDisabled(Event());

        var request = await harness.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(
            WebhookNotifier.EventChannelAutoDisabled,
            Encoding.UTF8.GetString(request.Body!));
    }

    private static ChannelAutoDisabledEvent Event() => new()
    {
        ChannelId = 42,
        ChannelName = "ops",
        ChannelProvider = "openai",
        ChannelBaseUrl = "https://api.example.com",
        ChannelStatus = "disabled",
        StatusCode = 429,
        Threshold = 3,
        ActualCount = 3,
        Reason = "rate limited",
        OccurredAt = OccurredAt,
    };

    private sealed class Harness : IDisposable
    {
        public FakeGcSettingsService Settings { get; } = new();

        public FakeSystemStore Systems { get; } = new();

        public ScriptedExecutor Executor { get; } = new();

        public ScriptedProxyExecutorFactory Proxies { get; } = new();

        public TaskCompletionSource<HttpRequestData> Delivered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WebhookNotifier Notifier { get; }

        public Harness()
        {
            Executor.OnDo = (_, request, _) =>
            {
                Delivered.TrySetResult(request);
                return new HttpResponseData { StatusCode = 200, Request = request };
            };

            Notifier = new WebhookNotifier(Settings, Systems, Executor, Proxies);
        }

        public void UseTargets(params WebhookTarget[] targets)
            => Settings.WebhookNotifier = new WebhookNotifierConfig
            {
                Targets = targets,
                Subscriptions =
                [
                    new WebhookSubscription
                    {
                        Event = WebhookNotifier.EventChannelAutoDisabled,
                        TargetNames = targets.Select(target => target.Name).ToList(),
                    },
                ],
            };

        public void Dispose() => GC.SuppressFinalize(this);
    }
}
