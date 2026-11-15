namespace AxonHub.Llm;

/// <summary>
/// Port of the sentinel errors declared in llm/model.go. They are shared
/// instances so that <c>GoErrors.Is</c> can reproduce Go's identity comparison.
/// </summary>
public static class LlmErrors
{
    /// <summary>Go: <c>llm.ErrStreamIncomplete</c>.</summary>
    public static readonly Exception StreamIncomplete = new StreamIncompleteException();

    /// <summary>
    /// Go: <c>llm.ErrEmptyResponse</c>. A streaming response completed without
    /// meaningful content; pipelines may retry it before content is committed.
    /// </summary>
    public static readonly Exception EmptyResponse = new EmptyResponseException();
}

/// <summary>Carrier type for <see cref="LlmErrors.EmptyResponse"/>.</summary>
public sealed class EmptyResponseException : Exception
{
    public EmptyResponseException()
        : base("empty response detected")
    {
    }
}

/// <summary>Carrier type for <see cref="LlmErrors.StreamIncomplete"/>.</summary>
public sealed class StreamIncompleteException : Exception
{
    public StreamIncompleteException()
        : this("stream ended without terminal event")
    {
    }

    /// <summary>
    /// A message constructor so the orchestrator can carry its own
    /// <c>ErrStreamIncomplete</c> sentinel with the reference's wording
    /// ("stream ended without terminal event or completed response").
    /// </summary>
    public StreamIncompleteException(string message)
        : base(message)
    {
    }
}
