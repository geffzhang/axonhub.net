using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;

namespace AxonHub.Api.Http;

/// <summary>
/// Port of orchestrator.StreamTerminalTracker
/// (internal/server/orchestrator/terminal_tracker.go): recognizes the event that
/// ends a stream, so the writer can tell a completed stream from one the upstream
/// dropped and suppress errors that arrive after the terminal event.
/// </summary>
public sealed class StreamTerminalTracker(int expectedChoices)
{
    private static readonly HashSet<string> NonChoiceTerminalEvents = new(StringComparer.Ordinal)
    {
        "response.completed",
        "response.failed",
        "response.incomplete",
        "response.cancelled",
        "error",
        "message_stop",
        "speech.audio.done",
        "transcript.text.done",
    };

    private readonly HashSet<int> _choices = [];
    private readonly HashSet<int> _finished = [];
    private bool _terminal;

    public bool Terminal => _terminal;

    /// <summary>
    /// Port of StreamTerminalTracker.AllChoicesFinished: every expected choice
    /// reported a finish reason. Aggregation alone cannot prove a multi-choice
    /// OpenAI stream completed after the connection broke.
    /// </summary>
    public bool AllChoicesFinished()
        => expectedChoices > 0
            && _choices.Count == expectedChoices
            && _finished.Count == expectedChoices;

    /// <summary>Port of StreamTerminalTracker.Observe.</summary>
    public bool Observe(StreamEvent? streamEvent)
    {
        if (_terminal || streamEvent is null)
        {
            return _terminal;
        }

        if (IsDoneEvent(streamEvent.Data)
            || NonChoiceTerminalEvents.Contains(streamEvent.Type)
            || NonChoiceTerminalEvents.Contains(DataString(streamEvent.Data, "type")))
        {
            _terminal = true;
            return true;
        }

        using (var document = TryParse(streamEvent.Data))
        {
            if (document is not null
                && document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array)
            {
                foreach (var choice in choices.EnumerateArray())
                {
                    if (choice.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var index = 0;
                    if (choice.TryGetProperty("index", out var indexElement)
                        && indexElement.ValueKind == JsonValueKind.Number
                        && indexElement.TryGetInt32(out var parsedIndex))
                    {
                        index = parsedIndex;
                    }

                    _choices.Add(index);
                    if (choice.TryGetProperty("finish_reason", out var finishReason)
                        && finishReason.ValueKind == JsonValueKind.String
                        && !string.IsNullOrEmpty(finishReason.GetString()))
                    {
                        _finished.Add(index);
                    }
                }

                if (expectedChoices > 0
                    && _finished.Count >= expectedChoices
                    && _choices.Count == expectedChoices)
                {
                    _terminal = true;
                }
            }

            if (!_terminal
                && document is not null
                && document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("candidates", out var candidates)
                && candidates.ValueKind == JsonValueKind.Array
                && HasNonEmptyStringField(candidates, "finishReason"))
            {
                _terminal = true;
            }
        }

        return _terminal;
    }

    private static bool IsDoneEvent(byte[] data)
        => data.Length == 6
            && data[0] == (byte)'['
            && data[1] == (byte)'D'
            && data[2] == (byte)'O'
            && data[3] == (byte)'N'
            && data[4] == (byte)'E'
            && data[5] == (byte)']';

    private static bool HasNonEmptyStringField(JsonElement array, string name)
    {
        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(value.GetString()))
            {
                return true;
            }
        }

        return false;
    }

    private static string DataString(byte[] data, string property)
    {
        using var document = TryParse(data);
        if (document is not null
            && document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static JsonDocument? TryParse(byte[] data)
    {
        if (data.Length == 0)
        {
            return null;
        }

        try
        {
            // gjson (the reference's reader here) ignores ill-formed UTF-8.
            return JsonDocument.Parse(GoJsonUtf8.SanitizeStringLiterals(data) ?? data);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
