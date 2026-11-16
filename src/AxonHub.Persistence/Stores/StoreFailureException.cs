namespace AxonHub.Persistence;

/// <summary>
/// A store failure whose message is already formatted exactly like the Go
/// reference wraps it (for example "failed to disable user personal API keys:
/// %w"). Services forward this message unchanged instead of prefixing it again.
/// </summary>
public sealed class StoreFailureException(string message, Exception innerException)
    : Exception(message, innerException);

/// <summary>
/// A bulk mutation named N rows but the store found M. The reference formats
/// "expected to find %d channels, but found %d" at the biz layer; the store
/// carries both counts so the service can reproduce that wording.
/// </summary>
public sealed class ExpectedRowsMismatchException(int expected, int found)
    : Exception($"expected to find {expected} channels, but found {found}")
{
    public int Expected { get; } = expected;

    public int Found { get; } = found;
}
