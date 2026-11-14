using System.Text.Json;
using AxonHub.Api.Auth;
using AxonHub.Api.Orchestration;
using AxonHub.Llm.Http;
using Microsoft.Extensions.Logging;

namespace AxonHub.Api.Safeguards;

/// <summary>Reviewer wiring for one HTTP route (nil disables safeguards).</summary>
public sealed class SafeguardsEndpointOptions
{
    /// <summary>
    /// The static <c>safeguards.Config</c> from the server configuration.
    /// Used when the request does not ask for server-side review or no
    /// provider is configured (internal/server/api/chat.go:99-109).
    /// </summary>
    public SafeguardsConfig Config { get; init; } = new();

    /// <summary>
    /// Port of <c>safeguardsConfigProvider</c>: re-reads the settings whenever
    /// the body carries a <c>dangerous_tool_use</c> safeguard.
    /// </summary>
    public Func<CancellationToken, Task<SafeguardsConfig>>? ConfigProvider { get; init; }

    /// <summary>
    /// Builds the reviewer seam for one request. The reference's
    /// <c>newSafeguardsClassifier</c> captures the handler's orchestrator at
    /// construction and reads the caller identity from the request context;
    /// the port passes the identity and the outer wire request (session seams)
    /// explicitly (C062).
    /// </summary>
    public Func<ChatCompletionOrchestrator, RequestIdentity, HttpRequestData, SafeguardsClassifierFunc>? ClassifierFactory { get; init; }

    /// <summary>
    /// Optional reviewer observability seam (live evaluator.go:287,301). Null
    /// keeps library callers and tests silent.
    /// </summary>
    public ILogger<SafeguardsEvaluator>? Logger { get; init; }
}

/// <summary>
/// Port of api.prepareSafeguardsRequest and the gjson opt-in probe
/// (internal/server/api/safeguards.go:14-52, chat.go:99-115).
/// </summary>
public static class SafeguardsRequest
{
    /// <summary>The prepared request plus the evaluator that reviews it.</summary>
    public sealed record Prepared(SafeguardsEvaluator? Evaluator, HttpRequestData Request);

    /// <summary>
    /// Port of
    /// <c>gjson.GetBytes(body, "safeguards.#(type==\"dangerous_tool_use\")").Exists()</c>:
    /// the first <c>safeguards</c> array is scanned for an element whose first
    /// <c>type</c> property is the exact dangerous-tool-use literal. gjson is
    /// more tolerant of malformed input than the strict parser used here
    /// (accepted difference C265).
    /// </summary>
    public static bool HasDangerousToolUse(byte[]? body)
    {
        if (body is null || body.Length == 0)
        {
            return false;
        }

        try
        {
            using var document = SafeguardsJson.ParseDocument(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, "safeguards", StringComparison.Ordinal))
                {
                    continue;
                }

                if (property.Value.ValueKind != JsonValueKind.Array)
                {
                    return false;
                }

                foreach (var item in property.Value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    foreach (var field in item.EnumerateObject())
                    {
                        if (!string.Equals(field.Name, "type", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        if (field.Value.ValueKind == JsonValueKind.String
                            && string.Equals(
                                field.Value.GetString(),
                                SafeguardsLiterals.DangerousToolUse,
                                StringComparison.Ordinal))
                        {
                            return true;
                        }

                        // gjson matches a single type value per object.
                        break;
                    }
                }

                return false;
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Port of <c>prepareSafeguardsRequest</c>: removes only the locally
    /// handled safeguard so pass-through channels do not independently review
    /// the same tool calls, and replaces the body without the obsolete content
    /// length.
    /// </summary>
    /// <exception cref="SafeguardsDecodeException">
    /// The request body, the safeguards array or one of its entries could not be
    /// decoded; the HTTP boundary maps this to 400.
    /// </exception>
    public static Prepared Prepare(
        HttpRequestData request,
        SafeguardsConfig config,
        SafeguardsClassifierFunc? call,
        ILogger<SafeguardsEvaluator>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(config);

        var evaluator = SafeguardsEvaluator.Create(request.Body, config, call, logger);
        if (evaluator is null)
        {
            return new Prepared(null, request);
        }

        var bodyKind = SafeguardsJson.ReadRoot(request.Body ?? [], StringComparer.Ordinal, out var body);
        if (bodyKind != SafeguardsRootKind.Object || body is null)
        {
            // Create already decoded the same bytes as a Go object struct, so
            // this only guards against internal inconsistency.
            throw new SafeguardsDecodeException(
                "decode safeguards request: expected JSON object");
        }

        if (!body.TryGetValue("safeguards", out var safeguardsRaw))
        {
            throw new SafeguardsDecodeException(
                "decode safeguards request: unexpected end of JSON input");
        }

        if (!SafeguardsJson.TryParseArray(safeguardsRaw, out var entries))
        {
            throw new SafeguardsDecodeException(
                "decode safeguards request: json: cannot unmarshal value into Go value of type []json.RawMessage");
        }

        var remaining = new List<byte[]>(entries?.Count ?? 0);
        foreach (var entry in entries ?? [])
        {
            var entryKind = SafeguardsJson.ReadRoot(entry, StringComparer.OrdinalIgnoreCase, out var entryFields);
            if (entryKind == SafeguardsRootKind.Invalid)
            {
                throw new SafeguardsDecodeException(
                    "decode safeguards request: invalid JSON document");
            }

            if (entryKind == SafeguardsRootKind.Null)
            {
                remaining.Add(entry);
                continue;
            }

            if (entryKind != SafeguardsRootKind.Object
                || entryFields is null
                || !SafeguardsJson.TryFieldString(entryFields, "type", out var entryType))
            {
                throw new SafeguardsDecodeException(
                    "decode safeguards request: json: cannot unmarshal value into Go value of type struct { Type string }");
            }

            if (!string.Equals(entryType, SafeguardsLiterals.DangerousToolUse, StringComparison.Ordinal))
            {
                remaining.Add(entry);
            }
        }

        if (remaining.Count == 0)
        {
            body.Remove("safeguards");
        }
        else
        {
            body["safeguards"] = SafeguardsJson.MarshalRawArray(remaining);
        }

        var prepared = CopyRequest(request);
        prepared.Body = SafeguardsJson.MarshalRawMap(body);
        prepared.Headers = CloneHeaders(request.Headers);
        if (prepared.Headers is not null)
        {
            // The body changed; do not forward an obsolete content length.
            HttpClientUtils.HeaderDel(prepared.Headers, "Content-Length");
        }

        return new Prepared(evaluator, prepared);
    }

    /// <summary>Go <c>prepared := *request</c> (field-wise shallow copy).</summary>
    private static HttpRequestData CopyRequest(HttpRequestData request) => new()
    {
        Method = request.Method,
        Url = request.Url,
        Path = request.Path,
        Query = request.Query,
        RouteValues = request.RouteValues,
        Headers = request.Headers,
        ContentType = request.ContentType,
        Body = request.Body,
        JsonBody = request.JsonBody,
        Auth = request.Auth,
        RequestId = request.RequestId,
        ClientIp = request.ClientIp,
        UserAgent = request.UserAgent,
        RequestType = request.RequestType,
        ApiFormat = request.ApiFormat,
        RawRequest = request.RawRequest,
        Metadata = request.Metadata,
        TransformerMetadata = request.TransformerMetadata,
        SkipInboundQueryMerge = request.SkipInboundQueryMerge,
        SessionId = request.SessionId,
        SessionScope = request.SessionScope,
        RequestCancellationToken = request.RequestCancellationToken,
        OnResponseHeaders = request.OnResponseHeaders,
    };

    /// <summary>Go <c>http.Header.Clone()</c>: one copied value slice per key.</summary>
    private static Dictionary<string, List<string>>? CloneHeaders(
        Dictionary<string, List<string>>? headers)
    {
        if (headers is null)
        {
            return null;
        }

        var clone = new Dictionary<string, List<string>>(headers.Count, StringComparer.Ordinal);
        foreach (var (key, values) in headers)
        {
            clone[key] = [.. values];
        }

        return clone;
    }
}
