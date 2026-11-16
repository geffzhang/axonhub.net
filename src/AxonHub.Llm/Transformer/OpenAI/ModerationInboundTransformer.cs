using System.Globalization;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;

namespace AxonHub.Llm.Transformer.OpenAI;

/// <summary>
/// Port of openai.ModerationInboundTransformer
/// (llm/transformer/openai/moderation_inbound.go): the /v1/moderations surface.
/// </summary>
public sealed class ModerationInboundTransformer : IInboundTransformer
{
    /// <summary>Go: defaultModerationModel, used when the client omits model.</summary>
    public const string DefaultModerationModel = "omni-moderation-latest";

    /// <summary>Port of TransformRequest.</summary>
    public LlmRequest TransformRequest(HttpRequestData? request)
    {
        if (request is null)
        {
            throw TransformerErrors.InvalidRequest("http request is nil");
        }

        if (request.Body is null || request.Body.Length == 0)
        {
            throw TransformerErrors.InvalidRequest("request body is empty");
        }

        var contentType = HttpClientUtils.HeaderGet(request.Headers, "Content-Type");
        if (contentType.Length == 0)
        {
            contentType = "application/json";
        }

        if (!contentType.ToLowerInvariant().Contains("application/json", StringComparison.Ordinal))
        {
            throw TransformerErrors.InvalidRequest("unsupported content type: " + contentType);
        }

        var moderationRequest = DecodeRequest(request.Body);

        ValidateModerationInput(moderationRequest.Input);

        // OpenAI allows omitting model; the default keeps channel routing and
        // upstream compatibility consistent with omni-moderation-latest.
        var model = (moderationRequest.Model ?? string.Empty).Trim();
        if (model.Length == 0)
        {
            model = DefaultModerationModel;
        }

        return new LlmRequest
        {
            Model = model,
            Messages = [],
            RawRequest = request,
            RequestType = RequestTypes.Moderation,
            ApiFormat = ApiFormats.OpenAIModeration,
            Stream = null,
            Moderation = new ModerationRequest
            {
                Input = moderationRequest.Input,
            },
        };
    }

    /// <summary>Port of TransformResponse.</summary>
    public HttpResponseData TransformResponse(LlmResponse? response)
    {
        if (response is null)
        {
            throw new InvalidOperationException("moderation response is nil");
        }

        if (response.Moderation is null)
        {
            throw new InvalidOperationException("moderation response missing moderation data");
        }

        var apiResponse = new OpenAiModerationCreateResponse
        {
            Id = response.Id,
            Model = response.Model,
            Results = response.Moderation.Results,
        };

        var body = GoJson.Marshal(apiResponse);

        return new HttpResponseData
        {
            StatusCode = 200,
            Body = body,
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json"],
                ["Cache-Control"] = ["no-cache"],
            },
        };
    }

    /// <summary>Go: ModerationInboundTransformer.TransformStream.</summary>
    public IStream<StreamEvent> TransformStream(IStream<LlmResponse> stream)
        => throw TransformerErrors.InvalidRequest("moderations do not support streaming");

    /// <summary>Go: ModerationInboundTransformer.AggregateStreamChunks.</summary>
    public byte[] AggregateStreamChunks(IReadOnlyList<StreamEvent> chunks, out ResponseMeta meta)
    {
        meta = new ResponseMeta();
        throw new InvalidOperationException("moderations do not support streaming");
    }

    /// <summary>Go: TransformError delegates to the chat transformer.</summary>
    public HttpResponseData TransformError(Exception? error)
        => new OpenAiInboundTransformer().TransformError(error);

    /// <summary>Decodes the client body with Go's json.Unmarshal semantics.</summary>
    private static OpenAiModerationCreateRequest DecodeRequest(byte[] body)
    {
        var syntaxError = GoJsonSyntax.Validate(body);
        if (syntaxError is not null)
        {
            throw TransformerErrors.InvalidRequest("failed to decode moderation request: " + syntaxError);
        }

        var rootKind = GoStructJson.RootKind(body);
        if (rootKind == "null")
        {
            return new OpenAiModerationCreateRequest();
        }

        if (rootKind is not null)
        {
            throw TransformerErrors.InvalidRequest(
                "failed to decode moderation request: "
                + GoStructJson.TypeMismatch(rootKind, "openai.ModerationCreateRequest"));
        }

        try
        {
            return LlmJson.Deserialize<OpenAiModerationCreateRequest>(body)
                ?? new OpenAiModerationCreateRequest();
        }
        catch (JsonException ex)
        {
            throw TransformerErrors.InvalidRequest(
                "failed to decode moderation request: " + OpenAiInboundTransformer.JsonErrorText(ex),
                ex);
        }
    }

    /// <summary>Port of validateModerationInput.</summary>
    private static void ValidateModerationInput(ModerationInput input)
    {
        if (input.Parts is { Count: > 0 } parts)
        {
            for (var i = 0; i < parts.Count; i++)
            {
                var part = parts[i] ?? new ModerationMultiModalPart();

                switch (part.Type.Trim())
                {
                    case "text":
                        if (string.IsNullOrWhiteSpace(part.Text))
                        {
                            throw InvalidPart(i, "text cannot be empty");
                        }

                        break;
                    case "image_url":
                        if (part.ImageURL is null || string.IsNullOrWhiteSpace(part.ImageURL.Url))
                        {
                            throw InvalidPart(i, "image_url.url is required");
                        }

                        break;
                    default:
                        throw InvalidPart(i, "type must be text or image_url");
                }
            }

            return;
        }

        if (input.StringArray is { } strings)
        {
            if (strings.Count == 0)
            {
                throw TransformerErrors.InvalidRequest("input cannot be empty array");
            }

            for (var i = 0; i < strings.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(strings[i]))
                {
                    throw TransformerErrors.InvalidRequest(
                        "input[" + i.ToString(CultureInfo.InvariantCulture)
                        + "] cannot be empty string");
                }
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(input.String))
        {
            throw TransformerErrors.InvalidRequest("input cannot be empty string");
        }
    }

    private static TransformerException InvalidPart(int index, string detail)
        => TransformerErrors.InvalidRequest(
            "input[" + index.ToString(CultureInfo.InvariantCulture) + "]." + detail);
}
