using System.Text.Json;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer;

namespace AxonHub.Llm.Transformer.OpenAI.Responses;

/// <summary>
/// Port of llm/transformer/openai/responses/namespace.go: encodes native
/// namespace/local function names into the unified model and restores them on
/// the response side.
/// </summary>
internal static class ResponsesNamespace
{
    /// <summary>Go: <c>namespaceMappingMetadataKey</c>.</summary>
    public const string NamespaceMappingMetadataKey = "namespace_mapping";

    /// <summary>
    /// Go: <c>flatFunctionName</c>. Names are encoded only when entering the
    /// unified model from native data; a native local name may itself begin
    /// with the namespace prefix, so trimming happens exactly once.
    /// </summary>
    public static string FlatFunctionName(string? @namespace, string? name)
    {
        if (string.IsNullOrEmpty(@namespace) || string.IsNullOrEmpty(name))
        {
            return name ?? string.Empty;
        }

        return @namespace + "__" + name;
    }

    /// <summary>Go: <c>localFunctionName</c>.</summary>
    public static string LocalFunctionName(string? @namespace, string? name)
    {
        name ??= string.Empty;
        if (string.IsNullOrEmpty(@namespace))
        {
            return name;
        }

        var prefix = @namespace + "__";
        return name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..] : name;
    }

    /// <summary>
    /// Go: <c>flattenRequestFunctionNames</c>. Shallow-copies the request and
    /// the slices it edits (Go struct-copy semantics), validating name
    /// conflicts without modifying <paramref name="src"/>.
    /// </summary>
    public static LlmRequest FlattenRequestFunctionNames(LlmRequest src)
    {
        var req = src.ShallowClone();
        req.Tools = CloneTools(src.Tools);
        req.Messages = CloneMessages(src.Messages);

        var identities = new Dictionary<string, (string Namespace, string Name)>(StringComparer.Ordinal);

        string Register(string @namespace, string name)
        {
            if (@namespace.Length > 0 && name.Length == 0)
            {
                throw TransformerErrors.InvalidRequest("namespace function name is empty");
            }

            var flat = FlatFunctionName(@namespace, name);
            var identity = (@namespace, name);
            if (identities.TryGetValue(flat, out var previous) && previous != identity)
            {
                throw TransformerErrors.InvalidRequest(
                    "namespace tool flat name " + ResponsesCreatedAt.GoQuote(flat) + " conflicts with another function");
            }

            identities[flat] = identity;
            return flat;
        }

        var declared = new HashSet<string>(StringComparer.Ordinal);
        if (req.Tools is { } tools)
        {
            foreach (var tool in tools)
            {
                if (tool.Type != ToolTypes.Function)
                {
                    continue;
                }

                var flat = Register(tool.Function.Namespace ?? string.Empty, tool.Function.Name);
                if (!declared.Add(flat))
                {
                    throw TransformerErrors.InvalidRequest(
                        "duplicate function tool name " + ResponsesCreatedAt.GoQuote(flat));
                }

                tool.Function.Name = flat;
            }
        }

        for (var i = 0; i < req.Messages.Count; i++)
        {
            req.Messages[i].ToolCalls = CloneToolCalls(src.Messages[i].ToolCalls);
            if (req.Messages[i].ToolCalls is not { } calls)
            {
                continue;
            }

            foreach (var call in calls)
            {
                if (call.Type is not (null or ToolTypes.Function))
                {
                    continue;
                }

                var flat = Register(call.Function.Namespace ?? string.Empty, call.Function.Name);
                call.Function.Name = flat;
            }
        }

        return req;
    }

    /// <summary>
    /// Go: <c>namespaceMetadata</c>. Records the inbound identities used to
    /// restore Responses output, replacing any previous mapping. The mapping is
    /// immutable after creation; the pipeline transports it as opaque metadata.
    /// </summary>
    public static Dictionary<string, JsonElement>? NamespaceMetadata(LlmRequest? src)
    {
        if (src is null)
        {
            return null;
        }

        Dictionary<string, string>? mapping = null;

        void Add(string name, string? @namespace)
        {
            if (name.Length == 0 || string.IsNullOrEmpty(@namespace))
            {
                return;
            }

            mapping ??= new Dictionary<string, string>(StringComparer.Ordinal);
            mapping[name] = @namespace;
        }

        if (src.Tools is { } tools)
        {
            foreach (var tool in tools)
            {
                if (tool.Type == ToolTypes.Function)
                {
                    Add(tool.Function.Name, tool.Function.Namespace);
                }
            }
        }

        foreach (var message in src.Messages)
        {
            if (message.ToolCalls is not { } calls)
            {
                continue;
            }

            foreach (var call in calls)
            {
                if (call.Type is null or ToolTypes.Function)
                {
                    Add(call.Function.Name, call.Function.Namespace);
                }
            }
        }

        // Go: maps.Clone then delete the key first, so a re-encoded mapping
        // never leaks through from the source metadata.
        var metadata = src.TransformerMetadata is { } existing
            ? new Dictionary<string, JsonElement>(existing, StringComparer.Ordinal)
            : null;
        metadata?.Remove(NamespaceMappingMetadataKey);

        if (mapping is { Count: > 0 })
        {
            metadata ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            // The live map[string]string becomes a sorted JSON object, exactly
            // as Go would re-marshal it when the metadata is persisted.
            metadata[NamespaceMappingMetadataKey] = JsonSerializer.SerializeToElement(mapping, LlmJson.Options);
        }

        return metadata;
    }

    /// <summary>
    /// Go: <c>namespaceForName</c>. Accepts the original map and its
    /// JSON-decoded form; the port always stores the decoded JSON object.
    /// </summary>
    public static string NamespaceForName(Dictionary<string, JsonElement>? metadata, string name)
    {
        if (metadata is null
            || !metadata.TryGetValue(NamespaceMappingMetadataKey, out var element)
            || element.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        return element.TryGetProperty(name, out var @namespace) && @namespace.ValueKind == JsonValueKind.String
            ? @namespace.GetString()!
            : string.Empty;
    }

    /// <summary>
    /// Go: <c>mapResponseFunctionNames</c>. Copies only the edited portions of
    /// a response; stream decoders may retain their own tool calls or return
    /// the same Current repeatedly, so the source must never be mutated.
    /// </summary>
    public static LlmResponse? MapResponseFunctionNames(LlmResponse? src, bool nativeToUnified)
    {
        if (src is null || ReferenceEquals(src, LlmResponse.DoneResponse))
        {
            return src;
        }

        var metadata = src.TransformerMetadata;

        Message? Convert(Message? message)
        {
            if (message is null)
            {
                return null;
            }

            var result = message;
            if (message.ToolCalls is not { } toolCalls)
            {
                return result;
            }

            for (var i = 0; i < toolCalls.Count; i++)
            {
                var call = toolCalls[i];
                if (call.Type is not (null or "function"))
                {
                    continue;
                }

                // Go: function := call.Function — a struct value copy.
                var function = call.Function.ShallowClone();
                if (nativeToUnified)
                {
                    function.Name = FlatFunctionName(function.Namespace, function.Name);
                }
                else
                {
                    if (string.IsNullOrEmpty(function.Namespace))
                    {
                        function.Namespace = NamespaceForName(metadata, function.Name);
                    }

                    function.Name = LocalFunctionName(function.Namespace, function.Name);
                }

                if (FunctionCallEquals(function, call.Function))
                {
                    continue;
                }

                if (ReferenceEquals(result, message))
                {
                    var copied = message.ShallowClone();
                    copied.ToolCalls = CloneToolCalls(message.ToolCalls);
                    result = copied;
                }

                result.ToolCalls![i].Function = function;
            }

            return result;
        }

        var mapped = src;
        for (var i = 0; i < src.Choices.Count; i++)
        {
            var choice = src.Choices[i];
            var message = Convert(choice.Message);
            var delta = Convert(choice.Delta);
            if (ReferenceEquals(message, choice.Message) && ReferenceEquals(delta, choice.Delta))
            {
                continue;
            }

            if (ReferenceEquals(mapped, src))
            {
                var copied = src.ShallowClone();
                copied.Choices = [.. src.Choices];
                mapped = copied;
            }

            // Go's slices.Clone copies the Choice structs, so assigning the
            // mapped message/delta never touches the source choice.
            var mappedChoice = src.Choices[i].ShallowClone();
            mappedChoice.Message = message;
            mappedChoice.Delta = delta;
            mapped.Choices[i] = mappedChoice;
        }

        return mapped;
    }

    /// <summary>Go struct equality for FunctionCall (Name, Namespace, Arguments).</summary>
    private static bool FunctionCallEquals(FunctionCall a, FunctionCall b)
        => string.Equals(a.Namespace ?? string.Empty, b.Namespace ?? string.Empty, StringComparison.Ordinal)
            && string.Equals(a.Name, b.Name, StringComparison.Ordinal)
            && string.Equals(a.Arguments, b.Arguments, StringComparison.Ordinal);

    /// <summary>Go: <c>slices.Clone(src.Tools)</c> with struct-copy semantics.</summary>
    private static List<Tool>? CloneTools(List<Tool>? src)
    {
        if (src is null)
        {
            return null;
        }

        var cloned = new List<Tool>(src.Count);
        foreach (var tool in src)
        {
            var copy = tool.ShallowClone();
            copy.Function = tool.Function.ShallowClone();
            cloned.Add(copy);
        }

        return cloned;
    }

    /// <summary>Go: <c>slices.Clone(src.Messages)</c> with struct-copy semantics.</summary>
    private static List<Message> CloneMessages(List<Message> src)
    {
        var cloned = new List<Message>(src.Count);
        foreach (var message in src)
        {
            cloned.Add(message.ShallowClone());
        }

        return cloned;
    }

    /// <summary>Go: <c>slices.Clone(src.ToolCalls)</c> with struct-copy semantics.</summary>
    private static List<ToolCall>? CloneToolCalls(List<ToolCall>? src)
    {
        if (src is null)
        {
            return null;
        }

        var cloned = new List<ToolCall>(src.Count);
        foreach (var call in src)
        {
            var copy = call.ShallowClone();
            copy.Function = call.Function.ShallowClone();
            cloned.Add(copy);
        }

        return cloned;
    }
}
