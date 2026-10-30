namespace AxonHub.Llm.Transformer.Anthropic;

/// <summary>
/// Port of anthropic.PlatformType (outbound.go:33-49). The wire value is the
/// lowercase provider id used by the reference's channel configuration.
/// </summary>
public enum AnthropicPlatformType
{
    Direct,
    Bedrock,
    Vertex,
    DeepSeek,
    Doubao,
    Moonshot,
    Zhipu,
    Zai,
    LongCat,
    ClaudeCode,
    Ollama,
    CommandCode,
}

/// <summary>Go string values for <see cref="AnthropicPlatformType"/>.</summary>
public static class AnthropicPlatforms
{
    public const string Direct = "direct";
    public const string Bedrock = "bedrock";
    public const string Vertex = "vertex";
    public const string DeepSeek = "deepseek";
    public const string Doubao = "doubao";
    public const string Moonshot = "moonshot";
    public const string Zhipu = "zhipu";
    public const string Zai = "zai";
    public const string LongCat = "longcat";
    public const string ClaudeCode = "claudecode";
    public const string Ollama = "ollama";
    public const string CommandCode = "commandcode";

    /// <summary>Go <c>string(PlatformType)</c>.</summary>
    public static string ToWireValue(AnthropicPlatformType type)
        => type switch
        {
            AnthropicPlatformType.Direct => Direct,
            AnthropicPlatformType.Bedrock => Bedrock,
            AnthropicPlatformType.Vertex => Vertex,
            AnthropicPlatformType.DeepSeek => DeepSeek,
            AnthropicPlatformType.Doubao => Doubao,
            AnthropicPlatformType.Moonshot => Moonshot,
            AnthropicPlatformType.Zhipu => Zhipu,
            AnthropicPlatformType.Zai => Zai,
            AnthropicPlatformType.LongCat => LongCat,
            AnthropicPlatformType.ClaudeCode => ClaudeCode,
            AnthropicPlatformType.Ollama => Ollama,
            AnthropicPlatformType.CommandCode => CommandCode,
            _ => Direct,
        };
}
