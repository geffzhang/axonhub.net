using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port of internal/server/orchestrator/retry_test.go (TestIsRetryableErrorForChannel),
/// plus the settings parsing that feeds it.
/// </summary>
public sealed class ChannelRetryPredicateTests
{
    private static readonly ChannelRetrySettings Configured = ChannelRetrySettings.Parse(
        """{"retryableStatusCodes":[400,403],"retryableErrorPatterns":[{"pattern":"Console API returned 403"},{"pattern":"Console API returned \\d+","regex":true}]}""");

    public static TheoryData<string, Exception?, ChannelRetrySettings?, bool> Cases => new()
    {
        { "error is nil", null, Configured, false },
        {
            "default retryable status without settings",
            new ResponseError { StatusCode = 500 },
            null,
            true
        },
        {
            "wrapped transport failure without settings",
            new InvalidOperationException("failed to stream request: i/o timeout", new IOException("connection reset")),
            null,
            true
        },
        {
            "configured 400 status",
            new HttpClientError("POST", "http://upstream.test", 400, "400 status"),
            Configured,
            true
        },
        {
            "unconfigured 401 status",
            new HttpClientError("POST", "http://upstream.test", 401, "401 status"),
            Configured,
            false
        },
        {
            "configured error text",
            new InvalidOperationException(
                "failed to stream request: error: Console API returned 403, code: upstream_error, type: upstream_error"),
            Configured,
            true
        },
        {
            "configured error regex",
            new InvalidOperationException(
                "failed to stream request: error: Console API returned 502, code: upstream_error, type: upstream_error"),
            Configured,
            true
        },
        {
            "unmatched error text",
            new InvalidOperationException("failed to stream request: error: credentials rejected"),
            Configured,
            false
        },
        {
            "configured status without channel settings",
            new HttpClientError("POST", "http://upstream.test", 400, "400 status"),
            new ChannelRetrySettings(),
            false
        },
        {
            "structured code matches configured pattern",
            new ResponseError
            {
                Detail = new ErrorDetail { Code = "server_is_overloaded" },
            },
            ChannelRetrySettings.Parse(
                """{"retryableErrorPatterns":[{"pattern":"server_is_overloaded"}]}"""),
            true
        },
        {
            "unrelated structured code does not match",
            new ResponseError
            {
                Detail = new ErrorDetail { Code = "invalid_api_key" },
            },
            ChannelRetrySettings.Parse(
                """{"retryableErrorPatterns":[{"pattern":"server_is_overloaded"}]}"""),
            false
        },
        {
            "structured code without patterns is not retryable",
            new ResponseError
            {
                Detail = new ErrorDetail { Code = "server_is_overloaded" },
            },
            new ChannelRetrySettings(),
            false
        },
        {
            "response error renders detail text like Go",
            new ResponseError
            {
                StatusCode = 400,
                Detail = new ErrorDetail
                {
                    Message = "Console API returned 403",
                    Type = "upstream_error",
                    Code = "upstream_error",
                },
            },
            Configured,
            true
        },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void MatchesReferenceRetryPredicate(
        string name,
        Exception? error,
        ChannelRetrySettings? settings,
        bool expected)
    {
        _ = name;
        Assert.Equal(expected, ChannelRetryPredicate.IsRetryable(error, settings));
    }

    [Theory]
    [InlineData(401, "invalid_api_key", true)]
    [InlineData(403, "permission_denied", true)]
    [InlineData(429, "insufficient_quota", true)]
    [InlineData(429, "insufficient_balance", true)]
    [InlineData(400, "content_filter", true)]
    [InlineData(429, "rate_limit_exceeded", false)]
    [InlineData(500, "server_error", false)]
    public void IdentifiesTerminalProviderErrors(int statusCode, string code, bool expected)
    {
        var error = new ResponseError
        {
            StatusCode = statusCode,
            Detail = new ErrorDetail { Code = code },
        };

        Assert.Equal(expected, ChannelRetryPredicate.IsTerminalUpstreamError(error));
    }

    [Fact]
    public void IdentifiesTerminalCodeInRawHttpClientErrorBody()
    {
        var error = new HttpClientError(
            "POST",
            "http://upstream.test",
            429,
            "429 Too Many Requests",
            """{"error":{"code":"insufficient_quota"}}"""u8.ToArray());

        Assert.True(ChannelRetryPredicate.IsTerminalUpstreamError(error));
    }

    [Fact]
    public void ParsesRetrySettingsFromStoredJson()
    {
        var settings = ChannelRetrySettings.Parse(
            """{"retryableStatusCodes":[400,403],"retryableErrorPatterns":[{"pattern":"x","regex":true}],"unrelated":1}""");

        Assert.Equal([400, 403], settings.RetryableStatusCodes);
        var pattern = Assert.Single(settings.RetryableErrorPatterns);
        Assert.Equal("x", pattern.Pattern);
        Assert.True(pattern.Regex);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"retryableStatusCodes":"x"}""")]
    public void MalformedSettingsDegradeToNoExtraRules(string? json)
    {
        var settings = ChannelRetrySettings.Parse(json);

        Assert.Empty(settings.RetryableStatusCodes);
        Assert.Empty(settings.RetryableErrorPatterns);
    }

    [Fact]
    public void InvalidStoredRegexNeverMatchesLikeGo()
    {
        var settings = ChannelRetrySettings.Parse(
            """{"retryableErrorPatterns":[{"pattern":"[","regex":true}]}""");

        Assert.False(settings.Matches(0, "anything"));
    }
}
