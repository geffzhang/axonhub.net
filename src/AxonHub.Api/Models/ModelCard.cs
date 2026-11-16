using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm.Json;

namespace AxonHub.Api.Models;

/// <summary>Port of <c>objects.ModelCardReasoning</c> (internal/objects/model.go:3-6).</summary>
public sealed record ModelCardReasoning(bool Supported, bool Default);

/// <summary>Port of <c>objects.ModelCardModalities</c> (internal/objects/model.go:8-12).</summary>
/// <remarks>
/// A nil Go slice stays nil through decoding and marshals as JSON <c>null</c>;
/// the GraphQL non-null list marshaller turns it into <c>[]</c> at the boundary.
/// </remarks>
public sealed record ModelCardModalities(
    IReadOnlyList<string>? Input,
    IReadOnlyList<string>? Output);

/// <summary>Port of <c>objects.ModelCardCost</c> (internal/objects/model.go:14-19).</summary>
public sealed record ModelCardCost(double Input, double Output, double CacheRead, double CacheWrite);

/// <summary>Port of <c>objects.ModelCardLimit</c> (internal/objects/model.go:21-24).</summary>
public sealed record ModelCardLimit(int Context, int Output);

/// <summary>
/// Port of <c>objects.ModelCard</c> (internal/objects/model.go:26-40): the
/// <c>models.model_card</c> JSON column. The reference has no custom marshaler,
/// so field order follows the struct declaration and absent capabilities are
/// encoded as zero values rather than omitted.
/// </summary>
public sealed record ModelCard
{
    public ModelCardReasoning Reasoning { get; init; } = new(false, false);

    /// <summary>Nil means the levels are unknown and marshals as JSON null.</summary>
    public IReadOnlyList<string>? ReasoningEfforts { get; init; }

    public bool ToolCall { get; init; }

    public bool Temperature { get; init; }

    public ModelCardModalities Modalities { get; init; } = new(null, null);

    public bool Vision { get; init; }

    public ModelCardCost Cost { get; init; } = new(0, 0, 0, 0);

    public ModelCardLimit Limit { get; init; } = new(0, 0);

    public string Knowledge { get; init; } = string.Empty;

    public string ReleaseDate { get; init; } = string.Empty;

    public string LastUpdated { get; init; } = string.Empty;

    /// <summary>The zero card, which is what <c>{}</c> decodes to.</summary>
    public static ModelCard Default { get; } = new();
}

/// <summary>
/// Go-compatible codec for <c>objects.ModelCard</c>. ent scans the JSON column
/// into <c>*objects.ModelCard</c> (internal/ent/model.go:139-145) and
/// <c>SetModelCard</c> marshals the struct back (internal/ent/model_builder.go
/// via field.TypeJSON).
/// </summary>
public static class ModelCardCodec
{
    private static readonly JsonSerializerOptions ReadOptions = new(LlmJson.Options)
    {
        Converters = { new GoNullableDoubleConverter() },
    };

    private static readonly JsonSerializerOptions WriteOptions = new(LlmJson.Options)
    {
        Converters = { new GoNullableDoubleConverter() },
    };

    /// <summary>
    /// Port of <c>json.Unmarshal(data, &amp;m.ModelCard)</c>. A JSON
    /// <c>null</c> (or a NULL/empty column, which ent skips) leaves the pointer
    /// nil; the reference then fails the non-null GraphQL field.
    /// </summary>
    /// <exception cref="JsonException">The document does not decode.</exception>
    public static ModelCard? Deserialize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var root = value.AsSpan().Trim();
        if (root.IsEmpty)
        {
            throw new JsonException("unexpected end of JSON input");
        }

        if (root.SequenceEqual("null"))
        {
            return null;
        }

        if (root[0] != '{')
        {
            throw new JsonException(
                "json: cannot unmarshal " + GoKindName(root[0])
                + " into Go value of type objects.ModelCard");
        }

        var stored = JsonSerializer.Deserialize<ModelCardJson>(value, ReadOptions);
        return stored is null ? null : ToModelCard(stored);
    }

    /// <summary>Port of <c>json.Marshal(objects.ModelCard)</c>.</summary>
    public static string Serialize(ModelCard card)
    {
        ArgumentNullException.ThrowIfNull(card);

        var write = new ModelCardWriteJson
        {
            Reasoning = new ReasoningWriteJson
            {
                Supported = card.Reasoning.Supported,
                Default = card.Reasoning.Default,
            },
            ReasoningEfforts = card.ReasoningEfforts,
            ToolCall = card.ToolCall,
            Temperature = card.Temperature,
            Modalities = new ModalitiesWriteJson
            {
                Input = card.Modalities.Input,
                Output = card.Modalities.Output,
            },
            Vision = card.Vision,
            Cost = new CostWriteJson
            {
                Input = card.Cost.Input,
                Output = card.Cost.Output,
                CacheRead = card.Cost.CacheRead,
                CacheWrite = card.Cost.CacheWrite,
            },
            Limit = new LimitWriteJson
            {
                Context = card.Limit.Context,
                Output = card.Limit.Output,
            },
            Knowledge = card.Knowledge,
            ReleaseDate = card.ReleaseDate,
            LastUpdated = card.LastUpdated,
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(write, WriteOptions);
        return Encoding.UTF8.GetString(GoJson.Escape(bytes));
    }

    private static ModelCard ToModelCard(ModelCardJson stored) => new()
    {
        Reasoning = new ModelCardReasoning(
            stored.Reasoning?.Supported ?? false,
            stored.Reasoning?.Default ?? false),
        ReasoningEfforts = ToStrings(stored.ReasoningEfforts),
        ToolCall = stored.ToolCall ?? false,
        Temperature = stored.Temperature ?? false,
        Modalities = new ModelCardModalities(
            ToStrings(stored.Modalities?.Input),
            ToStrings(stored.Modalities?.Output)),
        Vision = stored.Vision ?? false,
        Cost = new ModelCardCost(
            stored.Cost?.Input ?? 0,
            stored.Cost?.Output ?? 0,
            stored.Cost?.CacheRead ?? 0,
            stored.Cost?.CacheWrite ?? 0),
        Limit = new ModelCardLimit(
            stored.Limit?.Context ?? 0,
            stored.Limit?.Output ?? 0),
        Knowledge = stored.Knowledge ?? string.Empty,
        ReleaseDate = stored.ReleaseDate ?? string.Empty,
        LastUpdated = stored.LastUpdated ?? string.Empty,
    };

    /// <summary>
    /// Go decodes a JSON null into a string element as the zero value, so a
    /// null inside <c>[]string</c> becomes "" rather than failing.
    /// </summary>
    private static List<string>? ToStrings(List<string?>? stored)
        => stored?.Select(static value => value ?? string.Empty).ToList();

    private static string GoKindName(char first) => first switch
    {
        '[' => "array",
        '"' => "string",
        't' or 'f' => "bool",
        '-' or (>= '0' and <= '9') => "number",
        _ => "object",
    };

    private sealed class ModelCardJson
    {
        [JsonPropertyName("reasoning")]
        public ReasoningJson? Reasoning { get; set; }

        [JsonPropertyName("reasoningEfforts")]
        public List<string?>? ReasoningEfforts { get; set; }

        [JsonPropertyName("toolCall")]
        public bool? ToolCall { get; set; }

        [JsonPropertyName("temperature")]
        public bool? Temperature { get; set; }

        [JsonPropertyName("modalities")]
        public ModalitiesJson? Modalities { get; set; }

        [JsonPropertyName("vision")]
        public bool? Vision { get; set; }

        [JsonPropertyName("cost")]
        public CostJson? Cost { get; set; }

        [JsonPropertyName("limit")]
        public LimitJson? Limit { get; set; }

        [JsonPropertyName("knowledge")]
        public string? Knowledge { get; set; }

        [JsonPropertyName("releaseDate")]
        public string? ReleaseDate { get; set; }

        [JsonPropertyName("lastUpdated")]
        public string? LastUpdated { get; set; }
    }

    private sealed class ReasoningJson
    {
        [JsonPropertyName("supported")]
        public bool? Supported { get; set; }

        [JsonPropertyName("default")]
        public bool? Default { get; set; }
    }

    private sealed class ModalitiesJson
    {
        [JsonPropertyName("input")]
        public List<string?>? Input { get; set; }

        [JsonPropertyName("output")]
        public List<string?>? Output { get; set; }
    }

    private sealed class CostJson
    {
        [JsonPropertyName("input")]
        [JsonConverter(typeof(GoNullableDoubleConverter))]
        public double? Input { get; set; }

        [JsonPropertyName("output")]
        [JsonConverter(typeof(GoNullableDoubleConverter))]
        public double? Output { get; set; }

        [JsonPropertyName("cacheRead")]
        [JsonConverter(typeof(GoNullableDoubleConverter))]
        public double? CacheRead { get; set; }

        [JsonPropertyName("cacheWrite")]
        [JsonConverter(typeof(GoNullableDoubleConverter))]
        public double? CacheWrite { get; set; }
    }

    private sealed class LimitJson
    {
        [JsonPropertyName("context")]
        public int? Context { get; set; }

        [JsonPropertyName("output")]
        public int? Output { get; set; }
    }

    private sealed class ModelCardWriteJson
    {
        [JsonPropertyName("reasoning")]
        public ReasoningWriteJson Reasoning { get; init; } = new();

        [JsonPropertyName("reasoningEfforts")]
        public IReadOnlyList<string>? ReasoningEfforts { get; init; }

        [JsonPropertyName("toolCall")]
        public bool ToolCall { get; init; }

        [JsonPropertyName("temperature")]
        public bool Temperature { get; init; }

        [JsonPropertyName("modalities")]
        public ModalitiesWriteJson Modalities { get; init; } = new();

        [JsonPropertyName("vision")]
        public bool Vision { get; init; }

        [JsonPropertyName("cost")]
        public CostWriteJson Cost { get; init; } = new();

        [JsonPropertyName("limit")]
        public LimitWriteJson Limit { get; init; } = new();

        [JsonPropertyName("knowledge")]
        public string Knowledge { get; init; } = string.Empty;

        [JsonPropertyName("releaseDate")]
        public string ReleaseDate { get; init; } = string.Empty;

        [JsonPropertyName("lastUpdated")]
        public string LastUpdated { get; init; } = string.Empty;
    }

    private sealed class ReasoningWriteJson
    {
        [JsonPropertyName("supported")]
        public bool Supported { get; init; }

        [JsonPropertyName("default")]
        public bool Default { get; init; }
    }

    private sealed class ModalitiesWriteJson
    {
        [JsonPropertyName("input")]
        public IReadOnlyList<string>? Input { get; init; }

        [JsonPropertyName("output")]
        public IReadOnlyList<string>? Output { get; init; }
    }

    private sealed class CostWriteJson
    {
        [JsonPropertyName("input")]
        [JsonConverter(typeof(GoNullableDoubleConverter))]
        public double? Input { get; init; }

        [JsonPropertyName("output")]
        [JsonConverter(typeof(GoNullableDoubleConverter))]
        public double? Output { get; init; }

        [JsonPropertyName("cacheRead")]
        [JsonConverter(typeof(GoNullableDoubleConverter))]
        public double? CacheRead { get; init; }

        [JsonPropertyName("cacheWrite")]
        [JsonConverter(typeof(GoNullableDoubleConverter))]
        public double? CacheWrite { get; init; }
    }

    private sealed class LimitWriteJson
    {
        [JsonPropertyName("context")]
        public int Context { get; init; }

        [JsonPropertyName("output")]
        public int Output { get; init; }
    }

    /// <summary>
    /// Reads float64 the way Go does: JSON null leaves the zero value, and a
    /// number that overflows float64 is a decode error. Writes the shortest
    /// Go-compatible literal (<c>1e+21</c>, not STJ's <c>1E+21</c>).
    /// </summary>
    private sealed class GoNullableDoubleConverter : JsonConverter<double?>
    {
        public override double? Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.Null:
                    return null;
                case JsonTokenType.Number:
                    var value = reader.GetDouble();
                    if (!double.IsFinite(value))
                    {
                        throw new JsonException(
                            "json: cannot unmarshal number into Go value of type float64");
                    }

                    return value;
                default:
                    throw new JsonException(
                        "json: cannot unmarshal " + TokenName(reader.TokenType)
                        + " into Go value of type float64");
            }
        }

        public override void Write(
            Utf8JsonWriter writer,
            double? value,
            JsonSerializerOptions options)
        {
            if (value is null)
            {
                writer.WriteNullValue();
                return;
            }

            writer.WriteRawValue(GoJsonFloat.Format(value.Value), skipInputValidation: true);
        }

        private static string TokenName(JsonTokenType token) => token switch
        {
            JsonTokenType.StartArray => "array",
            JsonTokenType.String => "string",
            JsonTokenType.True or JsonTokenType.False => "bool",
            JsonTokenType.StartObject => "object",
            _ => "value",
        };
    }
}
