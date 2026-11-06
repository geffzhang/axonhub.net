using AxonHub.Api.Orchestration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AxonHub.Api.Management;

/// <summary>
/// Performs the two startup reads of the reference provider quota service after
/// the host starts: <c>loadQuotaCache(context.Background())</c> runs in the
/// service constructor (provider_quota.go:369), and the port additionally seeds
/// the synchronous settings snapshot.
/// </summary>
/// <remarks>
/// The reference loads the quota cache synchronously before the server serves
/// requests; the port's singleton cannot do async I/O in its constructor, so the
/// load runs as a hosted service. Between host start and the load completing the
/// routing gate sees no quota status (the reference's neutral "no quota data"
/// state) instead of the persisted rows (C182).
/// </remarks>
public sealed class ProviderQuotaInitializationHostedService(
    IServiceScopeFactory scopeFactory,
    ProviderQuotaService quotaService,
    ILogger<ProviderQuotaInitializationHostedService> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogSettingsFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(0, "ProviderQuotaStartupSettingsFailed"),
            "Failed to load provider quota collection settings at startup");

    private static readonly Action<ILogger, Exception?> LogInitializeFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(0, "ProviderQuotaStartupInitializeFailed"),
            "Failed to load quota cache at startup");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ = stoppingToken;

        // BackgroundService runs ExecuteAsync synchronously until its first
        // await; yield so host startup is never blocked by the reads.
        await Task.Yield();

        try
        {
            using var scope = scopeFactory.CreateScope();
            var settings = scope.ServiceProvider
                .GetRequiredService<IProviderQuotaCollectionSettingsService>();
            // ProviderQuotaCollectionSettingsOrDefault: failures fall back to the
            // defaults and refresh the snapshot the routing gate reads.
            _ = await settings.GetOrDefaultAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogSettingsFailed(logger, ex);
        }

        try
        {
            // The reference passes context.Background() and does not tie the
            // load to shutdown.
            await quotaService.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogInitializeFailed(logger, ex);
        }
    }
}
