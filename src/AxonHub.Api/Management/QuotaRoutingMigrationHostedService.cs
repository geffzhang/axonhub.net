using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AxonHub.Api.Management;

/// <summary>
/// Runs <see cref="QuotaRoutingMigrationService"/> once per process after the
/// host starts, mirroring the reference's ProviderQuotaService OnStart hook
/// (internal/server/biz/fx_module.go), which launches
/// <c>quotaRoutingMigrator.Migrate(context.Background())</c> in a goroutine:
/// startup does not wait for the migration and a failure is only logged.
/// </summary>
public sealed class QuotaRoutingMigrationHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<QuotaRoutingMigrationHostedService> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogMigrationFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(0, "QuotaRoutingMigrationFailed"),
            "quota routing migration failed");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ = stoppingToken;

        // BackgroundService runs ExecuteAsync synchronously until its first
        // await; yield so host startup is never blocked by the migration.
        await Task.Yield();

        using var scope = scopeFactory.CreateScope();
        var migration = scope.ServiceProvider.GetRequiredService<QuotaRoutingMigrationService>();

        try
        {
            // The reference passes context.Background() and does not tie the
            // migration to shutdown.
            await migration.MigrateAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogMigrationFailed(logger, ex);
        }
    }
}
