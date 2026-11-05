using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Api.Http;
using AxonHub.Api.Management;
using AxonHub.Api.Models;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer.Anthropic;
using AxonHub.Persistence;
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
/// The <c>include</c> query parameter selects the extended metadata fields
/// (name, description, context_length, max_output_tokens, modalities,
/// capabilities, pricing, icon, type), which the reference reads from the ent
/// Model entities; models without an enabled row fall back to the facade fields
/// (C178).
/// </remarks>
public static class ModelsEndpoint
{
    private const string ModelObjectType = "model";
    private const string ListObjectType = "list";

    private const string InternalServerErrorCode = "internal_server_error";
    private const string ModelNotFoundCode = "model_not_found";
    private const string PermissionDeniedCode = "permission_denied";
    private const string PermissionDeniedMessage = "insufficient permissions to access this resource";
    private const string ServerErrorType = "server_error";
    private const string AuthenticationErrorType = "authentication_error";
    private const string InvalidRequestType = "invalid_request_error";
    private const string ModelParam = "model";

    /// <summary>Port of OpenAIHandlers.ListModels.</summary>
    public static async Task ListOpenAiModelsAsync(
        HttpContext context,
        ModelService models,
        IModelSettingsProvider settings,
        IModelStore modelStore)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(modelStore);

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

        // The include default comes from the system settings and is resolved
        // before the model list, exactly like the reference.
        var includeDefault = (await settings.GetModelSettingsOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false))
            .DefaultModelApiIncludeAll;
        var (include, needFullData) = ParseOpenAiModelInclude(
            context.Request.Query["include"].FirstOrDefault(),
            includeDefault);

        IReadOnlyList<ModelFacade> visibleModels;
        try
        {
            visibleModels = await models.ListEnabledModelsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await WriteModelListErrorAsync(context, requestId, ex, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (visibleModels.Count == 0)
        {
            await WriteJsonAsync(
                context,
                StatusCodes.Status200OK,
                GoJson.Marshal(new OpenAiModelListBody { Data = [] }),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        IReadOnlyList<OpenAiModelBody>? data = null;
        if (needFullData)
        {
            IReadOnlyList<ModelRecord> dbModels;
            try
            {
                dbModels = await ListEnabledModelsByIdAsync(
                        modelStore,
                        [.. visibleModels.Select(static model => model.Id)],
                        cancellationToken)
                    .ConfigureAwait(false);
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

            // Go maps the rows by model id; a duplicate id keeps the last row.
            var byModelId = new Dictionary<string, ModelRecord>(StringComparer.Ordinal);
            foreach (var dbModel in dbModels)
            {
                byModelId[dbModel.ModelId] = dbModel;
            }

            var modelsData = new OpenAiModelBody[visibleModels.Count];
            try
            {
                for (var i = 0; i < visibleModels.Count; i++)
                {
                    var visibleModel = visibleModels[i];
                    modelsData[i] = byModelId.TryGetValue(visibleModel.Id, out var dbModel)
                        ? ConvertModelToOpenAiExtended(dbModel, include)
                        : ToOpenAiModel(visibleModel);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // ent scans model_card while decoding the query rows, so a
                // malformed document fails the whole request (C178).
                await WriteJsonAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    InternalServerError(requestId, ex),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            data = modelsData;
        }

        data ??= [.. visibleModels.Select(ToOpenAiModel)];

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
        ModelService models,
        IModelStore modelStore)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(modelStore);

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

        // RetrieveModel always uses the strict include default (false).
        var (include, needFullData) = ParseOpenAiModelInclude(
            context.Request.Query["include"].FirstOrDefault(),
            defaultIncludeAll: false);

        IReadOnlyList<ModelFacade> visibleModels;
        try
        {
            visibleModels = await models.ListEnabledModelsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await WriteModelListErrorAsync(context, requestId, ex, cancellationToken).ConfigureAwait(false);
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

        if (!needFullData)
        {
            await WriteJsonAsync(
                context,
                StatusCodes.Status200OK,
                GoJson.Marshal(ToOpenAiModel(visibleModel)),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        // ent Query().Where(model_id, status=enabled).Only(ctx): zero rows fall
        // back to the facade, any other error is the internal error branch.
        IReadOnlyList<ModelRecord> configuredModels;
        try
        {
            configuredModels = await modelStore.ListAllAsync(
                    new ModelListFilter
                    {
                        ModelId = modelId,
                        Status = "enabled",
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await WriteJsonAsync(
                context,
                StatusCodes.Status500InternalServerError,
                InternalServerError(requestId, ex),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (configuredModels.Count == 0)
        {
            await WriteJsonAsync(
                context,
                StatusCodes.Status200OK,
                GoJson.Marshal(ToOpenAiModel(visibleModel)),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (configuredModels.Count > 1)
        {
            // ent's Only returns this sentinel when the query matches several
            // live rows (the models_by_model_id unique index normally prevents
            // it).
            await WriteJsonAsync(
                context,
                StatusCodes.Status500InternalServerError,
                InternalServerError(
                    requestId,
                    new InvalidOperationException("ent: model not singular")),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        byte[] body;
        try
        {
            body = GoJson.Marshal(ConvertModelToOpenAiExtended(configuredModels[0], include));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await WriteJsonAsync(
                context,
                StatusCodes.Status500InternalServerError,
                InternalServerError(requestId, ex),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await WriteJsonAsync(
            context,
            StatusCodes.Status200OK,
            body,
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
            // entprivacy.Deny → 403 permission_error; everything else is the
            // 500 internal_server_error branch.
            var errorType = PrivacyDenyException.IsDeny(ex)
                ? "permission_error"
                : InternalServerErrorCode;
            var statusCode = PrivacyDenyException.IsDeny(ex)
                ? StatusCodes.Status403Forbidden
                : StatusCodes.Status500InternalServerError;

            var error = new AnthropicError
            {
                Type = errorType,
                RequestId = requestId,
                Error = new AnthropicErrorDetail
                {
                    Type = errorType,
                    Message = ex.Message,
                },
            };

            await WriteJsonAsync(
                context,
                statusCode,
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

    /// <summary>Port of parseOpenAIModelInclude (openai.go:620-652).</summary>
    public static (HashSet<string>? Include, bool NeedFullData) ParseOpenAiModelInclude(
        string? includeParam,
        bool defaultIncludeAll)
    {
        if (string.IsNullOrEmpty(includeParam))
        {
            return (null, defaultIncludeAll);
        }

        if (string.Equals(includeParam, "all", StringComparison.Ordinal))
        {
            return (null, true);
        }

        var include = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in includeParam.Split(','))
        {
            var trimmed = field.Trim();
            if (trimmed.Length != 0)
            {
                include.Add(trimmed);
            }
        }

        string[] extendedFields =
        [
            "name", "description", "context_length", "max_output_tokens",
            "modalities", "capabilities", "pricing", "icon", "type",
        ];
        var needFullData = extendedFields.Any(include.Contains);

        return (include, needFullData);
    }

    private static async Task<IReadOnlyList<ModelRecord>> ListEnabledModelsByIdAsync(
        IModelStore modelStore,
        IReadOnlyList<string> modelIds,
        CancellationToken cancellationToken)
        => await modelStore.ListAllAsync(
                new ModelListFilter
                {
                    Status = "enabled",
                    ModelIdIn = modelIds,
                },
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// The shared ListEnabledModels error branch: a deny is the 403
    /// permission_denied envelope, everything else the 500 internal error.
    /// </summary>
    private static Task WriteModelListErrorAsync(
        HttpContext context,
        string? requestId,
        Exception error,
        CancellationToken cancellationToken)
        => PrivacyDenyException.IsDeny(error)
            ? WriteJsonAsync(
                context,
                StatusCodes.Status403Forbidden,
                GoJson.Marshal(new ResponseError
                {
                    Detail = new ErrorDetail
                    {
                        Code = PermissionDeniedCode,
                        Message = PermissionDeniedMessage,
                        Type = AuthenticationErrorType,
                        RequestId = requestId,
                    },
                }),
                cancellationToken)
            : WriteJsonAsync(
                context,
                StatusCodes.Status500InternalServerError,
                InternalServerError(requestId, error),
                cancellationToken);

    private static OpenAiModelBody ToOpenAiModel(ModelFacade model) => new()
    {
        Id = model.Id,
        Created = model.Created,
        OwnedBy = model.OwnedBy,
    };

    /// <summary>Port of convertModelToOpenAIExtended (openai.go:667-744).</summary>
    private static OpenAiModelBody ConvertModelToOpenAiExtended(
        ModelRecord model,
        HashSet<string>? include)
    {
        bool ShouldInclude(string field) => include is null || include.Contains(field);

        var body = new OpenAiModelBody
        {
            Id = model.ModelId,
            Created = GoTime.UnixSeconds(model.CreatedAt),
            OwnedBy = model.Developer,
        };

        // Go's omitempty drops empty strings; the body nulls them so the shared
        // serializer omits the property.
        if (ShouldInclude("name") && model.Name.Length != 0)
        {
            body.Name = model.Name;
        }

        if (ShouldInclude("icon") && model.Icon.Length != 0)
        {
            body.Icon = model.Icon;
        }

        if (ShouldInclude("type") && model.Type.Length != 0)
        {
            body.Type = model.Type;
        }

        if (ShouldInclude("description") && !string.IsNullOrEmpty(model.Remark))
        {
            body.Description = model.Remark;
        }

        ModelCard? card;
        try
        {
            card = ModelCardCodec.Deserialize(model.ModelCard);
        }
        catch (JsonException ex)
        {
            // ent scan: "unmarshal field model_card: %w".
            throw new InvalidOperationException("unmarshal field model_card: " + ex.Message, ex);
        }

        if (card is not null)
        {
            if (ShouldInclude("modalities"))
            {
                body.Modalities = new OpenAiModalities
                {
                    Input = card.Modalities.Input ?? [],
                    Output = card.Modalities.Output ?? [],
                };
            }

            if (ShouldInclude("capabilities"))
            {
                body.Capabilities = new OpenAiCapabilities
                {
                    Vision = card.Vision,
                    ToolCall = card.ToolCall,
                    Reasoning = card.Reasoning.Supported,
                };
            }

            if (ShouldInclude("context_length") && card.Limit.Context != 0)
            {
                body.ContextLength = card.Limit.Context;
            }

            if (ShouldInclude("max_output_tokens") && card.Limit.Output != 0)
            {
                body.MaxOutputTokens = card.Limit.Output;
            }

            if (ShouldInclude("pricing"))
            {
                body.Pricing = new OpenAiPricing
                {
                    Input = card.Cost.Input,
                    Output = card.Cost.Output,
                    CacheRead = card.Cost.CacheRead,
                    CacheWrite = card.Cost.CacheWrite,
                };
            }
        }

        return body;
    }

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

    /// <summary>
    /// Port of api.OpenAIModel. The optional fields follow Go's omitempty: a
    /// null (or the empty string / zero the converter nulls) is not emitted.
    /// </summary>
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

        [JsonPropertyName("name")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Name { get; set; }

        [JsonPropertyName("description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Description { get; set; }

        [JsonPropertyName("context_length")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? ContextLength { get; set; }

        [JsonPropertyName("max_output_tokens")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? MaxOutputTokens { get; set; }

        [JsonPropertyName("modalities")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public OpenAiModalities? Modalities { get; set; }

        [JsonPropertyName("capabilities")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public OpenAiCapabilities? Capabilities { get; set; }

        [JsonPropertyName("pricing")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public OpenAiPricing? Pricing { get; set; }

        [JsonPropertyName("icon")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Icon { get; set; }

        [JsonPropertyName("type")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Type { get; set; }
    }

    /// <summary>Port of api.Modalities (input/output are never null).</summary>
    private sealed class OpenAiModalities
    {
        [JsonPropertyName("input")]
        public required IReadOnlyList<string> Input { get; init; }

        [JsonPropertyName("output")]
        public required IReadOnlyList<string> Output { get; init; }
    }

    /// <summary>Port of api.Capabilities.</summary>
    private sealed class OpenAiCapabilities
    {
        [JsonPropertyName("vision")]
        public bool Vision { get; init; }

        [JsonPropertyName("tool_call")]
        public bool ToolCall { get; init; }

        [JsonPropertyName("reasoning")]
        public bool Reasoning { get; init; }
    }

    /// <summary>Port of api.Pricing.</summary>
    private sealed class OpenAiPricing
    {
        [JsonPropertyName("input")]
        [JsonConverter(typeof(GoOptionalNumberConverter))]
        public double? Input { get; init; }

        [JsonPropertyName("output")]
        [JsonConverter(typeof(GoOptionalNumberConverter))]
        public double? Output { get; init; }

        [JsonPropertyName("cache_read")]
        [JsonConverter(typeof(GoOptionalNumberConverter))]
        public double? CacheRead { get; init; }

        [JsonPropertyName("cache_write")]
        [JsonConverter(typeof(GoOptionalNumberConverter))]
        public double? CacheWrite { get; init; }

        [JsonPropertyName("unit")]
        public string Unit { get; init; } = "per_1m_tokens";

        [JsonPropertyName("currency")]
        public string Currency { get; init; } = "USD";
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
