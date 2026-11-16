namespace AxonHub.Llm.Pipeline;

/// <summary>
/// Port of llm/pipeline/stream.EnsureUsage (usage.go:12-24): stream requests
/// always ask the provider for usage so billing sees a complete record.
/// </summary>
public sealed class EnsureUsageMiddleware : DummyMiddleware
{
    public EnsureUsageMiddleware()
        : base("stream-usage")
    {
    }

    public override LlmRequest TransformInboundLlmRequest(LlmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Stream is true)
        {
            request.StreamOptions ??= new StreamOptions();
            request.StreamOptions.IncludeUsage = true;
        }

        return request;
    }
}
