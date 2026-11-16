using AxonHub.Api.Management;
using AxonHub.Api.Models;
using AxonHub.Llm;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer;
using AxonHub.Persistence;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of orchestrator.ModelMapper
/// (internal/server/orchestrator/model_mapper.go:97-171): applies the API
/// key's active profile model mappings and restores the client model on the
/// responses.
/// </summary>
public static class ModelMapper
{
    /// <summary>
    /// Port of ModelMapper.MapModel: a nil key, absent profiles, an empty or
    /// unknown active profile, or no matching mapping all return the original
    /// model. The first matching <c>from</c> wins; matching is
    /// <see cref="XRegexp.MatchString"/> (exact strings stay exact, wildcards
    /// and regexes are anchored).
    /// </summary>
    public static string MapModel(ApiKeyRecord? apiKey, string originalModel)
    {
        ArgumentNullException.ThrowIfNull(originalModel);

        if (apiKey is null)
        {
            return originalModel;
        }

        var activeProfile = ApiKeyProfilesCodec.ActiveProfile(apiKey.Profiles);
        if (activeProfile is null)
        {
            return originalModel;
        }

        foreach (var mapping in activeProfile.ModelMappings ?? [])
        {
            if (XRegexp.MatchString(mapping.From, originalModel))
            {
                return mapping.To;
            }
        }

        return originalModel;
    }

    /// <summary>
    /// Port of ModelMapper.ReplaceResponseModel: an empty response model means
    /// the provider did not report one, so it stays empty; any other model is
    /// replaced with the client's original request model.
    /// </summary>
    public static void ReplaceResponseModel(LlmResponse? response, string requestModel)
    {
        if (response is not null
            && response.Model.Length != 0
            && !string.Equals(response.Model, requestModel, StringComparison.Ordinal))
        {
            response.Model = requestModel;
        }
    }
}

/// <summary>
/// Port of orchestrator.apiKeyModelMappingMiddleware
/// (internal/server/orchestrator/model_mapper.go:18-95): one middleware per
/// request that remembers the client model, maps the routing model through the
/// API key's active profile, and restores the client model on responses and
/// stream chunks.
/// </summary>
/// <remarks>
/// The reference keeps <c>RequestModel</c> and <c>OriginalModel</c> on the
/// middleware and the shared <c>PersistenceState</c> respectively. The port
/// applies this instance once before candidate selection (the reference runs
/// it inside the pipeline's inbound pass, before its selector) and then mounts
/// the same instance in the pipeline, so the second inbound pass takes the
/// reference's "restore OriginalModel" branch exactly like a duplicate call
/// would.
/// </remarks>
internal sealed class ApiKeyModelMappingMiddleware : DummyMiddleware
{
    private readonly ApiKeyRecord? _apiKey;

    public ApiKeyModelMappingMiddleware(ApiKeyRecord? apiKey, string originalModel = "")
        : base("apply-model-mapping")
    {
        _apiKey = apiKey;
        OriginalModel = originalModel;
    }

    /// <summary>The client request model, captured before any profile mapping.</summary>
    public string RequestModel { get; private set; } = string.Empty;

    /// <summary>
    /// The routing model saved after profile mapping (the reference's
    /// <c>state.OriginalModel</c>).
    /// </summary>
    public string OriginalModel { get; private set; } = string.Empty;

    public override LlmRequest TransformInboundLlmRequest(LlmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // model_mapper.go:36-39: the mapping stage is also the first inbound
        // stage that rejects a request without a model; channel selection and
        // prompt matching must never see an empty model.
        if (request.Model.Length == 0)
        {
            throw TransformerErrors.InvalidModel("request model is empty");
        }

        if (RequestModel.Length == 0)
        {
            RequestModel = request.Model;
        }

        if (_apiKey is not null)
        {
            var originalModel = request.Model;
            var mappedModel = ModelMapper.MapModel(_apiKey, originalModel);

            if (!string.Equals(mappedModel, originalModel, StringComparison.Ordinal))
            {
                request.Model = mappedModel;
            }
        }

        // model_mapper.go:60-71: routing uses the post-profile model; a second
        // inbound pass restores it instead of mapping the already-mapped
        // request again.
        if (OriginalModel.Length == 0)
        {
            OriginalModel = request.Model;
        }
        else
        {
            request.Model = OriginalModel;
        }

        return request;
    }

    public override LlmResponse TransformOutboundLlmResponse(LlmResponse response)
    {
        ModelMapper.ReplaceResponseModel(response, RequestModel);
        return response;
    }

    public override IStream<LlmResponse> TransformOutboundLlmStream(IStream<LlmResponse> stream)
    {
        if (RequestModel.Length == 0)
        {
            return stream;
        }

        return new MapStream<LlmResponse, LlmResponse>(
            stream,
            response =>
            {
                ModelMapper.ReplaceResponseModel(response, RequestModel);
                return response;
            });
    }
}
