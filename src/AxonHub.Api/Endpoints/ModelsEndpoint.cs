using System.Text.Json.Serialization;
using AxonHub.Api.Http;
using AxonHub.Api.Models;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer.Anthropic;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Endpoints;

/// <summary>
/// Port of api.OpenAIHandlers.ListModels/RetrieveModel
/// (internal/server/api/openai.go:792-917) and api.AnthropicHandlers.ListModels
/// (internal/server/api/anthropic.go:85-138): the models surface mounted at
/// <c>GET /v1/models</c>, <c>GET /v1/models/*model</c> and
/// <c>GET /anthropic/v1/models</c>.
/// </summary>
/// <remarks>
/// The reference's extended <c>include</c> metadata comes from the ent Model
/// entities; the port has no <c>models</c> table, so every model takes the
/// reference's "configured model not found" path and renders the facade fields
/// only. Against a database without <c>models</c> rows the two produce the same
/// bytes (C088).
/// </remarks>
public static class ModelsEndpoint
{
    private const string ModelObjectType = "model";
    private const string ListObjectType = "list";

    private const string InternalServerErrorCode = "internal_server_error";
    private const string ModelNotFoundCode = "model_not_found";
    private const string ServerErrorType = "server_error";
    private const string InvalidRequestType = "invalid_request_error";
    private const string ModelParam = "model";

    /// <summary>Port of OpenAIHandlers.ListModels.</summary>
    public static async Task ListOpenAiModelsAsync(HttpContext context, ModelService models)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(models);

        var cancellationToken = context.RequestAborted;
        var requestId = RequestIds.FromHttpContext(context);

        // gin matches "/v1/models/" with the catch-all ("/v1/models/*model"),
        // whose parameter is "/" and trims to the empty id — a 404, never the
        // list. ASP.NET ignores the trailing slash on the literal route, so the
        // reference behaviour is restored here (C088).
        if (IsTrailingSlashPath(context.Request.Path))
        {
            await WriteJsonAsync(
                context,
                StatusCodes.Status404NotFound,
                ModelNotFoundError(requestId, string.Empty),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        IReadOnlyList<ModelFacade> visibleModels;
        try
        {
            visibleModels = await models.ListEnabledModelsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(
                context,
                StatusCodes.Status500InternalServerError,
                InternalServerError(requestId, ex),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var data = new OpenAiModelBody[visibleModels.Count];
        for (var i = 0; i < visibleModels.Count; i++)
        {
            data[i] = ToOpenAiModel(visibleModels[i]);
        }

        await WriteJsonAsync(
            context,
            StatusCodes.Status200OK,
            GoJson.Marshal(new OpenAiModelListBody { Data = data }),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Port of OpenAIHandlers.RetrieveModel. The <paramref name="model"/> route
    /// value is ASP.NET's catch-all capture, which omits the separator slash
    /// gin's parameter keeps (gin then trims it), so no trimming is needed.
    /// </summary>
    public static async Task RetrieveOpenAiModelAsync(
        HttpContext context,
        string? model,
        ModelService models)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(models);

        var cancellationToken = context.RequestAborted;
        var requestId = RequestIds.FromHttpContext(context);

        var modelId = model ?? string.Empty;
        if (modelId.Length == 0)
        {
            await WriteJsonAsync(
                context,
                StatusCodes.Status404NotFound,
                ModelNotFoundError(requestId, string.Empty),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        IReadOnlyList<ModelFacade> visibleModels;
        try
        {
            visibleModels = await models.ListEnabledModelsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(
                context,
                StatusCodes.Status500InternalServerError,
                InternalServerError(requestId, ex),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var visibleModel = visibleModels.FirstOrDefault(
            facade => string.Equals(facade.Id, modelId, StringComparison.Ordinal));
        if (visibleModel is null)
        {
            await WriteJsonAsync(
                context,
                StatusCodes.Status404NotFound,
                ModelNotFoundError(requestId, modelId),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        // Extended include fields need an enabled ent Model row; the port has no
        // models table, so the reference's NotFound fallback is the only path.
        await WriteJsonAsync(
            context,
            StatusCodes.Status200OK,
            GoJson.Marshal(ToOpenAiModel(visibleModel)),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Port of AnthropicHandlers.ListModels.</summary>
    public static async Task ListAnthropicModelsAsync(HttpContext context, ModelService models)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(models);

        var cancellationToken = context.RequestAborted;
        var requestId = RequestIds.FromHttpContext(context) ?? string.Empty;

        IReadOnlyList<ModelFacade> visibleModels;
        try
        {
            visibleModels = await models.ListEnabledModelsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var error = new AnthropicError
            {
                Type = InternalServerErrorCode,
                RequestId = requestId,
                Error = new AnthropicErrorDetail
                {
                    Type = InternalServerErrorCode,
                    Message = ex.Message,
                },
            };

            await WriteJsonAsync(
                context,
                StatusCodes.Status500InternalServerError,
                GoJson.Marshal(error),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var data = new AnthropicModelBody[visibleModels.Count];
        for (var i = 0; i < visibleModels.Count; i++)
        {
            data[i] = new AnthropicModelBody
            {
                Id = visibleModels[i].Id,
                DisplayName = visibleModels[i].DisplayName,
                Created = GoTime.Rfc3339Nano(visibleModels[i].CreatedAt),
            };
        }

        var body = new AnthropicModelListBody
        {
            Data = data,
            FirstId = data.Length > 0 ? data[0].Id : string.Empty,
            LastId = data.Length > 0 ? data[^1].Id : string.Empty,
        };

        await WriteJsonAsync(
            context,
            StatusCodes.Status200OK,
            GoJson.Marshal(body),
            cancellationToken).ConfigureAwait(false);
    }

    private static OpenAiModelBody ToOpenAiModel(ModelFacade model) => new()
    {
        Id = model.Id,
        Created = model.Created,
        OwnedBy = model.OwnedBy,
    };

    /// <summary>gin: <c>c.JSON(status, openai.OpenAIError{Detail: ...})</c>.</summary>
    private static byte[] ModelNotFoundError(string? requestId, string modelId)
    {
        // writeOpenAIModelNotFoundError: the id is quoted with backticks, and an
        // empty id drops the backticked clause entirely.
        var message = modelId.Length > 0
            ? $"The model `{modelId}` does not exist or you do not have access to it."
            : "The model does not exist or you do not have access to it.";

        return GoJson.Marshal(new ResponseError
        {
            Detail = new ErrorDetail
            {
                Code = ModelNotFoundCode,
                Message = message,
                Type = InvalidRequestType,
                Param = ModelParam,
                RequestId = requestId,
            },
        });
    }

    /// <summary>writeOpenAIInternalError: the wrapped error's text is the message.</summary>
    private static byte[] InternalServerError(string? requestId, Exception error)
        => GoJson.Marshal(new ResponseError
        {
            Detail = new ErrorDetail
            {
                Code = InternalServerErrorCode,
                Message = error.Message,
                Type = ServerErrorType,
                RequestId = requestId,
            },
        });

    /// <summary>True for the "/v1/models/" spelling gin routes to the catch-all.</summary>
    private static bool IsTrailingSlashPath(PathString path)
        => path.Value is { } value && value.Length > 1 && value.EndsWith('/');

    /// <summary>gin: <c>c.JSON(status, ...)</c> — UTF-8 JSON, no trailing newline.</summary>
    private static Task WriteJsonAsync(
        HttpContext context,
        int statusCode,
        byte[] body,
        CancellationToken cancellationToken)
        => ChatCompletionEndpoint.WriteResponseAsync(
            context,
            new HttpResponseData
            {
                StatusCode = statusCode,
                Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
                {
                    ["Content-Type"] = ["application/json; charset=utf-8"],
                },
                Body = body,
            },
            cancellationToken);

    /// <summary>Port of api.OpenAIModel, facade fields only (C088).</summary>
    private sealed class OpenAiModelBody
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("object")]
        public string Object { get; init; } = ModelObjectType;

        [JsonPropertyName("created")]
        public long Created { get; init; }

        [JsonPropertyName("owned_by")]
        public required string OwnedBy { get; init; }
    }

    /// <summary>
    /// gin.H{"object": "list", "data": [...]}. Go marshals map keys sorted, so
    /// "data" precedes "object" on the wire.
    /// </summary>
    private sealed class OpenAiModelListBody
    {
        [JsonPropertyName("data")]
        public required IReadOnlyList<OpenAiModelBody> Data { get; init; }

        [JsonPropertyName("object")]
        public string Object { get; init; } = ListObjectType;
    }

    /// <summary>Port of api.AnthropicModel (created is a time.Time).</summary>
    private sealed class AnthropicModelBody
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("type")]
        public string Type { get; init; } = ModelObjectType;

        [JsonPropertyName("display_name")]
        public required string DisplayName { get; init; }

        [JsonPropertyName("created")]
        public required string Created { get; init; }
    }

    /// <summary>gin.H in Go's sorted-key order: data, first_id, has_more, last_id, object.</summary>
    private sealed class AnthropicModelListBody
    {
        [JsonPropertyName("data")]
        public required IReadOnlyList<AnthropicModelBody> Data { get; init; }

        [JsonPropertyName("first_id")]
        public required string FirstId { get; init; }

        [JsonPropertyName("has_more")]
        public bool HasMore { get; init; }

        [JsonPropertyName("last_id")]
        public required string LastId { get; init; }

        [JsonPropertyName("object")]
        public string Object { get; init; } = ListObjectType;
    }
}
