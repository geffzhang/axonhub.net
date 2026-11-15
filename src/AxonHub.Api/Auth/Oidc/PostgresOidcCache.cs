using AxonHub.Persistence;

namespace AxonHub.Api.Auth.Oidc;

/// <summary>Cluster OIDC cache. Database failures are logged and never treated as cache misses.</summary>
public sealed class PostgresOidcCache(
    PostgresOidcStateStore store,
    ILogger<PostgresOidcCache> logger) : IOidcCache
{
    private static readonly Action<ILogger, string, Exception?> LogFailure =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(0, "OidcStateStoreFailed"),
            "Shared OIDC state store operation {Operation} failed");

    public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default)
        => ReadAsync(() => store.GetAsync(key, cancellationToken), "get");

    public Task<byte[]?> ConsumeAsync(string key, CancellationToken cancellationToken = default)
        => ReadAsync(() => store.ConsumeAsync(key, cancellationToken), "consume");

    public Task SetAsync(
        string key,
        byte[] value,
        TimeSpan expiration,
        CancellationToken cancellationToken = default)
        => WriteAsync(() => store.SetAsync(key, value, expiration, cancellationToken), "set");

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        => WriteAsync(() => store.DeleteAsync(key, cancellationToken), "delete");

    private async Task<byte[]?> ReadAsync(Func<Task<byte[]?>> action, string operation)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogFailure(logger, operation, exception);
            throw;
        }
    }

    private async Task WriteAsync(Func<Task> action, string operation)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogFailure(logger, operation, exception);
            throw;
        }
    }
}
