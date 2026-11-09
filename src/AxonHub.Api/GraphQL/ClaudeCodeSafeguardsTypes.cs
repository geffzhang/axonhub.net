using AxonHub.Api.Settings;
using HotChocolate;

namespace AxonHub.Api.GraphQL;

/// <summary>
/// Port of the <c>ClaudeCodeSafeguardsConfig</c> type
/// (system.graphql:542-551). Seconds are floats at the settings boundary,
/// exactly like the Go struct.
/// </summary>
[GraphQLName("ClaudeCodeSafeguardsConfig")]
public sealed class ClaudeCodeSafeguardsConfigType
{
    public string ClassifierModel { get; init; } = string.Empty;

    public string Policy { get; init; } = string.Empty;

    public IReadOnlyList<string> FallbackModels { get; init; } = [];

    public bool EscalateOnDenial { get; init; }

    public double AttemptTimeoutSeconds { get; init; }

    public double TimeoutSeconds { get; init; }

    public int MaxTokens { get; init; }

    public int TranscriptCharBudget { get; init; }
}

/// <summary>
/// Port of the <c>UpdateClaudeCodeSafeguardsInput</c> input
/// (system.graphql:553-562). Every field is non-null, matching the SDL.
/// </summary>
public sealed class UpdateClaudeCodeSafeguardsInput
{
    public string ClassifierModel { get; set; } = string.Empty;

    public string Policy { get; set; } = string.Empty;

    public IReadOnlyList<string> FallbackModels { get; set; } = [];

    public bool EscalateOnDenial { get; set; }

    public double AttemptTimeoutSeconds { get; set; }

    public double TimeoutSeconds { get; set; }

    public int MaxTokens { get; set; }

    public int TranscriptCharBudget { get; set; }
}

/// <summary>
/// Port of the <c>ClaudeCodeSafeguardsSettings</c> type
/// (system.graphql:563-567).
/// </summary>
[GraphQLName("ClaudeCodeSafeguardsSettings")]
public sealed class ClaudeCodeSafeguardsSettingsType
{
    public ClaudeCodeSafeguardsConfigType Config { get; init; } = new();

    public bool UsingServerDefaults { get; init; }

    public string DefaultPolicy { get; init; } = string.Empty;
}

/// <summary>
/// Maps the safeguards settings between the biz-shaped domain and the GraphQL
/// types (system.resolvers.go:804-807 and 445-455 pass values through
/// unchanged).
/// </summary>
public static class ClaudeCodeSafeguardsMapping
{
    public static ClaudeCodeSafeguardsSettingsType ToGraphQL(
        ClaudeCodeSafeguardsSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new ClaudeCodeSafeguardsSettingsType
        {
            Config = ToGraphQL(settings.Config),
            UsingServerDefaults = settings.UsingServerDefaults,
            DefaultPolicy = settings.DefaultPolicy,
        };
    }

    public static ClaudeCodeSafeguardsConfigType ToGraphQL(
        ClaudeCodeSafeguardsConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return new ClaudeCodeSafeguardsConfigType
        {
            ClassifierModel = config.ClassifierModel,
            Policy = config.Policy,
            FallbackModels = config.FallbackModels,
            EscalateOnDenial = config.EscalateOnDenial,
            AttemptTimeoutSeconds = config.AttemptTimeoutSeconds,
            TimeoutSeconds = config.TimeoutSeconds,
            MaxTokens = config.MaxTokens,
            TranscriptCharBudget = config.TranscriptCharBudget,
        };
    }

    /// <summary>
    /// gqlgen decodes the input straight into biz.ClaudeCodeSafeguardsConfig;
    /// the list is copied so later mutations of the input object cannot leak
    /// into the service call.
    /// </summary>
    public static ClaudeCodeSafeguardsConfig ToDomain(
        UpdateClaudeCodeSafeguardsInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new ClaudeCodeSafeguardsConfig
        {
            ClassifierModel = input.ClassifierModel,
            Policy = input.Policy,
            FallbackModels = [.. input.FallbackModels],
            EscalateOnDenial = input.EscalateOnDenial,
            AttemptTimeoutSeconds = input.AttemptTimeoutSeconds,
            TimeoutSeconds = input.TimeoutSeconds,
            MaxTokens = input.MaxTokens,
            TranscriptCharBudget = input.TranscriptCharBudget,
        };
    }
}
