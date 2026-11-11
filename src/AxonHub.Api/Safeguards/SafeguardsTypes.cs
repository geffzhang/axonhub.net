namespace AxonHub.Api.Safeguards;

/// <summary>
/// Port of safeguards.Config (internal/server/safeguards/evaluator.go:48-58).
/// Durations stay in Go nanoseconds so a value read from the settings surface
/// does not gain the 100ns rounding of <see cref="TimeSpan"/>.
/// </summary>
public sealed class SafeguardsConfig
{
    /// <summary>Replaces <see cref="SafeguardsDefaults.DefaultPolicy"/> when nonblank.</summary>
    public string Policy { get; init; } = string.Empty;

    /// <summary>Re-reviews a primary denial with the fallback reviewers.</summary>
    public bool EscalateOnDenial { get; init; }

    /// <summary>An empty reviewer model disables server-side review.</summary>
    public string ClassifierModel { get; init; } = string.Empty;

    /// <summary>Tried in order for tools the previous reviewer left unresolved.</summary>
    public IReadOnlyList<string> FallbackModels { get; init; } = [];

    /// <summary>Caps one reviewer attempt; zero uses the remaining overall budget.</summary>
    public long AttemptTimeoutNanoseconds { get; init; }

    /// <summary>Bounds the entire review.</summary>
    public long TimeoutNanoseconds { get; init; }

    /// <summary>Maximum completion tokens for a reviewer call.</summary>
    public int MaxTokens { get; init; }

    /// <summary>UTF-8 byte budget for the rendered conversation transcript.</summary>
    public int TranscriptCharBudget { get; init; }
}

/// <summary>
/// Port of safeguards.ClassifierRequest (evaluator.go:62-67): bounded policy
/// and evidence for a non-streaming, tool-free model call.
/// </summary>
public sealed class SafeguardsClassifierRequest
{
    public string Model { get; init; } = string.Empty;

    public string System { get; init; } = string.Empty;

    public string User { get; init; } = string.Empty;

    public int MaxTokens { get; init; }
}

/// <summary>
/// Port of safeguards.ClassifierFunc (evaluator.go:69-71). The call must
/// honour cancellation and return only the model's final text, failing on a
/// refused or incomplete generation.
/// </summary>
public delegate Task<string> SafeguardsClassifierFunc(
    CancellationToken cancellationToken,
    SafeguardsClassifierRequest request);

/// <summary>
/// Port of safeguards.ToolUse (evaluator.go:73-80): a proposed tool call and
/// the complete JSON-object input. <see cref="Input"/> is the verbatim
/// <c>json.RawMessage</c> bytes (null when the field was absent).
/// </summary>
public sealed class SafeguardsToolUse
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    /// <summary>The verbatim <c>input</c> fragment; streamed arguments replace it.</summary>
    public byte[]? Input { get; set; }

    /// <summary>Truncated calls are never submitted to the classifier.</summary>
    public bool Truncated { get; set; }
}

/// <summary>Port of safeguards.Verdict (evaluator.go:82-89).</summary>
public sealed class SafeguardsVerdict
{
    /// <summary><c>evaluated</c> or <c>unavailable</c>.</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary><c>flagged</c> or <c>not_flagged</c> when evaluated.</summary>
    public string Outcome { get; init; } = string.Empty;

    public string Explanation { get; init; } = string.Empty;

    /// <summary>The unavailability reason when not evaluated.</summary>
    public string Reason { get; init; } = string.Empty;
}

/// <summary>Port of safeguards.Status (evaluator.go:91-95).</summary>
public sealed class SafeguardsStatus
{
    public string Type { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, SafeguardsVerdict> ToolUses { get; init; } =
        new Dictionary<string, SafeguardsVerdict>(StringComparer.Ordinal);
}

/// <summary>Port of safeguards.Result (evaluator.go:97-101).</summary>
public sealed class SafeguardsResult
{
    public string Type { get; init; } = string.Empty;

    public SafeguardsStatus Status { get; init; } = new();
}

/// <summary>
/// A reviewer payload could not be decoded. The reference returns these as
/// plain errors with a fixed prefix; the HTTP boundary maps the request-side
/// variants to 400 and the response-side variants to 502.
/// </summary>
public sealed class SafeguardsDecodeException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// A classifier call failed (transport error, unexpected stream, non-2xx
/// status, undecodable or incomplete output). The evaluator turns any failure
/// into an <c>unavailable</c> verdict, never an approval.
/// </summary>
public sealed class SafeguardsClassifierException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>Verdict/status/reason literals used by the reviewer protocol.</summary>
internal static class SafeguardsLiterals
{
    public const string DangerousToolUse = "dangerous_tool_use";
    public const string Evaluated = "evaluated";
    public const string Unavailable = "unavailable";
    public const string Available = "available";
    public const string Flagged = "flagged";
    public const string NotFlagged = "not_flagged";
    public const string Error = "error";
    public const string Timeout = "timeout";
    public const string Truncated = "truncated";
    public const string InputTooLong = "input_too_long";
    public const string InvalidVerdict = "invalid_verdict";
    public const string Denial = "denial";
}
