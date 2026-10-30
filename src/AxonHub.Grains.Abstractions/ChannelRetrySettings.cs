using System.Text.Json;
using System.Text.RegularExpressions;
using Orleans;

namespace AxonHub.Grains.Abstractions;

/// <summary>
/// Retry-related half of the reference <c>objects.ChannelSettings</c>
/// (internal/objects/channel.go:231-240,297-300): extra HTTP status codes and
/// error text matchers that make an otherwise non-retryable failure retryable.
/// </summary>
[GenerateSerializer]
public sealed class ChannelRetrySettings
{
    private static readonly JsonSerializerOptions SettingsJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Additional retryable status codes; 429 and 5xx are always retryable.</summary>
    [Id(0)]
    public int[] RetryableStatusCodes { get; set; } = [];

    /// <summary>Additional error text matchers.</summary>
    [Id(1)]
    public ChannelRetryableErrorPattern[] RetryableErrorPatterns { get; set; } = [];

    /// <summary>
    /// Reads the retry fields from a stored <c>settings</c> jsonb value. The
    /// reference validates them when the channel is written; this build has no
    /// channel write API yet, so malformed values degrade to no extra rules.
    /// </summary>
    public static ChannelRetrySettings Parse(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return new ChannelRetrySettings();
        }

        try
        {
            return JsonSerializer.Deserialize<ChannelRetrySettings>(settingsJson, SettingsJsonOptions)
                ?? new ChannelRetrySettings();
        }
        catch (JsonException)
        {
            return new ChannelRetrySettings();
        }
    }

    /// <summary>
    /// Port of the channel-settings branch of orchestrator.isRetryableErrorForChannel:
    /// a configured status code or error text matcher makes the error retryable.
    /// </summary>
    public bool Matches(int statusCode, string message)
    {
        if (Array.IndexOf(RetryableStatusCodes, statusCode) >= 0)
        {
            return true;
        }

        if (string.IsNullOrEmpty(message))
        {
            return false;
        }

        foreach (var pattern in RetryableErrorPatterns)
        {
            if (pattern is null || string.IsNullOrEmpty(pattern.Pattern))
            {
                continue;
            }

            if (pattern.Regex)
            {
                // Go uses RE2; NonBacktracking rejects the same constructs
                // (lookaround, backreferences) and runs in linear time.
                try
                {
                    if (Regex.IsMatch(message, pattern.Pattern, RegexOptions.NonBacktracking))
                    {
                        return true;
                    }
                }
                catch (ArgumentException)
                {
                    // Go skips a pattern that fails to compile.
                }

                continue;
            }

            if (message.Contains(pattern.Pattern, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Port of <c>objects.RetryableErrorPattern</c>.</summary>
[GenerateSerializer]
public sealed class ChannelRetryableErrorPattern
{
    [Id(0)]
    public string Pattern { get; set; } = string.Empty;

    /// <summary>Match <see cref="Pattern"/> as a regex instead of a substring.</summary>
    [Id(1)]
    public bool Regex { get; set; }
}
