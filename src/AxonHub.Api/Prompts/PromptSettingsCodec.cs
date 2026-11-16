using System.Text;
using System.Text.Json;

namespace AxonHub.Api.Prompts;

/// <summary>
/// Port of Go's <c>encoding/json</c> marshalling of objects.PromptSettings
/// (internal/objects/prompt.go): the action is always emitted, the optional
/// lists use <c>omitempty</c>, a nil pointer field is omitted while a pointer
/// to an empty string is kept, and the key order follows the struct.
/// </summary>
/// <remarks>
/// jsonb normalizes formatting and key order on write, so byte-for-byte
/// equality with the reference is not observable; semantic equality is.
/// </remarks>
public static class PromptSettingsCodec
{
    public static string Serialize(PromptSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            writer.WritePropertyName("action");
            writer.WriteStartObject();
            writer.WriteString("type", settings.Action?.Type ?? string.Empty);
            writer.WriteEndObject();

            if (settings.Conditions is { Count: > 0 })
            {
                writer.WritePropertyName("conditions");
                writer.WriteStartArray();
                foreach (var composite in settings.Conditions)
                {
                    writer.WriteStartObject();
                    if (composite?.Conditions is { Count: > 0 })
                    {
                        writer.WritePropertyName("conditions");
                        writer.WriteStartArray();
                        foreach (var condition in composite.Conditions)
                        {
                            writer.WriteStartObject();
                            writer.WriteString("type", condition?.Type ?? string.Empty);
                            if (condition?.ModelId is not null)
                            {
                                writer.WriteString("model_id", condition.ModelId);
                            }

                            if (condition?.ModelPattern is not null)
                            {
                                writer.WriteString("model_pattern", condition.ModelPattern);
                            }

                            if (condition?.ApiKeyId is not null)
                            {
                                writer.WriteNumber("api_key_id", condition.ApiKeyId.Value);
                            }

                            writer.WriteEndObject();
                        }

                        writer.WriteEndArray();
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
