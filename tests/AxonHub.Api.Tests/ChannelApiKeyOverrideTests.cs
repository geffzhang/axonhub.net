using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the apiKeyOverride seam used by the channel key test flow
/// (channel_llm.go:655-658, 677-693, 716, 1392-1428): the forced key is
/// trimmed once and wins over every stored key, a blank override disappears,
/// and only the channel types whose credential check accepts an override can
/// be built without enabled keys.
/// </summary>
public sealed class ChannelApiKeyOverrideTests
{
    private static ChannelCandidate Candidate(
        string type,
        string credentials,
        string? overrideApiKey = null,
        string? disabledApiKeys = null)
        => new()
        {
            ChannelId = 7801,
            ChannelName = "override-wiring",
            ActualModel = "test-model",
            Type = type,
            BaseUrl = type is ChannelOutboundFactory.CommandcodeChannelType
                or ChannelOutboundFactory.CommandcodeAnthropicChannelType
                ? "https://commandcode.example.com/v1"
                : "http://localhost:11434",
            Credentials = credentials,
            DisabledApiKeys = disabledApiKeys,
            ApiKeyOverride = overrideApiKey,
        };

    private static LlmRequest Request(string? apiFormat = null)
        => new()
        {
            Model = "test-model",
            ApiFormat = apiFormat,
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
        };

    [Theory]
    [InlineData(ChannelOutboundFactory.OllamaChannelType)]
    [InlineData(ChannelOutboundFactory.OllamaAnthropicChannelType)]
    public void AnOverrideKeyBuildsAKeylessOllamaChannel(string type)
    {
        // Port of TestOllamaAnthropicChannel_APIKeyOverrideWithoutStoredKeys:
        // the provider is installed when an override is present even though the
        // channel stores no keys, so the forced key reaches the wire.
        var request = Request();
        var transformer = ChannelOutboundFactory.Create(
            Candidate(type, "{}", overrideApiKey: "override-test-key"),
            request,
            DateTimeOffset.UnixEpoch);

        var httpRequest = transformer.TransformRequest(request);
        var provenance = Assert.IsAssignableFrom<IApiKeyProvenance>(transformer);

        Assert.Equal(AuthTypes.Bearer, httpRequest.Auth!.Type);
        Assert.Equal("override-test-key", httpRequest.Auth.ApiKey);
        Assert.Equal("override-test-key", provenance.SelectedApiKey);

        // No stored keys: the reference writes no channel_api_key_index.
        Assert.Null(provenance.SelectedApiKeyIndex);
    }

    [Theory]
    [InlineData(ChannelOutboundFactory.OllamaChannelType)]
    [InlineData(ChannelOutboundFactory.OllamaAnthropicChannelType)]
    public void AWhitespaceOverrideLeavesAKeylessOllamaChannelUnauthenticated(string type)
    {
        // channel_llm.go:656-658 trims first; an all-whitespace override is the
        // same as no override and must not be sent as an Authorization value.
        var request = Request();
        var transformer = ChannelOutboundFactory.Create(
            Candidate(type, "{}", overrideApiKey: "  \t "),
            request,
            DateTimeOffset.UnixEpoch);

        Assert.Null(transformer.TransformRequest(request).Auth);
    }

    [Theory]
    [InlineData(ChannelOutboundFactory.CommandcodeChannelType)]
    [InlineData(ChannelOutboundFactory.CommandcodeAnthropicChannelType)]
    public void AnOverrideBuildsCommandCodeWhenEveryStoredKeyIsDisabled(string type)
    {
        // channel_llm.go:683-688: Command Code accepts "enabled keys or the
        // key-test override". The disabled stored key stays out of the request
        // and the trimmed override is sent instead.
        var apiFormat = type == ChannelOutboundFactory.CommandcodeAnthropicChannelType
            ? ApiFormats.AnthropicMessage
            : ApiFormats.OpenAIChatCompletion;
        var request = Request(apiFormat);
        var transformer = ChannelOutboundFactory.Create(
            Candidate(
                type,
                """{"apiKey":"stored-key"}""",
                overrideApiKey: "  override-key  ",
                disabledApiKeys: """[{"key":"stored-key"}]"""),
            request,
            DateTimeOffset.UnixEpoch);

        var httpRequest = transformer.TransformRequest(request);

        Assert.Equal(AuthTypes.Bearer, httpRequest.Auth!.Type);
        Assert.Equal("override-key", httpRequest.Auth.ApiKey);
    }

    [Theory]
    [InlineData(ChannelOutboundFactory.CommandcodeChannelType)]
    [InlineData(ChannelOutboundFactory.CommandcodeAnthropicChannelType)]
    public void AWhitespaceOverrideDoesNotSatisfyTheCommandCodeKeyCheck(string type)
    {
        // The credential check compares the trimmed override with "", so a
        // blank forced key cannot stand in for a missing stored key.
        var candidate = Candidate(type, "{}", overrideApiKey: " \t ");

        var error = Assert.Throws<InvalidOperationException>(
            () => ChannelOutboundFactory.Create(candidate, Request(), DateTimeOffset.UnixEpoch));

        Assert.Equal("missing api key for channel " + candidate.ChannelName, error.Message);
    }

    [Fact]
    public void AnOverrideDoesNotSatisfyTheDefaultCredentialCheck()
    {
        // channel_llm.go:691-693: every other channel type requires an enabled
        // stored key before the override is even consulted.
        var candidate = Candidate(
            ChannelOutboundFactory.OpenAiChannelType,
            "{}",
            overrideApiKey: "override-key");

        var error = Assert.Throws<InvalidOperationException>(
            () => ChannelOutboundFactory.Create(candidate, Request(), DateTimeOffset.UnixEpoch));

        Assert.Equal("missing api key for channel " + candidate.ChannelName, error.Message);
    }

    [Fact]
    public void AWhitespaceOverrideFallsBackToTheStoredKey()
    {
        var request = Request();
        var transformer = ChannelOutboundFactory.Create(
            Candidate(
                ChannelOutboundFactory.OpenAiChannelType,
                """{"apiKey":"stored-key"}""",
                overrideApiKey: "   "),
            request,
            DateTimeOffset.UnixEpoch);

        var httpRequest = transformer.TransformRequest(request);

        Assert.Equal(AuthTypes.Bearer, httpRequest.Auth!.Type);
        Assert.Equal("stored-key", httpRequest.Auth.ApiKey);
    }

    [Fact]
    public void TheOverrideAuditIndexFollowsTheStoredKeyList()
    {
        // request.go:414-421: the 1-based index is looked up only in the full
        // stored list; a forced key that is not stored stays null.
        var storedRequest = Request();
        var stored = ChannelOutboundFactory.Create(
            Candidate(
                ChannelOutboundFactory.OpenAiChannelType,
                """{"apiKeys":["sk-a","sk-b"]}""",
                overrideApiKey: "sk-b"),
            storedRequest,
            DateTimeOffset.UnixEpoch);
        stored.TransformRequest(storedRequest);
        var storedProvenance = Assert.IsAssignableFrom<IApiKeyProvenance>(stored);

        Assert.Equal("sk-b", storedProvenance.SelectedApiKey);
        Assert.Equal(2, storedProvenance.SelectedApiKeyIndex);

        var foreignRequest = Request();
        var foreign = ChannelOutboundFactory.Create(
            Candidate(
                ChannelOutboundFactory.OpenAiChannelType,
                """{"apiKeys":["sk-a","sk-b"]}""",
                overrideApiKey: "sk-forced"),
            foreignRequest,
            DateTimeOffset.UnixEpoch);
        foreign.TransformRequest(foreignRequest);
        var foreignProvenance = Assert.IsAssignableFrom<IApiKeyProvenance>(foreign);

        Assert.Equal("sk-forced", foreignProvenance.SelectedApiKey);
        Assert.Null(foreignProvenance.SelectedApiKeyIndex);
    }
}
