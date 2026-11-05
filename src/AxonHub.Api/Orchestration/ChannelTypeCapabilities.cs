namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of the channel-type capability predicates
/// (internal/ent/channel/type.go:29-44). The reference hangs these on the ent
/// <c>channel.Type</c>; the port keeps channel types as strings, so the
/// predicates live next to the outbound factory that owns the constants.
/// </summary>
public static class ChannelTypeCapabilities
{
    /// <summary>
    /// Port of <c>Type.SupportsGoogleNativeTools</c>: only the native Gemini
    /// formats (<c>gemini</c>, <c>gemini_vertex</c>); the OpenAI-compatible
    /// <c>gemini_openai</c> endpoint does not support Google native tools.
    /// </summary>
    public static bool SupportsGoogleNativeTools(string? channelType)
        => channelType is ChannelOutboundFactory.GeminiChannelType
            or ChannelOutboundFactory.GeminiVertexChannelType;

    /// <summary>
    /// Port of <c>Type.SupportsAnthropicNativeTools</c>: direct Anthropic,
    /// Bedrock (<c>anthropic_aws</c>), <c>anthropic_gcp</c>, Claude Code and
    /// DeepSeek's verified Anthropic-compatible endpoint. Other Anthropic-like
    /// channels stay excluded until support is verified.
    /// </summary>
    public static bool SupportsAnthropicNativeTools(string? channelType)
        => channelType is ChannelOutboundFactory.AnthropicChannelType
            or ChannelOutboundFactory.AnthropicAwsChannelType
            or ChannelOutboundFactory.AnthropicGcpChannelType
            or ChannelOutboundFactory.ClaudeCodeChannelType
            or ChannelOutboundFactory.DeepseekAnthropicChannelType;
}
