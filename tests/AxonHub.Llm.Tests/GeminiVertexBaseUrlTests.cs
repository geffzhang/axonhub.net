using AxonHub.Llm;
using AxonHub.Llm.Transformer.Gemini;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Port of gemini.resolveBaseURL (outbound.go:198-209) and the Vertex
/// credential-bound base URL tests (vertex_base_url_test.go): the provider is
/// consulted after the API key provider selects the credential, and an empty
/// result falls back to the configured Base URL.
/// </summary>
public sealed class GeminiVertexBaseUrlTests
{
    private static LlmRequest Request()
        => new()
        {
            Model = "gemini-2.5-flash",
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
        };

    [Fact]
    public void SelectedCredentialBaseUrlOverridesTheConfiguredUrl()
    {
        var transformer = new GeminiOutboundTransformer(new GeminiOutboundConfig
        {
            BaseUrl = "https://aiplatform.googleapis.com/v1",
            ApiKeyProvider = new StaticApiKeyProvider("vertex-key-alpha"),
            BaseUrlProvider = () =>
                "https://aiplatform.googleapis.com/v1/projects/project-alpha/locations/us-central1",
            PlatformType = GeminiConstants.PlatformVertex,
        });

        var httpRequest = transformer.TransformRequest(Request());

        Assert.Equal(
            "https://aiplatform.googleapis.com/v1/projects/project-alpha/locations/us-central1"
            + "/publishers/google/models/gemini-2.5-flash:generateContent",
            httpRequest.Url);
    }

    [Fact]
    public void EmptyProviderFallsBackToTheConfiguredUrl()
    {
        var transformer = new GeminiOutboundTransformer(new GeminiOutboundConfig
        {
            BaseUrl = "https://aiplatform.googleapis.com",
            ApiKeyProvider = new StaticApiKeyProvider("vertex-key"),
            BaseUrlProvider = () => string.Empty,
            PlatformType = GeminiConstants.PlatformVertex,
        });

        var httpRequest = transformer.TransformRequest(Request());

        Assert.Equal(
            "https://aiplatform.googleapis.com/v1/publishers/google/models/gemini-2.5-flash:generateContent",
            httpRequest.Url);
    }
}
