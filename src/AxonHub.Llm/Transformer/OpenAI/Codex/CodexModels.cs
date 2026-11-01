using System.Text.Json.Serialization;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.OAuth;

namespace AxonHub.Llm.Transformer.OpenAI.Codex;

/// <summary>Port of the anonymous model entry in codex.ParseModelCatalog.</summary>
public sealed class CodexCatalogModel
{
    [JsonPropertyName("slug")]
    public string Slug { get; set; } = string.Empty;

    [JsonPropertyName("visibility")]
    public string Visibility { get; set; } = string.Empty;

    [JsonPropertyName("priority")]
    public int Priority { get; set; }
}

/// <summary>Port of the anonymous catalog envelope in codex.ParseModelCatalog.</summary>
public sealed class CodexModelCatalog
{
    [JsonPropertyName("models")]
    public List<CodexCatalogModel>? Models { get; set; }
}

/// <summary>Port of llm/transformer/openai/codex/models.go.</summary>
public static class CodexModels
{
    /// <summary>Go: <c>codexBaseURL</c>; the trailing <c>#</c> marks the base URL as raw.</summary>
    internal const string CodexBaseUrl = "https://chatgpt.com/backend-api/codex#";

    /// <summary>Port of <c>SupportsModelCatalog</c>: whether the base URL uses the official Codex catalog.</summary>
    public static bool SupportsModelCatalog(string? baseUrl)
    {
        if (string.IsNullOrEmpty(baseUrl) || baseUrl == "https://api.openai.com/v1")
        {
            return true;
        }

        if (!CodexUtils.TryParseGoUrl(baseUrl.TrimEnd('#'), out var parsed))
        {
            return false;
        }

        return parsed.Scheme == "https"
            && parsed.Host == "chatgpt.com"
            && parsed.Path.TrimEnd('/') == "/backend-api/codex";
    }

    /// <summary>
    /// Port of <c>ModelsRequest</c>: the catalog request uses the same OAuth
    /// provider and client version as inference.
    /// </summary>
    public static HttpRequestData ModelsRequest(
        CancellationToken cancellationToken,
        ITokenGetter tokens,
        string? baseUrl)
    {
        var creds = tokens.Get(cancellationToken);

        if (string.IsNullOrEmpty(baseUrl) || baseUrl == "https://api.openai.com/v1")
        {
            baseUrl = CodexBaseUrl;
        }

        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Authorization", "Bearer " + creds.AccessToken);
        HttpClientUtils.HeaderSet(headers, "Accept", "application/json");
        HttpClientUtils.HeaderSet(headers, "Version", CodexConstants.DefaultVersion);
        HttpClientUtils.HeaderSet(headers, "Originator", CodexConstants.AxonHubOriginator);
        HttpClientUtils.HeaderSet(headers, CodexHeaders.BetaFeaturesHeader, CodexConstants.FabricatedBetaFeatures);

        var accountId = CodexUtils.ExtractChatGptAccountIdFromJwt(creds.AccessToken);
        if (accountId.Length > 0)
        {
            HttpClientUtils.HeaderSet(headers, "Chatgpt-Account-Id", accountId);
        }

        return new HttpRequestData
        {
            Method = "GET",
            Url = baseUrl.TrimEnd('/', '#') + "/models?client_version=" + CodexConstants.DefaultVersion,
            Headers = headers,
        };
    }

    /// <summary>
    /// Port of <c>ParseModelCatalog</c>: only picker-visible models, in upstream
    /// priority order (stable, so equal priorities keep their wire order).
    /// </summary>
    public static List<string> ParseModelCatalog(ReadOnlySpan<byte> body)
    {
        // Go unmarshals "null"/an empty object into a zero struct without an
        // error, so both reach the "no visible models" branch.
        var response = LlmJson.Deserialize<CodexModelCatalog>(body) ?? new CodexModelCatalog();
        var models = response.Models ?? [];

        var ordered = models.OrderBy(static model => model.Priority);
        var visible = new List<string>(models.Count);
        foreach (var model in ordered)
        {
            if (model.Visibility == "list" && model.Slug.Trim().Length > 0)
            {
                // The slug is appended untrimmed, exactly as the reference does.
                visible.Add(model.Slug);
            }
        }

        if (visible.Count == 0)
        {
            throw new InvalidOperationException("Codex catalog has no visible models");
        }

        return visible;
    }
}
