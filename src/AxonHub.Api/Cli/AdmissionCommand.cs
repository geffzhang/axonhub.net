using System.Text.Json;
using AxonHub.Persistence;
using Microsoft.Extensions.Configuration;

namespace AxonHub.Api.Cli;

internal static class AdmissionCommand
{
    private const string Usage =
        "Usage: axonhub admission list | release <permit-id> --confirm-upstream-stopped";

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        var list = args is ["admission", "list"];
        var release = args is ["admission", "release", _, "--confirm-upstream-stopped"];
        var id = Guid.Empty;
        if (!list && (!release || !Guid.TryParse(args[2], out id)))
        {
            error.WriteLine(Usage);
            return 1;
        }

        try
        {
            var configuration = CliConfiguration.Load();
            var dsn = configuration.GetConnectionString("Default") ?? configuration["Db:Dsn"];
            if (string.IsNullOrWhiteSpace(dsn))
            {
                error.WriteLine("Admission command requires ConnectionStrings:Default or db.dsn.");
                return 1;
            }

            var store = new PostgresChannelAdmissionStore(new PostgresConnectionFactory(dsn));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var rows = store.ListAsync(timeout.Token).GetAwaiter().GetResult();
            if (list)
            {
                output.WriteLine(JsonSerializer.Serialize(rows));
                return 0;
            }

            var permit = rows.SingleOrDefault(row => row.Id == id);
            if (permit is null)
            {
                error.WriteLine("Admission permit not found; no permit was released.");
                return 1;
            }

            store.ReleaseAsync(id, permit.ChannelId, timeout.Token).GetAwaiter().GetResult();
            output.WriteLine("Released confirmed stopped permit " + id);
            return 0;
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or IOException
            or InvalidOperationException or ArgumentException or OperationCanceledException or TimeoutException)
        {
            error.WriteLine("Admission command failed: " + ex.Message);
            return 1;
        }
    }
}
