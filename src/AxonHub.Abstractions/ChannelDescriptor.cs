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

    /// <summary>
    /// Ent <c>created_at</c> (timestamptz). The models API exposes it as the
    /// facade's <c>created</c> / <c>created_at</c>, exactly like
    /// <c>biz.Channel.CreatedAt</c>. Values from PostgreSQL are UTC; an
    /// <see cref="DateTimeKind.Unspecified"/> value is treated as UTC.
    /// </summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>
    /// Ent <c>updated_at</c> (timestamptz). The reference's TokenProvider keeps
    /// a channel snapshot and persists refreshed OAuth credentials with an
    /// optimistic <c>WHERE updated_at = snapshot.updated_at</c> predicate
    /// (channel_llm.go:1670); the candidate carries the cached value so the
    /// token grain can order a stale cache entry against the row it last saw.
    /// </summary>
    public DateTime UpdatedAt { get; init; }

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

    /// <summary>
    /// Raw <c>endpoints</c> jsonb text ([]objects.ChannelEndpoint). Resolved
    /// against the built-in defaults by <c>ChannelEndpoints.Resolve</c>.
    /// </summary>
    public string? Endpoints { get; init; }

    /// <summary>
    /// Ent <c>default_test_model</c>. Codex channels use it as the main model of
    /// the image-generation tool (biz.buildCodexOutbound).
    /// </summary>
    public string DefaultTestModel { get; init; } = string.Empty;

    /// <summary>
    /// Raw <c>tags</c> jsonb text ([]string). The model association matcher
    /// reads them for the channel-tags rules and for exclude rules.
    /// </summary>
    public string? Tags { get; init; }

    /// <summary>
    /// Raw <c>policies</c> jsonb text (objects.ChannelPolicies). The stream
    /// policy selector reads <c>policies.stream</c> (unlimited / require /
    /// forbid) to filter candidates for streaming and non-streaming requests.
    /// </summary>
    public string? Policies { get; init; }
}
