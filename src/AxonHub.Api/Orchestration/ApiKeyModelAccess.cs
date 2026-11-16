using AxonHub.Api.Management;
using AxonHub.Llm;
using AxonHub.Llm.Transformer;
using AxonHub.Persistence;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of orchestrator.checkApiKeyModelAccess
/// (internal/server/orchestrator/model_access.go:14-54): the API key's active
/// profile may restrict routing to an exact allow-list of model ids.
/// </summary>
public static class ApiKeyModelAccess
{
    /// <summary>
    /// Rejects an empty model with <c>model not found: request model is empty</c>
    /// and a model outside the active profile's <c>ModelIDs</c> with
    /// <c>model not found: &lt;model&gt;</c>. A nil key, absent profile or empty
    /// allow-list allows everything; matching is ordinal and exact (Go's
    /// slices.Contains).
    /// </summary>
    public static void Check(ApiKeyRecord? apiKey, LlmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Model.Length == 0)
        {
            throw TransformerErrors.InvalidModel("request model is empty");
        }

        if (apiKey is null)
        {
            return;
        }

        var profile = ApiKeyProfilesCodec.ActiveProfile(apiKey.Profiles);
        if (profile is null || profile.ModelIDs is null || profile.ModelIDs.Count == 0)
        {
            return;
        }

        foreach (var allowedModel in profile.ModelIDs)
        {
            if (string.Equals(allowedModel, request.Model, StringComparison.Ordinal))
            {
                return;
            }
        }

        throw TransformerErrors.InvalidModel(request.Model);
    }
}
