using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Api.GraphQL;
using AxonHub.Api.Management;
using AxonHub.Api.Safeguards;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer.Shared;
using AxonHub.Persistence;

namespace AxonHub.Api.Settings;

/// <summary>
/// Server-side defaults for the Claude Code safeguards, i.e. the
/// <c>server.safeguards</c> block of the reference configuration
/// (internal/server/config.go:99; conf/conf.go:257-264). Durations are Go
/// duration nanoseconds: the reference keeps <c>time.Duration</c> values and
/// converts them back with <c>Duration.Seconds()</c>, while a .NET
/// <see cref="TimeSpan"/> would round to 100ns ticks and change the emitted
/// <c>timeoutSeconds</c>. Configuration-file binding is covered by C027; the
/// defaults here are the reference's conf defaults.
/// </summary>
public sealed class ClaudeCodeSafeguardsServerDefaults
{
    public string ClassifierModel { get; init; } = string.Empty;

    public string Policy { get; init; } = string.Empty;

    public IReadOnlyList<string> FallbackModels { get; init; } = [];

    public bool EscalateOnDenial { get; init; }

    public long AttemptTimeoutNanoseconds { get; init; }

    public long TimeoutNanoseconds { get; init; } = SafeguardsDefaults.DefaultTimeoutNanoseconds;

    public int MaxTokens { get; init; } = SafeguardsDefaults.MaxTokens;

    public int TranscriptCharBudget { get; init; } = SafeguardsDefaults.TranscriptCharBudget;
}

/// <summary>
/// Port of biz.ClaudeCodeSafeguardsConfig (system_claude_code.go:18-27): the
/// settings boundary uses seconds, matching the Go struct's float64 fields.
/// </summary>
public sealed record ClaudeCodeSafeguardsConfig
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
/// Port of the settings-facing subset of safeguards.Config
/// (evaluator.go:69-84): the validated runtime shape, with Go durations kept as
/// nanoseconds so the seconds round-trip matches <c>time.Duration.Seconds()</c>.
/// </summary>
public sealed record ClaudeCodeSafeguardsRuntimeConfig
{
    public string ClassifierModel { get; init; } = string.Empty;

    public string Policy { get; init; } = string.Empty;

    public IReadOnlyList<string> FallbackModels { get; init; } = [];

    public bool EscalateOnDenial { get; init; }

    public long AttemptTimeoutNanoseconds { get; init; }

    public long TimeoutNanoseconds { get; init; }

    public int MaxTokens { get; init; }

    public int TranscriptCharBudget { get; init; }
}

/// <summary>Port of biz.ClaudeCodeSafeguardsSettings (system_claude_code.go:29-33).</summary>
public sealed class ClaudeCodeSafeguardsSettings
{
    public ClaudeCodeSafeguardsConfig Config { get; init; } = new();

    public bool UsingServerDefaults { get; init; } = true;

    public string DefaultPolicy { get; init; } = SafeguardsDefaults.DefaultPolicy;
}

/// <summary>
/// Reads and writes the <c>claude_code_safeguards</c> system setting
/// (internal/server/biz/system_claude_code.go). The runtime accessor is
/// unscoped, like the reference's <c>authz.RunWithSystemBypass</c>; the
/// management surface requires read_settings / write_settings through
/// <c>authz.RequireScope</c>.
/// </summary>
public interface IClaudeCodeSafeguardsSettingsService
{
    /// <summary>Port of SystemService.ClaudeCodeSafeguardsSettings.</summary>
    Task<ClaudeCodeSafeguardsSettings> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Port of SystemService.ClaudeCodeSafeguardsConfig.</summary>
    Task<ClaudeCodeSafeguardsRuntimeConfig> GetRuntimeConfigAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Port of SystemService.SetClaudeCodeSafeguardsSettings.</summary>
    Task UpdateAsync(
        ClaudeCodeSafeguardsConfig input,
        CancellationToken cancellationToken = default);

    /// <summary>Port of SystemService.ResetClaudeCodeSafeguardsSettings.</summary>
    Task ResetAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Store-backed implementation. The reference reads the row directly instead of
/// through the system cache so an older in-flight cache fill (or another
/// process's local cache) cannot restore a stale reviewer policy; the port's
/// Dapper read is always direct (C029), so the intent is preserved.
/// </summary>
public sealed class ClaudeCodeSafeguardsSettingsService(
    ISystemStore systems,
    ClaudeCodeSafeguardsServerDefaults serverDefaults,
    AuthorizationScope scope)
    : IClaudeCodeSafeguardsSettingsService
{
    /// <summary>biz.SystemKeyClaudeCodeSafeguards.</summary>
    public const string SystemKey = "claude_code_safeguards";

    public async Task<ClaudeCodeSafeguardsSettings> GetAsync(
        CancellationToken cancellationToken = default)
    {
        await scope.RequireScopeAsync(AuthorizationScope.ReadSettings, cancellationToken)
            .ConfigureAwait(false);
        return await ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ClaudeCodeSafeguardsRuntimeConfig> GetRuntimeConfigAsync(
        CancellationToken cancellationToken = default)
    {
        // The reviewer model calls run under the caller's model access and
        // quota, but the config itself is server-owned: the reference reads it
        // through authz.RunWithSystemBypass, and the port's systems table has no
        // principal-scoped read to bypass.
        var settings = await ReadAsync(cancellationToken).ConfigureAwait(false);
        return ClaudeCodeSafeguardsCodec.ToRuntimeConfig(settings.Config);
    }

    public async Task UpdateAsync(
        ClaudeCodeSafeguardsConfig input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        await scope.RequireScopeAsync(AuthorizationScope.WriteSettings, cancellationToken)
            .ConfigureAwait(false);

        var runtime = ClaudeCodeSafeguardsCodec.ToRuntimeConfig(input);
        var normalized = ClaudeCodeSafeguardsCodec.FromRuntimeConfig(runtime);

        // json.Marshal(claudeCodeConfig(config)): declaration order, trimmed
        // reviewer names and duration seconds.
        var json = GoJson.MarshalToString(ClaudeCodeSafeguardsCodec.ToStored(normalized));

        try
        {
            await systems.SetAsync(SystemKey, json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // setSystemValue's own prefix is the only wrapper here.
            throw new InvalidOperationException(
                "failed to create system setting: " + ex.Message,
                ex);
        }
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await scope.RequireScopeAsync(AuthorizationScope.WriteSettings, cancellationToken)
            .ConfigureAwait(false);

        // A null override keeps reset on the standard atomic upsert path; the
        // settings read treats the literal "null" as "use the server defaults".
        try
        {
            await systems.SetAsync(SystemKey, "null", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to create system setting: " + ex.Message,
                ex);
        }
    }

    /// <summary>Port of SystemService.claudeCodeSafeguardsSettings.</summary>
    private async Task<ClaudeCodeSafeguardsSettings> ReadAsync(
        CancellationToken cancellationToken)
    {
        SystemRecord? row;
        try
        {
            row = await systems.GetByKeyAsync(SystemKey, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // fmt.Errorf("read Claude Code safeguards settings: %w", err)
            throw new InvalidOperationException(
                "read Claude Code safeguards settings: " + ex.Message,
                ex);
        }

        // Zero-valued server settings fall back to the package defaults before
        // they are exposed; a stored override is always validated.
        var timeoutNanoseconds = serverDefaults.TimeoutNanoseconds == 0
            ? SafeguardsDefaults.DefaultTimeoutNanoseconds
            : serverDefaults.TimeoutNanoseconds;
        var maxTokens = serverDefaults.MaxTokens == 0
            ? SafeguardsDefaults.MaxTokens
            : serverDefaults.MaxTokens;
        var transcriptCharBudget = serverDefaults.TranscriptCharBudget == 0
            ? SafeguardsDefaults.TranscriptCharBudget
            : serverDefaults.TranscriptCharBudget;

        var defaults = new ClaudeCodeSafeguardsConfig
        {
            ClassifierModel = serverDefaults.ClassifierModel,
            Policy = serverDefaults.Policy,
            FallbackModels = [.. serverDefaults.FallbackModels],
            EscalateOnDenial = serverDefaults.EscalateOnDenial,
            AttemptTimeoutSeconds =
                ClaudeCodeSafeguardsCodec.FromNanoseconds(serverDefaults.AttemptTimeoutNanoseconds),
            TimeoutSeconds = ClaudeCodeSafeguardsCodec.FromNanoseconds(timeoutNanoseconds),
            MaxTokens = maxTokens,
            TranscriptCharBudget = transcriptCharBudget,
        };

        var result = new ClaudeCodeSafeguardsSettings
        {
            Config = defaults,
            UsingServerDefaults = true,
            DefaultPolicy = SafeguardsDefaults.DefaultPolicy,
        };

        if (row is null || row.Value == "null")
        {
            return result;
        }

        ClaudeCodeSafeguardsStoredConfig? stored;
        try
        {
            stored = LlmJson.Deserialize<ClaudeCodeSafeguardsStoredConfig>(row.Value);
        }
        catch (JsonException ex)
        {
            // fmt.Errorf("decode Claude Code safeguards settings: %w", err)
            throw new InvalidOperationException(
                "decode Claude Code safeguards settings: " + ex.Message,
                ex);
        }

        var runtime = ClaudeCodeSafeguardsCodec.ToRuntimeConfig(
            ClaudeCodeSafeguardsCodec.FromStored(
                stored ?? new ClaudeCodeSafeguardsStoredConfig()));

        return new ClaudeCodeSafeguardsSettings
        {
            Config = ClaudeCodeSafeguardsCodec.FromRuntimeConfig(runtime),
            UsingServerDefaults = false,
            DefaultPolicy = SafeguardsDefaults.DefaultPolicy,
        };
    }
}

/// <summary>
/// JSON shape of the stored <c>claude_code_safeguards</c> value
/// (biz.ClaudeCodeSafeguardsConfig; system_claude_code.go:18-27). Property
/// order is the Go struct order and matters because the value is written with
/// <see cref="GoJson"/>; missing fields decode to the Go zero values.
/// </summary>
public sealed class ClaudeCodeSafeguardsStoredConfig
{
    [JsonPropertyName("classifierModel")]
    public string? ClassifierModel { get; set; }

    [JsonPropertyName("policy")]
    public string? Policy { get; set; }

    [JsonPropertyName("fallbackModels")]
    public IReadOnlyList<string>? FallbackModels { get; set; }

    [JsonPropertyName("escalateOnDenial")]
    public bool EscalateOnDenial { get; set; }

    [JsonPropertyName("attemptTimeoutSeconds")]
    public double AttemptTimeoutSeconds { get; set; }

    [JsonPropertyName("timeoutSeconds")]
    public double TimeoutSeconds { get; set; }

    [JsonPropertyName("maxTokens")]
    public int MaxTokens { get; set; }

    [JsonPropertyName("transcriptCharBudget")]
    public int TranscriptCharBudget { get; set; }
}

/// <summary>
/// Conversions between the settings boundary, the Go-duration runtime shape and
/// the stored JSON (system_claude_code.go:36-98).
/// </summary>
public static class ClaudeCodeSafeguardsCodec
{
    /// <summary>Port of claudeCodeConfig(c safeguards.Config).</summary>
    public static ClaudeCodeSafeguardsConfig FromRuntimeConfig(
        ClaudeCodeSafeguardsRuntimeConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return new ClaudeCodeSafeguardsConfig
        {
            ClassifierModel = config.ClassifierModel,
            Policy = config.Policy,
            FallbackModels = [.. config.FallbackModels],
            EscalateOnDenial = config.EscalateOnDenial,
            AttemptTimeoutSeconds = FromNanoseconds(config.AttemptTimeoutNanoseconds),
            TimeoutSeconds = FromNanoseconds(config.TimeoutNanoseconds),
            MaxTokens = config.MaxTokens,
            TranscriptCharBudget = config.TranscriptCharBudget,
        };
    }

    /// <summary>
    /// Port of ClaudeCodeSafeguardsConfig.runtimeConfig
    /// (system_claude_code.go:48-86), including the validation order and the
    /// xerrors.ValidationError messages.
    /// </summary>
    /// <exception cref="CodedException">
    /// VALIDATION_FAILED with the reference message.
    /// </exception>
    public static ClaudeCodeSafeguardsRuntimeConfig ToRuntimeConfig(
        ClaudeCodeSafeguardsConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (!double.IsFinite(config.TimeoutSeconds)
            || config.TimeoutSeconds <= 0
            || config.TimeoutSeconds > 120)
        {
            throw Validation(
                "overall timeout must be greater than zero and at most 120 seconds");
        }

        if (!double.IsFinite(config.AttemptTimeoutSeconds)
            || config.AttemptTimeoutSeconds < 0
            || config.AttemptTimeoutSeconds > 120)
        {
            throw Validation("attempt timeout must be between zero and 120 seconds");
        }

        if (config.MaxTokens < 1 || config.MaxTokens > 16384)
        {
            throw Validation("max tokens must be between 1 and 16384");
        }

        if (config.TranscriptCharBudget < 1 || config.TranscriptCharBudget > 1000000)
        {
            throw Validation("transcript budget must be between 1 and 1000000 UTF-8 bytes");
        }

        var model = GoStrings.TrimSpace(config.ClassifierModel ?? string.Empty);
        var fallback = new List<string>(config.FallbackModels.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal) { model };
        foreach (var raw in config.FallbackModels)
        {
            var name = GoStrings.TrimSpace(raw ?? string.Empty);
            if (name.Length == 0 || !seen.Add(name))
            {
                throw Validation(
                    "fallback models must be nonempty, distinct and different from "
                    + "the primary model");
            }

            fallback.Add(name);
        }

        // time.Duration(c.TimeoutSeconds * float64(time.Second)) truncates
        // toward zero; a positive value below 1ns becomes a zero duration.
        var timeout = ToNanoseconds(config.TimeoutSeconds);
        if (timeout <= 0)
        {
            throw Validation("overall timeout must be at least one nanosecond");
        }

        return new ClaudeCodeSafeguardsRuntimeConfig
        {
            ClassifierModel = model,
            Policy = config.Policy ?? string.Empty,
            FallbackModels = fallback,
            EscalateOnDenial = config.EscalateOnDenial,
            AttemptTimeoutNanoseconds = ToNanoseconds(config.AttemptTimeoutSeconds),
            TimeoutNanoseconds = timeout,
            MaxTokens = config.MaxTokens,
            TranscriptCharBudget = config.TranscriptCharBudget,
        };
    }

    /// <summary>Maps the decoded stored value back to the boundary shape.</summary>
    public static ClaudeCodeSafeguardsConfig FromStored(
        ClaudeCodeSafeguardsStoredConfig stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        return new ClaudeCodeSafeguardsConfig
        {
            ClassifierModel = stored.ClassifierModel ?? string.Empty,
            Policy = stored.Policy ?? string.Empty,
            FallbackModels = stored.FallbackModels is null ? [] : [.. stored.FallbackModels],
            EscalateOnDenial = stored.EscalateOnDenial,
            AttemptTimeoutSeconds = stored.AttemptTimeoutSeconds,
            TimeoutSeconds = stored.TimeoutSeconds,
            MaxTokens = stored.MaxTokens,
            TranscriptCharBudget = stored.TranscriptCharBudget,
        };
    }

    /// <summary>Builds the Go-shaped JSON value for the boundary config.</summary>
    public static ClaudeCodeSafeguardsStoredConfig ToStored(ClaudeCodeSafeguardsConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return new ClaudeCodeSafeguardsStoredConfig
        {
            ClassifierModel = config.ClassifierModel,
            Policy = config.Policy,
            FallbackModels = [.. config.FallbackModels],
            EscalateOnDenial = config.EscalateOnDenial,
            AttemptTimeoutSeconds = config.AttemptTimeoutSeconds,
            TimeoutSeconds = config.TimeoutSeconds,
            MaxTokens = config.MaxTokens,
            TranscriptCharBudget = config.TranscriptCharBudget,
        };
    }

    /// <summary>time.Duration(seconds * float64(time.Second)).</summary>
    public static long ToNanoseconds(double seconds) => (long)(seconds * 1_000_000_000d);

    /// <summary>time.Duration.Seconds().</summary>
    public static double FromNanoseconds(long nanoseconds)
        => nanoseconds / 1_000_000_000d;

    private static CodedException Validation(string message)
        => new(CodedException.ValidationFailed, message);
}
