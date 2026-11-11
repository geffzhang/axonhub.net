using System.Text;
using System.Text.Json;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port of internal/server/orchestrator/override_test.go: the channel-level body
/// and header rewrite middlewares, covering template rendering, the JSON media
/// gate and every override operation.
/// </summary>
/// <remarks>
/// The reference tests build a <c>PersistenceState</c> and call the middleware
/// closures directly. The port reaches the same state by running one outbound
/// transform through a stub transformer, which is also what captures
/// <see cref="PersistentOutboundTransformer.OriginalModel"/> and the unified
/// request the templates render against.
/// </remarks>
public sealed class ChannelOverrideTests
{
    private const string JsonContentType = "application/json";

    private const string UpstreamUrl = "https://api.example.com/v1/chat/completions";

    // ------------------------------------------------------------------
    // Fixtures
    // ------------------------------------------------------------------

    /// <summary>
    /// Minimal outbound transformer: it accepts the unified request without
    /// building a provider body (the override tests only exercise the raw-request
    /// middlewares).
    /// </summary>
    private sealed class StubOutboundTransformer : IOutboundTransformer
    {
        public string ApiFormat => ApiFormats.OpenAIChatCompletion;

        public HttpRequestData TransformRequest(LlmRequest? request) => new();

        public LlmResponse TransformResponse(HttpResponseData? response)
            => throw new NotSupportedException();

        public IStream<LlmResponse> TransformStream(HttpRequestData? request, IStream<StreamEvent> stream)
            => throw new NotSupportedException();

        public ResponseError TransformError(HttpClientError? error)
            => throw new NotSupportedException();

        public byte[] AggregateStreamChunks(
            HttpRequestData? request,
            IReadOnlyList<StreamEvent> chunks,
            out ResponseMeta meta)
            => throw new NotSupportedException();
    }

    private static ChannelCandidate Candidate(string? settingsJson, string actualModel) => new()
    {
        ChannelId = 1,
        ChannelName = "test-channel",
        ActualModel = actualModel,
        OrderingWeight = 0,
        Type = "openai",
        BaseUrl = "http://upstream.test",
        Credentials = "{\"apiKey\":\"sk-test\"}",
        OverrideSettings = ChannelOverrideSettings.Parse(settingsJson),
    };

    /// <summary>
    /// Runs one outbound transform so the middleware sees the same state the
    /// reference constructs directly, then returns the transformer.
    /// </summary>
    private static PersistentOutboundTransformer Outbound(
        string? settingsJson,
        LlmRequest llmRequest,
        string? actualModel = null)
    {
        var outbound = new PersistentOutboundTransformer(
            [Candidate(settingsJson, actualModel ?? llmRequest.Model)],
            TimeProvider.System,
            new RecordingMetricsRecorder(),
            static (_, _, _) => new StubOutboundTransformer());

        outbound.TransformRequest(llmRequest);
        return outbound;
    }

    private static HttpRequestData RunBody(
        string? settingsJson,
        LlmRequest llmRequest,
        HttpRequestData request,
        string? actualModel = null)
        => new ChannelOverrideBodyMiddleware(Outbound(settingsJson, llmRequest, actualModel))
            .TransformOutboundRawRequest(request);

    private static HttpRequestData RunHeaders(
        string? settingsJson,
        LlmRequest llmRequest,
        HttpRequestData request,
        string? actualModel = null)
        => new ChannelOverrideHeaderMiddleware(Outbound(settingsJson, llmRequest, actualModel))
            .TransformOutboundRawRequest(request);

    /// <summary>Stores a legacy <c>overrideParameters</c> raw string.</summary>
    private static string LegacyParameters(string json)
        => "{\"overrideParameters\":" + JsonSerializer.Serialize(json) + "}";

    /// <summary>Stores legacy <c>overrideHeaders</c> entries.</summary>
    private static string LegacyHeaders(params (string Key, string Value)[] entries)
        => "{\"overrideHeaders\":["
            + string.Join(",", entries.Select(static entry =>
                "{\"key\":" + JsonSerializer.Serialize(entry.Key)
                + ",\"value\":" + JsonSerializer.Serialize(entry.Value) + "}"))
            + "]}";

    private static string BodyOps(params string[] operations)
        => "{\"bodyOverrideOperations\":[" + string.Join(",", operations) + "]}";

    private static string HeaderOps(params string[] operations)
        => "{\"headerOverrideOperations\":[" + string.Join(",", operations) + "]}";

    private static HttpRequestData Request(byte[] body, string? contentType = JsonContentType, bool withHeader = true)
    {
        Dictionary<string, List<string>>? headers = null;
        if (contentType is not null && withHeader)
        {
            headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = [contentType],
            };
        }

        return new HttpRequestData
        {
            Method = "POST",
            Url = UpstreamUrl,
            Headers = headers,
            ContentType = contentType ?? string.Empty,
            Body = body,
        };
    }

    private static HttpRequestData JsonRequest(string json, string? contentType = JsonContentType)
        => Request(Encoding.UTF8.GetBytes(json), contentType);

    private static HttpRequestData HeaderRequest(params (string Key, string Value)[] headers)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (key, value) in headers)
        {
            map[key] = [value];
        }

        return new HttpRequestData
        {
            Method = "POST",
            Url = UpstreamUrl,
            Headers = map,
        };
    }

    private static HttpRequestData NoHeadersRequest()
        => new() { Method = "POST", Url = UpstreamUrl };

    // ------------------------------------------------------------------
    // Assertions
    // ------------------------------------------------------------------

    private static JsonLookup Lookup(HttpRequestData request, string path)
        => OverrideJson.Get(request.Body ?? [], path);

    private static string Text(HttpRequestData request)
        => Encoding.UTF8.GetString(request.Body ?? []);

    private static void AssertString(HttpRequestData request, string path, string expected)
        => Assert.Equal(expected, Lookup(request, path).StringValue);

    private static void AssertNumber(HttpRequestData request, string path, double expected)
    {
        var scalar = Assert.IsType<OverrideJsonScalar>(Lookup(request, path).Node);
        Assert.Equal(OverrideJsonKind.Number, scalar.Kind);
        Assert.Equal(expected, scalar.NumberValue);
    }

    private static void AssertBool(HttpRequestData request, string path, bool expected)
    {
        var scalar = Assert.IsType<OverrideJsonScalar>(Lookup(request, path).Node);
        Assert.Equal(expected ? OverrideJsonKind.True : OverrideJsonKind.False, scalar.Kind);
    }

    private static void AssertMissing(HttpRequestData request, string path)
        => Assert.False(Lookup(request, path).Exists);

    private static void AssertRaw(HttpRequestData request, string path, string expectedRaw)
        => Assert.Equal(expectedRaw, Lookup(request, path).Node?.Raw);

    private static void AssertJsonEqual(string expected, string actual)
    {
        using var expectedDocument = JsonDocument.Parse(expected);
        using var actualDocument = JsonDocument.Parse(actual);
        AssertElementEqual(expectedDocument.RootElement, actualDocument.RootElement);
    }

    private static void AssertElementEqual(JsonElement expected, JsonElement actual)
    {
        Assert.Equal(expected.ValueKind, actual.ValueKind);

        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                {
                    var expectedMembers = expected.EnumerateObject()
                        .ToDictionary(static member => member.Name, static member => member.Value, StringComparer.Ordinal);
                    var actualMembers = actual.EnumerateObject()
                        .ToDictionary(static member => member.Name, static member => member.Value, StringComparer.Ordinal);

                    Assert.Equal(
                        expectedMembers.Keys.Order(StringComparer.Ordinal),
                        actualMembers.Keys.Order(StringComparer.Ordinal));

                    foreach (var (name, value) in expectedMembers)
                    {
                        AssertElementEqual(value, actualMembers[name]);
                    }

                    return;
                }

            case JsonValueKind.Array:
                {
                    var expectedItems = expected.EnumerateArray().ToArray();
                    var actualItems = actual.EnumerateArray().ToArray();
                    Assert.Equal(expectedItems.Length, actualItems.Length);
                    for (var index = 0; index < expectedItems.Length; index++)
                    {
                        AssertElementEqual(expectedItems[index], actualItems[index]);
                    }

                    return;
                }

            case JsonValueKind.Number:
                Assert.Equal(expected.GetDouble(), actual.GetDouble());
                return;

            default:
                Assert.Equal(expected.GetRawText(), actual.GetRawText());
                return;
        }
    }

    // ------------------------------------------------------------------
    // Templates (override_test.go:19-590)
    // ------------------------------------------------------------------

    /// <summary>Port of TestOverrideParametersWithTemplate.</summary>
    [Fact]
    public void TemplatesRenderModelReasoningMetadataAndNestedJson()
    {
        var llmRequest = new LlmRequest
        {
            Model = "gpt-4",
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["user_id"] = "user-123" },
            ReasoningEffort = "high",
        };

        var request = RunBody(
            LegacyParameters(
                """{"custom_field": "model-{{.Model}}", "effort_field": "effort-{{.ReasoningEffort}}", "user_field": "user-{{index .Metadata \"user_id\"}}", "json_field": "{\"id\": \"{{.Model}}\", \"val\": 123}"}"""),
            llmRequest,
            JsonRequest("{}"));

        AssertString(request, "custom_field", "model-gpt-4");
        AssertString(request, "effort_field", "effort-high");
        AssertString(request, "user_field", "user-user-123");

        // A rendered value that is a JSON document is decoded and set as an object.
        Assert.IsType<OverrideJsonObject>(Lookup(request, "json_field").Node);
        AssertString(request, "json_field.id", "gpt-4");
        AssertNumber(request, "json_field.val", 123);

        var headerRequest = RunHeaders(
            LegacyHeaders(("X-Custom-Model", "header-{{.Model}}")),
            llmRequest,
            NoHeadersRequest());

        Assert.Equal("header-gpt-4", HttpClientUtils.HeaderGet(headerRequest.Headers, "X-Custom-Model"));
    }

    /// <summary>Port of TestOverrideParametersWithMappedRequestModel.</summary>
    [Fact]
    public void ConditionSeesTheOriginalRequestModel()
    {
        var llmRequest = new LlmRequest { Model = "gpt-4-fast" };

        var request = RunBody(
            BodyOps(
                """{"op":"set","path":"service_tier","value":"priority","condition":"{{or (eq .RequestModel \"gpt-4-fast\") (eq .RequestModel \"gpt-4-priority\")}}"}"""),
            llmRequest,
            JsonRequest("""{"model":"gpt-4"}"""),
            actualModel: "gpt-4");

        AssertString(request, "service_tier", "priority");
    }

    /// <summary>Port of TestOverrideParametersWithRequestHeaderTemplate.</summary>
    [Fact]
    public void TemplatesReadFilteredRequestHeaders()
    {
        var llmRequest = new LlmRequest
        {
            Model = "gpt-4",
            RawRequest = new HttpRequestData
            {
                Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
                {
                    ["X-Trace-Id"] = ["trace-123"],
                    ["X-Multi-Value"] = ["first", "second"],
                    ["Authorization"] = ["Bearer secret"],
                    ["Api-Key"] = ["secret-api-key"],
                    ["X-Google-Api-Key"] = ["google-secret"],
                },
            },
        };

        var request = RunBody(
            LegacyParameters(
                """{"trace_id_lower": "{{index .RequestHeader \"x-trace-id\"}}", "trace_id_canonical": "{{index .RequestHeader \"X-Trace-Id\"}}", "multi_value": "{{index .RequestHeader \"x-multi-value\"}}", "authorization": "{{index .RequestHeader \"authorization\"}}", "api_key": "{{index .RequestHeader \"api-key\"}}", "x_google_api_key": "{{index .RequestHeader \"x-google-api-key\"}}"}"""),
            llmRequest,
            JsonRequest("{}"));

        AssertString(request, "trace_id_lower", "trace-123");
        AssertString(request, "trace_id_canonical", "trace-123");
        AssertString(request, "multi_value", "first");
        AssertString(request, "authorization", string.Empty);
        AssertString(request, "api_key", string.Empty);
        AssertString(request, "x_google_api_key", string.Empty);
    }

    /// <summary>Port of TestOverrideParametersWithRequestHeaderTemplate_LowercaseSensitiveHeaders.</summary>
    [Fact]
    public void TemplatesReadLowercaseHeadersAndSkipSensitiveOnes()
    {
        var llmRequest = new LlmRequest
        {
            Model = "gpt-4",
            RawRequest = new HttpRequestData
            {
                Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
                {
                    ["authorization"] = ["Bearer secret"],
                    ["api-key"] = ["secret-api-key"],
                    ["x-goog-api-key"] = ["goog-secret"],
                    ["x-trace-id"] = ["trace-lowercase"],
                },
            },
        };

        var request = RunBody(
            LegacyParameters(
                """{"trace_id": "{{index .RequestHeader \"x-trace-id\"}}", "authorization": "{{index .RequestHeader \"authorization\"}}", "api_key": "{{index .RequestHeader \"api-key\"}}", "x_goog_api_key": "{{index .RequestHeader \"x-goog-api-key\"}}"}"""),
            llmRequest,
            JsonRequest("{}"));

        AssertString(request, "trace_id", "trace-lowercase");
        AssertString(request, "authorization", string.Empty);
        AssertString(request, "api_key", string.Empty);
        AssertString(request, "x_goog_api_key", string.Empty);
    }

    /// <summary>Port of TestOverrideParametersComplex.</summary>
    [Fact]
    public void TemplatesSupportIfElseNestedJsonAndClearDirective()
    {
        var llmRequest = new LlmRequest
        {
            Model = "gpt-4",
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["env"] = "prod" },
            ReasoningEffort = "low",
        };

        var request = RunBody(
            LegacyParameters(
                """{"logic_field": "{{if eq .Model \"gpt-4\"}}is-gpt-4{{else}}not-gpt-4{{end}}", "effort_logic": "{{if eq .ReasoningEffort \"high\"}}high-effort{{else}}low-effort{{end}}", "json_complex": "{\"array\": [1, 2, \"{{.Model}}\"], \"nested\": {\"key\": \"val\"}}", "clear_me": "__AXONHUB_CLEAR__"}"""),
            llmRequest,
            JsonRequest("""{"clear_me": "to-be-deleted", "keep_me": "stay"}"""));

        AssertString(request, "logic_field", "is-gpt-4");
        AssertString(request, "effort_logic", "low-effort");
        Assert.IsType<OverrideJsonObject>(Lookup(request, "json_complex").Node);
        AssertString(request, "json_complex.array.2", "gpt-4");
        AssertString(request, "json_complex.nested.key", "val");
        AssertMissing(request, "clear_me");
        AssertString(request, "keep_me", "stay");

        var headerRequest = RunHeaders(
            LegacyHeaders(
                ("X-Logic-Header", "{{if .Metadata.env}}env-{{.Metadata.env}}{{else}}no-env{{end}}"),
                ("X-Clear-Header", "__AXONHUB_CLEAR__")),
            llmRequest,
            HeaderRequest(("X-Clear-Header", "to-be-deleted")));

        Assert.Equal(string.Empty, HttpClientUtils.HeaderGet(headerRequest.Headers, "X-Clear-Header"));
        Assert.Equal("env-prod", HttpClientUtils.HeaderGet(headerRequest.Headers, "X-Logic-Header"));
    }

    /// <summary>Port of TestOverrideParametersNumeric.</summary>
    [Fact]
    public void RenderedNumbersBecomeJsonNumbersAndHeaderStrings()
    {
        var llmRequest = new LlmRequest
        {
            Model = "gpt-4",
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["temp"] = "0.8",
                ["max"] = "500",
            },
        };

        var request = RunBody(
            LegacyParameters(
                """{"direct_int": 100, "direct_float": 0.5, "template_int": "{{.Metadata.max}}", "template_float": "{{.Metadata.temp}}", "mixed_json": "{\"val\": {{.Metadata.max}}, \"active\": true}"}"""),
            llmRequest,
            JsonRequest("{}"));

        AssertNumber(request, "direct_int", 100);
        AssertNumber(request, "direct_float", 0.5);
        AssertNumber(request, "template_int", 500);
        AssertNumber(request, "template_float", 0.8);

        Assert.IsType<OverrideJsonObject>(Lookup(request, "mixed_json").Node);
        AssertNumber(request, "mixed_json.val", 500);
        AssertBool(request, "mixed_json.active", true);

        var headerRequest = RunHeaders(
            LegacyHeaders(("X-Int-Header", "{{.Metadata.max}}"), ("X-Float-Header", "{{.Metadata.temp}}")),
            llmRequest,
            NoHeadersRequest());

        Assert.Equal("500", HttpClientUtils.HeaderGet(headerRequest.Headers, "X-Int-Header"));
        Assert.Equal("0.8", HttpClientUtils.HeaderGet(headerRequest.Headers, "X-Float-Header"));
    }

    /// <summary>Port of TestOverrideHeadersKeepJSONLikeString.</summary>
    [Fact]
    public void HeaderSetKeepsAJsonLikeStringVerbatim()
    {
        var llmRequest = new LlmRequest { Model = "gpt-4" };
        const string Expected = """{"session_id":"843634473","camelCase":"AbC","xTraceId":"XyZ-001"}""";

        var request = RunHeaders(
            HeaderOps(
                """{"op":"set","path":"Extra","value":"{\"session_id\":\"843634473\",\"camelCase\":\"AbC\",\"xTraceId\":\"XyZ-001\"}"}"""),
            llmRequest,
            NoHeadersRequest());

        Assert.Equal(Expected, HttpClientUtils.HeaderGet(request.Headers, "Extra"));
    }

    /// <summary>Port of TestOverrideHeadersWithPromptCacheKeyTemplate.</summary>
    [Fact]
    public void PromptCacheKeyTemplateEscapesJsonAndIsSkippedWhenAbsent()
    {
        const string Operations =
            """{"op":"set","path":"Extra","value":"{\"session_id\":{{toJSON .PromptCacheKey}}}","condition":"{{if .PromptCacheKey}}true{{end}}"}""";

        // The key is JSON-escaped so it cannot break out of the header document.
        var escaped = RunHeaders(
            HeaderOps(Operations),
            new LlmRequest { Model = "gpt-5.5", PromptCacheKey = "cache-key-123\",\"admin\":true" },
            NoHeadersRequest());

        var header = HttpClientUtils.HeaderGet(escaped.Headers, "Extra");
        Assert.Equal("""{"session_id":"cache-key-123\",\"admin\":true"}""", header);
        using (var document = JsonDocument.Parse(header))
        {
            Assert.Single(document.RootElement.EnumerateObject());
        }

        // Absent key renders an empty condition, so the header is not set.
        var absent = RunHeaders(
            HeaderOps(Operations),
            new LlmRequest { Model = "gpt-5.5" },
            NoHeadersRequest());

        Assert.Equal(string.Empty, HttpClientUtils.HeaderGet(absent.Headers, "Extra"));

        // The top-level prompt cache key wins over the compact one (the
        // fallback is covered by PromptCacheKeyFallsBackToTheCompactRequest).
        var topLevel = RunHeaders(
            HeaderOps(Operations),
            new LlmRequest { Model = "gpt-5.5", PromptCacheKey = "top-level-cache-key" },
            NoHeadersRequest());

        Assert.Equal(
            """{"session_id":"top-level-cache-key"}""",
            HttpClientUtils.HeaderGet(topLevel.Headers, "Extra"));
    }

    /// <summary>
    /// Reference case "uses prompt cache key from compact request"
    /// (override_test.go): when the top-level key is absent the render context
    /// falls back to CompactRequest.PromptCacheKey (override.go:81-85).
    /// </summary>
    [Fact]
    public void PromptCacheKeyFallsBackToTheCompactRequest()
    {
        var request = RunHeaders(
            HeaderOps(
                """{"op":"set","path":"Extra","value":"{\"session_id\":{{toJSON .PromptCacheKey}}}"}"""),
            new LlmRequest
            {
                Model = "gpt-5.5",
                Compact = new CompactRequest { PromptCacheKey = "compact-cache-key" },
            },
            NoHeadersRequest());

        Assert.Equal(
            """{"session_id":"compact-cache-key"}""",
            HttpClientUtils.HeaderGet(request.Headers, "Extra"));
    }

    /// <summary>Port of TestOverrideParametersWithRequestHeaderTemplate_NoRawRequest.</summary>
    [Fact]
    public void MissingRawRequestYieldsEmptyHeaderValues()
    {
        var llmRequest = new LlmRequest { Model = "gpt-4.1" };

        var request = RunBody(
            BodyOps(
                """{"op":"set","path":"missing_header","value":"{{index .RequestHeader \"x-trace-id\"}}"}""",
                """{"op":"set","path":"model_value","value":"{{.Model}}"}"""),
            llmRequest,
            JsonRequest("{}"));

        AssertString(request, "missing_header", string.Empty);
        AssertString(request, "model_value", "gpt-4.1");
    }

    /// <summary>Port of TestOverrideHeadersWithRequestHeaderTemplate.</summary>
    [Fact]
    public void HeaderTemplatesReadFilteredRequestHeaders()
    {
        var llmRequest = new LlmRequest
        {
            Model = "gpt-4.1",
            RawRequest = new HttpRequestData
            {
                Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
                {
                    ["X-Trace-Id"] = ["trace-123"],
                    ["X-Api-Key"] = ["secret-key"],
                },
            },
        };

        var request = RunHeaders(
            HeaderOps(
                """{"op":"set","path":"X-Upstream-Trace","value":"{{index .RequestHeader \"X-Trace-Id\"}}"}""",
                """{"op":"set","path":"X-Filtered-Api-Key","value":"{{index .RequestHeader \"x-api-key\"}}"}"""),
            llmRequest,
            NoHeadersRequest());

        Assert.Equal("trace-123", HttpClientUtils.HeaderGet(request.Headers, "X-Upstream-Trace"));
        Assert.Equal(string.Empty, HttpClientUtils.HeaderGet(request.Headers, "X-Filtered-Api-Key"));
    }

    // ------------------------------------------------------------------
    // Legacy middleware cases (override_test.go:645-1315)
    // ------------------------------------------------------------------

    public static TheoryData<string, string, string> LegacyParameterCases => new()
    {
        {
            "override temperature",
            LegacyParameters("""{"temperature": 0.9}"""),
            """{"temperature":0.9,"max_tokens":1000}"""
        },
        {
            "override multiple parameters",
            LegacyParameters("""{"temperature": 0.7, "max_tokens": 2000, "top_p": 0.95}"""),
            """{"temperature":0.7,"max_tokens":2000,"top_p":0.95}"""
        },
        {
            "override nested parameter",
            LegacyParameters("""{"response_format.type": "json_object"}"""),
            """{"response_format.type":"json_object"}"""
        },
        {
            "stream override ignored",
            LegacyParameters("""{"stream": true, "temperature": 0.8}"""),
            """{"temperature":0.8,"stream":null}"""
        },
        {
            "no override parameters",
            LegacyParameters(string.Empty),
            """{"temperature":0.5,"max_tokens":1000,"stream":null}"""
        },
    };

    /// <summary>Port of TestOverrideParametersMiddleware.</summary>
    [Theory]
    [MemberData(nameof(LegacyParameterCases))]
    public void LegacyParametersApplyToTheOutboundBody(string name, string settingsJson, string expectationsJson)
    {
        var request = RunBody(
            settingsJson,
            new LlmRequest { Model = "gpt-4" },
            JsonRequest("""{"model":"gpt-4","temperature":0.5,"max_tokens":1000}"""));

        using var expectations = JsonDocument.Parse(expectationsJson);
        foreach (var expectation in expectations.RootElement.EnumerateObject())
        {
            var expected = expectation.Value;
            var lookup = Lookup(request, expectation.Name);

            if (expected.ValueKind == JsonValueKind.Null)
            {
                Assert.False(lookup.Exists, name + ": " + expectation.Name + " should be missing");
                continue;
            }

            Assert.True(lookup.Exists, name + ": " + expectation.Name + " should exist");

            if (expected.ValueKind == JsonValueKind.String)
            {
                Assert.Equal(expected.GetString(), lookup.StringValue);
                continue;
            }

            var scalar = Assert.IsType<OverrideJsonScalar>(lookup.Node);
            Assert.Equal(expected.GetDouble(), scalar.NumberValue);
        }
    }

    /// <summary>Port of TestOverrideParametersMiddleware_InvalidJSON.</summary>
    [Fact]
    public void InvalidLegacyParametersLeaveTheBodyUntouched()
    {
        var request = RunBody(
            LegacyParameters("invalid json"),
            new LlmRequest { Model = "gpt-4" },
            JsonRequest("""{"model":"gpt-4","temperature":0.5}"""));

        AssertNumber(request, "temperature", 0.5);
    }

    /// <summary>Port of TestOverrideParametersMiddleware_EmptySettings.</summary>
    [Fact]
    public void EmptySettingsLeaveTheBodyUntouched()
    {
        var request = RunBody(
            settingsJson: null,
            new LlmRequest { Model = "gpt-4" },
            JsonRequest("""{"model":"gpt-4","temperature":0.5}"""));

        AssertNumber(request, "temperature", 0.5);
    }

    public static TheoryData<string, string, string, string> AxonHubClearCases => new()
    {
        {
            "remove single parameter",
            """{"temperature": "__AXONHUB_CLEAR__"}""",
            """{"model": "gpt-4", "temperature": 0.5, "max_tokens": 1000}""",
            """{"model": "gpt-4", "max_tokens": 1000}"""
        },
        {
            "remove multiple parameters",
            """{"temperature": "__AXONHUB_CLEAR__", "max_tokens": "__AXONHUB_CLEAR__", "top_p": 0.95}""",
            """{"model": "gpt-4", "temperature": 0.5, "max_tokens": 1000, "frequency_penalty": 0.1}""",
            """{"model": "gpt-4", "frequency_penalty": 0.1, "top_p": 0.95}"""
        },
        {
            "remove nested parameter",
            """{"response_format.type": "__AXONHUB_CLEAR__"}""",
            """{"model": "gpt-4", "response_format": {"type": "json_object", "schema": {}}}""",
            """{"model": "gpt-4", "response_format": {"schema": {}}}"""
        },
        {
            "mix of removal and override",
            """{"temperature": "__AXONHUB_CLEAR__", "max_tokens": 2000, "top_p": 0.95}""",
            """{"model": "gpt-4", "temperature": 0.5, "max_tokens": 1000}""",
            """{"model": "gpt-4", "max_tokens": 2000, "top_p": 0.95}"""
        },
        {
            "attempt to remove non-existent parameter",
            """{"non_existent": "__AXONHUB_CLEAR__"}""",
            """{"model": "gpt-4", "temperature": 0.5}""",
            """{"model": "gpt-4", "temperature": 0.5}"""
        },
    };

    /// <summary>Port of TestOverrideParametersMiddleware_AxonHubClear.</summary>
    [Theory]
    [MemberData(nameof(AxonHubClearCases))]
    public void LegacyClearDirectiveDeletesBodyFields(
        string name,
        string parametersJson,
        string initialBody,
        string expectedBody)
    {
        _ = name;

        var request = RunBody(LegacyParameters(parametersJson), new LlmRequest { Model = "gpt-4" }, JsonRequest(initialBody));

        AssertJsonEqual(expectedBody, Text(request));
    }

    /// <summary>Port of TestOverrideHeadersMiddleware.</summary>
    [Fact]
    public void LegacyOverrideHeadersSetAndOverwrite()
    {
        var request = RunHeaders(
            LegacyHeaders(("User-Agent", "AxonHub/1.0")),
            new LlmRequest { Model = "gpt-4" },
            HeaderRequest(("Content-Type", JsonContentType)));

        Assert.Equal(JsonContentType, HttpClientUtils.HeaderGet(request.Headers, "Content-Type"));
        Assert.Equal("AxonHub/1.0", HttpClientUtils.HeaderGet(request.Headers, "User-Agent"));
    }

    /// <summary>Port of TestOverrideHeadersMiddleware (multiple headers).</summary>
    [Fact]
    public void LegacyOverrideHeadersReplaceExistingValues()
    {
        var request = RunHeaders(
            LegacyHeaders(
                ("User-Agent", "AxonHub/1.0"),
                ("X-Custom-Header", "custom-value"),
                ("Authorization", "Bearer token123")),
            new LlmRequest { Model = "gpt-4" },
            HeaderRequest(("Content-Type", JsonContentType), ("User-Agent", "Original-Agent")));

        Assert.Equal(JsonContentType, HttpClientUtils.HeaderGet(request.Headers, "Content-Type"));
        Assert.Equal("AxonHub/1.0", Assert.Single(request.Headers!["User-Agent"]));
        Assert.Equal("custom-value", Assert.Single(request.Headers!["X-Custom-Header"]));
        Assert.Equal("Bearer token123", Assert.Single(request.Headers!["Authorization"]));
    }

    /// <summary>Port of TestOverrideHeadersMiddleware (no override headers).</summary>
    [Fact]
    public void EmptyLegacyOverrideHeadersLeaveTheRequestUntouched()
    {
        var request = RunHeaders(
            LegacyHeaders(),
            new LlmRequest { Model = "gpt-4" },
            HeaderRequest(("Content-Type", JsonContentType)));

        Assert.Equal(JsonContentType, HttpClientUtils.HeaderGet(request.Headers, "Content-Type"));
        Assert.Single(request.Headers!);
    }

    /// <summary>
    /// Port of TestOverrideHeadersMiddleware (empty path). The reference does not
    /// assert the empty-key header away; it only checks the valid entry lands.
    /// </summary>
    [Fact]
    public void EmptyHeaderKeyDoesNotBlockTheFollowingOperations()
    {
        var request = RunHeaders(
            LegacyHeaders(("", "should-be-ignored"), ("Valid-Header", "valid-value")),
            new LlmRequest { Model = "gpt-4" },
            HeaderRequest(("Content-Type", JsonContentType)));

        Assert.Equal(JsonContentType, HttpClientUtils.HeaderGet(request.Headers, "Content-Type"));
        Assert.Equal("valid-value", HttpClientUtils.HeaderGet(request.Headers, "Valid-Header"));
    }

    /// <summary>Port of TestOverrideHeadersMiddleware (no existing headers).</summary>
    [Fact]
    public void LegacyOverrideHeadersApplyWithoutExistingHeaders()
    {
        var request = RunHeaders(
            LegacyHeaders(("User-Agent", "AxonHub/1.0"), ("X-API-Key", "secret-key")),
            new LlmRequest { Model = "gpt-4" },
            NoHeadersRequest());

        Assert.Equal("AxonHub/1.0", HttpClientUtils.HeaderGet(request.Headers, "User-Agent"));
        Assert.Equal("secret-key", HttpClientUtils.HeaderGet(request.Headers, "X-API-Key"));
    }

    /// <summary>Port of TestOverrideHeadersMiddleware_EmptySettings.</summary>
    [Fact]
    public void EmptySettingsLeaveHeadersUntouched()
    {
        var request = HeaderRequest(("Content-Type", JsonContentType));

        var modified = RunHeaders(settingsJson: null, new LlmRequest { Model = "gpt-4" }, request);

        Assert.Same(request.Headers, modified.Headers);
        Assert.Equal(JsonContentType, HttpClientUtils.HeaderGet(modified.Headers, "Content-Type"));
    }

    /// <summary>Port of TestOverrideHeadersMiddleware_EmptyOverrideHeaders.</summary>
    [Fact]
    public void EmptyOverrideHeaderArrayLeavesHeadersUntouched()
    {
        var request = HeaderRequest(("Content-Type", JsonContentType));

        var modified = RunHeaders(
            "{\"overrideHeaders\":[]}",
            new LlmRequest { Model = "gpt-4" },
            request);

        Assert.Same(request.Headers, modified.Headers);
        Assert.Equal(JsonContentType, HttpClientUtils.HeaderGet(modified.Headers, "Content-Type"));
    }

    /// <summary>Port of TestOverrideHeadersMiddleware_OverrideExistingAuth.</summary>
    [Fact]
    public void OverrideHeadersReplaceExistingAuthHeaders()
    {
        var request = RunHeaders(
            LegacyHeaders(
                ("Authorization", "Bearer override-token"),
                ("Api-Key", "override-key"),
                ("X-Api-Key", "override-x-key")),
            new LlmRequest { Model = "gpt-4" },
            HeaderRequest(("Authorization", "Bearer original-token"), ("Api-Key", "original-key")));

        Assert.Equal("Bearer override-token", HttpClientUtils.HeaderGet(request.Headers, "Authorization"));
        Assert.Equal("override-key", HttpClientUtils.HeaderGet(request.Headers, "Api-Key"));
        Assert.Equal("override-x-key", HttpClientUtils.HeaderGet(request.Headers, "X-Api-Key"));
    }

    public static TheoryData<string, string, string> BlockedHeaderCases => new()
    {
        {
            "transport headers are forwarded, sensitive allowed",
            "Content-Length|Authorization|User-Agent|X-Custom-Header",
            "123|Bearer token|CustomAgent|custom-value"
        },
        {
            "transport headers remain forwarded",
            "Content-Length|Transfer-Encoding|Accept-Encoding|Authorization|Api-Key|X-Api-Key|X-Api-Secret|X-Api-Token",
            "123|chunked|gzip|Bearer token|secret|secret|secret|secret"
        },
        {
            "mixed case sensitive headers allowed",
            "authorization|API-KEY|x-api-key|Valid-Header",
            "Bearer token|secret|secret|valid-value"
        },
    };

    /// <summary>Port of TestOverrideHeadersMiddleware_BlockedHeaders.</summary>
    [Theory]
    [MemberData(nameof(BlockedHeaderCases))]
    public void ChannelHeaderOverridesAreNotSubjectToTheBlockedHeaderList(
        string name,
        string keysCsv,
        string valuesCsv)
    {
        var keys = keysCsv.Split('|');
        var values = valuesCsv.Split('|');
        Assert.Equal(keys.Length, values.Length);

        var entries = new (string Key, string Value)[keys.Length];
        for (var index = 0; index < keys.Length; index++)
        {
            entries[index] = (keys[index], values[index]);
        }

        var request = RunHeaders(
            LegacyHeaders(entries),
            new LlmRequest { Model = "gpt-4" },
            HeaderRequest(("Content-Type", JsonContentType)));

        Assert.True(request.Headers is not null, name);
        for (var index = 0; index < keys.Length; index++)
        {
            Assert.Equal(values[index], HttpClientUtils.HeaderGet(request.Headers, keys[index]));
        }

        Assert.Equal(JsonContentType, HttpClientUtils.HeaderGet(request.Headers, "Content-Type"));
    }

    /// <summary>Port of TestOverrideParametersRenderClear.</summary>
    [Fact]
    public void RenderedClearDirectiveDeletesBodyFieldsAndHeaders()
    {
        var llmRequest = new LlmRequest
        {
            Model = "gpt-4",
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["clear_flag"] = "true" },
        };

        var request = RunBody(
            LegacyParameters(
                """{"clear_body_field": "{{if eq .Metadata.clear_flag \"true\"}}__AXONHUB_CLEAR__{{else}}keep-me{{end}}", "keep_body_field": "{{if eq .Metadata.clear_flag \"false\"}}__AXONHUB_CLEAR__{{else}}keep-me{{end}}"}"""),
            llmRequest,
            JsonRequest(
                """{"clear_body_field": "to-be-deleted", "keep_body_field": "to-be-overwritten", "other": "stay"}"""));

        AssertMissing(request, "clear_body_field");
        AssertString(request, "keep_body_field", "keep-me");
        AssertString(request, "other", "stay");

        var headerRequest = RunHeaders(
            LegacyHeaders(
                ("X-Clear-Header", "{{if eq .Metadata.clear_flag \"true\"}}__AXONHUB_CLEAR__{{else}}keep-me{{end}}"),
                ("X-Keep-Header", "{{if eq .Metadata.clear_flag \"false\"}}__AXONHUB_CLEAR__{{else}}keep-me{{end}}")),
            llmRequest,
            HeaderRequest(("X-Clear-Header", "to-be-deleted"), ("X-Keep-Header", "to-be-overwritten")));

        Assert.Equal(string.Empty, HttpClientUtils.HeaderGet(headerRequest.Headers, "X-Clear-Header"));
        Assert.Equal("keep-me", HttpClientUtils.HeaderGet(headerRequest.Headers, "X-Keep-Header"));
    }

    // ------------------------------------------------------------------
    // New operation format (override_test.go:1588-1964)
    // ------------------------------------------------------------------

    /// <summary>Port of TestOverrideOperationsNewFormat / "set and delete operations".</summary>
    [Fact]
    public void NewFormatSetDeleteAndTemplates()
    {
        var llmRequest = new LlmRequest { Model = "claude-3.5-sonnet", ReasoningEffort = "high" };

        var request = RunBody(
            LegacyParameters(
                """[{"op": "set", "path": "temperature", "value": "0.7"}, {"op": "set", "path": "custom", "value": "model-{{.Model}}"}, {"op": "delete", "path": "frequency_penalty"}]"""),
            llmRequest,
            JsonRequest("""{"frequency_penalty": 0.5, "existing": "keep"}"""));

        AssertNumber(request, "temperature", 0.7);
        AssertString(request, "custom", "model-claude-3.5-sonnet");
        AssertMissing(request, "frequency_penalty");
        AssertString(request, "existing", "keep");
    }

    /// <summary>Port of TestOverrideOperationsNewFormat / "rename operation".</summary>
    [Fact]
    public void RenameMovesTheValueAndDeletesTheSource()
    {
        var request = RunBody(
            LegacyParameters("""[{"op": "rename", "from": "max_tokens", "to": "max_completion_tokens"}]"""),
            new LlmRequest { Model = "claude-3.5-sonnet" },
            JsonRequest("""{"max_tokens": 4096, "model": "test"}"""));

        AssertMissing(request, "max_tokens");
        AssertNumber(request, "max_completion_tokens", 4096);
        AssertString(request, "model", "test");
    }

    /// <summary>Port of TestOverrideOperationsNewFormat / "copy operation".</summary>
    [Fact]
    public void CopyKeepsTheSourceAndCreatesTheNestedTarget()
    {
        var request = RunBody(
            LegacyParameters("""[{"op": "copy", "from": "model", "to": "metadata.original_model"}]"""),
            new LlmRequest { Model = "claude-3.5-sonnet" },
            JsonRequest("""{"model": "gpt-4"}"""));

        AssertString(request, "model", "gpt-4");
        AssertString(request, "metadata.original_model", "gpt-4");
    }

    /// <summary>Port of TestOverrideOperationsNewFormat / "rename non-existent field is no-op".</summary>
    [Fact]
    public void RenameOfAMissingFieldIsANoOp()
    {
        var request = RunBody(
            LegacyParameters("""[{"op": "rename", "from": "nonexistent", "to": "target"}]"""),
            new LlmRequest { Model = "claude-3.5-sonnet" },
            JsonRequest("""{"model": "gpt-4"}"""));

        AssertString(request, "model", "gpt-4");
        AssertMissing(request, "target");
    }

    /// <summary>Port of TestOverrideOperationsWithCondition / "condition true executes operation".</summary>
    [Fact]
    public void TrueConditionExecutesTheOperation()
    {
        var request = RunBody(
            LegacyParameters(
                """[{"op": "delete", "path": "reasoning_effort", "condition": "{{if eq .ReasoningEffort \"high\"}}true{{end}}"}, {"op": "set", "path": "thinking.type", "value": "enabled", "condition": "{{if .ReasoningEffort}}true{{end}}"}]"""),
            new LlmRequest { Model = "claude-3.5-sonnet", ReasoningEffort = "high" },
            JsonRequest("""{"reasoning_effort": "high", "model": "test"}"""));

        AssertMissing(request, "reasoning_effort");
        AssertString(request, "thinking.type", "enabled");
    }

    /// <summary>Port of TestOverrideOperationsWithCondition / "condition false skips operation".</summary>
    [Fact]
    public void FalseConditionSkipsTheOperation()
    {
        var request = RunBody(
            LegacyParameters(
                """[{"op": "delete", "path": "reasoning_effort", "condition": "{{if eq .ReasoningEffort \"high\"}}true{{end}}"}, {"op": "rename", "from": "max_tokens", "to": "max_output_tokens", "condition": "{{if eq .Model \"claude-3.5-sonnet\"}}true{{end}}"}]"""),
            new LlmRequest { Model = "gpt-4", ReasoningEffort = "low" },
            JsonRequest("""{"reasoning_effort": "low", "max_tokens": 1000}"""));

        AssertString(request, "reasoning_effort", "low");
        AssertNumber(request, "max_tokens", 1000);
        AssertMissing(request, "max_output_tokens");
    }

    /// <summary>Port of TestOverrideOperationsWithCondition / "conditional rename with reasoning effort mapping".</summary>
    [Fact]
    public void ConditionalRenameAndSetMapReasoningEffortToBudgetTokens()
    {
        var request = RunBody(
            LegacyParameters(
                """[{"op": "rename", "from": "reasoning_effort", "to": "thinking.budget_tokens", "condition": "{{if .ReasoningEffort}}true{{end}}"}, {"op": "set", "path": "thinking.type", "value": "enabled", "condition": "{{if .ReasoningEffort}}true{{end}}"}, {"op": "set", "path": "thinking.budget_tokens", "value": "{{if eq .ReasoningEffort \"high\"}}16384{{else if eq .ReasoningEffort \"medium\"}}8192{{else}}4096{{end}}", "condition": "{{if .ReasoningEffort}}true{{end}}"}]"""),
            new LlmRequest { Model = "claude-3.5-sonnet", ReasoningEffort = "high" },
            JsonRequest("""{"reasoning_effort": "high", "model": "test"}"""));

        AssertMissing(request, "reasoning_effort");
        AssertString(request, "thinking.type", "enabled");
        AssertNumber(request, "thinking.budget_tokens", 16384);
    }

    /// <summary>Port of TestOverrideOperationsWithCondition / "no condition means always execute".</summary>
    [Fact]
    public void MissingConditionAlwaysExecutes()
    {
        var request = RunBody(
            LegacyParameters("""[{"op": "set", "path": "temperature", "value": "0.5"}, {"op": "delete", "path": "top_p"}]"""),
            new LlmRequest { Model = "gpt-4" },
            JsonRequest("""{"top_p": 0.9, "model": "test"}"""));

        AssertNumber(request, "temperature", 0.5);
        AssertMissing(request, "top_p");
    }

    /// <summary>Port of TestOverrideLegacyFormatCompatibility.</summary>
    [Fact]
    public void LegacyObjectFormatStillWorks()
    {
        var request = RunBody(
            LegacyParameters("""{"temperature": 0.7, "max_tokens": 2000, "remove_me": "__AXONHUB_CLEAR__"}"""),
            new LlmRequest { Model = "gpt-4", ReasoningEffort = "high" },
            JsonRequest("""{"remove_me": "old_value", "keep": "yes"}"""));

        AssertNumber(request, "temperature", 0.7);
        AssertNumber(request, "max_tokens", 2000);
        AssertMissing(request, "remove_me");
        AssertString(request, "keep", "yes");
    }

    // ------------------------------------------------------------------
    // set_if_absent (override_test.go:1966-2166)
    // ------------------------------------------------------------------

    public static TheoryData<string, string, string, string> SetIfAbsentCases => new()
    {
        {
            "sets top-level default when path is absent",
            """{"model":"gpt-5.5"}""",
            """{"op":"set_if_absent","path":"max_output_tokens","value":"32000"}""",
            """{"model":"gpt-5.5","max_output_tokens":32000}"""
        },
        {
            "preserves existing number",
            """{"max_output_tokens":8000}""",
            """{"op":"set_if_absent","path":"max_output_tokens","value":"32000"}""",
            """{"max_output_tokens":8000}"""
        },
        {
            "preserves zero",
            """{"max_output_tokens":0}""",
            """{"op":"set_if_absent","path":"max_output_tokens","value":"32000"}""",
            """{"max_output_tokens":0}"""
        },
        {
            "preserves false",
            """{"feature":{"enabled":false}}""",
            """{"op":"set_if_absent","path":"feature.enabled","value":"true"}""",
            """{"feature":{"enabled":false}}"""
        },
        {
            "preserves empty string",
            """{"label":""}""",
            """{"op":"set_if_absent","path":"label","value":"fallback"}""",
            """{"label":""}"""
        },
        {
            "preserves explicit null",
            """{"max_output_tokens":null}""",
            """{"op":"set_if_absent","path":"max_output_tokens","value":"32000"}""",
            """{"max_output_tokens":null}"""
        },
        {
            "sets nested default when path is absent",
            """{"generation":{}}""",
            """{"op":"set_if_absent","path":"generation.max_output_tokens","value":"32000"}""",
            """{"generation":{"max_output_tokens":32000}}"""
        },
        {
            "preserves existing nested value",
            """{"generation":{"max_output_tokens":16000}}""",
            """{"op":"set_if_absent","path":"generation.max_output_tokens","value":"32000"}""",
            """{"generation":{"max_output_tokens":16000}}"""
        },
        {
            "respects false condition",
            """{}""",
            """{"op":"set_if_absent","path":"max_output_tokens","value":"32000","condition":"{{eq .Model \"other-model\"}}"}""",
            """{}"""
        },
    };

    /// <summary>Port of TestOverrideBodySetIfAbsent.</summary>
    [Theory]
    [MemberData(nameof(SetIfAbsentCases))]
    public void SetIfAbsentKeepsEveryPresentValue(string name, string body, string operation, string expected)
    {
        _ = name;

        var request = RunBody(
            BodyOps(operation),
            new LlmRequest { Model = "gpt-5.5" },
            JsonRequest(body));

        AssertJsonEqual(expected, Text(request));
    }

    /// <summary>Port of TestOverrideBodySetIfAbsent / "renders template when path is absent".</summary>
    [Fact]
    public void SetIfAbsentRendersItsValueWhenThePathIsAbsent()
    {
        var request = RunBody(
            BodyOps("""{"op":"set_if_absent","path":"max_output_tokens","value":"{{index .Metadata \"default_max\"}}"}"""),
            new LlmRequest
            {
                Model = "gpt-5.5",
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["default_max"] = "64000" },
            },
            JsonRequest("{}"));

        AssertJsonEqual("""{"max_output_tokens":64000}""", Text(request));
    }

    public static TheoryData<string, string, string, string, string> SetIfAbsentOriginalCases => new()
    {
        {
            "preserves field sent by client but dropped by transformation",
            """{"model":"deepseek-v4-flash","thinking":{"type":"enabled","budget_tokens":13312}}""",
            """{"model":"deepseek-v4-flash","reasoning_effort":"high"}""",
            """{"op":"set_if_absent","path":"thinking","value":"{\"type\":\"disabled\"}"}""",
            """{"model":"deepseek-v4-flash","reasoning_effort":"high"}"""
        },
        {
            "preserves explicit null sent by client",
            """{"thinking":null}""",
            """{}""",
            """{"op":"set_if_absent","path":"thinking","value":"{\"type\":\"disabled\"}"}""",
            """{}"""
        },
        {
            "preserves nested field sent by client",
            """{"generation":{"max_output_tokens":16000}}""",
            """{}""",
            """{"op":"set_if_absent","path":"generation.max_output_tokens","value":"32000"}""",
            """{}"""
        },
        {
            "sets default when absent from both bodies",
            """{"model":"deepseek-v4-flash"}""",
            """{"model":"deepseek-v4-flash","reasoning_effort":"high"}""",
            """{"op":"set_if_absent","path":"thinking","value":"{\"type\":\"disabled\"}"}""",
            """{"model":"deepseek-v4-flash","reasoning_effort":"high","thinking":{"type":"disabled"}}"""
        },
        {
            "preserves field present only in transformed body",
            """{}""",
            """{"max_output_tokens":8000}""",
            """{"op":"set_if_absent","path":"max_output_tokens","value":"32000"}""",
            """{"max_output_tokens":8000}"""
        },
        {
            "ignores non-JSON original body",
            """not-json""",
            """{}""",
            """{"op":"set_if_absent","path":"thinking","value":"{\"type\":\"disabled\"}"}""",
            """{"thinking":{"type":"disabled"}}"""
        },
        {
            "sets default when no original request is recorded",
            string.Empty,
            """{}""",
            """{"op":"set_if_absent","path":"thinking","value":"{\"type\":\"disabled\"}"}""",
            """{"thinking":{"type":"disabled"}}"""
        },
    };

    /// <summary>Port of TestOverrideBodySetIfAbsentOriginalRequest (issue 2133).</summary>
    [Theory]
    [MemberData(nameof(SetIfAbsentOriginalCases))]
    public void SetIfAbsentConsultsTheOriginalInboundBody(
        string name,
        string originalBody,
        string body,
        string operation,
        string expected)
    {
        _ = name;

        var llmRequest = new LlmRequest { Model = "deepseek-v4-flash" };
        if (originalBody.Length > 0)
        {
            llmRequest.RawRequest = new HttpRequestData { Body = Encoding.UTF8.GetBytes(originalBody) };
        }

        var request = RunBody(BodyOps(operation), llmRequest, JsonRequest(body));

        AssertJsonEqual(expected, Text(request));
    }

    // ------------------------------------------------------------------
    // Issue 632 and array operations (override_test.go:2232-2522)
    // ------------------------------------------------------------------

    public static TheoryData<string, bool> Issue632Cases => new()
    {
        { "low", false },
        { "high", true },
        { "medium", true },
    };

    /// <summary>Port of TestIssue632Override: templated JSON values become objects.</summary>
    [Theory]
    [MemberData(nameof(Issue632Cases))]
    public void RenderedJsonObjectFromMetadataDrivesChatTemplateKwargs(string reasoningEffort, bool expectedEnabled)
    {
        var request = RunBody(
            LegacyParameters(
                """{"chat_template_kwargs": "{\"enable_thinking\": {{if eq .ReasoningEffort \"low\"}}false{{else}}true{{end}}}"}"""),
            new LlmRequest { Model = "sglang-model", ReasoningEffort = reasoningEffort },
            JsonRequest("{}"));

        Assert.IsType<OverrideJsonObject>(Lookup(request, "chat_template_kwargs").Node);
        AssertBool(request, "chat_template_kwargs.enable_thinking", expectedEnabled);
    }

    private static LlmRequest ArrayModel() => new()
    {
        Model = "claude-sonnet-4-6",
        Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["session_id"] = "sess-9" },
    };

    private static HttpRequestData RunArrayOps(string operationsJson, string body)
        => RunBody(LegacyParameters(operationsJson), ArrayModel(), JsonRequest(body));

    /// <summary>Port of TestOverrideOperationsArrayOps / array_append.</summary>
    [Fact]
    public void ArrayAppendInsertsASingleObjectAtTheEnd()
    {
        var request = RunArrayOps(
            """[{"op":"array_append","path":"system","value":"{\"type\":\"text\",\"text\":\"injected\"}"}]""",
            """{"system":[{"type":"text","text":"original"}]}""");

        AssertString(request, "system.0.text", "original");
        AssertString(request, "system.1.text", "injected");
        Assert.Equal(2, ((OverrideJsonArray)Lookup(request, "system").Node!).Items.Count);
    }

    /// <summary>Port of TestOverrideOperationsArrayOps / array_prepend.</summary>
    [Fact]
    public void ArrayPrependInsertsASingleObjectAtTheStart()
    {
        var request = RunArrayOps(
            """[{"op":"array_prepend","path":"system","value":"{\"type\":\"text\",\"text\":\"injected\"}"}]""",
            """{"system":[{"type":"text","text":"original"}]}""");

        AssertString(request, "system.0.text", "injected");
        AssertString(request, "system.1.text", "original");
    }

    /// <summary>Port of TestOverrideOperationsArrayOps / splat default true.</summary>
    [Fact]
    public void ArrayPrependSpreadsArrayValuesByDefault()
    {
        var request = RunArrayOps(
            """[{"op":"array_prepend","path":"system","value":"[{\"text\":\"a\"},{\"text\":\"b\"}]"}]""",
            """{"system":[{"text":"original"}]}""");

        AssertString(request, "system.0.text", "a");
        AssertString(request, "system.1.text", "b");
        AssertString(request, "system.2.text", "original");
    }

    /// <summary>Port of TestOverrideOperationsArrayOps / splat=false.</summary>
    [Fact]
    public void ArrayPrependWithSplatFalseInsertsTheArrayAsOneElement()
    {
        var request = RunArrayOps(
            """[{"op":"array_prepend","path":"tags","value":"[\"a\",\"b\"]","splat":false}]""",
            """{"tags":["x"]}""");

        Assert.IsType<OverrideJsonArray>(Lookup(request, "tags.0").Node);
        AssertString(request, "tags.0.0", "a");
        AssertString(request, "tags.0.1", "b");
        AssertString(request, "tags.1", "x");
    }

    public static TheoryData<string, int, string, string> ArrayInsertCases => new()
    {
        { "array_insert at positive index", 1, """{"items":["a","b","c"]}""", """["a","X","b","c"]""" },
        { "array_insert with negative index counts from end", -1, """{"items":["a","b","c"]}""", """["a","b","X","c"]""" },
        { "array_insert clamps out-of-range index", 99, """{"items":["a","b"]}""", """["a","b","X"]""" },
    };

    /// <summary>Port of TestOverrideOperationsArrayOps / array_insert variants.</summary>
    [Theory]
    [MemberData(nameof(ArrayInsertCases))]
    public void ArrayInsertHonoursAndClampsTheIndex(string name, int index, string body, string expectedItems)
    {
        _ = name;

        var request = RunArrayOps(
            "[{\"op\":\"array_insert\",\"path\":\"items\",\"index\":" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",\"value\":\"X\"}]",
            body);

        AssertRaw(request, "items", expectedItems);
    }

    /// <summary>Port of TestOverrideOperationsArrayOps / append on missing path creates an array.</summary>
    [Fact]
    public void ArrayAppendCreatesAMissingPath()
    {
        var request = RunArrayOps(
            """[{"op":"array_append","path":"system","value":"{\"type\":\"text\",\"text\":\"only\"}"}]""",
            "{}");

        AssertString(request, "system.0.text", "only");
        Assert.IsType<OverrideJsonArray>(Lookup(request, "system").Node);
        Assert.Single(((OverrideJsonArray)Lookup(request, "system").Node!).Items);
    }

    /// <summary>Port of TestOverrideOperationsArrayOps / non-array path is a no-op.</summary>
    [Fact]
    public void ArrayPrependOnANonArrayPathLeavesTheBodyUntouched()
    {
        var request = RunArrayOps(
            """[{"op":"array_prepend","path":"system","value":"X"}]""",
            """{"system":"not-an-array"}""");

        AssertString(request, "system", "not-an-array");
    }

    /// <summary>Port of TestOverrideOperationsArrayOps / two prepends apply in declared order.</summary>
    [Fact]
    public void MultiplePrependsApplyInDeclaredOrder()
    {
        var request = RunArrayOps(
            """[{"op":"array_prepend","path":"system","value":"{\"text\":\"B\"}"}, {"op":"array_prepend","path":"system","value":"{\"text\":\"A\"}"}]""",
            """{"system":[{"text":"original"}]}""");

        AssertString(request, "system.0.text", "A");
        AssertString(request, "system.1.text", "B");
        AssertString(request, "system.2.text", "original");
    }

    /// <summary>Port of TestOverrideOperationsArrayOps / nested cache_control objects.</summary>
    [Fact]
    public void ArrayPrependKeepsNestedCacheControlObjects()
    {
        var request = RunArrayOps(
            """[{"op":"array_prepend","path":"system","value":"{\"type\":\"text\",\"text\":\"prefix\",\"cache_control\":{\"type\":\"ephemeral\"}}"}]""",
            """{"system":[{"type":"text","text":"user"}]}""");

        AssertString(request, "system.0.cache_control.type", "ephemeral");
        AssertString(request, "system.0.text", "prefix");
        AssertString(request, "system.1.text", "user");
    }

    /// <summary>Port of TestOverrideOperationsArrayOps / array op respects condition.</summary>
    [Fact]
    public void ArrayOperationsRespectTheirCondition()
    {
        var skipped = RunArrayOps(
            """[{"op":"array_append","path":"system","value":"X","condition":"{{eq .Model \"never\"}}"}]""",
            """{"system":["a"]}""");
        AssertRaw(skipped, "system", """["a"]""");

        var applied = RunArrayOps(
            """[{"op":"array_append","path":"system","value":"X","condition":"{{eq .Model \"claude-sonnet-4-6\"}}"}]""",
            """{"system":["a"]}""");
        AssertRaw(applied, "system", """["a","X"]""");
    }

    /// <summary>Port of TestOverrideOperationsArrayOps / templated value.</summary>
    [Fact]
    public void ArrayPrependSupportsTemplatedValues()
    {
        var request = RunArrayOps(
            """[{"op":"array_prepend","path":"system","value":"{\"type\":\"text\",\"text\":\"session-{{index .Metadata \"session_id\"}}\"}"}]""",
            """{"system":[{"text":"u"}]}""");

        AssertString(request, "system.0.text", "session-sess-9");
    }

    /// <summary>Port of TestOverrideOperationsArrayOps / array_remove by nested name.</summary>
    [Fact]
    public void ArrayRemoveMatchesANestedPath()
    {
        var request = RunArrayOps(
            """[{"op":"array_remove","path":"tools","match":{"path":"function.name","eq":"web_search"}}]""",
            """
            {"tools":[
                {"type":"function","function":{"name":"get_weather"}},
                {"type":"function","function":{"name":"web_search"}},
                {"type":"function","function":{"name":"calculate"}}
            ]}
            """);

        Assert.Equal(2, ((OverrideJsonArray)Lookup(request, "tools").Node!).Items.Count);
        AssertString(request, "tools.0.function.name", "get_weather");
        AssertString(request, "tools.1.function.name", "calculate");
    }

    /// <summary>Port of TestOverrideOperationsArrayOps / keeps non-matching items.</summary>
    [Fact]
    public void ArrayRemoveKeepsNonMatchingItems()
    {
        var request = RunArrayOps(
            """[{"op":"array_remove","path":"tools","match":{"path":"function.name","eq":"missing"}}]""",
            """{"tools":[{"function":{"name":"web_search"}}]}""");

        Assert.Single(((OverrideJsonArray)Lookup(request, "tools").Node!).Items);
        AssertString(request, "tools.0.function.name", "web_search");
    }

    /// <summary>Port of TestOverrideOperationsArrayOps / non-array path is a no-op.</summary>
    [Fact]
    public void ArrayRemoveOnANonArrayPathLeavesTheBodyUntouched()
    {
        var request = RunArrayOps(
            """[{"op":"array_remove","path":"tools","match":{"path":"function.name","eq":"web_search"}}]""",
            """{"tools":"not-an-array"}""");

        AssertString(request, "tools", "not-an-array");
    }

    /// <summary>Port of TestOverrideOperationsArrayOps / missing path is a no-op.</summary>
    [Fact]
    public void ArrayRemoveOnAMissingPathLeavesTheBodyUntouched()
    {
        var request = RunArrayOps(
            """[{"op":"array_remove","path":"tools","match":{"path":"function.name","eq":"web_search"}}]""",
            """{"messages":[{"role":"user","content":"hello"}]}""");

        AssertMissing(request, "tools");
        AssertString(request, "messages.0.content", "hello");
    }

    /// <summary>Port of TestOverrideOperationsArrayOps / empty match eq is rejected at runtime.</summary>
    [Fact]
    public void ArrayRemoveWithAnEmptyMatchValueIsANoOp()
    {
        var request = RunArrayOps(
            """[{"op":"array_remove","path":"tools","match":{"path":"function.name","eq":""}}]""",
            """{"tools":[{"function":{"name":"web_search"}},{"function":{"name":""}}]}""");

        Assert.Equal(2, ((OverrideJsonArray)Lookup(request, "tools").Node!).Items.Count);
    }

    /// <summary>Port of TestOverrideOperationsArrayOps / match eq is trimmed.</summary>
    [Fact]
    public void ArrayRemoveTrimsTheMatchValueBeforeComparing()
    {
        var request = RunArrayOps(
            """[{"op":"array_remove","path":"tools","match":{"path":"function.name","eq":" web_search "}}]""",
            """{"tools":[{"function":{"name":"web_search"}},{"function":{"name":"calculate"}}]}""");

        Assert.Single(((OverrideJsonArray)Lookup(request, "tools").Node!).Items);
        AssertString(request, "tools.0.function.name", "calculate");
    }

    // ------------------------------------------------------------------
    // Non-JSON gate (override_test.go:2524-2612)
    // ------------------------------------------------------------------

    private static readonly byte[] MultipartBody = Encoding.UTF8.GetBytes(
        "--boundary123\r\n"
        + "Content-Disposition: form-data; name=\"image\"; filename=\"image_1.png\"\r\n"
        + "Content-Type: image/png\r\n\r\n"
        + "\u0089PNG\r\n\u001a\nbinary-image-bytes\r\n"
        + "--boundary123\r\n"
        + "Content-Disposition: form-data; name=\"prompt\"\r\n\r\n"
        + "make it blue\r\n"
        + "--boundary123--\r\n");

    private static string ImageOverrideSettings() => BodyOps(
        """{"op":"set","path":"response_format","value":"b64_json"}""");

    /// <summary>Port of TestOverrideBodySkipsNonJSONBody / multipart body is left untouched.</summary>
    [Fact]
    public void MultipartBodyIsLeftUntouched()
    {
        var request = RunBody(
            ImageOverrideSettings(),
            new LlmRequest { Model = "gpt-image-1" },
            Request(MultipartBody, "multipart/form-data; boundary=boundary123"));

        Assert.Equal(MultipartBody, request.Body);
    }

    /// <summary>Port of TestOverrideBodySkipsNonJSONBody / multipart without a content type.</summary>
    [Fact]
    public void MultipartBodyWithoutContentTypeIsLeftUntouched()
    {
        var request = RunBody(
            ImageOverrideSettings(),
            new LlmRequest { Model = "gpt-image-1" },
            Request(MultipartBody, contentType: null));

        Assert.Equal(MultipartBody, request.Body);
    }

    /// <summary>Port of TestOverrideBodySkipsNonJSONBody / JSON body still gets overridden.</summary>
    [Fact]
    public void JsonBodyStillGetsOverridden()
    {
        var request = RunBody(
            ImageOverrideSettings(),
            new LlmRequest { Model = "gpt-image-1" },
            JsonRequest("""{"model":"gpt-image-1","prompt":"a cat"}"""));

        AssertString(request, "response_format", "b64_json");
        AssertString(request, "prompt", "a cat");
    }
}
