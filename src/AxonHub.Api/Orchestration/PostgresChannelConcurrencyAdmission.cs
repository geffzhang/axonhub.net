using System.Diagnostics;
using AxonHub.Grains.Abstractions;
using AxonHub.Persistence;

namespace AxonHub.Api.Orchestration;

public interface IChannelConcurrencyAdmission
{
    IDisposable? Acquire(
        long channelId, ChannelRateLimitSettings? settings, CancellationToken cancellationToken);
}

public sealed class PostgresChannelConcurrencyAdmission(
    PostgresChannelAdmissionStore store,
    ILogger<PostgresChannelConcurrencyAdmission> logger) : IChannelConcurrencyAdmission
{
    private static readonly Action<ILogger, Guid, long, Exception?> LogRetainedPermit =
        LoggerMessage.Define<Guid, long>(
            LogLevel.Error, new EventId(0, "ChannelPermitRetained"),
            "Global concurrency permit retained after storage failure: permit={PermitId} channel={ChannelId}");

    private readonly string _owner =
        Environment.MachineName + ":" + Environment.ProcessId + ":" + Guid.NewGuid().ToString("N");

    public IDisposable? Acquire(
        long channelId, ChannelRateLimitSettings? settings, CancellationToken cancellationToken)
        => settings?.MaxConcurrent is not > 0
            ? null
            : AcquireAsync(channelId, settings, cancellationToken).GetAwaiter().GetResult();

    private async Task<IDisposable> AcquireAsync(
        long channelId, ChannelRateLimitSettings settings, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var started = Stopwatch.GetTimestamp();
        try
        {
            var state = await store.JoinAsync(
                id, channelId, _owner, settings.MaxConcurrent!.Value,
                settings.QueueSize ?? 0, cancellationToken).ConfigureAwait(false);
            while (state != AdmissionState.Granted)
            {
                if (state == AdmissionState.Full)
                {
                    throw new ChannelQueueException(ChannelQueueReason.QueueFull);
                }

                if (state == AdmissionState.Missing)
                {
                    throw new InvalidOperationException("global concurrency waiting ticket expired");
                }

                if (settings.QueueTimeoutMs is > 0
                    && Stopwatch.GetElapsedTime(started).TotalMilliseconds >= settings.QueueTimeoutMs.Value)
                {
                    throw new ChannelQueueException(ChannelQueueReason.QueueTimeout);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
                state = await store.PollAsync(
                    id, channelId, settings.MaxConcurrent.Value, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new Permit(store, logger, id, channelId);
        }
        catch
        {
            // Acquire has not returned, so this request cannot have reached the upstream.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await store.ReleaseAsync(id, channelId, cleanup.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is Npgsql.NpgsqlException or OperationCanceledException or TimeoutException)
            {
                LogRetainedPermit(logger, id, channelId, ex);
            }

            throw;
        }
    }

    private sealed class Permit(
        PostgresChannelAdmissionStore store,
        ILogger logger,
        Guid id,
        long channelId) : IDisposable
    {
        private readonly object _gate = new();
        private bool _released;

        public void Dispose()
        {
            lock (_gate)
            {
                if (_released)
                {
                    return;
                }

                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    store.ReleaseAsync(id, channelId, timeout.Token).GetAwaiter().GetResult();
                    _released = true;
                }
                catch (Exception ex) when (ex is Npgsql.NpgsqlException or OperationCanceledException or TimeoutException)
                {
                    LogRetainedPermit(logger, id, channelId, ex);
                    throw;
                }
            }
        }
    }
}
