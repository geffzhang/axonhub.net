using System.Text.Json;
using AxonHub.Llm.Json;

namespace AxonHub.Api.Http;

/// <summary>Port of orchestrator.ExpectedStreamChoices (terminal_tracker.go:36-50).</summary>
public static class ExpectedStreamChoices
{
    public static int FromRequestBody(byte[]? body)
    {
        if (body is null || body.Length == 0)
        {
            return 0;
        }

        try
        {
            // gjson ignores ill-formed UTF-8; repair string literals the way
            // json.Unmarshal would before handing the body to System.Text.Json.
            using var document = JsonDocument.Parse(GoJsonUtf8.SanitizeStringLiterals(body) ?? body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return 0;
            }

            if (root.TryGetProperty("n", out var n)
                && n.ValueKind == JsonValueKind.Number
                && n.TryGetInt64(out var count))
            {
                return count is > 0 and <= int.MaxValue ? (int)count : 0;
            }

            if (root.TryGetProperty("stream", out var stream) && stream.ValueKind == JsonValueKind.True)
            {
                return 1;
            }

            return 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }
}
