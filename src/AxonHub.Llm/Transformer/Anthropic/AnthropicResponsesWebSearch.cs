using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer.OpenAI.Responses;

namespace AxonHub.Llm.Transformer.Anthropic;

/// <summary>Port of anthropic.responsesSearchSource.</summary>
internal sealed class ResponsesSearchSource
{
    public string Url { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;
}

internal sealed class ResponsesSearchAction
{
    public string Query { get; set; } = string.Empty;

    public List<string> Queries { get; set; } = [];

    public List<ResponsesSearchSource> Sources { get; set; } = [];
}

internal sealed class ResponsesSearchCall
{
    public string Id { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public ResponsesSearchAction? Action { get; set; }
}

internal sealed class ResponsesSearchState
{
    public bool Started { get; set; }

    public bool Finished { get; set; }

    public int Order { get; set; }

    public ResponsesSearchCall Call { get; set; } = new();
}

/// <summary>
/// Port of llm/transformer/anthropic/responses_web_search.go: turns Responses
/// web_search_call metadata into Anthropic server_tool_use and
/// web_search_tool_result blocks. Responses search calls are metadata rather
/// than client function calls, so this adapter stays on the Anthropic side.
/// </summary>
internal static class AnthropicResponsesWebSearch
{
    public const string CallsMetadataKey = "openai_responses_web_search_calls";

    public static List<AnthropicMessageContentBlock> Blocks(
        Dictionary<string, JsonElement>? metadata,
        Dictionary<string, ResponsesSearchState> seen,
        bool final,
        IReadOnlyList<ResponsesSearchSource> citedSources)
    {
        JsonElement raw = default;
        var hasSnapshot = metadata is not null
            && metadata.TryGetValue(CallsMetadataKey, out raw)
            && raw.ValueKind != JsonValueKind.Null;
        if (!hasSnapshot && !final)
        {
            return [];
        }

        if (hasSnapshot)
        {
            List<ResponsesItem>? calls = null;
            try
            {
                calls = raw.Deserialize<List<ResponsesItem>>(LlmJson.Options);
            }
            catch (JsonException)
            {
                calls = null;
            }

            if (calls is not null)
            {
                foreach (var item in calls)
                {
                    var call = FromItem(item);
                    if (call.Id.Length == 0)
                    {
                        continue;
                    }

                    if (!seen.TryGetValue(call.Id, out var state))
                    {
                        state = new ResponsesSearchState { Order = seen.Count };
                    }

                    if (call.Status.Length == 0
                        || state.Call.Status is "completed" or "failed")
                    {
                        call.Status = state.Call.Status;
                    }

                    if (call.Action is null)
                    {
                        call.Action = state.Call.Action;
                    }
                    else if (state.Call.Action is { } earlier)
                    {
                        if (call.Action.Query.Length == 0)
                        {
                            call.Action.Query = earlier.Query;
                        }

                        if (call.Action.Queries.Count == 0)
                        {
                            call.Action.Queries = [.. earlier.Queries];
                        }

                        var sources = earlier.Sources
                            .Select(static source => new ResponsesSearchSource
                            {
                                Url = source.Url,
                                Title = source.Title,
                            })
                            .ToList();
                        foreach (var source in call.Action.Sources)
                        {
                            var index = sources.FindIndex(existing => existing.Url == source.Url);
                            if (index < 0)
                            {
                                sources.Add(source);
                            }
                            else if (source.Title.Length > 0)
                            {
                                sources[index] = source;
                            }
                        }

                        call.Action.Sources = sources;
                    }

                    state.Call = call;
                    seen[call.Id] = state;
                }
            }
        }

        var blocks = new List<AnthropicMessageContentBlock>();
        foreach (var callId in seen.Keys.OrderBy(id => seen[id].Order))
        {
            var state = seen[callId];
            var call = state.Call;
            var id = "srvtoolu_" + call.Id;
            var hasQuery = call.Action is not null
                && (call.Action.Query.Length > 0 || call.Action.Queries.Count > 0);

            if (!state.Started && (hasQuery || final))
            {
                var input = new Dictionary<string, object?>(StringComparer.Ordinal);
                if (call.Action is { } action)
                {
                    if (action.Query.Length > 0)
                    {
                        input["query"] = action.Query;
                    }
                    else if (action.Queries.Count == 1)
                    {
                        input["query"] = action.Queries[0];
                    }

                    if (action.Queries.Count > 1)
                    {
                        input["queries"] = action.Queries;
                    }
                }

                blocks.Add(new AnthropicMessageContentBlock
                {
                    Type = "server_tool_use",
                    Id = id,
                    Name = "web_search",
                    Input = AnthropicJson.RawElement(
                        Encoding.UTF8.GetBytes(GoJson.MarshalToString(input))),
                });
                state.Started = true;
            }

            if (final
                && !state.Finished
                && call.Status is "completed" or "failed")
            {
                var content = new AnthropicMessageContent();
                if (call.Status == "failed")
                {
                    content.SetRaw(
                        """{"type":"web_search_tool_result_error","error_code":"unavailable"}"""u8
                            .ToArray());
                }
                else
                {
                    // OpenAI does not provide Anthropic's encrypted_content; do
                    // not fabricate it or infer sources from prose. Empty source
                    // lists are valid because the cited answer is separate.
                    var sources = new List<Dictionary<string, string>>();
                    var urls = new HashSet<string>(StringComparer.Ordinal);
                    if (call.Action is { } action)
                    {
                        foreach (var source in action.Sources)
                        {
                            if (source.Url.Length == 0 || !urls.Add(source.Url))
                            {
                                continue;
                            }

                            var title = source.Title;
                            if (string.IsNullOrWhiteSpace(title))
                            {
                                // A matching citation can supply a title without
                                // changing which sources belong to this search.
                                foreach (var citation in citedSources)
                                {
                                    if (ResponsesSearchTitleUrl(citation.Url)
                                            == ResponsesSearchTitleUrl(source.Url)
                                        && !string.IsNullOrWhiteSpace(citation.Title))
                                    {
                                        title = citation.Title;
                                        break;
                                    }
                                }
                            }

                            if (string.IsNullOrWhiteSpace(title))
                            {
                                title = source.Url;
                            }

                            sources.Add(new Dictionary<string, string>(StringComparer.Ordinal)
                            {
                                ["type"] = "web_search_result",
                                ["url"] = source.Url,
                                ["title"] = title,
                            });
                        }
                    }

                    // Citations describe the answer, not a particular call. They
                    // are attributable only when exactly one search was observed.
                    if (sources.Count == 0 && seen.Count == 1)
                    {
                        foreach (var source in citedSources)
                        {
                            sources.Add(new Dictionary<string, string>(StringComparer.Ordinal)
                            {
                                ["type"] = "web_search_result",
                                ["url"] = source.Url,
                                ["title"] = source.Title,
                            });
                        }
                    }

                    content.SetRaw(Encoding.UTF8.GetBytes(GoJson.MarshalToString(sources)));
                }

                blocks.Add(new AnthropicMessageContentBlock
                {
                    Type = "web_search_tool_result",
                    ToolUseId = id,
                    Content = content,
                });
                state.Finished = true;
            }

            seen[callId] = state;
        }

        return blocks;
    }

    /// <summary>
    /// Port of responsesSearchTitleURL: ignore known tracking parameters only
    /// for title matching; result URLs remain original.
    /// </summary>
    public static string ResponsesSearchTitleUrl(string raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var parsed)
            || parsed.Host.Length == 0
            || (parsed.Scheme != "http" && parsed.Scheme != "https"))
        {
            return raw;
        }

        // Use the raw query text: System.Uri normalizes invalid escapes, while
        // Go's url.ParseQuery reports them and the reference keeps the raw URL.
        var queryStart = raw.IndexOf('?', StringComparison.Ordinal);
        var fragmentStart = raw.IndexOf('#', StringComparison.Ordinal);
        var rawQuery = queryStart < 0
            ? string.Empty
            : raw[(queryStart + 1)..(fragmentStart > queryStart ? fragmentStart : raw.Length)];
        var query = HttpClientUtils.ParseQuery(rawQuery, out var error);
        if (error is not null)
        {
            return raw;
        }

        foreach (var key in new[]
                 {
                     "utm_source",
                     "utm_medium",
                     "utm_campaign",
                     "utm_term",
                     "utm_content",
                 })
        {
            query.Remove(key);
        }

        var builder = new UriBuilder(parsed)
        {
            Query = HttpClientUtils.EncodeQuery(query),
        };
        return builder.Uri.AbsoluteUri;
    }

    /// <summary>
    /// Port of appendResponsesSearchCitations: only structured URL annotations
    /// are cited sources, not a reconstruction of every result.
    /// </summary>
    public static List<ResponsesSearchSource> AppendCitations(
        List<ResponsesSearchSource> sources,
        List<Annotation>? annotations)
    {
        if (annotations is null)
        {
            return sources;
        }

        foreach (var annotation in annotations)
        {
            var citation = annotation.UrlCitation;
            if (citation is null
                || (annotation.Type != "url_citation"
                    && annotation.Type != "web_search_result_location"))
            {
                continue;
            }

            var url = citation.Url ?? string.Empty;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
                || parsed.Host.Length == 0
                || (parsed.Scheme != "http" && parsed.Scheme != "https"))
            {
                continue;
            }

            var index = sources.FindIndex(source => source.Url == url);
            if (index >= 0)
            {
                if (!string.IsNullOrEmpty(citation.Title))
                {
                    sources[index].Title = citation.Title;
                }

                continue;
            }

            sources.Add(new ResponsesSearchSource
            {
                Url = url,
                Title = string.IsNullOrEmpty(citation.Title) ? url : citation.Title,
            });
        }

        return sources;
    }

    private static ResponsesSearchCall FromItem(ResponsesItem item)
    {
        var call = new ResponsesSearchCall
        {
            Id = item.Id,
            Status = item.Status ?? string.Empty,
        };
        if (item.Action?.WebSearch is { } action)
        {
            call.Action = new ResponsesSearchAction
            {
                Query = action.Query ?? string.Empty,
                Queries = action.Queries is { Count: > 0 } queries ? [.. queries] : [],
                Sources = action.Sources is { Count: > 0 } sources
                    ? sources.Select(static source => new ResponsesSearchSource
                    {
                        Url = source?.Url ?? string.Empty,
                        Title = source?.Title ?? string.Empty,
                    }).ToList()
                    : [],
            };
        }

        return call;
    }
}
