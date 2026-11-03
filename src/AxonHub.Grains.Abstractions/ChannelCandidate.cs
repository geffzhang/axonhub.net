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
}
