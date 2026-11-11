using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AxonHub.Api.Safeguards;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port of internal/server/safeguards/fallback_test.go: ordered reviewers only
/// re-review unresolved tools, share one overall deadline, and can never turn a
/// failed review into an approval. Denial escalation is a second opinion only.
/// </summary>
public sealed class SafeguardsFallbackTests
{
    private const string RequestBody =
        """{"safeguards":[{"type":"dangerous_tool_use","classifier_context":{"live_cwd":"/project","trusted_directories":["/project"],"secret_field":"omit"}}],"messages":[{"role":"user","content":"Run tests"}]}""";

    private static SafeguardsEvaluator FallbackEvaluator(
        SafeguardsConfig config,
        SafeguardsClassifierFunc call)
    {
        var evaluator = SafeguardsEvaluator.Create(
            Encoding.UTF8.GetBytes(RequestBody),
            config,
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
        IReadOnlyList<SafeguardsToolUse>? tools = null,
        CancellationToken cancellationToken = default)
        => Assert
            .Single(evaluator.Evaluate(cancellationToken, tools ?? Tools()))
            .Status
            .ToolUses
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);

    private static string[] ProposedIds(SafeguardsClassifierRequest request)
    {
        const string marker = "\n\nProposed tool calls:\n";
        var index = request.User.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, "missing proposed tools");
        using var document = JsonDocument.Parse(request.User[(index + marker.Length)..]);
        return
        [
            .. document.RootElement
                .EnumerateArray()
                .Select(static item => item.GetProperty("id").GetString() ?? string.Empty),
        ];
    }

    private static string Bool(bool value) => value ? "true" : "false";

    [Theory]
    [InlineData("error")]
    [InlineData("malformed")]
    [InlineData("timeout")]
    [InlineData("late_success")]
    public void FallbackRecoversFailures(string failure)
    {
        var called = new List<string>();

        async Task<string> Call(CancellationToken cancellationToken, SafeguardsClassifierRequest request)
        {
            called.Add(request.Model);
            if (request.Model == "primary")
            {
                switch (failure)
                {
                    case "error":
                        throw new InvalidOperationException("upstream failed");
                    case "malformed":
                        return "{\"verdicts\":";
                    case "timeout":
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                        return string.Empty;
                    case "late_success":
                        try
                        {
                            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                        }
                        catch (OperationCanceledException)
                        {
                            // A late answer cannot be trusted after the deadline.
                        }

                        return """{"verdicts":{"safe":{"flagged":true},"danger":{"flagged":false}}}""";
                }
            }

            Assert.False(
                cancellationToken.IsCancellationRequested,
                "backup inherits attempt cancellation");
            return """{"verdicts":{"safe":{"flagged":false},"danger":{"flagged":true}}}""";
        }

        var evaluator = FallbackEvaluator(
            new SafeguardsConfig
            {
                ClassifierModel = "primary",
                FallbackModels = ["backup"],
                TimeoutNanoseconds = 1_000_000_000,
                // Only the timeout failures need the attempt deadline. For the
                // synchronous failures the backup is expected to succeed, so a
                // tight deadline would race ordinary scheduling jitter.
                AttemptTimeoutNanoseconds = failure is "timeout" or "late_success"
                    ? 10_000_000
                    : 1_000_000_000,
            },
            Call);

        var result = Verdicts(evaluator);

        Assert.Equal(new[] { "primary", "backup" }, called);
        Assert.Equal("not_flagged", result["safe"].Outcome);
        Assert.Equal("flagged", result["danger"].Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FallbackOnlyReviewsUnresolvedTools(bool flagged)
    {
        var called = new List<string>();
        var evaluator = FallbackEvaluator(
            new SafeguardsConfig
            {
                ClassifierModel = " primary ",
                FallbackModels = ["", "primary", " backup ", "backup", "unused"],
            },
            (_, request) =>
            {
                called.Add(request.Model);
                if (request.Model == "primary")
                {
                    Assert.Equal(new[] { "safe", "danger" }, ProposedIds(request));
                    var encoded = "{\"verdicts\":{\"safe\":{\"flagged\":"
                        + Bool(flagged)
                        + "},\"danger\":{\"flagged\":\"false\"}}}";
                    return Task.FromResult(encoded);
                }

                Assert.Equal(new[] { "danger" }, ProposedIds(request));

                // An unsolicited contradictory verdict must not overwrite the
                // primary's verdict.
                var backup = "{\"verdicts\":{\"safe\":{\"flagged\":"
                    + Bool(!flagged)
                    + "},\"danger\":{\"flagged\":true}}}";
                return Task.FromResult(backup);
            });

        var result = Verdicts(evaluator);

        Assert.Equal(new[] { "primary", "backup" }, called);
        Assert.Equal(flagged ? "flagged" : "not_flagged", result["safe"].Outcome);
        Assert.Equal("flagged", result["danger"].Outcome);
    }

    [Fact]
    public void CompleteVerdictsAreNotRetried()
    {
        var calls = 0;
        var evaluator = FallbackEvaluator(
            new SafeguardsConfig
            {
                ClassifierModel = "primary",
                FallbackModels = ["backup"],
            },
            (_, _) =>
            {
                calls++;
                return Task.FromResult(
                    """{"verdicts":{"safe":{"flagged":false},"danger":{"flagged":true}}}""");
            });

        _ = Verdicts(evaluator);

        Assert.Equal(1, calls);
    }

    [Fact]
    public void ExhaustionPreservesEvaluatedVerdicts()
    {
        var calls = 0;
        var evaluator = FallbackEvaluator(
            new SafeguardsConfig
            {
                ClassifierModel = "primary",
                FallbackModels = ["backup", "last"],
            },
            (_, request) =>
            {
                calls++;
                return request.Model == "primary"
                    ? Task.FromResult("""{"verdicts":{"safe":{"flagged":true}}}""")
                    : Task.FromException<string>(new InvalidOperationException("unavailable"));
            });

        var result = Verdicts(evaluator);

        Assert.Equal(3, calls);
        Assert.Equal("flagged", result["safe"].Outcome);
        Assert.Equal("unavailable", result["danger"].Type);
        Assert.Equal("error", result["danger"].Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationStopsAttempts(bool preCanceled)
    {
        using var cancellation = new CancellationTokenSource();
        if (preCanceled)
        {
            cancellation.Cancel();
        }

        var calls = 0;
        var evaluator = FallbackEvaluator(
            new SafeguardsConfig
            {
                ClassifierModel = "primary",
                FallbackModels = ["backup"],
            },
            async (token, _) =>
            {
                calls++;
                cancellation.Cancel();
                await Task.Yield();
                throw new OperationCanceledException(token);
            });

        var result = Verdicts(evaluator, cancellationToken: cancellation.Token);

        Assert.Equal(preCanceled ? 0 : 1, calls);
        Assert.Equal("unavailable", result["safe"].Type);
        Assert.Equal("error", result["safe"].Reason);
    }

    [Fact]
    public void FallbackSharesTheOverallBudget()
    {
        var calls = 0;
        var attemptsPrimaryDeadline = false;

        async Task<string> Call(CancellationToken cancellationToken, SafeguardsClassifierRequest request)
        {
            calls++;
            Assert.True(cancellationToken.CanBeCanceled, "no deadline");
            if (request.Model == "primary")
            {
                attemptsPrimaryDeadline = true;
                throw new InvalidOperationException("upstream failed");
            }

            // The fallback inherits the overall deadline; if it started a new
            // one it would wait for the one-second attempt timeout instead.
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // The shared deadline fired.
            }

            return string.Empty;
        }

        var evaluator = FallbackEvaluator(
            new SafeguardsConfig
            {
                ClassifierModel = "primary",
                FallbackModels = ["backup", "last"],
                TimeoutNanoseconds = 40_000_000,
                AttemptTimeoutNanoseconds = 1_000_000_000,
            },
            Call);

        var stopwatch = Stopwatch.StartNew();
        var result = Verdicts(evaluator);
        stopwatch.Stop();

        Assert.True(attemptsPrimaryDeadline);
        Assert.Equal(2, calls);
        Assert.Equal("timeout", result["safe"].Reason);
        Assert.True(
            stopwatch.ElapsedMilliseconds < 800,
            $"fallback extended the overall deadline ({stopwatch.ElapsedMilliseconds}ms)");
    }

    [Fact]
    public void InvalidToolsAreNeverReviewed()
    {
        var calls = 0;
        var evaluator = FallbackEvaluator(
            new SafeguardsConfig
            {
                ClassifierModel = "primary",
                FallbackModels = ["backup"],
            },
            (_, request) =>
            {
                calls++;
                Assert.Equal(new[] { "valid" }, ProposedIds(request));
                return Task.FromResult("""{"verdicts":{}}""");
            });

        List<SafeguardsToolUse> uses =
        [
            new()
            {
                Id = "valid",
                Name = "Bash",
                Input = Encoding.UTF8.GetBytes("{}"),
            },
            new()
            {
                Id = "truncated",
                Name = "Bash",
                Input = Encoding.UTF8.GetBytes("{}"),
                Truncated = true,
            },
            new()
            {
                Id = "invalid",
                Name = "Bash",
                Input = Encoding.UTF8.GetBytes("null"),
            },
        ];

        var result = Verdicts(evaluator, uses);

        Assert.Equal(2, calls);
        Assert.Equal("truncated", result["truncated"].Reason);
        Assert.Equal("unavailable", result["invalid"].Type);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("approve")]
    [InlineData("deny")]
    [InlineData("error")]
    [InlineData("malformed")]
    [InlineData("timeout")]
    [InlineData("cancel")]
    [InlineData("overall_timeout")]
    public void DenialEscalationIsASecondOpinion(string scenario)
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        Exception? delegateError = null;

        async Task<string> Call(CancellationToken cancellationToken, SafeguardsClassifierRequest request)
        {
            calls++;
            if (request.Model == "primary")
            {
                return """{"verdicts":{"safe":{"flagged":false},"danger":{"flagged":true,"explanation":"original denial"}}}""";
            }

            try
            {
                Assert.Equal(new[] { "danger" }, ProposedIds(request));
            }
            catch (Exception ex)
            {
                delegateError = ex;
                throw;
            }
            Assert.DoesNotContain(
                "original denial",
                request.User,
                StringComparison.Ordinal);

            switch (scenario)
            {
                case "approve":
                    return """{"verdicts":{"danger":{"flagged":false},"safe":{"flagged":true}}}""";
                case "deny":
                    return """{"verdicts":{"danger":{"flagged":true,"explanation":"confirmed"}}}""";
                case "error":
                    throw new InvalidOperationException("failed");
                case "malformed":
                    return """{"verdicts":{}}""";
                case "timeout":
                case "overall_timeout":
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        // The escalation attempt ran out of time.
                    }

                    return string.Empty;
                case "cancel":
                    cancellation.Cancel();
                    throw new OperationCanceledException(cancellationToken);
                default:
                    throw new InvalidOperationException("unexpected fallback");
            }
        }

        var evaluator = FallbackEvaluator(
            new SafeguardsConfig
            {
                ClassifierModel = "primary",
                FallbackModels = ["backup", "last"],
                EscalateOnDenial = scenario != "disabled",
                // Only "timeout" needs an attempt-level deadline. The other
                // scenarios expect the escalation call to succeed, so a tight
                // deadline would race ordinary thread scheduling jitter in
                // the test callback (Go's 10ms budget assumes a synchronous
                // callback never gets preempted mid-attempt).
                TimeoutNanoseconds = scenario == "overall_timeout"
                    ? 10_000_000
                    : 1_000_000_000,
                AttemptTimeoutNanoseconds = scenario == "timeout"
                    ? 10_000_000
                    : 1_000_000_000,
            },
            Call);

        var result = Verdicts(evaluator, cancellationToken: cancellation.Token);

        Assert.Null(delegateError);
        Assert.Equal(
            scenario == "approve" ? "not_flagged" : "flagged",
            result["danger"].Outcome);
        Assert.Equal("not_flagged", result["safe"].Outcome);

        var expectedCalls = scenario switch
        {
            "disabled" => 1,
            "approve" or "deny" or "cancel" or "overall_timeout" => 2,
            _ => 3,
        };
        Assert.Equal(expectedCalls, calls);

        if (scenario is not ("approve" or "deny"))
        {
            Assert.Equal("original denial", result["danger"].Explanation);
        }
    }
}
