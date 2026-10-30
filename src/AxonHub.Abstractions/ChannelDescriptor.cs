namespace AxonHub.Abstractions;

/// <summary>
/// Minimal view of a live channel needed to score it for selection and to build
/// its outbound transformer. Mirrors the reference <c>biz.Channel</c> fields the
/// first vertical slice (OpenAI chat) reads: identity, ordering weight, model
/// entries, settings and credentials.
/// </summary>
public sealed record ChannelDescriptor
{
    public required long Id { get; init; }

    public required string Name { get; init; }

    public required long OrderingWeight { get; init; }

    /// <summary>Ent channel type, e.g. <c>openai</c>.</summary>
    public string Type { get; init; } = string.Empty;

    public string? BaseUrl { get; init; }

    /// <summary>Raw <c>credentials</c> jsonb text (objects.ChannelCredentials).</summary>
    public string Credentials { get; init; } = "{}";

    /// <summary>Raw <c>disabled_api_keys</c> jsonb text ([]objects.DisabledAPIKey).</summary>
    public string? DisabledApiKeys { get; init; }

    /// <summary>
    /// Raw <c>supported_models</c> jsonb text, exactly as stored by the reference.
    /// </summary>
    public string SupportedModels { get; init; } = "null";

    /// <summary>
    /// Raw <c>settings</c> jsonb text, exactly as stored by the reference.
    /// </summary>
    public string? Settings { get; init; }
}
