using System.Text;
using System.Text.Json;
using AxonHub.Api.Safeguards;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port of internal/server/safeguards/evaluator_test.go: the reviewer prompt,
/// verdict validation, deadlines, tool-input bounds and stream observation are
/// the behavioural contract the settings surface and the HTTP handler rely on.
/// </summary>
public sealed class SafeguardsEvaluatorTests
{
    private const string RequestBody =
        """{"safeguards":[{"type":"dangerous_tool_use","classifier_context":{"live_cwd":"/project","trusted_directories":["/project"],"secret_field":"omit"}}],"messages":[{"role":"user","content":"Run tests"}]}""";

    private static SafeguardsEvaluator NewEvaluator(
        SafeguardsClassifierFunc? call,
        string model = "cheap",
        long timeoutNanoseconds = 1_000_000_000)
    {
        var evaluator = SafeguardsEvaluator.Create(
            Encoding.UTF8.GetBytes(RequestBody),
            new SafeguardsConfig
            {
                ClassifierModel = model,
                TimeoutNanoseconds = timeoutNanoseconds,
            },
            call);
        Assert.NotNull(evaluator);
        return evaluator!;
    }

    private static List<SafeguardsToolUse> Tools() =>
    [
        new()
        {
            Id = "safe",
            Name = "Bash",
            Input = Encoding.UTF8.GetBytes("""{"command":"go test"}"""),
        },
        new()
        {
            Id = "danger",
            Name = "Bash",
            Input = Encoding.UTF8.GetBytes("""{"command":"rm -rf /"}"""),
        },
    ];

    private static Dictionary<string, SafeguardsVerdict> Verdicts(
        SafeguardsEvaluator evaluator,
        IReadOnlyList<SafeguardsToolUse> tools,
        CancellationToken cancellationToken = default)
        => Assert
            .Single(evaluator.Evaluate(cancellationToken, tools))
            .Status
            .ToolUses
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);

    [Theory]
    [InlineData("")]
    [InlineData(" \n\t")]
    [InlineData("Allow force-pushing user-owned feature branches.\nFlag disclosure of secrets.")]
    public void ConfiguredPolicyAcrossReviewers(string policy)
    {
        var calls = 0;
        var firstSystem = string.Empty;
        var evaluator = SafeguardsEvaluator.Create(
            Encoding.UTF8.GetBytes(RequestBody),
            new SafeguardsConfig
            {
                ClassifierModel = "primary",
                FallbackModels = ["backup"],
                Policy = policy,
            },
            (_, request) =>
            {
                calls++;
                Assert.Contains(
                    SafeguardsDefaults.ClassifierEvidence,
                    request.System,
                    StringComparison.Ordinal);
                Assert.Contains(
                    SafeguardsDefaults.ClassifierOutput,
                    request.System,
                    StringComparison.Ordinal);

                if (policy.Trim().Length == 0)
                {
                    Assert.Equal(SafeguardsDefaults.DefaultClassifierSystem, request.System);
                }
                else
                {
                    Assert.Contains(policy, request.System, StringComparison.Ordinal);
                    Assert.DoesNotContain(
                        SafeguardsDefaults.DefaultPolicy,
                        request.System,
                        StringComparison.Ordinal);
                }

                if (calls == 1)
                {
                    firstSystem = request.System;
                    return Task.FromException<string>(
                        new InvalidOperationException("primary unavailable"));
                }

                Assert.Equal("backup", request.Model);
                Assert.Equal(firstSystem, request.System);

                // A custom policy must not bypass verdict validation.
                return Task.FromResult("""{"verdicts":{"safe":{"flagged":"false"}}}""");
            });

        Assert.NotNull(evaluator);
        var result = Verdicts(evaluator!, Tools());
        Assert.Equal(2, calls);
        Assert.Equal("unavailable", result["safe"].Type);
        Assert.Equal("unavailable", result["danger"].Type);
    }

    [Theory]
    [InlineData(
        "valid",
        """{"verdicts":{"safe":{"flagged":false},"danger":{"flagged":true,"explanation":"destruction"}}}""",
        "not_flagged",
        "flagged")]
    [InlineData(
        "missing",
        """{"verdicts":{"safe":{"flagged":false}}}""",
        "not_flagged",
        "")]
    [InlineData("string boolean", """{"verdicts":{"safe":{"flagged":"false"}}}""", "", "")]
    [InlineData("null", """{"verdicts":{"safe":{"flagged":null}}}""", "", "")]
    [InlineData(
        "duplicate id",
        """{"verdicts":{"safe":{"flagged":true},"safe":{"flagged":false}}}""",
        "",
        "")]
    [InlineData(
        "duplicate flag",
        """{"verdicts":{"safe":{"flagged":true,"flagged":false}}}""",
        "",
        "")]
    [InlineData("trailing", """{"verdicts":{"safe":{"flagged":false}}} garbage""", "", "")]
    [InlineData(
        "bad explanation",
        """{"verdicts":{"safe":{"flagged":false,"explanation":42}}}""",
        "",
        "")]
    public void VerdictsAreValidatedStrictly(string _, string text, string safe, string danger)
    {
        var evaluator = NewEvaluator(
            (token, request) =>
            {
                Assert.Equal("cheap", request.Model);
                Assert.Equal(4096, request.MaxTokens);
                Assert.DoesNotContain("secret_field", request.User, StringComparison.Ordinal);
                Assert.True(token.CanBeCanceled, "missing deadline");
                return Task.FromResult(text);
            });

        var result = Verdicts(evaluator, Tools());

        Assert.Equal(safe, result["safe"].Outcome);
        Assert.Equal(danger, result["danger"].Outcome);
        foreach (var verdict in result.Values)
        {
            if (verdict.Outcome.Length == 0)
            {
                Assert.Equal("unavailable", verdict.Type);
                Assert.Equal("error", verdict.Reason);
            }
        }
    }

    [Fact]
    public void TimeoutAndCancellationFailClosed()
    {
        var evaluator = NewEvaluator(WaitUntilCanceled, timeoutNanoseconds: 1_000_000);

        Assert.Equal("timeout", Verdicts(evaluator, Tools())["safe"].Reason);

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Equal("error", Verdicts(evaluator, Tools(), canceled.Token)["safe"].Reason);

        var failed = NewEvaluator(
            (_, _) => Task.FromException<string>(new InvalidOperationException("upstream failed")));
        Assert.Equal("unavailable", Verdicts(failed, Tools())["safe"].Type);
    }

    [Fact]
    public void TruncatedAndOversizedToolsAreNotReviewed()
    {
        var calls = 0;
        var evaluator = NewEvaluator(
            (_, _) =>
            {
                calls++;
                return Task.FromResult("""{"verdicts":{}}""");
            });

        var uses = Tools();
        uses[0].Truncated = true;
        uses[1].Input = Encoding.UTF8.GetBytes(
            "{\"s\":\"" + new string('x', 64000) + "\"}");
        var result = Verdicts(evaluator, uses);
        Assert.Equal(0, calls);
        Assert.Equal("truncated", result["safe"].Reason);
        Assert.Equal("input_too_long", result["danger"].Reason);

        uses = Tools();
        uses[1] = new SafeguardsToolUse
        {
            Id = "safe",
            Name = uses[1].Name,
            Input = uses[1].Input,
        };
        Verdicts(evaluator, uses);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void TranscriptIsBounded()
    {
        var messages = new List<byte[]>();
        for (var i = 0; i < 100; i++)
        {
            messages.Add(Encoding.UTF8.GetBytes(
                "{\"role\":\"user\",\"content\":\"" + new string('a', 1800) + "\"}"));
        }

        var text = SafeguardsEvaluator.RenderTranscript(messages, 24000);
        Assert.True(
            Encoding.UTF8.GetByteCount(text) <= 24000,
            "unbounded transcript");
        Assert.Contains("earlier turns omitted", text, StringComparison.Ordinal);
        Assert.Equal(string.Empty, SafeguardsEvaluator.RenderTranscript([], 24000));
    }

    [Fact]
    public void ReviewIsOptIn()
    {
        Assert.Null(SafeguardsEvaluator.Create(
            Encoding.UTF8.GetBytes("""{"messages":[]}"""),
            new SafeguardsConfig { ClassifierModel = "cheap" },
            call: null));
        Assert.Null(SafeguardsEvaluator.Create(
            Encoding.UTF8.GetBytes(RequestBody),
            new SafeguardsConfig(),
            call: null));
    }

    [Fact]
    public void AnnotateResponseReplacesVerdictsAndDetectsTruncation()
    {
        var evaluator = NewEvaluator(
            (_, _) => Task.FromResult(
                """{"verdicts":{"safe":{"flagged":true,"explanation":"reviewed"}}}"""));

        var bodyText =
            """{"content":[{"type":"tool_use","id":"safe","name":"Bash","input":{}}],"stop_reason":"tool_use","safeguard_results":[{"type":"other","status":{"type":"available"}},{"type":"dangerous_tool_use","status":{"type":"unsupported"}}]}""";

        var result = Encoding.UTF8.GetString(
            evaluator.AnnotateResponse(CancellationToken.None, Encoding.UTF8.GetBytes(bodyText)));
        using (JsonDocument.Parse(result))
        {
            // The annotated response must stay valid JSON for protocol clients.
        }

        Assert.Contains("\"outcome\":\"flagged\"", result, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"other\"", result, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(result, "\"type\":\"dangerous_tool_use\""));

        var truncated = Encoding.UTF8.GetString(evaluator.AnnotateResponse(
            CancellationToken.None,
            Encoding.UTF8.GetBytes(bodyText.Replace(
                "\"stop_reason\":\"tool_use\"",
                "\"stop_reason\":\"max_tokens\"",
                StringComparison.Ordinal))));
        Assert.Contains("\"reason\":\"truncated\"", truncated, StringComparison.Ordinal);
    }

    [Fact]
    public void StreamReviewIsParallelAndIdempotent()
    {
        var calls = 0;
        var evaluator = NewEvaluator(
            (_, request) =>
            {
                calls++;
                Assert.Contains(
                    "\"command\":\"go test\"",
                    request.User,
                    StringComparison.Ordinal);
                return Task.FromResult(
                    """{"verdicts":{"safe":{"flagged":false},"danger":{"flagged":true}}}""");
            });
        var reviewer = new SafeguardsReviewer(evaluator);

        string[] events =
        [
            """{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"safe","name":"Bash","input":{}}}""",
            """{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"danger","name":"Bash","input":{}}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"command\":"}}""",
            """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"command\":\"rm /\"}"}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"\"go test\"}"}}""",
            """{"type":"content_block_stop","index":0}""",
            """{"type":"content_block_stop","index":1}""",
        ];
        foreach (var streamEvent in events)
        {
            var output = reviewer.Observe(
                CancellationToken.None,
                Encoding.UTF8.GetBytes(streamEvent));
            Assert.Equal(streamEvent, Encoding.UTF8.GetString(output));
        }

        var final = Encoding.UTF8.GetBytes(
            """{"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":10}}""");
        for (var i = 0; i < 2; i++)
        {
            var output = Encoding.UTF8.GetString(
                reviewer.Observe(CancellationToken.None, final));
            using (JsonDocument.Parse(output))
            {
                // A reviewed terminal delta must remain a valid event envelope.
            }

            Assert.Contains("\"outcome\":\"flagged\"", output, StringComparison.Ordinal);
            Assert.Contains("\"outcome\":\"not_flagged\"", output, StringComparison.Ordinal);
            Assert.Contains("\"output_tokens\":10", output, StringComparison.Ordinal);
        }

        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("""{""")]
    [InlineData("""{"a":1}""")]
    public void IncompleteStreamedArgumentsFailClosed(string partial)
    {
        var calls = 0;
        var evaluator = NewEvaluator(
            (_, _) =>
            {
                calls++;
                return Task.FromResult(string.Empty);
            });
        var reviewer = new SafeguardsReviewer(evaluator);

        _ = reviewer.Observe(
            CancellationToken.None,
            Encoding.UTF8.GetBytes(
                """{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"safe","name":"Bash","input":{}}}"""));
        _ = reviewer.Observe(
            CancellationToken.None,
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                type = "content_block_delta",
                index = 0,
                delta = new
                {
                    type = "input_json_delta",
                    partial_json = partial,
                },
            }));

        var output = Encoding.UTF8.GetString(reviewer.Observe(
            CancellationToken.None,
            Encoding.UTF8.GetBytes(
                """{"type":"message_delta","delta":{"stop_reason":"max_tokens"}}""")));

        Assert.Equal(0, calls);
        using (JsonDocument.Parse(output))
        {
            // The fail-closed delta must remain a valid event envelope.
        }

        Assert.Contains("\"reason\":\"truncated\"", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"type":"content_block_start","index":"bad"}""")]
    [InlineData("""{"type":"content_block_delta","index":"bad"}""")]
    [InlineData(
        """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":42}}""")]
    [InlineData("""{"type":"message_delta","delta":[]}""")]
    public void MalformedStreamEventsReturnErrors(string malformed)
    {
        var calls = 0;
        var evaluator = NewEvaluator(
            (_, _) =>
            {
                calls++;
                return Task.FromResult("""{"verdicts":{"safe":{"flagged":false}}}""");
            });
        var reviewer = new SafeguardsReviewer(evaluator);

        _ = reviewer.Observe(
            CancellationToken.None,
            Encoding.UTF8.GetBytes(
                """{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"safe","name":"Bash","input":{}}}"""));

        Assert.Throws<SafeguardsDecodeException>(() => reviewer.Observe(
            CancellationToken.None,
            Encoding.UTF8.GetBytes(malformed)));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"command\"")]
    [InlineData("""{"command":"rm /","command":"ls"}""")]
    public void InvalidToolInputsFailClosed(string raw)
    {
        var calls = 0;
        var evaluator = NewEvaluator(
            (_, _) =>
            {
                calls++;
                return Task.FromResult("""{"verdicts":{"safe":{"flagged":false}}}""");
            });

        var uses = Tools()[..1];
        uses[0].Input = Encoding.UTF8.GetBytes(raw);
        var result = Verdicts(evaluator, uses)["safe"];

        Assert.Equal(0, calls);
        Assert.Equal("unavailable", result.Type);
    }

    [Fact]
    public void StreamReplacesUpstreamApprovals()
    {
        var calls = 0;
        var evaluator = NewEvaluator(
            (_, _) =>
            {
                calls++;
                return Task.FromResult(
                    """{"verdicts":{"safe":{"flagged":true,"explanation":"local denial"}}}""");
            });
        var reviewer = new SafeguardsReviewer(evaluator);

        string[] earlyEvents =
        [
            """{"type":"message_start","message":{"safeguard_results":[{"type":"dangerous_tool_use","status":{"type":"available","tool_uses":{"safe":{"type":"evaluated","outcome":"not_flagged"}}}},{"type":"other","status":{"type":"available"}}]}}""",
            """{"type":"message_delta","delta":{"safeguard_results":[{"type":"dangerous_tool_use","status":{"type":"available","tool_uses":{"safe":{"type":"evaluated","outcome":"not_flagged"}}}}]}}""",
        ];
        foreach (var streamEvent in earlyEvents)
        {
            var output = Encoding.UTF8.GetString(reviewer.Observe(
                CancellationToken.None,
                Encoding.UTF8.GetBytes(streamEvent)));
            Assert.DoesNotContain("not_flagged", output, StringComparison.Ordinal);
            Assert.DoesNotContain("dangerous_tool_use", output, StringComparison.Ordinal);
            if (streamEvent.Contains("\"other\"", StringComparison.Ordinal))
            {
                Assert.Contains("\"other\"", output, StringComparison.Ordinal);
            }
        }

        Assert.Equal(0, calls);

        _ = reviewer.Observe(
            CancellationToken.None,
            Encoding.UTF8.GetBytes(
                """{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"safe","name":"Bash","input":{}}}"""));
        _ = reviewer.Observe(
            CancellationToken.None,
            Encoding.UTF8.GetBytes("""{"type":"content_block_stop","index":0}"""));
        var final = Encoding.UTF8.GetString(reviewer.Observe(
            CancellationToken.None,
            Encoding.UTF8.GetBytes(
                """{"type":"message_delta","delta":{"stop_reason":"tool_use","safeguard_results":[{"type":"dangerous_tool_use","status":{"type":"available","tool_uses":{"safe":{"type":"evaluated","outcome":"not_flagged"}}}}]}}""")));

        Assert.Equal(1, calls);
        using (JsonDocument.Parse(final))
        {
            // The rewritten delta must remain a valid event envelope.
        }

        Assert.DoesNotContain("not_flagged", final, StringComparison.Ordinal);
        Assert.Contains("\"outcome\":\"flagged\"", final, StringComparison.Ordinal);
    }

    [Fact]
    public void TranscriptBudgetsNeverOverflow()
    {
        byte[][] messages =
        [
            Encoding.UTF8.GetBytes("""{"role":"user","content":"hello"}"""),
            Encoding.UTF8.GetBytes("""{"role":"assistant","content":"a longer conversation"}"""),
        ];

        for (var budget = 0; budget <= 100; budget++)
        {
            var text = SafeguardsEvaluator.RenderTranscript(messages, budget);
            Assert.True(
                Encoding.UTF8.GetByteCount(text) <= budget,
                $"budget {budget} produced {Encoding.UTF8.GetByteCount(text)} bytes: {text}");
        }
    }

    [Fact]
    public void MalformedTranscriptTextDoesNotRepeat()
    {
        byte[][] messages =
        [
            Encoding.UTF8.GetBytes(
                """{"role":"user","content":[{"type":"text","text":"Explicit authorization"},{"type":"text","text":42},{"type":"text"}]}"""),
        ];

        var text = SafeguardsEvaluator.RenderTranscript(messages, 24000);
        Assert.Equal(1, CountOccurrences(text, "Explicit authorization"));
    }

    private static async Task<string> WaitUntilCanceled(
        CancellationToken cancellationToken,
        SafeguardsClassifierRequest request)
    {
        _ = request;
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return string.Empty;
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
