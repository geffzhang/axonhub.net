using AxonHub.Api.Auth.Oidc;
using AxonHub.Persistence;
using Microsoft.Extensions.Logging;

namespace AxonHub.Api.Tests;

public sealed class OidcCacheTests
{
    [Fact]
    public async Task MemoryConsumeHasOnlyOneWinner()
    {
        var cache = new OidcMemoryCache(TimeProvider.System);
        await cache.SetAsync("code", "42"u8.ToArray(), TimeSpan.FromMinutes(1));

        var results = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => cache.ConsumeAsync("code"))));

        Assert.Equal("42"u8.ToArray(), Assert.Single(results, result => result is not null));
        Assert.Null(await cache.GetAsync("code"));
        Assert.Null(await cache.ConsumeAsync("missing"));
    }

    [Fact]
    public async Task MemoryConsumePreservesExpiryBoundaryAndCancellation()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UnixEpoch);
        var cache = new OidcMemoryCache(clock);
        await cache.SetAsync("boundary", "value"u8.ToArray(), TimeSpan.FromMinutes(1));
        await cache.SetAsync("expired", "value"u8.ToArray(), TimeSpan.FromMinutes(1));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.NotNull(await cache.ConsumeAsync("boundary"));
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.Null(await cache.ConsumeAsync("expired"));

        await cache.SetAsync("cancelled", "value"u8.ToArray(), TimeSpan.FromMinutes(1));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cache.ConsumeAsync("cancelled", cancellation.Token));
        Assert.NotNull(await cache.ConsumeAsync("cancelled"));
    }

    [Theory]
    [InlineData("get")]
    [InlineData("consume")]
    [InlineData("set")]
    [InlineData("delete")]
    public async Task PostgresAdapterLogsAndPropagatesInfrastructureFailures(string operation)
    {
        var logger = new RecordingLogger();
        var store = new PostgresOidcStateStore(new PostgresConnectionFactory("InvalidKeyword=true"));
        var cache = new PostgresOidcCache(store, logger);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => InvokeAsync(cache, operation));

        var entry = Assert.Single(logger.Events);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(exception, entry.Exception);
        Assert.Contains(operation, entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-bearer-code", entry.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("get")]
    [InlineData("consume")]
    [InlineData("set")]
    [InlineData("delete")]
    public async Task PostgresAdapterPreservesCancellationWithoutLoggingAnOutage(string operation)
    {
        var logger = new RecordingLogger();
        var store = new PostgresOidcStateStore(new PostgresConnectionFactory(
            "Host=127.0.0.1;Port=1;Username=oidc;Database=oidc;Pooling=false"));
        var cache = new PostgresOidcCache(store, logger);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => InvokeAsync(cache, operation, cancellation.Token));

        Assert.Empty(logger.Events);
    }

    private static Task InvokeAsync(
        PostgresOidcCache cache,
        string operation,
        CancellationToken cancellationToken = default)
        => operation switch
        {
            "get" => cache.GetAsync("raw-bearer-code", cancellationToken),
            "consume" => cache.ConsumeAsync("raw-bearer-code", cancellationToken),
            "set" => cache.SetAsync("raw-bearer-code", "42"u8.ToArray(), TimeSpan.FromMinutes(1), cancellationToken),
            "delete" => cache.DeleteAsync("raw-bearer-code", cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

    private sealed class RecordingLogger : ILogger<PostgresOidcCache>
    {
        public List<(LogLevel Level, Exception? Exception, string Message)> Events { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Events.Add((logLevel, exception, formatter(state, exception)));
    }
}
