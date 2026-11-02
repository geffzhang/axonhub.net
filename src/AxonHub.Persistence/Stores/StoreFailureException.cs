namespace AxonHub.Persistence;

/// <summary>
/// A store failure whose message is already formatted exactly like the Go
/// reference wraps it (for example "failed to disable user personal API keys:
/// %w"). Services forward this message unchanged instead of prefixing it again.
/// </summary>
public sealed class StoreFailureException(string message, Exception innerException)
    : Exception(message, innerException);
