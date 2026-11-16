using AxonHub.Api.Cli;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AxonHub.Api.Http;

/// <summary>
/// internal/server/server.go:50-54: gin keeps multipart parts in memory up to
/// <c>server.max_multipart_memory</c> (32 MiB by default) and spills the rest to
/// temporary files. HotChocolate 16's multipart reader reads the same threshold
/// from <see cref="FormOptions"/>, so the host maps the key onto
/// <see cref="FormOptions.MemoryBufferThreshold"/>.
/// </summary>
internal static class MultipartMemoryOptions
{
    /// <summary>gin.Engine.MaxMultipartMemory's zero value.</summary>
    internal const long GinDefaultBytes = 32L * 1024 * 1024;

    internal static void Configure(IServiceCollection services, IConfiguration configuration)
    {
        var bytes = CliConfiguration.GetMaxMultipartMemory(configuration);
        if (bytes <= 0)
        {
            // server.go:52 only assigns when the configured value is positive;
            // gin's own default is 32 MiB.
            bytes = GinDefaultBytes;
        }

        services.Configure<FormOptions>(options =>
            options.MemoryBufferThreshold = (int)Math.Min(bytes, int.MaxValue));
    }
}
