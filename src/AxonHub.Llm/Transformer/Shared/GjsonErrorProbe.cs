using System.Text.Json;

namespace AxonHub.Llm.Transformer.Shared;

/// <summary>
/// The <c>gjson.GetBytes(data, "error")</c> probe the OpenRouter and NanoGPT
/// stream paths use: an explicit, top-level <c>error</c> key means the event is
/// an in-band error, and its gjson string form becomes the error message.
/// </summary>
public static class GjsonErrorProbe
{
    /// <summary>
    /// Returns true when the event data has a top-level <c>error</c> member.
    /// JSON <c>null</c> still exists in gjson terms; its string form is empty.
    /// Objects and arrays keep their raw JSON text, matching
    /// <c>gjson.Result.String()</c>.
    /// </summary>
    public static bool TryGetErrorString(byte[] data, out string message)
    {
        message = string.Empty;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(data);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!property.NameEquals("error"))
                {
                    continue;
                }

                message = GjsonString(property.Value);
                return true;
            }
        }

        return false;
    }

    private static string GjsonString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Null => string.Empty,
        _ => value.GetRawText(),
    };
}
