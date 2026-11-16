using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Api.Auth;
using AxonHub.Api.GraphQL;
using AxonHub.Api.Http;
using AxonHub.Api.Management;
using AxonHub.Api.Orchestration;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.AiSdk;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Endpoints;

/// <summary>
/// Port of api.PlaygroundHandlers.ChatCompletion
/// (internal/server/api/playground.go:213-331) for
/// <c>POST /admin/playground/chat</c>.
/// </summary>
/// <remarks>
/// The reference builds its handler with an aisdk inbound transformer and swaps
/// in a <c>SpecifiedChannelSelector</c> when the caller pins a channel; the port
/// derives the same two orchestrator instances per request
/// (playground.go:NewPlaygroundHandlers). The route's WithSource and
/// WithResponseHeaders middlewares become the identity source and the
/// end-of-handler header snapshot the shared chat endpoint uses.
/// </remarks>
public static class PlaygroundEndpoint
{
    public static async Task HandleAsync(
        HttpContext context,
        ChatCompletionOrchestrator orchestrator,
        IChannelWriteStore channels)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(channels);

        var cancellationToken = context.RequestAborted;
        var requestId = 0L;

        try
        {
            HttpRequestData genericRequest;
            try
            {
                genericRequest = await HttpRequestReader.ReadAsync(context.Request, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // playground.go:215-227: ReadHTTPRequest failures answer 400
                // directly instead of going through HandleError.
                await WriteErrorAsync(
                        context,
                        StatusCodes.Status400BadRequest,
                        StatusCodes.Status400BadRequest,
                        ex.Message,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            // playground.go:229-236: the query value wins over the header.
            var channelIdText = FirstQueryValue(context, "channel_id");
            if (channelIdText.Length == 0)
            {
                channelIdText = FirstHeaderValue(context, "X-Channel-ID");
            }

            var projectIdText = FirstQueryValue(context, "project_id");
            if (projectIdText.Length == 0)
            {
                projectIdText = FirstHeaderValue(context, "X-Project-ID");
            }

            var admin = context.Items[AdminGraphqlIdentity.ContextKey] as AdminGraphqlIdentity;
            var projectId = admin?.ProjectId ?? 0L;

            // playground.go:246-268: a project in the request overrides the
            // one WithProjectID resolved from the header.
            if (projectIdText.Length > 0)
            {
                GidValue projectGid;
                try
                {
                    projectGid = GidValue.Parse(projectIdText);
                }
                catch (InvalidOperationException ex)
                {
                    await WriteErrorAsync(
                            context,
                            StatusCodes.Status400BadRequest,
                            StatusCodes.Status400BadRequest,
                            "Invalid project ID: " + ex.Message,
                            cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                projectId = projectGid.Id;
            }

            // contexts.GetUser/GetSource: the playground records the JWT user on
            // the request row and no API key (request.go:279-283).
            var identity = new RequestIdentity(projectId, ApiKeyId: null)
            {
                Source = RequestIdentity.SourcePlayground,
                UserId = admin?.User?.Id,
                RequestId = RequestIds.FromHttpContext(context),
            };

            var processor = orchestrator.WithInbound(new AiSdkDataStreamTransformer());

            if (channelIdText.Length > 0)
            {
                GidValue channelGid;
                try
                {
                    channelGid = GidValue.Parse(channelIdText);
                }
                catch (InvalidOperationException ex)
                {
                    await WriteErrorAsync(
                            context,
                            StatusCodes.Status400BadRequest,
                            StatusCodes.Status400BadRequest,
                            ex.Message,
                            cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                processor = processor.WithChannelSelector(
                    new SpecifiedChannelSelector(channels, channelGid.Id));
            }

            ChatCompletionResult result;
            try
            {
                result = await processor
                    .ProcessAsync(genericRequest, identity, id => requestId = id, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var errorResponse = HandleError(ex);
                await WriteErrorAsync(
                        context,
                        errorResponse.Status,
                        errorResponse.Error.Code,
                        errorResponse.Error.Message,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (result.Response is { } response)
            {
                // playground.go:295-304: c.Data with the upstream content type.
                await ChatCompletionEndpoint
                    .WriteResponseAsync(context, response, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (result.Stream is { } stream)
            {
                // playground.go:306-327: AI SDK data stream headers, then the
                // default SSE writer (FormatStreamError, no upstream policy).
                context.Response.Headers["Content-Type"] = "text/event-stream";
                context.Response.Headers["Cache-Control"] = "no-cache";
                context.Response.Headers["Connection"] = "keep-alive";
                context.Response.Headers["Access-Control-Allow-Origin"] = "*";
                context.Response.Headers["X-Vercel-AI-Data-Stream"] = "v1";
                context.Response.StatusCode = StatusCodes.Status200OK;

                try
                {
                    await SseStreamWriter.WriteAsync(
                            context,
                            stream,
                            ExpectedStreamChoices.FromRequestBody(genericRequest.Body),
                            cancellationToken,
                            transformStreamError: null)
                        .ConfigureAwait(false);
                }
                finally
                {
                    stream.Dispose();
                }
            }
        }
        finally
        {
            RecordResponseHeaders(context, orchestrator, requestId);
        }
    }

    /// <summary>
    /// Port of PlaygroundHandlers.HandleError (playground.go:121-211). The
    /// order matters: quota, transport error, invalid request, provider error,
    /// then the generic 500.
    /// </summary>
    internal static PlaygroundResponseError HandleError(Exception rawError)
    {
        ArgumentNullException.ThrowIfNull(rawError);

        if (GoErrors.As<QuotaExhaustedException>(rawError) is { } quotaError)
        {
            return new PlaygroundResponseError
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Error = new PlaygroundErrorBody
                {
                    Code = StatusCodes.Status503ServiceUnavailable,
                    Message = quotaError.Message,
                },
            };
        }

        if (GoErrors.As<HttpClientError>(rawError) is { } httpError)
        {
            // Prefer upstream error message when available.
            var message = TryExtractUpstreamErrorMessage(httpError.Body);
            if (message.Length == 0)
            {
                message = GoHttpStatus.Text(httpError.StatusCode);
            }

            return new PlaygroundResponseError
            {
                Status = httpError.StatusCode,
                Error = new PlaygroundErrorBody
                {
                    Code = httpError.StatusCode,
                    Message = message,
                },
            };
        }

        if (GoErrors.As<TransformerException>(rawError) is { Kind: TransformerErrorKind.InvalidRequest })
        {
            return new PlaygroundResponseError
            {
                Status = StatusCodes.Status400BadRequest,
                Error = new PlaygroundErrorBody
                {
                    Code = StatusCodes.Status400BadRequest,
                    Message = GoHttpStatus.Text(StatusCodes.Status400BadRequest),
                },
            };
        }

        if (GoErrors.As<ResponseError>(rawError) is { } llmError)
        {
            if (llmError.Detail.Message.Length == 0)
            {
                return new PlaygroundResponseError
                {
                    Status = llmError.StatusCode,
                    Error = new PlaygroundErrorBody
                    {
                        Code = llmError.StatusCode,
                        Message = GoHttpStatus.Text(llmError.StatusCode),
                    },
                };
            }

            // The provider error code is used when numeric; otherwise the HTTP
            // status. strconv.Atoi's error is discarded but its clamped value
            // is kept (playground.go:195-197).
            var parsedCode = GoAtoi(llmError.Detail.Code);
            if (parsedCode == 0)
            {
                parsedCode = llmError.StatusCode;
            }

            return new PlaygroundResponseError
            {
                Status = llmError.StatusCode,
                Error = new PlaygroundErrorBody
                {
                    Code = parsedCode,
                    Message = llmError.Detail.Message,
                },
            };
        }

        return new PlaygroundResponseError
        {
            Status = StatusCodes.Status500InternalServerError,
            Error = new PlaygroundErrorBody
            {
                Code = StatusCodes.Status500InternalServerError,
                Message = GoHttpStatus.Text(StatusCodes.Status500InternalServerError),
            },
        };
    }

    /// <summary>
    /// Port of tryExtractUpstreamErrorMessage (playground.go:74-119). Each shape
    /// is a separate json.Unmarshal into a struct, so a type mismatch fails that
    /// shape only.
    /// </summary>
    internal static string TryExtractUpstreamErrorMessage(byte[]? body)
    {
        if (body is null || body.Length == 0)
        {
            return string.Empty;
        }

        // 1) OpenAI / OpenRouter-like: {"error": {"message": "..."}}
        if (TryUnmarshal<WrappedErrorMessageBody>(body) is { Error.Message.Length: > 0 } wrapped)
        {
            return wrapped.Error!.Message;
        }

        // 2) Generic: {"message": "..."}
        if (TryUnmarshal<GenericMessageBody>(body) is { Message.Length: > 0 } generic)
        {
            return generic.Message;
        }

        // 3) Some providers may return: {"error": "..."}
        if (TryUnmarshal<AltErrorMessageBody>(body) is { Error.Length: > 0 } alt)
        {
            return alt.Error;
        }

        return string.Empty;
    }

    /// <summary>
    /// Go's <c>strconv.Atoi</c> as used by the playground: syntax errors yield
    /// 0, range errors yield the clamped value (the reference discards the
    /// error but keeps the value).
    /// </summary>
    internal static long GoAtoi(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }

        var index = 0;
        var negative = false;
        if (value[0] is '+' or '-')
        {
            negative = value[0] == '-';
            index = 1;
        }

        if (index == value.Length)
        {
            return 0;
        }

        var cutoff = negative
            ? (ulong)long.MaxValue + 1UL
            : (ulong)long.MaxValue;
        ulong accumulated = 0;

        for (; index < value.Length; index++)
        {
            var character = value[index];
            if (character is < '0' or > '9')
            {
                return 0;
            }

            var digit = (ulong)(character - '0');
            if (accumulated > (cutoff - digit) / 10UL)
            {
                return negative ? long.MinValue : long.MaxValue;
            }

            accumulated = (accumulated * 10UL) + digit;
        }

        if (negative)
        {
            return accumulated == (ulong)long.MaxValue + 1UL
                ? long.MinValue
                : -(long)accumulated;
        }

        return (long)accumulated;
    }

    /// <summary>
    /// gin <c>c.JSON(status, PlaygroundResponseError)</c>: Go marshals the
    /// struct in field order, omits a zero code, and HTML-escapes the output.
    /// </summary>
    private static async Task WriteErrorAsync(
        HttpContext context,
        int statusCode,
        long code,
        string message,
        CancellationToken cancellationToken)
    {
        var body = new PlaygroundResponseError
        {
            Status = statusCode,
            Error = new PlaygroundErrorBody
            {
                Code = code,
                Message = message,
            },
        };

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.Body
            .WriteAsync(AxonHub.Llm.Json.GoJson.Marshal(body), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Snapshots the response headers once the handler has written the response,
    /// mirroring middleware.WithResponseHeaders. ASP.NET freezes headers after
    /// the first write, so this matches the reference's capture at first write.
    /// </summary>
    private static void RecordResponseHeaders(
        HttpContext context,
        ChatCompletionOrchestrator orchestrator,
        long requestId)
    {
        if (requestId == 0)
        {
            return;
        }

        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var header in context.Response.Headers)
        {
            var values = new List<string>(header.Value.Count);
            foreach (var value in header.Value)
            {
                if (value is not null)
                {
                    values.Add(value);
                }
            }

            headers[header.Key] = values;
        }

        orchestrator.RecordResponseHeaders(requestId, headers);
    }

    /// <summary>gin <c>c.Query(key)</c>, including net/url's '+' decoding.</summary>
    private static string FirstQueryValue(HttpContext context, string key)
    {
        var query = WebhookEchoEndpoint.ParseGoQuery(context.Request.QueryString.Value);
        return query.TryGetValue(key, out var values) && values.Count > 0
            ? values[0]
            : string.Empty;
    }

    /// <summary>gin <c>c.GetHeader(key)</c>: the first header line.</summary>
    private static string FirstHeaderValue(HttpContext context, string name)
    {
        var header = context.Request.Headers[name];
        return header.Count == 0 ? string.Empty : header[0] ?? string.Empty;
    }

    private static T? TryUnmarshal<T>(byte[] body)
        where T : class
    {
        try
        {
            return LlmJson.Deserialize<T>(body);
        }
        catch (JsonException)
        {
            // Go's json.Unmarshal returns an error for the same inputs; the
            // shape is skipped.
            return null;
        }
    }

    private sealed class WrappedErrorMessageBody
    {
        [JsonPropertyName("error")]
        public WrappedErrorMessage? Error { get; set; }
    }

    private sealed class WrappedErrorMessage
    {
        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
    }

    private sealed class GenericMessageBody
    {
        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
    }

    private sealed class AltErrorMessageBody
    {
        [JsonPropertyName("error")]
        public string Error { get; set; } = string.Empty;
    }
}

/// <summary>Port of api.PlaygroundResponseError (playground.go:24-31).</summary>
internal sealed class PlaygroundResponseError
{
    /// <summary>Go's <c>json:"-"</c>: the HTTP status is not part of the body.</summary>
    [JsonIgnore]
    public int Status { get; init; }

    [JsonPropertyName("error")]
    public PlaygroundErrorBody Error { get; init; } = new();
}

/// <summary>The nested error object: <c>code</c> is omitempty, <c>message</c> is not.</summary>
internal sealed class PlaygroundErrorBody
{
    /// <summary>
    /// Go's <c>int</c> is 64-bit on the supported platforms, and
    /// <c>strconv.Atoi</c> keeps the clamped value on range errors, so the port
    /// keeps the full width here.
    /// </summary>
    [JsonPropertyName("code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long Code { get; init; }

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;
}
