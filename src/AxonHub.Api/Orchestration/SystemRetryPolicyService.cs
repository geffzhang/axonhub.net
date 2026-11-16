using System.Text.Json;
using AxonHub.Persistence;

namespace AxonHub.Api.Orchestration;

/// <summary>Read side of the reference's SystemService.RetryPolicyOrDefault.</summary>
public interface IRetryPolicyProvider
{
    /// <summary>
    /// Port of SystemService.RetryPolicyOrDefault: a missing row, a lookup
    /// failure or malformed JSON falls back to <see cref="RetryPolicy.Default"/>.
    /// A JSON <c>null</c> is a valid document and yields the zero policy after
    /// normalization, exactly like Go's encoding/json.
    /// </summary>
    Task<RetryPolicy> GetRetryPolicyAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads and normalizes the <c>retry_policy</c> system setting
/// (internal/server/biz/system.go:1042-1172). The JSON codec and
/// normalization live in <see cref="RetryPolicyCodec"/> so the management
/// GraphQL surface shares them.
/// </summary>
public sealed class SystemRetryPolicyService(ISystemStore systems) : IRetryPolicyProvider
{
    /// <summary>biz.SystemKeyRetryPolicy.</summary>
    public const string RetryPolicyKey = RetryPolicyCodec.RetryPolicyKey;

    public async Task<RetryPolicy> GetRetryPolicyAsync(
        CancellationToken cancellationToken = default)
    {
        SystemRecord? row;
        try
        {
            row = await systems.GetByKeyAsync(RetryPolicyKey, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // RetryPolicyOrDefault swallows every lookup error.
            return RetryPolicy.Default;
        }

        if (row is null)
        {
            // RetryPolicy normalizes the default before returning it.
            return RetryPolicy.Default;
        }

        try
        {
            return RetryPolicyCodec.Deserialize(row.Value);
        }
        catch (JsonException)
        {
            // RetryPolicy returns the unmarshal error; RetryPolicyOrDefault
            // logs it and falls back to the default policy.
            return RetryPolicy.Default;
        }
    }
}
