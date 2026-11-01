namespace AxonHub.Llm;

/// <summary>
/// Port of llm.RequestType (llm/constants.go). Stored as string on the wire.
/// </summary>
public static class RequestTypes
{
    public const string Chat = "chat";
    public const string Embedding = "embedding";
    public const string Rerank = "rerank";
    public const string Image = "image";
    public const string Video = "video";
    public const string Compact = "compact";
    public const string Completion = "completion";
    public const string Speech = "speech";
    public const string Transcription = "transcription";
    public const string Translation = "translation";
    public const string Moderation = "moderation";
    public const string AlphaSearch = "alpha_search";
    public const string SystemOne = "systemone";
    public const string Decisions = "decisions";
}

/// <summary>
/// Port of llm.APIFormat (llm/constants.go). Only formats needed by the first
/// vertical slice are listed; the rest are added as their transformers land.
/// </summary>
public static class ApiFormats
{
    public const string OpenAIChatCompletion = "openai/chat_completions";
    public const string OpenAICompletion = "openai/completions";
    public const string OpenAIResponse = "openai/responses";
    public const string OpenAIResponseCompact = "openai/responses/compact";
    public const string OpenAIEmbedding = "openai/embeddings";
    public const string OpenAIModeration = "openai/moderations";
    public const string OpenAIAlphaSearch = "openai/alpha_search";
    public const string OpenAIDecisions = "openai/decisions";
    public const string OpenAIImageGeneration = "openai/image_generation";
    public const string OpenAIImageEdit = "openai/image_edit";
    public const string OpenAIImageVariation = "openai/image_variation";
    public const string OpenAISpeech = "openai/audio_speech";
    public const string OpenAIVideo = "openai/video";
    public const string SeedanceVideo = "seedance/video";
    public const string ZenmuxVideo = "zenmux/video";
    public const string JinaRerank = "jina/rerank";
    public const string JinaEmbedding = "jina/embeddings";
    public const string OpenAITranscription = "openai/audio_transcriptions";
    public const string OpenAITranslation = "openai/audio_translations";
    public const string AnthropicMessage = "anthropic/messages";
    public const string GeminiContents = "gemini/contents";
    public const string TypeSafeSystemOne = "typesafe/systemone";
}

/// <summary>
/// Port of llm.ReasoningEffort* (llm/reasoning.go): protocol-independent
/// labels carried by <see cref="LlmRequest.ReasoningEffort"/>.
/// </summary>
public static class ReasoningEfforts
{
    public const string None = "none";
    public const string Minimal = "minimal";
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";
    public const string XHigh = "xhigh";
    public const string Max = "max";
}

/// <summary>Port of llm.ToolType* constants.</summary>
public static class ToolTypes
{
    public const string Function = "function";
    public const string ImageGeneration = "image_generation";
    public const string WebSearch = "web_search";
    public const string GoogleSearch = "google_search";
    public const string GoogleCodeExecution = "google_code_execution";
    public const string GoogleUrlContext = "google_url_context";
    public const string ResponsesCustomTool = "responses_custom_tool";
}

/// <summary>Port of llm.StreamCompletionEvidence (llm/model.go:675-680).</summary>
public enum StreamCompletionEvidence
{
    None = 0,
    OpenAiChatCleanEof = 1,
}

/// <summary>Go values for <see cref="StreamCompletionEvidence"/>.</summary>
public static class StreamCompletionEvidenceValues
{
    public const string OpenAIChatEof = "openai_chat_clean_eof";
}
