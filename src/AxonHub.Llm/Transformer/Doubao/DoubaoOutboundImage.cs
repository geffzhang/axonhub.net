using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Transformer.Doubao;

/// <summary>
/// Port of the image half of the Doubao outbound
/// (llm/transformer/doubao/outbound.go:237-350). Doubao uses the same
/// <c>/images/generations</c> endpoint for both generation and editing and maps
/// the OpenAI quality field onto Ark's <c>guidance_scale</c>.
/// </summary>
public sealed partial class DoubaoOutboundTransformer
{
    /// <summary>Go: <c>buildImageGenerationAPIRequest</c>.</summary>
    internal HttpRequestData BuildImageGenerationApiRequest(LlmRequest llmReq)
    {
        if (llmReq.Image is null)
        {
            throw new InvalidOperationException("image request is required");
        }

        var prompt = llmReq.Image.Prompt ?? string.Empty;
        if (prompt.Length == 0)
        {
            throw new InvalidOperationException("prompt is required for image generation");
        }

        var hasImages = llmReq.Image.Images is { Count: > 0 };

        List<string>? images = null;
        if (hasImages)
        {
            images = new List<string>(llmReq.Image.Images!.Count);
            foreach (var data in llmReq.Image.Images)
            {
                images.Add(OpenAiOutboundTransformer.EncodeImageBytesToDataUrl(data));
            }
        }

        // Go marshals a map[string]any, so GoJson's sorted-map converter emits
        // the keys alphabetically, exactly like encoding/json.
        var reqBody = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = llmReq.Model,
            ["prompt"] = prompt,
            ["response_format"] = "b64_json",
            ["stream"] = false,
        };

        // Add images if present (for editing).
        if (hasImages)
        {
            reqBody["image"] = images!.Count == 1 ? images[0] : images;
        }

        if (llmReq.Image.N is { } n)
        {
            reqBody["n"] = n;
        }

        if (llmReq.Image.Size is { Length: > 0 } size)
        {
            reqBody["size"] = size;
        }

        switch (llmReq.Image.Quality)
        {
            case "hd":
                reqBody["guidance_scale"] = 7.5;
                break;
            case "standard":
                reqBody["guidance_scale"] = 2.5;
                break;
        }

        if (llmReq.Image.ResponseFormat is { Length: > 0 } responseFormat)
        {
            reqBody["response_format"] = responseFormat;
        }

        if (llmReq.Image.User is { Length: > 0 } user)
        {
            reqBody["user"] = user;
        }

        var body = GoJson.Marshal(reqBody);

        var apiKey = _config.ApiKeyProvider!.Get();
        _selectedApiKey = apiKey;

        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Content-Type", "application/json");
        HttpClientUtils.HeaderSet(headers, "Accept", "application/json");

        var request = new HttpRequestData
        {
            Method = "POST",
            Url = _baseUrl + "/images/generations",
            ContentType = "application/json",
            Headers = headers,
            Body = body,
            Auth = new AuthConfig
            {
                Type = AuthTypes.Bearer,
                ApiKey = apiKey,
            },
            RequestType = RequestTypes.Image,
            ApiFormat = ApiFormats.OpenAIImageGeneration,
        };

        // Save model to TransformerMetadata for response transformation.
        request.TransformerMetadata ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        request.TransformerMetadata["model"] = JsonSerializer.SerializeToElement(llmReq.Model);

        return request;
    }
}
