using System.Text.Json;
using AxonHub.Llm;
using AxonHub.Llm.Json;
using AxonHub.Llm.Pipeline;

namespace AxonHub.Api.Prompts;

/// <summary>
/// Port of orchestrator.injectPrompts (internal/server/orchestrator/prompt.go:37-87):
/// match the project's enabled prompts against the request model and the
/// caller's API key, then prepend/append them to the unified messages — or,
/// for decisions requests, splice them into the raw input body.
/// </summary>
/// <remarks>
/// The reference resolves the project and the enabled prompt list from the
/// request context inside the middleware. The port loads that snapshot before
/// the pipeline (RunPipelineAsync), exactly like the prompt-protection
/// snapshot, so this hook stays synchronous; a provider error or a missing
/// project yields an empty list there and the middleware is a no-op, matching
/// the reference's fail-open branches.
/// </remarks>
internal sealed class InjectPromptsMiddleware(IReadOnlyList<Prompt> prompts, long apiKeyId)
    : DummyMiddleware("inject-prompts")
{
    public override LlmRequest TransformInboundLlmRequest(LlmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (prompts.Count == 0)
        {
            return request;
        }

        var matching = PromptMatcher.FilterMatchingPrompts(prompts, request.Model, apiKeyId);
        if (matching.Count == 0)
        {
            return request;
        }

        if (string.Equals(request.ApiFormat, ApiFormats.OpenAIDecisions, StringComparison.Ordinal))
        {
            // injectDecisionsPrompts mutates the raw body instead of the unified
            // messages; a splice failure aborts the request like the reference's
            // wrapped error.
            DecisionsPrompts.Inject(request, matching);
            return request;
        }

        return PromptMatcher.ApplyPrompts(request, matching);
    }
}

/// <summary>Port of orchestrator.injectDecisionsPrompts (prompt.go:89-146).</summary>
internal static class DecisionsPrompts
{
    /// <summary>
    /// Mutates <see cref="LlmRequest.Decisions"/>'s raw body: a string
    /// <c>input</c> gets every matching prompt prepended/appended, separated by
    /// newlines; an array <c>input</c> gets the first <c>input_text</c> content
    /// part whose message role matches a prompt's scope rewritten the same way,
    /// and the function returns after that first part.
    /// </summary>
    public static void Inject(LlmRequest request, IReadOnlyList<Prompt> prompts)
    {
        var decisions = request.Decisions;
        if (decisions is null || decisions.Body.Length == 0)
        {
            return;
        }

        var body = decisions.Body;
        var input = DecisionsRawJson.GetTopLevel(body, "input"u8);
        if (!input.Exists)
        {
            return;
        }

        if (input.Kind == JsonTokenType.String)
        {
            // The string form has no role, so prompt scopes are not consulted.
            var text = input.Text;
            foreach (var prompt in prompts)
            {
                if (prompt is null)
                {
                    continue;
                }

                text = IsAppend(prompt)
                    ? text + "\n" + prompt.Content
                    : prompt.Content + "\n" + text;
            }

            decisions.Body = RawJsonStringSet.SetKey(body, "input", text);
            return;
        }

        var itemCount = DecisionsRawJson.TopLevelArrayLength(body, "input"u8);
        for (var i = 0; i < itemCount; i++)
        {
            var role = DecisionsRawJson.GetInputItemString(body, i, "role"u8);
            if (role.Length == 0)
            {
                role = "user";
            }

            var contentCount = DecisionsRawJson.GetInputItemArrayLength(body, i, "content"u8);
            for (var j = 0; j < contentCount; j++)
            {
                var type = DecisionsRawJson.GetContentPartString(body, i, j, "type"u8);
                if (!string.Equals(type, "input_text", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var text = DecisionsRawJson.GetContentPartString(body, i, j, "text"u8);
                foreach (var prompt in prompts)
                {
                    if (prompt is null || !PromptScopeMatches(prompt.Role, role))
                    {
                        continue;
                    }

                    text = IsAppend(prompt)
                        ? text + "\n" + prompt.Content
                        : prompt.Content + "\n" + text;
                }

                var updated = DecisionsRawJson.SetContentPartString(body, i, j, "text"u8, text);
                if (updated is not null)
                {
                    decisions.Body = updated;
                }

                return;
            }
        }
    }

    /// <summary>Port of orchestrator.promptScopeMatches.</summary>
    public static bool PromptScopeMatches(string promptRole, string inputRole)
        => promptRole.Length == 0
            || string.Equals(promptRole, inputRole, StringComparison.Ordinal)
            || string.Equals(promptRole, "system", StringComparison.Ordinal);

    private static bool IsAppend(Prompt prompt)
        => string.Equals(
            prompt.Settings.Action?.Type,
            PromptActionTypes.Append,
            StringComparison.Ordinal);
}
