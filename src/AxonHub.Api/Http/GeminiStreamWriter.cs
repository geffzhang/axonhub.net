using AxonHub.Llm.Http;
using AxonHub.Llm.Streams;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Http;

/// <summary>
/// Port of api.WriteGeminiStream (internal/server/api/gemini.go:78-126): the
/// default Gemini stream framing — a JSON array of GenerateContentResponse
/// objects, comma-separated, with a flush after every event. When the client
/// disconnects mid-stream the array stays open, exactly like the reference.
/// </summary>
/// <remarks>
/// The reference ignores every write error and keeps looping until the
/// stream ends or the context is cancelled; the port returns from the loop
/// on the first write failure, which is observably equivalent — the
/// response is already broken at that point and the client is gone.
/// </remarks>
public static class GeminiStreamWriter
{
    private static readonly byte[] OpenBracket = "["u8.ToArray();
    private static readonly byte[] CloseBracket = "]"u8.ToArray();
    private static readonly byte[] Comma = ","u8.ToArray();

    public static async Task WriteAsync(
        HttpContext context,
        IStream<StreamEvent> stream,
        int expectedChoices,
        CancellationToken cancellationToken,
        Func<Exception, CancellationToken, Task<Exception?>>? transformStreamError = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(stream);

        // The reference ignores the extra arguments on this writer.
        _ = expectedChoices;
        _ = transformStreamError;

        context.Response.ContentType = "application/json; charset=UTF-8";

        try
        {
            await context.Response.Body.WriteAsync(OpenBracket, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsBrokenPipe(ex))
        {
            return;
        }

        var first = true;

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                // Go's ctx.Done() branch: clientDisconnected, the array stays open.
                return;
            }

            if (stream.Next())
            {
                if (stream.Current is not { } streamEvent)
                {
                    continue;
                }

                try
                {
                    if (!first)
                    {
                        await context.Response.Body.WriteAsync(Comma, cancellationToken).ConfigureAwait(false);
                    }

                    await context.Response.Body.WriteAsync(streamEvent.Data, cancellationToken).ConfigureAwait(false);
                    first = false;
                    await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsBrokenPipe(ex))
                {
                    return;
                }
            }
            else
            {
                // stream.Err is dropped exactly like the reference's
                // log.Printf("stream error: %v") without a client-visible error.
                try
                {
                    await context.Response.Body.WriteAsync(CloseBracket, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsBrokenPipe(ex))
                {
                    // The reference only logs the failed trailing write.
                }

                return;
            }
        }
    }

    private static bool IsBrokenPipe(Exception error)
        => error is IOException or OperationCanceledException or ObjectDisposedException;
}
