namespace AxonHub.Llm;

/// <summary>
/// Port of the sentinel errors declared in llm/model.go. They are shared
/// instances so that <c>GoErrors.Is</c> can reproduce Go's identity comparison.
/// </summary>
public static class LlmErrors
{
    /// <summary>Go: <c>llm.ErrStreamIncomplete</c>.</summary>
    public static readonly Exception StreamIncomplete = new StreamIncompleteException();
}

/// <summary>Carrier type for <see cref="LlmErrors.StreamIncomplete"/>.</summary>
public sealed class StreamIncompleteException : Exception
{
    public StreamIncompleteException()
        : base("stream ended without terminal event")
    {
    }
}
