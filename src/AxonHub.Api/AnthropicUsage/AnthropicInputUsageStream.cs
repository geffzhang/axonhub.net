using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AxonHub.Llm.Http;
using AxonHub.Llm.Streams;

namespace AxonHub.Api.AnthropicUsage;

/// <summary>
/// Port of internal/server/api/anthropic_input_usage.go's
/// <c>anthropicInputUsageStream</c>. It runs after persistence, so only the
/// client sees the estimate; final usage, stored responses and costs remain
/// authoritative, even when the stream is interrupted.
/// </summary>
internal sealed class AnthropicInputUsageStream : IStream<StreamEvent>
{
    private readonly IStream<StreamEvent> _stream;
    private readonly AnthropicInputUsageCalibration? _calibration;
    private byte[]? _body;

    private long _actual;
    private bool _terminal;
    private bool _stopped;
    private bool _failed;
    private bool _completed;
    private bool _handled;
    private StreamEvent? _current;

    private AnthropicInputUsageStream(
        IStream<StreamEvent> stream,
        byte[]? body,
        AnthropicInputUsageCalibration? calibration)
    {
        _stream = stream;
        _body = body;
        _calibration = calibration;
    }

    /// <summary>
    /// Go: <c>wrapAnthropicInputUsage</c>. Only the Anthropic Messages route is
    /// wrapped, and the source's delivery capabilities are preserved.
    /// </summary>
    internal static IStream<StreamEvent> Wrap(
        IStream<StreamEvent> source,
        HttpRequestData? request,
        AnthropicInputUsageCalibration? calibration)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (request is null
            || request.Path is null
            || !request.Path.EndsWith("/messages", StringComparison.Ordinal))
        {
            return source;
        }

        return DeliveryCapabilities.Preserve(
            source,
            new AnthropicInputUsageStream(source, request.Body, calibration));
    }

    public StreamEvent? Current => _current;

    public Exception? Err => _stream.Err;

    public bool Next()
    {
        if (!_stream.Next())
        {
            if (!_completed
                && !_failed
                && _terminal
                && _stopped
                && _stream.Err is null
                && _calibration is not null)
            {
                _calibration.Complete(_actual);
            }

            _completed = true;
            return false;
        }

        _current = _stream.Current;
        if (_current is null || !IsValidJson(_current.Data))
        {
            if (_current is not null && _current.Data.Length > 0)
            {
                _failed = true;
            }

            return true;
        }

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(_current.Data) as JsonObject;
        }
        catch (JsonException)
        {
            _failed = true;
            return true;
        }

        if (root is null)
        {
            return true;
        }

        var eventType = GjsonString(root, "type");
        switch (eventType)
        {
            case "message_delta":
                if (root["usage"] is JsonObject usage
                    && usage.ContainsKey("input_tokens"))
                {
                    _actual = FinalAnthropicInputTokens(usage);
                }

                if (root["delta"] is JsonObject delta
                    && GjsonString(delta, "stop_reason") is "end_turn" or "tool_use" or "stop_sequence")
                {
                    _terminal = true;
                }

                break;
            case "message_stop":
                _stopped = true;
                break;
            case "error":
                _failed = true;
                break;
        }

        if (_handled || eventType != "message_start")
        {
            return true;
        }

        _handled = true;
        var body = _body;
        _body = null;
        if (body is null)
        {
            return true;
        }

        var message = root["message"] as JsonObject;
        var usageNode = message?["usage"] as JsonObject;
        if (usageNode is not null
            && (GjsonInt(usageNode, "input_tokens") != 0
                || GjsonInt(usageNode, "cache_read_input_tokens") != 0
                || GjsonInt(usageNode, "cache_creation_input_tokens") != 0))
        {
            return true;
        }

        long estimate;
        try
        {
            estimate = AnthropicInputUsageEstimator.Estimate(body);
        }
        catch (JsonException)
        {
            return true;
        }

        if (estimate <= 0)
        {
            return true;
        }

        if (_calibration is not null)
        {
            estimate = _calibration.Estimate(
                body,
                message is null ? string.Empty : GjsonString(message, "model"),
                estimate);
        }

        if (!TrySetInputTokens(root, estimate))
        {
            return true;
        }

        _current = new StreamEvent
        {
            LastEventId = _current.LastEventId,
            Type = _current.Type,
            Data = JsonSerializer.SerializeToUtf8Bytes(root),
            CleanEofCompletionEvidence = _current.CleanEofCompletionEvidence,
            Size = _current.Size,
            Headers = _current.Headers,
        };
        return true;
    }

    public void Dispose() => _stream.Dispose();

    /// <summary>Go: <c>finalAnthropicInputTokens</c>.</summary>
    internal static long FinalAnthropicInputTokens(JsonObject usage)
    {
        if (!usage.ContainsKey("input_tokens"))
        {
            return 0;
        }

        long total = 0;
        foreach (var key in new[]
                 {
                     "input_tokens",
                     "cache_read_input_tokens",
                     "cache_creation_input_tokens",
                 })
        {
            if (!usage.TryGetPropertyValue(key, out var value))
            {
                continue;
            }

            if (value is not JsonValue jsonValue
                || !TryParseInt64(jsonValue, out var n)
                || n < 0
                || total > long.MaxValue - n)
            {
                return 0;
            }

            total += n;
        }

        return total;
    }

    private static bool TrySetInputTokens(JsonObject root, long estimate)
    {
        if (root["message"] is not JsonObject message)
        {
            message = new JsonObject();
            root["message"] = message;
        }

        if (message["usage"] is not JsonObject usage)
        {
            usage = new JsonObject();
            message["usage"] = usage;
        }

        usage["input_tokens"] = estimate;
        return true;
    }

    private static long GjsonInt(JsonObject value, string property)
    {
        if (!value.TryGetPropertyValue(property, out var node)
            || node is not JsonValue jsonValue)
        {
            return 0;
        }

        return TryParseInt64(jsonValue, out var number) ? number : 0;
    }

    private static bool TryParseInt64(JsonValue value, out long number)
        => long.TryParse(
            value.ToJsonString(),
            NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture,
            out number);

    private static string GjsonString(JsonObject value, string property)
        => value.TryGetPropertyValue(property, out var node)
            && node is JsonValue jsonValue
            && jsonValue.TryGetValue<string>(out var text)
                ? text
                : string.Empty;

    private static bool IsValidJson(byte[] data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
