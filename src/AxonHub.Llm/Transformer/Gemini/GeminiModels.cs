using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm.Json;

namespace AxonHub.Llm.Transformer.Gemini;

/// <summary>Port of gemini.GenerateContentRequest (llm/transformer/gemini/model.go:9-30).</summary>
public sealed class GenerateContentRequest
{
    /// <summary>The content of the current conversation with the model.</summary>
    [JsonPropertyName("contents")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Content>? Contents { get; set; }

    /// <summary>The developer set system instruction.</summary>
    [JsonPropertyName("systemInstruction")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Content? SystemInstruction { get; set; }

    /// <summary>A list of Tools the Model may use to generate the next response.</summary>
    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Tool>? Tools { get; set; }

    /// <summary>The tool configuration for any Tool specified in the request.</summary>
    [JsonPropertyName("toolConfig")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ToolConfig? ToolConfig { get; set; }

    /// <summary>The configuration options for model generation and outputs.</summary>
    [JsonPropertyName("generationConfig")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GenerationConfig? GenerationConfig { get; set; }

    /// <summary>A list of unique SafetySetting instances for blocking unsafe content.</summary>
    [JsonPropertyName("safetySettings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SafetySetting>? SafetySettings { get; set; }

    /// <summary>The name of the cached content used as context to serve the prediction.</summary>
    [JsonPropertyName("cachedContent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CachedContent { get; set; }
}

/// <summary>Port of gemini.Content (llm/transformer/gemini/model.go:33-39).</summary>
public sealed class Content
{
    /// <summary>The ordered Parts that constitute a single message.</summary>
    [JsonPropertyName("parts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Part>? Parts { get; set; }

    /// <summary>The producer of the content. Must be either 'user' or 'model'.</summary>
    [JsonPropertyName("role")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Role { get; set; }
}

/// <summary>Port of gemini.Part (llm/transformer/gemini/model.go:42-63).</summary>
public sealed class Part
{
    /// <summary>Inline text.</summary>
    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    /// <summary>Inline media bytes.</summary>
    [JsonPropertyName("inlineData")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Blob? InlineData { get; set; }

    /// <summary>A URI based data.</summary>
    [JsonPropertyName("fileData")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FileData? FileData { get; set; }

    /// <summary>A predicted FunctionCall returned from the model.</summary>
    [JsonPropertyName("functionCall")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FunctionCall? FunctionCall { get; set; }

    /// <summary>The result output of a FunctionCall.</summary>
    [JsonPropertyName("functionResponse")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FunctionResponse? FunctionResponse { get; set; }

    /// <summary>Whether the part is thought from the model.</summary>
    [JsonPropertyName("thought")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Thought { get; set; }

    /// <summary>An opaque signature for the thought.</summary>
    [JsonPropertyName("thoughtSignature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ThoughtSignature { get; set; }
}

/// <summary>Port of gemini.Blob (llm/transformer/gemini/model.go:66-72).</summary>
public sealed class Blob
{
    /// <summary>The IANA standard MIME type of the source data.</summary>
    [JsonPropertyName("mimeType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MIMEType { get; set; }

    /// <summary>The raw bytes (base64 in JSON).</summary>
    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Data { get; set; }
}

/// <summary>Port of gemini.FileData (llm/transformer/gemini/model.go:75-81).</summary>
public sealed class FileData
{
    /// <summary>The IANA standard MIME type of the source data.</summary>
    [JsonPropertyName("mimeType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MIMEType { get; set; }

    /// <summary>The URI.</summary>
    [JsonPropertyName("fileUri")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FileURI { get; set; }
}

/// <summary>Port of gemini.FunctionCall (llm/transformer/gemini/model.go:84-93).</summary>
public sealed class FunctionCall
{
    /// <summary>The unique ID of the function call.</summary>
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    /// <summary>The name of the function to call.</summary>
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    /// <summary>The function parameters and values in JSON object format.</summary>
    [JsonPropertyName("args")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, JsonElement>? Args { get; set; }
}

/// <summary>Port of gemini.FunctionResponse (llm/transformer/gemini/model.go:96-108).</summary>
public sealed class FunctionResponse
{
    /// <summary>The ID of the function call this response is for.</summary>
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    /// <summary>The name of the function.</summary>
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    /// <summary>
    /// The function response in JSON object format. Use "output" key to specify
    /// function output and "error" key to specify error details (if any). If
    /// "output" and "error" keys are not specified, then whole "response" is
    /// treated as function output.
    /// </summary>
    [JsonPropertyName("response")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, JsonElement>? Response { get; set; }
}

/// <summary>
/// Port of gemini.Tool (llm/transformer/gemini/model.go:111-164). The custom
/// unmarshal accepts both the official camelCase names and the snake_case
/// aliases Google sends on some endpoints, with camelCase winning.
/// </summary>
[JsonConverter(typeof(ToolJsonConverter))]
public sealed class Tool
{
    /// <summary>A list of FunctionDeclarations available to the model.</summary>
    [JsonPropertyName("functionDeclarations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<FunctionDeclaration>? FunctionDeclarations { get; set; }

    /// <summary>Enables the model to execute code as part of generation.</summary>
    [JsonPropertyName("codeExecution")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CodeExecution? CodeExecution { get; set; }

    /// <summary>Enables Google Search grounding.</summary>
    [JsonPropertyName("googleSearch")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GoogleSearch? GoogleSearch { get; set; }

    /// <summary>Enables URL context grounding for Gemini 2.0+.</summary>
    [JsonPropertyName("urlContext")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UrlContext? UrlContext { get; set; }
}

/// <summary>
/// Port of gemini.Tool.UnmarshalJSON (llm/transformer/gemini/model.go:126-164):
/// snake_case aliases fill the field only when the camelCase value is empty
/// (list) or null (pointer).
/// </summary>
public sealed class ToolJsonConverter : JsonConverter<Tool>
{
    public override Tool? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("cannot unmarshal non-object into gemini.Tool");
        }

        var tool = new Tool();
        foreach (var property in root.EnumerateObject())
        {
            if (EqualsFold(property.Name, "functionDeclarations")
                || EqualsFold(property.Name, "function_declarations"))
            {
                var declarations = DeserializeList<FunctionDeclaration>(property.Value);
                if (tool.FunctionDeclarations is null || tool.FunctionDeclarations.Count == 0)
                {
                    tool.FunctionDeclarations = declarations;
                }
            }
            else if (EqualsFold(property.Name, "codeExecution")
                || EqualsFold(property.Name, "code_execution"))
            {
                tool.CodeExecution ??= DeserializeValue<CodeExecution>(property.Value);
            }
            else if (EqualsFold(property.Name, "googleSearch")
                || EqualsFold(property.Name, "google_search"))
            {
                tool.GoogleSearch ??= DeserializeValue<GoogleSearch>(property.Value);
            }
            else if (EqualsFold(property.Name, "urlContext")
                || EqualsFold(property.Name, "url_context"))
            {
                tool.UrlContext ??= DeserializeValue<UrlContext>(property.Value);
            }
        }

        return tool;
    }

    public override void Write(Utf8JsonWriter writer, Tool value, JsonSerializerOptions options)
    {
        // Re-entering with this converter would recurse; write the plain
        // properties (camelCase only, matching the Go struct tags).
        var withoutThis = new JsonSerializerOptions(options);
        for (var i = withoutThis.Converters.Count - 1; i >= 0; i--)
        {
            if (withoutThis.Converters[i] is ToolJsonConverter)
            {
                withoutThis.Converters.RemoveAt(i);
            }
        }

        JsonSerializer.Serialize(writer, value, withoutThis);
    }

    private static bool EqualsFold(string name, string tag)
        => string.Equals(name, tag, StringComparison.OrdinalIgnoreCase);

    private static List<FunctionDeclaration>? DeserializeList<T>(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return LlmJson.Deserialize<List<FunctionDeclaration>>(element.GetRawText());
    }

    private static T? DeserializeValue<T>(JsonElement element) where T : class
        => element.ValueKind == JsonValueKind.Null
            ? null
            : LlmJson.Deserialize<T>(element.GetRawText());
}

/// <summary>Port of gemini.FunctionDeclaration (llm/transformer/gemini/model.go:167-202).</summary>
public sealed class FunctionDeclaration
{
    /// <summary>Description and purpose of the function. Model uses it to decide how and whether to call the function.</summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    /// <summary>The name of the function to call. Must start with a letter or an underscore.</summary>
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    /// <summary>Describes the parameters to this function in JSON Schema Object format.</summary>
    [JsonPropertyName("parameters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Parameters { get; set; }

    /// <summary>Describes the parameters to the function in JSON Schema format; mutually exclusive with <c>parameters</c>.</summary>
    [JsonPropertyName("parametersJsonSchema")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? ParametersJsonSchema { get; set; }

    /// <summary>Describes the output from this function in JSON Schema format.</summary>
    [JsonPropertyName("response")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Response { get; set; }

    /// <summary>Describes the output from this function in JSON Schema format; mutually exclusive with <c>response</c>.</summary>
    [JsonPropertyName("responseJsonSchema")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? ResponseJsonSchema { get; set; }
}

/// <summary>Port of gemini.CodeExecution (llm/transformer/gemini/model.go:205).</summary>
public sealed class CodeExecution
{
}

/// <summary>Port of gemini.GoogleSearch (llm/transformer/gemini/model.go:208).</summary>
public sealed class GoogleSearch
{
}

/// <summary>Port of gemini.UrlContext (llm/transformer/gemini/model.go:212).</summary>
public sealed class UrlContext
{
}

/// <summary>Port of gemini.ToolConfig (llm/transformer/gemini/model.go:215-218).</summary>
public sealed class ToolConfig
{
    /// <summary>The function calling config.</summary>
    [JsonPropertyName("functionCallingConfig")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FunctionCallingConfig? FunctionCallingConfig { get; set; }
}

/// <summary>Port of gemini.FunctionCallingConfig (llm/transformer/gemini/model.go:221-227).</summary>
public sealed class FunctionCallingConfig
{
    /// <summary>The function calling mode. One of: AUTO, ANY, NONE, VALIDATED.</summary>
    [JsonPropertyName("mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mode { get; set; }

    /// <summary>The function names to call.</summary>
    [JsonPropertyName("allowedFunctionNames")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? AllowedFunctionNames { get; set; }
}

/// <summary>Port of gemini.GenerationConfig (llm/transformer/gemini/model.go:230-283).</summary>
public sealed class GenerationConfig
{
    /// <summary>The set of character sequences that will stop output generation.</summary>
    [JsonPropertyName("stopSequences")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? StopSequences { get; set; }

    /// <summary>Specifies the output types that the model should generate. Supported values: "TEXT", "IMAGE", "AUDIO".</summary>
    [JsonPropertyName("responseModalities")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ResponseModalities { get; set; }

    /// <summary>The MIME type of the generated candidate text.</summary>
    [JsonPropertyName("responseMimeType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResponseMIMEType { get; set; }

    /// <summary>The output schema of the generated candidate text (OpenAPI 3.0 subset).</summary>
    [JsonPropertyName("responseSchema")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? ResponseSchema { get; set; }

    /// <summary>A raw JSON Schema directly. When set, ResponseSchema must be omitted, but ResponseMIMEType is required.</summary>
    [JsonPropertyName("responseJsonSchema")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? ResponseJsonSchema { get; set; }

    /// <summary>The number of generated responses to return.</summary>
    [JsonPropertyName("candidateCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long CandidateCount { get; set; }

    /// <summary>The maximum number of tokens to include in a candidate.</summary>
    [JsonPropertyName("maxOutputTokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long MaxOutputTokens { get; set; }

    /// <summary>Controls the randomness of the output.</summary>
    [JsonPropertyName("temperature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Temperature { get; set; }

    /// <summary>The maximum cumulative probability of tokens to consider when sampling.</summary>
    [JsonPropertyName("topP")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? TopP { get; set; }

    /// <summary>The maximum number of tokens to consider when sampling.</summary>
    [JsonPropertyName("topK")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? TopK { get; set; }

    /// <summary>Penalizes tokens that already appear in the generated text.</summary>
    [JsonPropertyName("presencePenalty")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? PresencePenalty { get; set; }

    /// <summary>Penalizes tokens that repeatedly appear in the generated text.</summary>
    [JsonPropertyName("frequencyPenalty")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? FrequencyPenalty { get; set; }

    /// <summary>Used for deterministic generation.</summary>
    [JsonPropertyName("seed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Seed { get; set; }

    /// <summary>Whether to return log probabilities.</summary>
    [JsonPropertyName("responseLogprobs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ResponseLogprobs { get; set; }

    /// <summary>The number of top candidate tokens to return log probabilities for.</summary>
    [JsonPropertyName("logprobs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Logprobs { get; set; }

    /// <summary>The thinking features configuration.</summary>
    [JsonPropertyName("thinkingConfig")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ThinkingConfig? ThinkingConfig { get; set; }

    /// <summary>The image generation configuration.</summary>
    [JsonPropertyName("imageConfig")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImageConfig? ImageConfig { get; set; }
}

/// <summary>Port of gemini.ImageConfig (llm/transformer/gemini/model.go:285-293).</summary>
public sealed class ImageConfig
{
    /// <summary>Aspect ratio of the generated images. Supported values are "1:1", "2:3", "3:2", "3:4", "4:3", "9:16", "16:9", and "21:9".</summary>
    [JsonPropertyName("aspectRatio")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AspectRatio { get; set; }

    /// <summary>Specifies the size of generated images. Supported values are `1K`, `2K`, `4K`.</summary>
    [JsonPropertyName("imageSize")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImageSize { get; set; }
}

/// <summary>Port of gemini.ThinkingConfig (llm/transformer/gemini/model.go:296-305).</summary>
public sealed class ThinkingConfig
{
    /// <summary>Whether to include thoughts in the response.</summary>
    [JsonPropertyName("includeThoughts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IncludeThoughts { get; set; }

    /// <summary>The thinking budget in tokens.</summary>
    [JsonPropertyName("thinkingBudget")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ThinkingBudget { get; set; }

    /// <summary>The level of thoughts tokens that the model should generate.</summary>
    [JsonPropertyName("thinkingLevel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ThinkingLevel { get; set; }
}

/// <summary>Port of gemini.SafetySetting (llm/transformer/gemini/model.go:308-314).</summary>
public sealed class SafetySetting
{
    /// <summary>The harm category.</summary>
    [JsonPropertyName("category")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Category { get; set; }

    /// <summary>The harm block threshold.</summary>
    [JsonPropertyName("threshold")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Threshold { get; set; }
}

/// <summary>Port of gemini.GenerateContentResponse (llm/transformer/gemini/model.go:317-332).</summary>
public sealed class GenerateContentResponse
{
    /// <summary>The list of candidate responses from the model.</summary>
    [JsonPropertyName("candidates")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Candidate>? Candidates { get; set; }

    /// <summary>Content filter results for the prompt.</summary>
    [JsonPropertyName("promptFeedback")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PromptFeedback? PromptFeedback { get; set; }

    /// <summary>The usage metadata about the response.</summary>
    [JsonPropertyName("usageMetadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UsageMetadata? UsageMetadata { get; set; }

    /// <summary>The model version used to generate the response.</summary>
    [JsonPropertyName("modelVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModelVersion { get; set; }

    /// <summary>Used to identify each response.</summary>
    [JsonPropertyName("responseId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResponseId { get; set; }
}

/// <summary>Port of gemini.Candidate (llm/transformer/gemini/model.go:335-362).</summary>
public sealed class Candidate
{
    /// <summary>The generated content returned from the model.</summary>
    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Content? Content { get; set; }

    /// <summary>The reason why the model stopped generating tokens.</summary>
    [JsonPropertyName("finishReason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FinishReason { get; set; }

    /// <summary>The index of the candidate in the list of response candidates. Go: no omitempty.</summary>
    [JsonPropertyName("index")]
    public long Index { get; set; }

    /// <summary>The list of ratings for the safety of a response candidate.</summary>
    [JsonPropertyName("safetyRatings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SafetyRating>? SafetyRatings { get; set; }

    /// <summary>The citation information for model-generated candidate.</summary>
    [JsonPropertyName("citationMetadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CitationMetadata? CitationMetadata { get; set; }

    /// <summary>The number of tokens for this candidate.</summary>
    [JsonPropertyName("tokenCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long TokenCount { get; set; }

    /// <summary>The average log probability score of the candidate.</summary>
    [JsonPropertyName("avgLogprobs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double AvgLogprobs { get; set; }

    /// <summary>The log-likelihood scores for the response tokens.</summary>
    [JsonPropertyName("logprobsResult")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LogprobsResult? LogprobsResult { get; set; }

    /// <summary>The metadata specifies sources used to ground generated content.</summary>
    [JsonPropertyName("groundingMetadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GroundingMetadata? GroundingMetadata { get; set; }
}

/// <summary>Port of gemini.SafetyRating (llm/transformer/gemini/model.go:365-374).</summary>
public sealed class SafetyRating
{
    /// <summary>The harm category.</summary>
    [JsonPropertyName("category")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Category { get; set; }

    /// <summary>The harm probability level.</summary>
    [JsonPropertyName("probability")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Probability { get; set; }

    /// <summary>Whether the content was filtered.</summary>
    [JsonPropertyName("blocked")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Blocked { get; set; }
}

/// <summary>Port of gemini.CitationMetadata (llm/transformer/gemini/model.go:377-380).</summary>
public sealed class CitationMetadata
{
    /// <summary>The list of citations.</summary>
    [JsonPropertyName("citations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Citation>? Citations { get; set; }
}

/// <summary>Port of gemini.Citation (llm/transformer/gemini/model.go:383-398).</summary>
public sealed class Citation
{
    /// <summary>The start index into the content.</summary>
    [JsonPropertyName("startIndex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long StartIndex { get; set; }

    /// <summary>The end index into the content.</summary>
    [JsonPropertyName("endIndex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long EndIndex { get; set; }

    /// <summary>The URI reference of the attribution.</summary>
    [JsonPropertyName("uri")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? URI { get; set; }

    /// <summary>The title of the attribution.</summary>
    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }

    /// <summary>The license of the attribution.</summary>
    [JsonPropertyName("license")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? License { get; set; }
}

/// <summary>Port of gemini.LogprobsResult (llm/transformer/gemini/model.go:401-407).</summary>
public sealed class LogprobsResult
{
    /// <summary>The list of top candidate tokens.</summary>
    [JsonPropertyName("topCandidates")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<TopCandidates>? TopCandidates { get; set; }

    /// <summary>The list of chosen candidate tokens.</summary>
    [JsonPropertyName("chosenCandidates")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<LogprobsCandidate>? ChosenCandidates { get; set; }
}

/// <summary>Port of gemini.TopCandidates (llm/transformer/gemini/model.go:410-413).</summary>
public sealed class TopCandidates
{
    /// <summary>The list of candidates.</summary>
    [JsonPropertyName("candidates")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<LogprobsCandidate>? Candidates { get; set; }
}

/// <summary>Port of gemini.LogprobsCandidate (llm/transformer/gemini/model.go:416-425).</summary>
public sealed class LogprobsCandidate
{
    /// <summary>The candidate's token string value.</summary>
    [JsonPropertyName("token")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Token { get; set; }

    /// <summary>The candidate's token ID value.</summary>
    [JsonPropertyName("tokenId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long TokenId { get; set; }

    /// <summary>The candidate's log probability.</summary>
    [JsonPropertyName("logProbability")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double LogProbability { get; set; }
}

/// <summary>Port of gemini.PromptFeedback (llm/transformer/gemini/model.go:428-442).</summary>
public sealed class PromptFeedback
{
    /// <summary>The reason why the prompt was blocked.</summary>
    [JsonPropertyName("blockReason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BlockReason { get; set; }

    /// <summary>The list of safety ratings for the prompt.</summary>
    [JsonPropertyName("safetyRatings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SafetyRating>? SafetyRatings { get; set; }
}

/// <summary>
/// Port of gemini.UsageMetadata (llm/transformer/gemini/model.go:445-472) with
/// its custom unmarshal (usage_json.go): the AxonHub extension field
/// <c>cost</c> accepts a JSON number or a quoted number and yields null for
/// anything else instead of failing the decode.
/// </summary>
public sealed class UsageMetadata
{
    /// <summary>The number of tokens in the prompt (includes cached content).</summary>
    [JsonPropertyName("promptTokenCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long PromptTokenCount { get; set; }

    /// <summary>The total number of tokens across all generated candidates (excludes thoughts).</summary>
    [JsonPropertyName("candidatesTokenCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long CandidatesTokenCount { get; set; }

    /// <summary>The total number of tokens.</summary>
    [JsonPropertyName("totalTokenCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long TotalTokenCount { get; set; }

    /// <summary>The number of tokens in the cached content.</summary>
    [JsonPropertyName("cachedContentTokenCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long CachedContentTokenCount { get; set; }

    /// <summary>The number of tokens in the model's thoughts.</summary>
    [JsonPropertyName("thoughtsTokenCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long ThoughtsTokenCount { get; set; }

    /// <summary>A detailed breakdown of the token count for each modality in the candidates.</summary>
    [JsonPropertyName("candidatesTokensDetails")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ModalityTokenCount>? CandidatesTokensDetails { get; set; }

    /// <summary>A detailed breakdown of the token count for each modality in the prompt.</summary>
    [JsonPropertyName("promptTokensDetails")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ModalityTokenCount>? PromptTokensDetails { get; set; }

    /// <summary>The request cost calculated by AxonHub from channel model prices (AxonHub extension).</summary>
    [JsonPropertyName("cost")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(GoOptionalNumberConverter))]
    public double? Cost { get; set; }
}

/// <summary>Port of gemini.ModalityTokenCount (llm/transformer/gemini/model.go:474-478).</summary>
public sealed class ModalityTokenCount
{
    /// <summary>The modality.</summary>
    [JsonPropertyName("modality")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Modality { get; set; }

    /// <summary>The number of tokens.</summary>
    [JsonPropertyName("tokenCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long TokenCount { get; set; }
}

/// <summary>Port of gemini.GeminiError (llm/transformer/gemini/model.go:481-483).</summary>
public sealed class GeminiError
{
    /// <summary>The error detail.</summary>
    [JsonPropertyName("error")]
    public GeminiErrorDetail Error { get; set; } = new();
}

/// <summary>
/// Port of gemini.ErrorDetail (llm/transformer/gemini/model.go:486-490).
/// Renamed from the Go name: C# nested namespaces would shadow the unified
/// <see cref="AxonHub.Llm.ErrorDetail"/> for every file under this namespace,
/// which Go package boundaries do not.
/// </summary>
public sealed class GeminiErrorDetail
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;
}

/// <summary>Port of gemini.GroundingMetadata (llm/transformer/gemini/model.go:493-508).</summary>
public sealed class GroundingMetadata
{
    /// <summary>The list of supporting references retrieved from specified grounding source.</summary>
    [JsonPropertyName("groundingChunks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<GroundingChunk>? GroundingChunks { get; set; }

    /// <summary>The list of grounding support.</summary>
    [JsonPropertyName("groundingSupports")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<GroundingSupport>? GroundingSupports { get; set; }

    /// <summary>The retrieval metadata.</summary>
    [JsonPropertyName("retrievalMetadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RetrievalMetadata? RetrievalMetadata { get; set; }

    /// <summary>The Google search entry for the following-up web searches.</summary>
    [JsonPropertyName("searchEntryPoint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SearchEntryPoint? SearchEntryPoint { get; set; }

    /// <summary>The web search queries for the following-up web search.</summary>
    [JsonPropertyName("webSearchQueries")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? WebSearchQueries { get; set; }
}

/// <summary>Port of gemini.GroundingChunk (llm/transformer/gemini/model.go:511-517).</summary>
public sealed class GroundingChunk
{
    /// <summary>The grounding chunk from the web.</summary>
    [JsonPropertyName("web")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GroundingChunkWeb? Web { get; set; }

    /// <summary>The grounding chunk from context retrieved by the retrieval tools.</summary>
    [JsonPropertyName("retrievedContext")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GroundingChunkRetrievedContext? RetrievedContext { get; set; }
}

/// <summary>Port of gemini.GroundingChunkWeb (llm/transformer/gemini/model.go:520-529).</summary>
public sealed class GroundingChunkWeb
{
    /// <summary>The URI reference of the chunk.</summary>
    [JsonPropertyName("uri")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? URI { get; set; }

    /// <summary>The title of the chunk.</summary>
    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }

    /// <summary>The domain of the (original) URI.</summary>
    [JsonPropertyName("domain")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Domain { get; set; }
}

/// <summary>Port of gemini.GroundingChunkRetrievedContext (llm/transformer/gemini/model.go:532-541).</summary>
public sealed class GroundingChunkRetrievedContext
{
    /// <summary>The URI reference of the attribution.</summary>
    [JsonPropertyName("uri")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? URI { get; set; }

    /// <summary>The title of the attribution.</summary>
    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }

    /// <summary>The text of the attribution.</summary>
    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }
}

/// <summary>Port of gemini.GroundingSupport (llm/transformer/gemini/model.go:544-553).</summary>
public sealed class GroundingSupport
{
    /// <summary>The segment of the content this support belongs to.</summary>
    [JsonPropertyName("segment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Segment? Segment { get; set; }

    /// <summary>The list of indices (into 'grounding_chunk') specifying the citations.</summary>
    [JsonPropertyName("groundingChunkIndices")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<int>? GroundingChunkIndices { get; set; }

    /// <summary>The confidence score of the support references.</summary>
    [JsonPropertyName("confidenceScores")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<float>? ConfidenceScores { get; set; }
}

/// <summary>Port of gemini.Segment (llm/transformer/gemini/model.go:556-568).</summary>
public sealed class Segment
{
    /// <summary>The start index in the given Part, measured in bytes.</summary>
    [JsonPropertyName("startIndex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int StartIndex { get; set; }

    /// <summary>The end index in the given Part, measured in bytes.</summary>
    [JsonPropertyName("endIndex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int EndIndex { get; set; }

    /// <summary>The index of a Part object within its parent Content object.</summary>
    [JsonPropertyName("partIndex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int PartIndex { get; set; }

    /// <summary>The text corresponding to the segment from the response.</summary>
    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }
}

/// <summary>Port of gemini.RetrievalMetadata (llm/transformer/gemini/model.go:571-575).</summary>
public sealed class RetrievalMetadata
{
    /// <summary>The score indicating how likely information from Google Search could help answer the prompt.</summary>
    [JsonPropertyName("googleSearchDynamicRetrievalScore")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public float GoogleSearchDynamicRetrievalScore { get; set; }
}

/// <summary>Port of gemini.SearchEntryPoint (llm/transformer/gemini/model.go:578-584).</summary>
public sealed class SearchEntryPoint
{
    /// <summary>The web content snippet that can be embedded in a web page or an app webview.</summary>
    [JsonPropertyName("renderedContent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RenderedContent { get; set; }

    /// <summary>The base64 encoded JSON representing array of tuple. Go []byte marshals as base64.</summary>
    [JsonPropertyName("sdkBlob")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public byte[]? SdkBlob { get; set; }
}
