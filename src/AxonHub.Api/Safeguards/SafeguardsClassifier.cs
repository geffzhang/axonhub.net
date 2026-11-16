using System.Text.Json;
using AxonHub.Api.Auth;
using AxonHub.Api.Http;
using AxonHub.Api.Orchestration;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.Shared;
using GoJson = AxonHub.Llm.Json.GoJson;

namespace AxonHub.Api.Safeguards;

/// <summary>
/// Port of api.newSafeguardsClassifier and safeguardsClassifierWithProcess
/// (internal/server/api/safeguards_classifier.go:22-79). The reviewer uses the
/// ordinary routing, model access, quota and persistence pipeline but a fresh
/// OpenAI inbound transformer, so it cannot recursively re-enter the Anthropic
/// HTTP handler.
/// </summary>
public static class SafeguardsClassifier
{
    /// <summary>
    /// Builds the classifier seam over <paramref name="parent"/>. The caller
    /// supplies the deadline through the cancellation token; the returned
    /// delegate only accepts one nonempty, unrefused choice ending with
    /// <c>stop</c>.
    /// </summary>
    public static SafeguardsClassifierFunc Create(
        ChatCompletionOrchestrator parent,
        RequestIdentity identity,
        string? sessionId,
        string? sessionScope)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(identity);

        var classifier = parent.WithInbound(new OpenAiInboundTransformer());
        return Create(
            identity,
            sessionId,
            sessionScope,
            (wireRequest, classifierIdentity, cancellationToken) => classifier.ProcessAsync(
                wireRequest,
                classifierIdentity,
                onRequestCreated: null,
                cancellationToken));
    }

    /// <summary>
    /// Port of the <c>safeguardsClassifierWithProcess</c> seam
    /// (safeguards_classifier.go:38-79): the process delegate receives the
    /// forked identity and the exact wire request, so tests can observe request
    /// identity and protocol shape without an HTTP server or a live channel.
    /// </summary>
    internal static SafeguardsClassifierFunc Create(
        RequestIdentity identity,
        string? sessionId,
        string? sessionScope,
        Func<HttpRequestData, RequestIdentity, CancellationToken, Task<ChatCompletionResult>> process)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(process);

        return (cancellationToken, request) => CallAsync(
            process,
            identity,
            sessionId,
            sessionScope,
            cancellationToken,
            request);
    }

    private static async Task<string> CallAsync(
        Func<HttpRequestData, RequestIdentity, CancellationToken, Task<ChatCompletionResult>> process,
        RequestIdentity identity,
        string? sessionId,
        string? sessionScope,
        CancellationToken cancellationToken,
        SafeguardsClassifierRequest request)
    {
        // contexts.ForkRequest keeps the project, trace, API key and source but
        // isolates request bookkeeping: a fresh request id and the
        // "safeguards-classifier" operation name.
        var requestId = RequestIds.Generate();
        var classifierIdentity = identity with { RequestId = requestId };

        var body = EncodeRequest(request);
        var wireRequest = new HttpRequestData
        {
            Method = "POST",
            Path = "/v1/chat/completions",
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json"],
            },
            ContentType = "application/json",
            Body = body,
            RequestId = requestId,
            ApiFormat = ApiFormats.OpenAIChatCompletion,
            SessionId = sessionId,
            SessionScope = sessionScope,
            // The reference passes the classifier's ctx into the executor;
            // the port carries the caller's token on the wire request (C104).
            RequestCancellationToken = cancellationToken,
        };

        ChatCompletionResult result;
        try
        {
            result = await process(wireRequest, classifierIdentity, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new SafeguardsClassifierException(
                "safeguards classifier request: " + ex.Message,
                ex);
        }

        if (result.Stream is { } stream)
        {
            stream.Dispose();
            throw new SafeguardsClassifierException(
                "safeguards classifier returned an unexpected stream");
        }

        if (result.Response is not { StatusCode: >= 200 and < 300 } response)
        {
            throw new SafeguardsClassifierException(
                "safeguards classifier did not return a successful response");
        }

        var choice = DecodeSingleChoice(response.Body);
        if (!string.Equals(choice.FinishReason, "stop", StringComparison.Ordinal)
            || choice.Refusal.Length != 0
            || GoStrings.TrimSpace(choice.Content).Length == 0)
        {
            throw new SafeguardsClassifierException(
                "safeguards classifier returned incomplete or refused output");
        }

        return choice.Content;
    }

    /// <summary>
    /// Go <c>json.Marshal(map[string]any{...})</c>: sorted keys, declaration
    /// order inside each message map, no trailing newline.
    /// </summary>
    private static byte[] EncodeRequest(SafeguardsClassifierRequest request)
    {
        var messages = new List<Dictionary<string, string>>
        {
            new(StringComparer.Ordinal) { ["role"] = "system", ["content"] = request.System },
            new(StringComparer.Ordinal) { ["role"] = "user", ["content"] = request.User },
        };

        return GoJson.Marshal(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = request.Model,
            ["messages"] = messages,
            ["max_tokens"] = request.MaxTokens,
            ["stream"] = false,
        });
    }

    /// <summary>
    /// Port of the classifier response struct decode plus the
    /// single-completion contract (safeguards_classifier.go:62-74). A null root
    /// decodes to the zero struct and therefore fails the contract; malformed
    /// JSON is a decode error.
    /// </summary>
    private static Choice DecodeSingleChoice(byte[]? body)
    {
        try
        {
            using var document = SafeguardsJson.ParseDocument(body ?? []);
            var choices = DecodeChoices(document.RootElement);
            if (choices.Count != 1)
            {
                throw new SafeguardsClassifierException(
                    "safeguards classifier returned incomplete or refused output");
            }

            return choices[0];
        }
        catch (JsonException ex)
        {
            throw new SafeguardsClassifierException(
                "decode safeguards classifier response: " + ex.Message,
                ex);
        }
    }

    private static List<Choice> DecodeChoices(JsonElement root)
    {
        var choices = new List<Choice>();
        if (root.ValueKind == JsonValueKind.Null)
        {
            return choices;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw DecodeError("json: cannot unmarshal value into Go value of type struct { Choices []struct { ... } }");
        }

        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, "choices", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Go's struct decoder keeps the last matching field.
            choices = DecodeChoicesValue(property.Value);
        }

        return choices;
    }

    private static List<Choice> DecodeChoicesValue(JsonElement element)
    {
        var choices = new List<Choice>();
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                return choices;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    choices.Add(DecodeChoice(item));
                }

                return choices;
            default:
                throw DecodeError(
                    "json: cannot unmarshal value into Go value of type []struct { FinishReason string; Message struct { ... } }");
        }
    }

    private static Choice DecodeChoice(JsonElement element)
    {
        var choice = new Choice();
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                return choice;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, "finish_reason", StringComparison.OrdinalIgnoreCase))
                    {
                        var finishReason = DecodeOptionalString(property.Value, "FinishReason");
                        if (finishReason is not null)
                        {
                            choice.FinishReason = finishReason;
                        }
                    }
                    else if (string.Equals(property.Name, "message", StringComparison.OrdinalIgnoreCase))
                    {
                        DecodeMessage(property.Value, choice);
                    }
                }

                return choice;
            default:
                throw DecodeError(
                    "json: cannot unmarshal value into Go value of type struct { FinishReason string; Message struct { ... } }");
        }
    }

    private static void DecodeMessage(JsonElement element, Choice choice)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                return;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, "content", StringComparison.OrdinalIgnoreCase))
                    {
                        var content = DecodeOptionalString(property.Value, "Content");
                        if (content is not null)
                        {
                            choice.Content = content;
                        }
                    }
                    else if (string.Equals(property.Name, "refusal", StringComparison.OrdinalIgnoreCase))
                    {
                        var refusal = DecodeOptionalString(property.Value, "Refusal");
                        if (refusal is not null)
                        {
                            choice.Refusal = refusal;
                        }
                    }
                }

                return;
            default:
                throw DecodeError(
                    "json: cannot unmarshal value into Go value of type struct { Content string; Refusal string }");
        }
    }

    /// <summary>Null is a no-op for Go string fields; other non-strings fail.</summary>
    private static string? DecodeOptionalString(JsonElement element, string field)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => element.GetString() ?? string.Empty,
            _ => throw DecodeError(
                "json: cannot unmarshal value into Go struct field ."
                + field
                + " of type string"),
        };
    }

    private static SafeguardsClassifierException DecodeError(string message)
        => new("decode safeguards classifier response: " + message);

    private sealed class Choice
    {
        public string FinishReason { get; set; } = string.Empty;

        public string Content { get; set; } = string.Empty;

        public string Refusal { get; set; } = string.Empty;
    }
}
