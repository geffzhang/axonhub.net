using AxonHub.Api.Orchestration;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Http;

/// <summary>
/// Port of api.WriteBinaryStream (internal/server/api/chat.go:668-757): raw
/// bytes from stream events written directly to the response body. The first
/// chunk's type becomes the response Content-Type; a <c>binary.done</c> event
/// is swallowed. Errors before the first write become a JSON error response;
/// afterwards they are log-only, exactly like the reference.
/// </summary>
/// <remarks>
/// The reference wraps chat streams with the upstream error policy before the
/// writer; binary TTS streams are NOT wrapped, so <c>expectedChoices</c> and
/// <c>transformStreamError</c> are intentionally unused.
/// </remarks>
public static class BinaryStreamWriter
{
    /// <summary>chat.go:671 — the default Content-Type when the first chunk has none.</summary>
    public const string DefaultContentType = "application/octet-stream";

    /// <summary>maxStreamEventsAfterCancel (chat.go): bounds the post-cancellation drain.</summary>
    public const int MaxEventsAfterCancel = 256;

    public static async Task WriteAsync(
        HttpContext context,
        IStream<StreamEvent> stream,
        int expectedChoices,
        CancellationToken cancellationToken,
        Func<Exception, CancellationToken, Task<Exception?>>? transformStreamError)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(stream);

        _ = expectedChoices;
        _ = transformStreamError;

        var headersWritten = false;
        var eventsAfterCancel = 0;

        while (true)
        {
            if (!stream.Next())
            {
                if (stream.Err is { } err)
                {
                    if (IsCancellation(err) || cancellationToken.IsCancellationRequested)
                    {
                        // Client disconnected; the reference only logs.
                    }
                    else if (!headersWritten)
                    {
                        // Keep genuine upstream failures visible: a classified
                        // transport failure becomes a JSON error response.
                        var failure = UpstreamTransportErrors.Classify(err);
                        context.Response.StatusCode = StreamErrorStatus(failure);
                        context.Response.ContentType = "application/json; charset=utf-8";
                        await context.Response.Body.WriteAsync(
                            StreamErrorFormatter.Format(failure),
                            cancellationToken);
                        return;
                    }

                    // Headers already written: the reference only logs the error.
                }

                try
                {
                    await context.Response.Body.FlushAsync(cancellationToken);
                }
                catch (Exception ex) when (IsClientWriteFailure(ex))
                {
                    // Client is gone; the reference only logs the failed flush.
                }

                return;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                eventsAfterCancel++;
                if (eventsAfterCancel > MaxEventsAfterCancel)
                {
                    return;
                }
            }

            var current = stream.Current;
            if (current is null || current.Type == AuthTypes.BinaryStreamDoneEventType || current.Data.Length == 0)
            {
                continue;
            }

            if (!headersWritten)
            {
                var contentType = current.Type.Trim();
                if (contentType.Length == 0)
                {
                    contentType = DefaultContentType;
                }

                context.Response.ContentType = contentType;
                context.Response.Headers["Cache-Control"] = "no-cache";
                context.Response.Headers["Connection"] = "keep-alive";
                context.Response.Headers["Access-Control-Allow-Origin"] = "*";
                headersWritten = true;
            }

            try
            {
                await context.Response.Body.WriteAsync(current.Data, cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
            }
            catch (Exception ex) when (IsClientWriteFailure(ex))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Port of streamErrorStatus (chat.go:759-776). The QuotaExhaustedError
    /// branch lands with the auth/admin slice (difference C014).
    /// </summary>
    private static int StreamErrorStatus(Exception err)
    {
        if (GoErrors.As<ResponseError>(err) is { StatusCode: not 0 } respErr)
        {
            return respErr.StatusCode;
        }

        if (GoErrors.As<HttpClientError>(err) is { StatusCode: not 0 } httpErr)
        {
            return httpErr.StatusCode;
        }

        return StatusCodes.Status500InternalServerError;
    }

    private static bool IsCancellation(Exception? error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is OperationCanceledException)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsClientWriteFailure(Exception error)
        => error is IOException or OperationCanceledException or ObjectDisposedException;
}
