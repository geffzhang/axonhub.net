using Orleans;

namespace AxonHub.Grains.Abstractions;

/// <summary>
/// A channel eligible for one requested model, after load balancing. Mirrors the
/// reference <c>ChannelModelsCandidate</c> fields the orchestrator needs before
/// transforming the request.
/// </summary>
[GenerateSerializer]
public sealed class ChannelCandidate
{
    [Id(0)]
    public long ChannelId { get; set; }

    [Id(1)]
    public string ChannelName { get; set; } = string.Empty;

    /// <summary>The upstream model the channel must actually request.</summary>
    [Id(2)]
    public string ActualModel { get; set; } = string.Empty;

    [Id(3)]
    public long OrderingWeight { get; set; }

    [Id(4)]
    public double Score { get; set; }

    /// <summary>Ent channel type, e.g. <c>openai</c> (candidate.Channel.Type).</summary>
    [Id(5)]
    public string Type { get; set; } = string.Empty;

    [Id(6)]
    public string? BaseUrl { get; set; }

    /// <summary>Raw <c>credentials</c> jsonb text.</summary>
    [Id(7)]
    public string Credentials { get; set; } = "{}";

    /// <summary>Raw <c>disabled_api_keys</c> jsonb text.</summary>
    [Id(8)]
    public string? DisabledApiKeys { get; set; }

    /// <summary>Channel-level extra retry rules (settings.retryable*).</summary>
    [Id(9)]
    public ChannelRetrySettings RetrySettings { get; set; } = new();

    /// <summary>Channel-level request rewrite rules (settings.*Override*).</summary>
    [Id(10)]
    public ChannelOverrideSettings OverrideSettings { get; set; } = new();

    /// <summary>
    /// Channel-level rate limits (settings.rateLimit), or <see langword="null"/>
    /// when the channel configures none.
    /// </summary>
    [Id(11)]
    public ChannelRateLimitSettings? RateLimitSettings { get; set; }

    /// <summary>Channel-level pass-through overrides (settings.passThrough*).</summary>
    [Id(12)]
    public ChannelPassThroughSettings PassThroughSettings { get; set; } = new();

    /// <summary>
    /// The channel's <c>default_test_model</c>, already resolved through its
    /// model entries exactly like <c>biz.buildCodexOutbound</c> (trimmed, then
    /// mapped by ChooseModel when the channel supports it). Codex channels use
    /// it as the main model of the image-generation tool.
    /// </summary>
    [Id(13)]
    public string DefaultTestModel { get; set; } = string.Empty;

    /// <summary>
    /// The outbound API format selected from the channel's resolved endpoints
    /// (<c>ChannelModelsCandidate.APIFormat</c>); null for pool-selected
    /// candidates, where the factory dispatches on the request format.
    /// </summary>
    [Id(14)]
    public string? ApiFormat { get; set; }

    /// <summary>
    /// Forces every attempt to authenticate with this key instead of selecting
    /// from the channel's enabled keys (the reference's
    /// <c>buildChannelWithTransformer(apiKeyOverride)</c> used by the channel
    /// key test flow).
    /// </summary>
    [Id(15)]
    public string? ApiKeyOverride { get; set; }

    /// <summary>
    /// The ordered upstream models this candidate may try (the reference's
    /// <c>ChannelModelsCandidate.Models</c>). A candidate assembled by
    /// <c>DefaultSelector</c> carries one entry per association connection; the
    /// persistent outbound transformer advances through the list on same-channel
    /// retries. Directly constructed candidates may leave this empty, in which
    /// case <see cref="ActualModel"/> is the single entry.
    /// </summary>
    [Id(16)]
    public List<ChannelCandidateModel> Models { get; set; } = [];

    /// <summary>
    /// Outbound API format per entry in <see cref="Models"/> (the reference's
    /// <c>modelAPIFormats</c>). Null when the candidate was built without a
    /// per-model protocol table; the transformer then falls back to
    /// <see cref="ApiFormat"/> or recomputes it from the channel endpoints.
    /// </summary>
    [Id(17)]
    public List<string>? ModelApiFormats { get; set; }

    /// <summary>Association priority; lower values are tried first.</summary>
    [Id(18)]
    public int Priority { get; set; }

    /// <summary>
    /// The model card's output limit (the reference's
    /// <c>DefaultMaxTokens</c>), applied only when the outbound request is an
    /// Anthropic message and the client set no max tokens.
    /// </summary>
    [Id(19)]
    public long DefaultMaxTokens { get; set; }

    /// <summary>
    /// Raw <c>channels.settings</c> jsonb text. The transformer re-reads the
    /// per-model protocol overrides from it when recomputing a candidate's
    /// format for a model that has no table entry.
    /// </summary>
    [Id(20)]
    public string? Settings { get; set; }

    /// <summary>
    /// Raw <c>channels.endpoints</c> jsonb text, used together with
    /// <see cref="Type"/> for the same format recomputation.
    /// </summary>
    [Id(21)]
    public string? Endpoints { get; set; }

    /// <summary>
    /// True when trace sticky routing pinned this candidate ahead of the
    /// load-balanced order (the reference's <c>ChannelModelsCandidate.TraceSticky</c>).
    /// Nothing downstream branches on it; it is carried for parity with the
    /// selector's output.
    /// </summary>
    [Id(22)]
    public bool TraceSticky { get; set; }

    /// <summary>
    /// Raw <c>tags</c> jsonb text ([]string). The project / API key profile
    /// filters read it exactly like the reference's <c>candidate.Channel.Tags</c>.
    /// </summary>
    [Id(23)]
    public string? Tags { get; set; }

    /// <summary>
    /// Raw <c>policies</c> jsonb text (objects.ChannelPolicies). The stream
    /// policy selector reads the reference's <c>candidate.Channel.Policies.Stream</c>
    /// from it; null / empty means unlimited.
    /// </summary>
    [Id(24)]
    public string? Policies { get; set; }
}

/// <summary>
/// One request-model → upstream-model entry inside a candidate (the reference's
/// <c>biz.ChannelModelEntry</c> as carried by <c>ChannelModelsCandidate.Models</c>).
/// </summary>
[GenerateSerializer]
public sealed class ChannelCandidateModel
{
    [Id(0)]
    public string RequestModel { get; set; } = string.Empty;

    [Id(1)]
    public string ActualModel { get; set; } = string.Empty;
}
