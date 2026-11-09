using AxonHub.Grains.Abstractions;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Executes one due quota check group (provider_quota.go:779-786). The
/// production implementation dispatches the group to the leader channel's
/// <see cref="IProviderQuotaGrain"/>; a service constructed directly (tests,
/// tools) falls back to the in-process check.
/// </summary>
public interface IQuotaGroupExecutor
{
    Task ExecuteAsync(
        ProviderQuotaCheckGroup group,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}
