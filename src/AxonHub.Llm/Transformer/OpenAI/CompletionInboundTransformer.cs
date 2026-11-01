using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer;

namespace AxonHub.Llm.Transformer.OpenAI;

/// <summary>
/// Port of openai.CompletionInboundTransformer
/// (llm/transformer/openai/completion_inbound.go): the legacy
/// <c>POST /v1/completions</c> surface. The error text, content-type default
/// and validation order match the reference one-for-one.
/// </summary>
public sealed class CompletionInboundTransformer : IInboundTransformer
{
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

        var compReq = DecodeRequest(request.Body);

        if (string.IsNullOrEmpty(compReq.Model))
        {
            throw TransformerErrors.InvalidRequest("model is required");
        }

        if (string.IsNullOrEmpty(compReq.Prompt))
        {
            throw TransformerErrors.InvalidRequest("prompt is required");
        }

        var completion = new CompletionRequest
        {
            Prompt = compReq.Prompt,
            Suffix = NullIfEmpty(compReq.Suffix),
            MaxTokens = compReq.MaxTokens,
            Temperature = compReq.Temperature,
            TopP = compReq.TopP,
            N = compReq.N,
            Logprobs = compReq.Logprobs,
            Echo = compReq.Echo,
            PresencePenalty = compReq.PresencePenalty,
            FrequencyPenalty = compReq.FrequencyPenalty,
            BestOf = compReq.BestOf,
            LogitBias = compReq.LogitBias,
            Seed = compReq.Seed,
            User = NullIfEmpty(compReq.User),
        };

        if (compReq.Stop is not null)
        {
            completion.Stop = new Stop
            {
                Single = compReq.Stop.Single,
                Multiple = compReq.Stop.Multiple,
            };
        }

        var llmReq = new LlmRequest
        {
            Model = compReq.Model,
            Messages = [],
            RawRequest = request,
            RequestType = RequestTypes.Completion,
            ApiFormat = ApiFormats.OpenAICompletion,
            Stream = compReq.Stream,
            StreamOptions = compReq.StreamOptions,
            Completion = completion,
        };

        if (compReq.Temperature is not null)
        {
            llmReq.Temperature = compReq.Temperature;
        }

        if (compReq.TopP is not null)
        {
            llmReq.TopP = compReq.TopP;
        }

        if (compReq.FrequencyPenalty is not null)
        {
            llmReq.FrequencyPenalty = compReq.FrequencyPenalty;
        }

        if (compReq.PresencePenalty is not null)
        {
            llmReq.PresencePenalty = compReq.PresencePenalty;
        }

        if (compReq.MaxTokens is not null)
        {
            llmReq.MaxTokens = compReq.MaxTokens;
        }

        if (!string.IsNullOrEmpty(compReq.User))
        {
            llmReq.User = compReq.User;
        }

        if (compReq.Seed is not null)
        {
            llmReq.Seed = compReq.Seed;
        }

        return llmReq;
    }

    /// <summary>Port of TransformResponse.</summary>
    public HttpResponseData TransformResponse(LlmResponse? response)
    {
        if (response is null)
        {
            throw new InvalidOperationException("completion response is nil");
        }

        byte[] body;
        var statusCode = 200;

        if (response.Completion is not null)
        {
            var resp = new OpenAiCompletionResponse
            {
                Id = response.Id,
                Object = response.Object,
                Created = response.Created,
                Model = response.Model,
            };

            // Go: make([]CompletionChoice, len(...)) — always a non-nil slice.
            resp.Choices = new List<OpenAiCompletionChoice>();
            foreach (var choice in response.Completion.Choices)
            {
                resp.Choices.Add(new OpenAiCompletionChoice
                {
                    Text = choice.Text,
                    Index = choice.Index,
                    Logprobs = choice.Logprobs,
                    FinishReason = choice.FinishReason,
                });
            }

            if (response.Usage is { } usage)
            {
                resp.Usage = new OpenAiCompletionUsage
                {
                    PromptTokens = usage.PromptTokens,
                    CompletionTokens = usage.CompletionTokens,
                    TotalTokens = usage.TotalTokens,
                    Cost = usage.Cost,
                };
            }

            body = GoJson.Marshal(resp);
        }
        else if (response.Error is { } error)
        {
            body = GoJson.Marshal(new OpenAiError { Detail = error.Detail });
            statusCode = error.StatusCode;
        }
        else
        {
            throw new InvalidOperationException("completion response missing completion data");
        }

        return new HttpResponseData
        {
            StatusCode = statusCode,
            Body = body,
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json"],
                ["Cache-Control"] = ["no-cache"],
            },
        };
    }

    /// <summary>Port of TransformStream: <c>streams.NoNil(streams.MapErr(...))</c>.</summary>
    public IStream<StreamEvent> TransformStream(IStream<LlmResponse> stream)
        => new MapStream<LlmResponse, StreamEvent>(stream, TransformStreamChunk);

    /// <summary>Port of transformStreamChunk. Returns null for filtered chunks.</summary>
    public StreamEvent? TransformStreamChunk(LlmResponse? llmResp)
    {
        if (llmResp is null)
        {
            throw new InvalidOperationException("completion response is nil");
        }

        if (llmResp.Object == "[DONE]")
        {
            return new StreamEvent
            {
                Data = Encoding.UTF8.GetBytes("[DONE]"),
            };
        }

        if (llmResp.Completion is null && llmResp.Usage is null)
        {
            return null;
        }

        var resp = new OpenAiCompletionResponse
        {
            Id = llmResp.Id,
            Object = llmResp.Object,
            Created = llmResp.Created,
            Model = llmResp.Model,
            Choices = [],
        };

        if (llmResp.Completion is { } completion)
        {
            resp.Choices = new List<OpenAiCompletionChoice>(completion.Choices.Count);
            foreach (var choice in completion.Choices)
            {
                resp.Choices.Add(new OpenAiCompletionChoice
                {
                    Text = choice.Text,
                    Index = choice.Index,
                    Logprobs = choice.Logprobs,
                    FinishReason = choice.FinishReason,
                });
            }
        }

        if (llmResp.Usage is { } usage)
        {
            resp.Usage = new OpenAiCompletionUsage
            {
                PromptTokens = usage.PromptTokens,
                CompletionTokens = usage.CompletionTokens,
                TotalTokens = usage.TotalTokens,
                Cost = usage.Cost,
            };
        }

        return new StreamEvent
        {
            Type = string.Empty,
            Data = GoJson.Marshal(resp),
        };
    }

    /// <summary>Port of AggregateStreamChunks.</summary>
    public byte[] AggregateStreamChunks(IReadOnlyList<StreamEvent> chunks, out ResponseMeta meta)
        => CompletionAggregator.AggregateStreamChunks(chunks, out meta);

    /// <summary>
    /// Port of CompletionInboundTransformer.TransformError
    /// (completion_inbound.go:236-286). Unlike the chat transformer this one
    /// passes HTTP error statuses through without normalization.
    /// </summary>
    public HttpResponseData TransformError(Exception? error)
    {
        if (error is null)
        {
            return Unexpected();
        }

        var transformerError = GoErrors.As<TransformerException>(error);
        if (transformerError is { Kind: TransformerErrorKind.InvalidModel })
        {
            return ErrorResponse(422, error.Message, "invalid_model_error");
        }

        if (GoErrors.As<HttpClientError>(error) is { } httpError)
        {
            return new HttpResponseData
            {
                StatusCode = httpError.StatusCode,
                Body = httpError.Body,
                Headers = JsonHeaders(),
            };
        }

        if (transformerError is { Kind: TransformerErrorKind.InvalidRequest })
        {
            return ErrorResponse(400, error.Message, "invalid_request_error");
        }

        if (GoErrors.As<ResponseError>(error) is { } llmError)
        {
            return new HttpResponseData
            {
                StatusCode = llmError.StatusCode,
                Body = GoJson.Marshal(llmError),
                Headers = JsonHeaders(),
            };
        }

        return ErrorResponse(500, error.Message, "internal_server_error");
    }

    private static HttpResponseData Unexpected()
        => ErrorResponse(500, "An unexpected error occurred", "unexpected_error");

    private static HttpResponseData ErrorResponse(int statusCode, string message, string type)
        => new()
        {
            StatusCode = statusCode,
            Body = GoJson.Marshal(new ResponseError
            {
                Detail = new ErrorDetail
                {
                    Message = message,
                    Type = type,
                },
            }),
            Headers = JsonHeaders(),
        };

    private static Dictionary<string, List<string>> JsonHeaders()
        => new(StringComparer.Ordinal)
        {
            ["Content-Type"] = ["application/json"],
        };

    /// <summary>
    /// Decodes the client body the way Go's json.Unmarshal does: syntax errors
    /// and root-type mismatches keep encoding/json's wording, and a JSON
    /// <c>null</c> leaves the zero request in place (then fails on model).
    /// </summary>
    private static OpenAiCompletionRequest DecodeRequest(byte[] body)
    {
        var syntaxError = GoJsonSyntax.Validate(body);
        if (syntaxError is not null)
        {
            throw TransformerErrors.InvalidRequest("failed to decode completion request: " + syntaxError);
        }

        var rootKind = GoStructJson.RootKind(body);
        if (rootKind == "null")
        {
            return new OpenAiCompletionRequest();
        }

        if (rootKind is not null)
        {
            throw TransformerErrors.InvalidRequest(
                "failed to decode completion request: "
                + GoStructJson.TypeMismatch(rootKind, "openai.CompletionRequest"));
        }

        var typeError = OpenAiRequestTypeCheck.ValidateCompletion(body);
        if (typeError is not null)
        {
            throw TransformerErrors.InvalidRequest("failed to decode completion request: " + typeError);
        }

        try
        {
            return LlmJson.Deserialize<OpenAiCompletionRequest>(body) ?? new OpenAiCompletionRequest();
        }
        catch (JsonException ex)
        {
            throw TransformerErrors.InvalidRequest(
                "failed to decode completion request: " + OpenAiInboundTransformer.JsonErrorText(ex),
                ex);
        }
    }

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrEmpty(value) ? null : value;
}
