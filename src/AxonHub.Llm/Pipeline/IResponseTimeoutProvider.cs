namespace AxonHub.Llm.Pipeline;

/// <summary>
/// Port of pipeline.ResponseTimeoutProvider (llm/pipeline/pipeline.go:54-66):
/// an outbound transformer may override the pipeline response timeouts for the
/// current attempt. The transformer knows which channel is selected, including
/// after a retry switches channels.
/// </summary>
/// <remarks>
/// A null value keeps the pipeline default for that timeout, so a channel may
/// override only one of the two. A non-null <see cref="TimeSpan.Zero"/> disables
/// the corresponding timeout.
/// </remarks>
public interface IResponseTimeoutProvider
{
    (TimeSpan? StreamFirstEventTimeout, TimeSpan? NonStreamTimeout) ResponseTimeouts();
}
