using System.Text;
using System.Text.Json;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer.Shared;
using Microsoft.Extensions.Logging;

namespace AxonHub.Api.Safeguards;

/// <summary>
/// Port of safeguards.Evaluator (internal/server/safeguards/evaluator.go:105-305):
/// per-request bounded authorization context plus the ordered reviewer calls.
/// A separate evaluator must be used for each incoming message request.
/// </summary>
public sealed class SafeguardsEvaluator
{
    private const int MaxContextBytes = 16000;
    private const int MaxToolBytes = 64000;
    private const int MaxVerdictBytes = 64000;

    private static readonly Action<ILogger, string, string, string, string, Exception?>
        LogDenialEscalationAssessed = LoggerMessage.Define<string, string, string, string>(
            LogLevel.Warning,
            new EventId(0, "SafeguardsDenialEscalationAssessed"),
            "Safeguards denial escalation assessed model={Model} tool_id={ToolId} previous_outcome={PreviousOutcome} outcome={Outcome}");

    private static readonly Action<ILogger, string, string, string, int, Exception?>
        LogReviewerFallback = LoggerMessage.Define<string, string, string, int>(
            LogLevel.Warning,
            new EventId(0, "SafeguardsReviewerFallback"),
            "Safeguards reviewer fallback model={Model} next_model={NextModel} reason={Reason} pending_tools={PendingTools}");

    private static readonly string[] ContextKeys =
    [
        "permission_mode", "platform", "live_cwd", "home_dir", "trusted_directories",
        "rules", "auto_mode",
    ];

    private static readonly string[] GitKeys =
    [
        "cwd", "root", "branch", "default_branch", "visibility",
    ];

    private readonly string _system;
    private readonly bool _escalateOnDenial;
    private readonly IReadOnlyList<string> _models;
    private readonly long _attemptTimeoutNanoseconds;
    private readonly long _timeoutNanoseconds;
    private readonly int _maxTokens;
    private readonly SafeguardsClassifierFunc? _call;
    private readonly string _context;
    private readonly string _transcript;
    private readonly bool _contextTooLong;
    private readonly ILogger<SafeguardsEvaluator>? _logger;
    private readonly TimeProvider _timeProvider;

    private SafeguardsEvaluator(
        string system,
        bool escalateOnDenial,
        IReadOnlyList<string> models,
        long attemptTimeoutNanoseconds,
        long timeoutNanoseconds,
        int maxTokens,
        SafeguardsClassifierFunc? call,
        string context,
        string transcript,
        bool contextTooLong,
        ILogger<SafeguardsEvaluator>? logger,
        TimeProvider timeProvider)
    {
        _system = system;
        _escalateOnDenial = escalateOnDenial;
        _models = models;
        _attemptTimeoutNanoseconds = attemptTimeoutNanoseconds;
        _timeoutNanoseconds = timeoutNanoseconds;
        _maxTokens = maxTokens;
        _call = call;
        _context = context;
        _transcript = transcript;
        _contextTooLong = contextTooLong;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <summary>The reviewer prompt actually used (for diagnostics/tests).</summary>
    public string System => _system;

    /// <summary>The bounded environment JSON handed to the reviewer.</summary>
    public string Context => _context;

    /// <summary>The bounded transcript handed to the reviewer.</summary>
    public string Transcript => _transcript;

    /// <summary>
    /// Port of <c>safeguards.New</c> (evaluator.go:118-177). Returns null when
    /// no reviewer is configured or the request asked for no
    /// <c>dangerous_tool_use</c> safeguard. A malformed request body throws the
    /// reference's <c>decode safeguards request</c> error.
    /// </summary>
    public static SafeguardsEvaluator? Create(
        byte[]? body,
        SafeguardsConfig config,
        SafeguardsClassifierFunc? call,
        ILogger<SafeguardsEvaluator>? logger = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (GoStrings.TrimSpace(config.ClassifierModel).Length == 0)
        {
            return null;
        }

        var models = new List<string> { GoStrings.TrimSpace(config.ClassifierModel) };
        var seen = new HashSet<string>(StringComparer.Ordinal) { models[0] };
        foreach (var fallback in config.FallbackModels)
        {
            var model = GoStrings.TrimSpace(fallback ?? string.Empty);
            if (model.Length > 0 && seen.Add(model))
            {
                models.Add(model);
            }
        }

        var maxTokens = config.MaxTokens <= 0 ? SafeguardsDefaults.MaxTokens : config.MaxTokens;
        var transcriptBudget = config.TranscriptCharBudget <= 0
            ? SafeguardsDefaults.TranscriptCharBudget
            : config.TranscriptCharBudget;

        DecodeRequest(body ?? [], out var safeguards, out var messages);

        if (safeguards is null)
        {
            return null;
        }

        foreach (var safeguard in safeguards)
        {
            if (!string.Equals(safeguard.Type, SafeguardsLiterals.DangerousToolUse, StringComparison.Ordinal))
            {
                continue;
            }

            var selected = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var sharedContext = safeguard.Context;
            if (sharedContext is not null)
            {
                foreach (var key in ContextKeys)
                {
                    if (sharedContext.TryGetValue(key, out var value))
                    {
                        selected[key] = value;
                    }
                }

                if (sharedContext.TryGetValue("git_state", out var gitRaw))
                {
                    var gitKind = SafeguardsJson.ReadRoot(gitRaw, StringComparer.Ordinal, out var git);
                    if (gitKind == SafeguardsRootKind.Null
                        || (gitKind == SafeguardsRootKind.Object && git is not null))
                    {
                        var reduced = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                        if (git is not null)
                        {
                            foreach (var key in GitKeys)
                            {
                                if (git.TryGetValue(key, out var value))
                                {
                                    reduced[key] = value;
                                }
                            }
                        }

                        selected["git_state"] = SafeguardsJson.MarshalRawMap(reduced);
                    }
                }
            }

            var env = SafeguardsJson.MarshalRawMap(selected);
            var timeout = config.TimeoutNanoseconds <= 0
                ? SafeguardsDefaults.DefaultTimeoutNanoseconds
                : config.TimeoutNanoseconds;

            return new SafeguardsEvaluator(
                SafeguardsDefaults.ClassifierSystem(config.Policy),
                config.EscalateOnDenial,
                models,
                config.AttemptTimeoutNanoseconds,
                timeout,
                maxTokens,
                call,
                Encoding.UTF8.GetString(env),
                RenderTranscript(messages ?? [], transcriptBudget),
                env.Length > MaxContextBytes,
                logger,
                timeProvider ?? TimeProvider.System);
        }

        return null;
    }

    /// <summary>
    /// Port of <c>Evaluator.Evaluate</c> (evaluator.go:192-305). Classifies
    /// valid tool calls with ordered reviewers within one deadline; only
    /// unresolved tools reach the next reviewer unless primary denial escalation
    /// is enabled. Absence of a usable classifier result can never produce a
    /// <c>not_flagged</c> verdict.
    /// </summary>
    public SafeguardsResult[] Evaluate(
        CancellationToken cancellationToken,
        IReadOnlyList<SafeguardsToolUse>? tools)
    {
        tools ??= [];
        var verdicts = new Dictionary<string, SafeguardsVerdict>(StringComparer.Ordinal);
        var pending = new List<ClassifierTool>(tools.Count);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var tool in tools)
        {
            counts[tool.Id] = counts.GetValueOrDefault(tool.Id) + 1;
        }

        foreach (var tool in tools)
        {
            verdicts[tool.Id] = Unavailable(SafeguardsLiterals.Error);

            if (tool.Truncated)
            {
                verdicts[tool.Id] = Unavailable(SafeguardsLiterals.Truncated);
                continue;
            }

            if (tool.Id.Length == 0
                || counts[tool.Id] != 1
                || tool.Name.Length == 0
                || !ValidToolInput(tool.Input))
            {
                continue;
            }

            if (tool.Input is { Length: > MaxToolBytes })
            {
                verdicts[tool.Id] = Unavailable(SafeguardsLiterals.InputTooLong);
                continue;
            }

            pending.Add(new ClassifierTool(tool.Id, tool.Name, tool.Input!));
        }

        // The result shares the live verdict map so later reviewer rounds are
        // visible in one result entry (evaluator.go:219).
        var result = new SafeguardsResult
        {
            Type = SafeguardsLiterals.DangerousToolUse,
            Status = new SafeguardsStatus
            {
                Type = SafeguardsLiterals.Available,
                ToolUses = verdicts,
            },
        };

        if (pending.Count == 0 || _call is null)
        {
            return [result];
        }

        var encoded = MarshalClassifierTools(pending);
        if (_contextTooLong || encoded.Length > MaxToolBytes)
        {
            foreach (var tool in pending)
            {
                verdicts[tool.Id] = Unavailable(SafeguardsLiterals.InputTooLong);
            }

            return [result];
        }

        using var deadline = new SafeguardsDeadline(cancellationToken, _timeoutNanoseconds, outer: null, _timeProvider);

        for (var index = 0; index < _models.Count; index++)
        {
            var model = _models[index];

            if (deadline.IsExpired)
            {
                var expiryReason = deadline.TimedOut
                    ? SafeguardsLiterals.Timeout
                    : SafeguardsLiterals.Error;
                foreach (var tool in pending)
                {
                    if (verdicts[tool.Id].Type != SafeguardsLiterals.Evaluated)
                    {
                        verdicts[tool.Id] = Unavailable(expiryReason);
                    }
                }

                break;
            }

            var attemptTimeout = _attemptTimeoutNanoseconds > 0
                ? _attemptTimeoutNanoseconds
                : _timeoutNanoseconds;

            string text;
            Exception? callError = null;
            bool attemptExpired;
            bool attemptTimedOut;

            using (var attempt = new SafeguardsDeadline(deadline.Token, attemptTimeout, deadline, _timeProvider))
            {
                encoded = MarshalClassifierTools(pending);
                var request = new SafeguardsClassifierRequest
                {
                    Model = model,
                    System = _system,
                    User = BuildUserPrompt(encoded),
                    MaxTokens = _maxTokens,
                };

                try
                {
                    text = InvokeClassifier(attempt.Token, request);
                }
                catch (Exception ex)
                {
                    callError = ex;
                    text = string.Empty;
                }

                attemptExpired = attempt.IsExpired;
                attemptTimedOut = attempt.TimedOut;
            }

            var reason = SafeguardsLiterals.InvalidVerdict;

            if (callError is not null || attemptExpired)
            {
                reason = SafeguardsLiterals.Error;
                if (attemptTimedOut || callError is TimeoutException)
                {
                    reason = SafeguardsLiterals.Timeout;
                }

                foreach (var tool in pending)
                {
                    if (verdicts[tool.Id].Type != SafeguardsLiterals.Evaluated)
                    {
                        verdicts[tool.Id] = Unavailable(reason);
                    }
                }
            }
            else
            {
                var parsed = ParseVerdicts(text);
                var remaining = new List<ClassifierTool>(pending.Count);

                foreach (var tool in pending)
                {
                    bool valid = false;
                    bool? flagged = null;
                    var explanation = string.Empty;

                    if (parsed is not null && parsed.TryGetValue(tool.Id, out var raw))
                    {
                        valid = SafeguardsJson.TryDecodeVerdictValue(raw, out flagged, out explanation)
                            && flagged.HasValue;
                    }

                    if (!valid)
                    {
                        if (verdicts[tool.Id].Type != SafeguardsLiterals.Evaluated)
                        {
                            verdicts[tool.Id] = Unavailable(SafeguardsLiterals.Error);
                        }

                        remaining.Add(tool);
                        continue;
                    }

                    var verdict = flagged!.Value
                        ? new SafeguardsVerdict
                        {
                            Type = SafeguardsLiterals.Evaluated,
                            Outcome = SafeguardsLiterals.Flagged,
                            Explanation = Clip(explanation, 2000),
                        }
                        : new SafeguardsVerdict
                        {
                            Type = SafeguardsLiterals.Evaluated,
                            Outcome = SafeguardsLiterals.NotFlagged,
                        };

                    if (_logger is not null
                        && verdicts[tool.Id].Type == SafeguardsLiterals.Evaluated)
                    {
                        LogDenialEscalationAssessed(
                            _logger,
                            model,
                            tool.Id,
                            verdicts[tool.Id].Outcome,
                            verdict.Outcome,
                            null);
                    }

                    verdicts[tool.Id] = verdict;

                    if (index == 0 && _escalateOnDenial && verdict.Outcome == SafeguardsLiterals.Flagged)
                    {
                        remaining.Add(tool);
                        reason = SafeguardsLiterals.Denial;
                    }
                }

                pending = remaining;
            }

            if (pending.Count == 0)
            {
                break;
            }

            if (_logger is not null
                && index + 1 < _models.Count
                && !deadline.IsExpired)
            {
                LogReviewerFallback(
                    _logger,
                    model,
                    _models[index + 1],
                    reason,
                    pending.Count,
                    null);
            }
        }

        return [result];
    }

    /// <summary>
    /// Port of <c>Evaluator.AnnotateResponse</c> (response.go:48-75). Replaces
    /// dangerous-tool-use verdicts in a complete Anthropic response while
    /// preserving unrelated safeguards. A malformed response throws rather than
    /// being treated as an approval.
    /// </summary>
    public byte[] AnnotateResponse(CancellationToken cancellationToken, byte[]? body)
    {
        var kind = SafeguardsJson.ReadRoot(body ?? [], StringComparer.Ordinal, out var response);
        switch (kind)
        {
            case SafeguardsRootKind.Invalid:
                throw new SafeguardsDecodeException(
                    "decode safeguard response: invalid JSON document");
            case SafeguardsRootKind.Null:
                throw new SafeguardsDecodeException("expected safeguard response object");
            case SafeguardsRootKind.Object when response is not null:
                break;
            default:
                throw new SafeguardsDecodeException(
                    "decode safeguard response: json: cannot unmarshal value into Go value of type map[string]json.RawMessage");
        }

        var blocks = new List<byte[]>();
        if (response.TryGetValue("content", out var contentRaw)
            && SafeguardsJson.TryParseArray(contentRaw, out var parsedBlocks)
            && parsedBlocks is not null)
        {
            blocks = parsedBlocks;
        }

        var stop = string.Empty;
        if (response.TryGetValue("stop_reason", out var stopRaw))
        {
            SafeguardsJson.TryString(stopRaw, out stop);
        }

        var tools = new List<SafeguardsToolUse>();
        for (var i = 0; i < blocks.Count; i++)
        {
            if (!SafeguardsJson.TryDecodeTool(blocks[i], out var tool) || tool is null)
            {
                continue;
            }

            tool.Truncated = string.Equals(stop, "max_tokens", StringComparison.Ordinal)
                && i == blocks.Count - 1;
            tools.Add(tool);
        }

        var existing = response.TryGetValue("safeguard_results", out var rawResults)
            ? rawResults
            : null;
        response["safeguard_results"] = SafeguardsJson.MergeResults(
            existing,
            Evaluate(cancellationToken, tools));

        return SafeguardsJson.MarshalRawMap(response);
    }

    /// <summary>
    /// Port of <c>parseVerdicts</c> (evaluator.go:348-365): oversized,
    /// malformed or duplicate-key JSON is rejected as a whole. Individual
    /// verdicts stay raw so valid entries survive independently.
    /// </summary>
    internal static Dictionary<string, byte[]>? ParseVerdicts(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > MaxVerdictBytes
            || !SafeguardsJson.IsValid(bytes)
            || !SafeguardsJson.HasUniqueKeys(bytes))
        {
            return null;
        }

        var kind = SafeguardsJson.ReadRoot(bytes, StringComparer.OrdinalIgnoreCase, out var fields);
        if (kind == SafeguardsRootKind.Null)
        {
            return null;
        }

        if (kind != SafeguardsRootKind.Object || fields is null)
        {
            return null;
        }

        if (!fields.TryGetValue("verdicts", out var verdictsRaw))
        {
            return null;
        }

        var verdictKind = SafeguardsJson.ReadRoot(verdictsRaw, StringComparer.Ordinal, out var verdicts);
        if (verdictKind == SafeguardsRootKind.Null)
        {
            return null;
        }

        return verdictKind == SafeguardsRootKind.Object ? verdicts : null;
    }

    /// <summary>
    /// Port of <c>clip</c> (evaluator.go:367-380): keep at most
    /// <paramref name="limit"/> UTF-8 bytes without splitting a sequence, then
    /// append the truncation marker outside that budget.
    /// </summary>
    internal static string Clip(string value, int limit)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length <= limit)
        {
            return value;
        }

        var end = limit;
        while (end > 0 && (bytes[end] & 0xC0) == 0x80)
        {
            end--;
        }

        return Encoding.UTF8.GetString(bytes, 0, end) + " [truncated]";
    }

    /// <summary>
    /// Port of <c>renderTranscript</c> (evaluator.go:382-459): role-labelled
    /// JSON lines within a total byte budget, reserving room for an omission
    /// marker and favouring the opening message plus recent evidence.
    /// </summary>
    internal static string RenderTranscript(IReadOnlyList<byte[]> messages, int budget)
    {
        if (budget <= 0)
        {
            return string.Empty;
        }

        var groups = new List<string>[messages.Count];
        for (var i = 0; i < messages.Count; i++)
        {
            var lines = new List<string>();
            groups[i] = lines;

            var kind = SafeguardsJson.ReadRoot(
                messages[i],
                StringComparer.OrdinalIgnoreCase,
                out var fields);
            if (kind != SafeguardsRootKind.Object || fields is null)
            {
                continue;
            }

            if (!SafeguardsJson.TryFieldString(fields, "role", out var role))
            {
                continue;
            }

            if (role is not ("user" or "assistant"))
            {
                continue;
            }

            if (!fields.TryGetValue("content", out var contentRaw))
            {
                continue;
            }

            if (SafeguardsJson.TryString(contentRaw, out var text))
            {
                lines.Add(SafeguardsJson.MarshalStringMap(role, Clip(text, 2000)));
                continue;
            }

            if (!TryParseContentBlocks(contentRaw, out var blocks))
            {
                continue;
            }

            foreach (var block in blocks)
            {
                var blockFields = block ?? [];
                var blockType = string.Empty;
                if (blockFields.TryGetValue("type", out var typeRaw))
                {
                    SafeguardsJson.TryString(typeRaw, out blockType);
                }

                switch (blockType)
                {
                    case "text":
                        if (blockFields.TryGetValue("text", out var textRaw)
                            && SafeguardsJson.TryString(textRaw, out var blockText))
                        {
                            lines.Add(SafeguardsJson.MarshalStringMap(role, Clip(blockText, 2000)));
                        }

                        break;
                    case "tool_use":
                        // A nil json.RawMessage marshals as the JSON null literal.
                        var toolLine = SafeguardsJson.MarshalRawMap(
                        [
                            new("name", blockFields.TryGetValue("name", out var nameRaw) ? nameRaw : "null"u8.ToArray()),
                            new("input", blockFields.TryGetValue("input", out var inputRaw) ? inputRaw : "null"u8.ToArray()),
                        ]);
                        lines.Add(SafeguardsJson.MarshalStringMap(
                            "assistant_tool_call",
                            Clip(Encoding.UTF8.GetString(toolLine), 2000)));
                        break;
                    case "tool_result":
                        lines.Add(SafeguardsJson.MarshalStringMap(
                            "tool_result",
                            Clip(
                                ContentText(blockFields.TryGetValue("content", out var resultRaw) ? resultRaw : null),
                                500)));
                        break;
                    case "compaction":
                        lines.Add(SafeguardsJson.MarshalStringMap(
                            "conversation_summary",
                            Clip(
                                ContentText(blockFields.TryGetValue("content", out var summaryRaw) ? summaryRaw : null),
                                2000)));
                        break;
                }
            }
        }

        var opening = groups.Length > 0 ? groups[0] : [];
        var later = new List<string>();
        for (var i = 1; i < groups.Length; i++)
        {
            later.AddRange(groups[i]);
        }

        var all = new List<string>(opening);
        all.AddRange(later);
        if (ByteLen(JoinLines(all)) <= budget)
        {
            return JoinLines(all);
        }

        var marker = "{\"meta\":\"earlier turns omitted\"}";
        if (budget < marker.Length)
        {
            return string.Empty;
        }

        // Reserve the marker before allocating space to opening and recent turns.
        opening = Newest(opening, (budget - marker.Length) / 2);
        later = Newest(
            later,
            budget - ByteLen(JoinLines(opening)) - marker.Length - 2);

        var combined = new List<string>(opening) { marker };
        combined.AddRange(later);
        return JoinLines(combined);
    }

    /// <summary>
    /// Port of <c>contentText</c> (evaluator.go:461-479): a string, or the
    /// concatenated <c>text</c> fields of content blocks. Malformed content
    /// contributes no text.
    /// </summary>
    internal static string ContentText(byte[]? raw)
    {
        if (raw is null)
        {
            return string.Empty;
        }

        if (SafeguardsJson.TryString(raw, out var text))
        {
            return text;
        }

        try
        {
            using var document = SafeguardsJson.ParseDocument(raw);
            if (document.RootElement.ValueKind == JsonValueKind.Null)
            {
                return string.Empty;
            }

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Null)
                {
                    continue;
                }

                if (item.ValueKind != JsonValueKind.Object)
                {
                    return string.Empty;
                }

                var blockText = string.Empty;
                foreach (var property in item.EnumerateObject())
                {
                    if (!string.Equals(property.Name, "text", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (property.Value.ValueKind == JsonValueKind.Null)
                    {
                        // Go's decoder leaves the field untouched for null.
                        continue;
                    }

                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        return string.Empty;
                    }

                    blockText = property.Value.GetString() ?? string.Empty;
                }

                builder.Append(blockText);
            }

            return builder.ToString();
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static void DecodeRequest(
        byte[] body,
        out List<SafeguardEntry>? safeguards,
        out List<byte[]>? messages)
    {
        safeguards = null;
        messages = null;

        JsonDocument document;
        try
        {
            document = SafeguardsJson.ParseDocument(body);
        }
        catch (JsonException ex)
        {
            throw new SafeguardsDecodeException(
                "decode safeguards request: " + ex.Message,
                ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Null)
            {
                return;
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new SafeguardsDecodeException(
                    "decode safeguards request: json: cannot unmarshal "
                    + KindName(root.ValueKind)
                    + " into Go value of type struct { Safeguards []struct { Type string; Context map[string]json.RawMessage }; Messages []json.RawMessage }");
            }

            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, "safeguards", StringComparison.OrdinalIgnoreCase))
                {
                    safeguards = DecodeSafeguards(property.Value);
                }
                else if (string.Equals(property.Name, "messages", StringComparison.OrdinalIgnoreCase))
                {
                    messages = DecodeMessages(property.Value);
                }
            }
        }
    }

    private static List<SafeguardEntry>? DecodeSafeguards(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                return null;
            case JsonValueKind.Array:
                var result = new List<SafeguardEntry>();
                foreach (var item in element.EnumerateArray())
                {
                    result.Add(DecodeSafeguard(item));
                }

                return result;
            default:
                throw new SafeguardsDecodeException(
                    "decode safeguards request: json: cannot unmarshal "
                    + KindName(element.ValueKind)
                    + " into Go value of type []struct { Type string; Context map[string]json.RawMessage }");
        }
    }

    private static SafeguardEntry DecodeSafeguard(JsonElement element)
    {
        var entry = new SafeguardEntry();
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                return entry;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, "type", StringComparison.OrdinalIgnoreCase))
                    {
                        var type = entry.Type;
                        DecodeStringInto(property.Value, ref type, "Type");
                        entry.Type = type;
                    }
                    else if (string.Equals(property.Name, "classifier_context", StringComparison.OrdinalIgnoreCase))
                    {
                        entry.Context = DecodeRawMap(property.Value);
                    }
                }

                return entry;
            default:
                throw new SafeguardsDecodeException(
                    "decode safeguards request: json: cannot unmarshal "
                    + KindName(element.ValueKind)
                    + " into Go value of type struct { Type string; Context map[string]json.RawMessage }");
        }
    }

    private static Dictionary<string, byte[]>? DecodeRawMap(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                return null;
            case JsonValueKind.Object:
                var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    result[property.Name] = Encoding.UTF8.GetBytes(property.Value.GetRawText());
                }

                return result;
            default:
                throw new SafeguardsDecodeException(
                    "decode safeguards request: json: cannot unmarshal "
                    + KindName(element.ValueKind)
                    + " into Go value of type map[string]json.RawMessage");
        }
    }

    private static List<byte[]>? DecodeMessages(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                return null;
            case JsonValueKind.Array:
                var result = new List<byte[]>(element.GetArrayLength());
                foreach (var item in element.EnumerateArray())
                {
                    result.Add(Encoding.UTF8.GetBytes(item.GetRawText()));
                }

                return result;
            default:
                throw new SafeguardsDecodeException(
                    "decode safeguards request: json: cannot unmarshal "
                    + KindName(element.ValueKind)
                    + " into Go value of type []json.RawMessage");
        }
    }

    /// <summary>Go struct decode into a string field: null is a no-op.</summary>
    private static void DecodeStringInto(JsonElement element, ref string target, string field)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            throw new SafeguardsDecodeException(
                "decode safeguards request: json: cannot unmarshal "
                + KindName(element.ValueKind)
                + " into Go struct field ."
                + field
                + " of type string");
        }

        target = element.GetString() ?? string.Empty;
    }

    private static string KindName(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "bool",
        JsonValueKind.Object => "object",
        _ => "null",
    };

    /// <summary>
    /// Port of <c>validToolInput</c> (evaluator.go:179-183): only complete JSON
    /// objects with unique keys are safe to review.
    /// </summary>
    private static bool ValidToolInput(byte[]? input)
    {
        if (input is null)
        {
            return false;
        }

        var trimmed = SafeguardsJson.TrimSpace(input);
        return trimmed.Length > 0
            && trimmed[0] == (byte)'{'
            && SafeguardsJson.IsValid(trimmed)
            && SafeguardsJson.HasUniqueKeys(trimmed);
    }

    private static SafeguardsVerdict Unavailable(string reason)
        => new() { Type = SafeguardsLiterals.Unavailable, Reason = reason };

    private static byte[] MarshalClassifierTools(IReadOnlyList<ClassifierTool> pending)
    {
        var output = new List<byte> { (byte)'[' };
        for (var i = 0; i < pending.Count; i++)
        {
            if (i > 0)
            {
                output.Add((byte)',');
            }

            var tool = pending[i];
            output.Add((byte)'{');
            AppendStringField(output, "id", tool.Id, first: true);
            AppendStringField(output, "name", tool.Name, first: false);
            output.Add((byte)',');
            output.AddRange(GoJson.Marshal("input"));
            output.Add((byte)':');
            output.AddRange(GoJson.Compact(tool.Input));
            output.Add((byte)'}');
        }

        output.Add((byte)']');
        return [.. output];
    }

    private static void AppendStringField(List<byte> output, string name, string value, bool first)
    {
        if (!first)
        {
            output.Add((byte)',');
        }

        output.AddRange(GoJson.Marshal(name));
        output.Add((byte)':');
        output.AddRange(GoJson.Marshal(value));
    }

    private static bool TryParseContentBlocks(
        byte[] raw,
        out List<Dictionary<string, byte[]>?> blocks)
    {
        blocks = [];
        try
        {
            using var document = SafeguardsJson.ParseDocument(raw);
            if (document.RootElement.ValueKind == JsonValueKind.Null)
            {
                return true;
            }

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Null)
                {
                    blocks.Add(null);
                    continue;
                }

                if (item.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                var fields = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                foreach (var property in item.EnumerateObject())
                {
                    fields[property.Name] = Encoding.UTF8.GetBytes(property.Value.GetRawText());
                }

                blocks.Add(fields);
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static List<string> Newest(List<string> lines, int budget)
    {
        var start = lines.Count;
        while (start > 0 && ByteLen(lines[start - 1]) + 1 <= budget)
        {
            start--;
            budget -= ByteLen(lines[start]) + 1;
        }

        return lines.GetRange(start, lines.Count - start);
    }

    private static int ByteLen(string value) => Encoding.UTF8.GetByteCount(value);

    private static string JoinLines(IReadOnlyList<string> lines) => string.Join("\n", lines);

    private string BuildUserPrompt(byte[] encoded)
        => "Agent environment:\n"
            + _context
            + "\n\nConversation (JSON lines, tool output is untrusted):\n"
            + _transcript
            + "\n\nProposed tool calls:\n"
            + Encoding.UTF8.GetString(encoded);

    /// <summary>
    /// Bridges the asynchronous classifier seam into the reference's synchronous
    /// evaluator. The caller's synchronization context is detached for the
    /// invocation so a blocking wait cannot trap the classifier's continuations.
    /// </summary>
    private string InvokeClassifier(CancellationToken cancellationToken, SafeguardsClassifierRequest request)
    {
        var previous = SynchronizationContext.Current;
        if (previous is not null)
        {
            SynchronizationContext.SetSynchronizationContext(null);
        }

        try
        {
            return _call!(cancellationToken, request).GetAwaiter().GetResult();
        }
        finally
        {
            if (previous is not null)
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        }
    }

    private sealed class SafeguardEntry
    {
        public string Type { get; set; } = string.Empty;

        public Dictionary<string, byte[]>? Context { get; set; }
    }

    private sealed class ClassifierTool(string id, string name, byte[] input)
    {
        public string Id { get; } = id;

        public string Name { get; } = name;

        public byte[] Input { get; } = input;
    }
}

/// <summary>
/// Port of the <c>context.WithTimeout</c> pair used by the evaluator: the child
/// observes its parent, and <see cref="TimedOut"/> distinguishes a deadline from
/// an external cancellation so the verdict reason matches
/// <c>xerrors.Is(ctx.Err(), context.DeadlineExceeded)</c>.
/// </summary>
internal sealed class SafeguardsDeadline : IDisposable
{
    private readonly CancellationTokenSource _source;
    private readonly SafeguardsDeadline? _outer;
    private readonly ITimer? _timer;
    private int _timedOut;
    private int _disposed;

    public SafeguardsDeadline(
        CancellationToken parent,
        long timeoutNanoseconds,
        SafeguardsDeadline? outer,
        TimeProvider timeProvider)
    {
        _outer = outer;
        var effectiveParent = outer?.Token ?? parent;
        _source = CancellationTokenSource.CreateLinkedTokenSource(effectiveParent);

        if (timeoutNanoseconds > 0)
        {
            _timer = timeProvider.CreateTimer(
                static state => ((SafeguardsDeadline)state!).OnTimeout(),
                this,
                ToDelay(timeoutNanoseconds),
                Timeout.InfiniteTimeSpan);
        }
        else
        {
            // A non-positive duration is already past its deadline.
            OnTimeout();
        }
    }

    public CancellationToken Token => _source.Token;

    public bool IsExpired => _source.IsCancellationRequested;

    public bool TimedOut
        => Volatile.Read(ref _timedOut) != 0 || (_outer?.TimedOut ?? false);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _timer?.Dispose();
        try
        {
            _source.Cancel();
        }
        catch (Exception)
        {
            // Cancellation callbacks are classifier-owned; the reference's
            // cancel func does not fail the request either.
        }

        _source.Dispose();
    }

    private void OnTimeout()
    {
        Interlocked.Exchange(ref _timedOut, 1);
        try
        {
            _source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The deadline was disposed after the timer had already fired.
        }
        catch (Exception)
        {
            // Same rationale as Dispose: callback failures do not change the
            // deadline state.
        }
    }

    private static TimeSpan ToDelay(long timeoutNanoseconds)
    {
        // Go durations are nanoseconds; a .NET timer has 100ns ticks. Values
        // below one tick expire immediately, like an already-past deadline.
        var ticks = timeoutNanoseconds / 100;
        if (ticks <= 0)
        {
            return TimeSpan.Zero;
        }

        // System.Threading.Timer rejects due times above ~49.7 days (the last
        // valid due time is uint.MaxValue - 1 milliseconds); the settings
        // validation caps durations at 120s, so a longer unbound config
        // behaves as "never fires within one request".
        const long maxTimerTicks = (uint.MaxValue - 1) * TimeSpan.TicksPerMillisecond;
        return TimeSpan.FromTicks(Math.Min(ticks, maxTimerTicks));
    }
}
