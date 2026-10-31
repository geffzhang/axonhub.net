namespace AxonHub.Llm.Pipeline;

/// <summary>
/// Port of llm/pipeline/empty_response.go content probes: what counts as a
/// meaningful response. Used by empty-response detection to decide whether an
/// attempt may be committed or must be retried.
/// </summary>
public static class ResponseContent
{
    /// <summary>Port of <c>hasMessageContent</c>.</summary>
    public static bool HasMessageContent(Message? msg)
    {
        if (msg is null)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(msg.Content?.Content))
        {
            return true;
        }

        if (msg.Content?.MultipleContent is { Count: > 0 })
        {
            return true;
        }

        if (msg.ToolCalls is { Count: > 0 })
        {
            return true;
        }

        if (!string.IsNullOrEmpty(msg.ReasoningContent))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(msg.Reasoning))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(msg.ReasoningSignature))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(msg.Refusal))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Port of <c>hasResponseContent</c> for the implemented response kinds
    /// (chat, moderation, embedding, images, audio, alpha search and
    /// decisions). The remaining non-chat branches (rerank, video, compact,
    /// system one, completion) land together with those request types; see
    /// porting/01-baseline.md.
    /// </summary>
    public static bool HasResponseContent(LlmResponse? resp)
    {
        if (resp is null || ReferenceEquals(resp, LlmResponse.DoneResponse) || resp.Object == "[DONE]")
        {
            return false;
        }

        // empty_response.go's order: moderation, embedding, rerank, image,
        // video, compact, speech, transcription, ... — the non-chat kinds carry
        // no choices, so each of them is content when its result is non-empty.
        if (resp.Moderation is { Results.Count: > 0 })
        {
            return true;
        }

        if (resp.Embedding is { Data.Count: > 0 })
        {
            return true;
        }

        if (resp.Image is { Data.Count: > 0 })
        {
            return true;
        }

        if (resp.Speech is { Audio.Length: > 0 })
        {
            return true;
        }

        if (resp.Transcription is not null
            && (!string.IsNullOrEmpty(resp.Transcription.Text) || resp.Transcription.Raw.Length > 0))
        {
            return true;
        }

        // Only audio deltas count as content. A bare "speech.audio.done" event
        // with no audio chunks must still be treated as empty so empty-response
        // detection can retry instead of completing a request with audio_bytes=0.
        if (resp.SpeechStreamEvent is not null && !string.IsNullOrEmpty(resp.SpeechStreamEvent.AudioBase64))
        {
            return true;
        }

        if (resp.SpeechAudioChunk is { Audio.Length: > 0 })
        {
            return true;
        }

        if (resp.TranscriptionStreamEvent is not null
            && (!string.IsNullOrEmpty(resp.TranscriptionStreamEvent.Delta)
                || !string.IsNullOrEmpty(resp.TranscriptionStreamEvent.Text)
                || resp.TranscriptionStreamEvent.Type.Length > 0))
        {
            return true;
        }

        if (resp.AlphaSearch is { Body.Length: > 0 })
        {
            return true;
        }

        if (resp.Decisions is { Answers.Count: > 0 })
        {
            return true;
        }

        foreach (var choice in resp.Choices)
        {
            if (HasMessageContent(choice.Delta) || HasMessageContent(choice.Message))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Port of <c>hasMeaningfulStreamOutput</c> (orchestrator/performance.go):
    /// does this streamed delta carry anything a client could read? The
    /// first-token clock only starts on such a delta.
    /// </summary>
    public static bool HasMeaningfulStreamOutput(LlmResponse? resp)
    {
        if (resp is null)
        {
            return false;
        }

        foreach (var choice in resp.Choices)
        {
            var delta = choice.Delta;
            if (delta is null)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(delta.Content?.Content)
                || delta.Content?.MultipleContent is { Count: > 0 }
                || delta.ToolCalls is { Count: > 0 }
                || !string.IsNullOrEmpty(delta.ReasoningContent)
                || !string.IsNullOrEmpty(delta.Reasoning)
                || delta.ReasoningItems is { Count: > 0 }
                || delta.InlineToolResults is { Count: > 0 }
                || delta.Audio is not null
                || !string.IsNullOrEmpty(delta.Refusal))
            {
                return true;
            }
        }

        return false;
    }
}
