namespace AxonHub.Abstractions;

public interface ICacheRevisionSource
{
    Task<long> GetRevisionAsync(CancellationToken cancellationToken = default);
}
