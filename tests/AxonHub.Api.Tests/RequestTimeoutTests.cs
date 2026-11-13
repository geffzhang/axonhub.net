using AxonHub.Api.Auth;
using AxonHub.Api.Http;
using AxonHub.Api.Orchestration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace AxonHub.Api.Tests;

/// <summary>
/// §214: server.request_timeout / server.llm_request_timeout parsing and the
/// port of middleware.WithTimeout (internal/server/middleware/timeout.go). The
/// first of the deadline and the parent abort wins, like Go's cancelCtx, and
/// the feature keeps that distinction for the SSE writer and persistence.
/// </summary>
public sealed class RequestTimeoutTests
{
    [Theory]
    [InlineData("30s", 30_000)]
    [InlineData("600s", 600_000)]
    [InlineData("1h30m", 5_400_000)]
    [InlineData("2h45m0.5s", 9_900_500)]
    [InlineData("1.5h", 5_400_000)]
    [InlineData("300ms", 300)]
    [InlineData("1us", 0.001)]
    [InlineData("1µs", 0.001)]
    [InlineData("1μs", 0.001)]
    [InlineData("-1.5s", -1_500)]
    [InlineData("+2m", 120_000)]
    [InlineData(".5s", 500)]
    [InlineData("0", 0)]
    [InlineData("0.0s", 0)]
    public void ParsesGoDurations(string text, double expectedMilliseconds)
    {
        Assert.Equal(expectedMilliseconds, GoDurationParser.Parse(text).TotalMilliseconds, 6);
    }

    [Theory]
    [InlineData("abc", "time: invalid duration \"abc\"")]
    [InlineData("", "time: invalid duration \"\"")]
    [InlineData("-", "time: invalid duration \"-\"")]
    [InlineData("1", "time: missing unit in duration \"1\"")]
    [InlineData("1h30", "time: missing unit in duration \"1h30\"")]
    [InlineData("1x", "time: unknown unit \"x\" in duration \"1x\"")]
    [InlineData("9223372036854775808h", "time: invalid duration \"9223372036854775808h\"")]
    public void RejectsInvalidGoDurations(string text, string message)
    {
        var error = Assert.Throws<FormatException>(() => GoDurationParser.Parse(text));
        Assert.Equal(message, error.Message);
    }

    [Fact]
    public void SettingsDefaultToTheReferenceDurations()
    {
        var settings = RequestTimeoutSettings.FromConfiguration(
            new ConfigurationBuilder().Build());

        Assert.Equal(TimeSpan.FromSeconds(30), settings.RequestTimeout);
        Assert.Equal(TimeSpan.FromSeconds(600), settings.LlmRequestTimeout);
    }

    [Fact]
    public void SettingsPreferTheServerSectionThenTheEnvironmentAlias()
    {
        var settings = RequestTimeoutSettings.FromConfiguration(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Server:RequestTimeout"] = "45s",
                    ["AXONHUB_SERVER_REQUEST_TIMEOUT"] = "1m",
                    ["AXONHUB_SERVER_LLM_REQUEST_TIMEOUT"] = "10m",
                })
                .Build());

        Assert.Equal(TimeSpan.FromSeconds(45), settings.RequestTimeout);
        Assert.Equal(TimeSpan.FromMinutes(10), settings.LlmRequestTimeout);
    }

    [Fact]
    public void SettingsTreatAnEmptyValueAsZero()
    {
        var settings = RequestTimeoutSettings.FromConfiguration(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Server:RequestTimeout"] = "  ",
                })
                .Build());

        Assert.Equal(TimeSpan.Zero, settings.RequestTimeout);
        Assert.Equal(TimeSpan.FromSeconds(600), settings.LlmRequestTimeout);
    }

    [Fact]
    public async Task MiddlewareReplacesTheRequestTokenAndMarksTheDeadline()
    {
        var provider = new ManualTimeProvider();
        var middleware = new RequestTimeoutMiddleware(
            context =>
            {
                Assert.False(context.RequestAborted.IsCancellationRequested);
                provider.Fire();
                Assert.True(context.RequestAborted.IsCancellationRequested);
                Assert.True(context.Features.Get<IRequestTimeoutFeature>()!.IsTimedOut);
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(5),
            provider);

        var context = new DefaultHttpContext();
        using var parent = new CancellationTokenSource();
        context.RequestAborted = parent.Token;

        await middleware.InvokeAsync(context);

        Assert.Equal(parent.Token, context.RequestAborted);
        Assert.Null(context.Features.Get<IRequestTimeoutFeature>());
        Assert.False(parent.IsCancellationRequested);
    }

    [Fact]
    public async Task ParentCancellationIsNotADeadline()
    {
        var provider = new ManualTimeProvider();
        using var parent = new CancellationTokenSource();
        var middleware = new RequestTimeoutMiddleware(
            context =>
            {
                parent.Cancel();
                Assert.True(context.RequestAborted.IsCancellationRequested);
                Assert.False(context.Features.Get<IRequestTimeoutFeature>()!.IsTimedOut);
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(5),
            provider);

        var context = new DefaultHttpContext { RequestAborted = parent.Token };
        await middleware.InvokeAsync(context);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task NonPositiveTimeoutsExpireImmediately(double seconds)
    {
        var provider = new ManualTimeProvider();
        var middleware = new RequestTimeoutMiddleware(
            context =>
            {
                Assert.True(context.RequestAborted.IsCancellationRequested);
                Assert.True(context.Features.Get<IRequestTimeoutFeature>()!.IsTimedOut);
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(seconds),
            provider);

        await middleware.InvokeAsync(new DefaultHttpContext());
    }

    [Fact]
    public void IdentityCarriesTheDeadlineProbe()
    {
        var provider = new ManualTimeProvider();
        using var feature = new RequestTimeoutFeature(
            TimeSpan.FromSeconds(5),
            CancellationToken.None,
            provider);
        var context = new DefaultHttpContext();
        context.Features.Set<IRequestTimeoutFeature>(feature);

        var identity = RequestIdentity.FromHttpContext(context);
        Assert.NotNull(identity.RequestDeadlineExpired);
        Assert.False(identity.RequestDeadlineExpired!());

        provider.Fire();
        Assert.True(identity.RequestDeadlineExpired!());
    }

    [Fact]
    public void DeadlineContextErrorsClassifyTheStreamOutcomeAsFailed()
    {
        var outcome = new StreamOutcome();
        outcome.ObserveContextError(new TimeoutException("context deadline exceeded"));

        var decision = outcome.FinalDecision();

        Assert.Equal(StreamTerminalState.Failed, decision.State);
        Assert.IsType<TimeoutException>(decision.Cause);
    }

    [Fact]
    public void CanceledContextErrorsKeepTheCanceledState()
    {
        var outcome = new StreamOutcome();
        outcome.ObserveContextError(new OperationCanceledException());

        Assert.Equal(StreamTerminalState.Canceled, outcome.FinalState());
    }

    /// <summary>A TimeProvider whose timers only fire when the test says so.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ManualTimer(callback, state);
            lock (_timers)
            {
                _timers.Add(timer);
            }

            return timer;
        }

        public void Fire()
        {
            ManualTimer[] timers;
            lock (_timers)
            {
                timers = [.. _timers];
            }

            foreach (var timer in timers)
            {
                timer.Fire();
            }
        }

        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
                // Nothing to release.
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public void Fire() => callback(state);
        }
    }
}
