using System.Text;
using AxonHub.Api.Orchestration;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks api.applyUpstreamErrorPolicy and api.upstreamErrorStream.Err
/// (internal/server/api/upstream_error_policy.go): passthrough by default,
/// hidden/custom message rewrites that keep status, type, code and request id,
/// and the rule that only upstream-marked errors are rewritten.
/// </summary>
public sealed class UpstreamErrorPolicyApplierTests
{
    [Fact]
    public async Task CustomModeRewritesHttpClientErrorAndKeepsClassification()
    {
        var applier = NewApplier(RetryPolicy.UpstreamErrorModeCustom, "try later");
        var raw = new HttpClientError(
            "POST",
            "http://upstream/v1/chat/completions",
            429,
            "429 Too Many Requests",
            Encoding.UTF8.GetBytes(
                """{"error":{"message":"raw provider secret","type":"rate_limit_error","code":"provider_rate_limit"},"request_id":"req_123"}"""));
        var error = UpstreamErrors.Wrap(raw)!;

        var result = await applier.ApplyAsync(error);

        var responseError = Assert.IsType<ResponseError>(result);
        Assert.Equal(429, responseError.StatusCode);
        Assert.Equal("try later", responseError.Detail.Message);
        Assert.Equal("rate_limit_error", responseError.Detail.Type);
        Assert.Equal("provider_rate_limit", responseError.Detail.Code);
        Assert.Equal("req_123", responseError.Detail.RequestId);
        Assert.DoesNotContain("raw provider secret", responseError.Detail.Message, StringComparison.Ordinal);
        Assert.Same(error, responseError.CauseException);
    }

    [Fact]
    public async Task HiddenModeReplacesTheMessageAndKeepsTheDetail()
    {
        var applier = NewApplier(RetryPolicy.UpstreamErrorModeHidden);
        var raw = new ResponseError
        {
            StatusCode = 400,
            Detail = new ErrorDetail
            {
                Message = "provider secret",
                Type = "invalid_request_error",
                Code = "bad_request",
                RequestId = "req_1",
            },
        };

        var result = await applier.ApplyAsync(UpstreamErrors.Wrap(raw));

        var responseError = Assert.IsType<ResponseError>(result);
        Assert.Equal(400, responseError.StatusCode);
        Assert.Equal(UpstreamErrorPolicyApplier.DefaultMessage, responseError.Detail.Message);
        Assert.Equal("invalid_request_error", responseError.Detail.Type);
        Assert.Equal("bad_request", responseError.Detail.Code);
        Assert.Equal("req_1", responseError.Detail.RequestId);
    }

    [Fact]
    public async Task HiddenModeDefaultsAnEmptyTypeToUpstreamError()
    {
        var applier = NewApplier(RetryPolicy.UpstreamErrorModeHidden);
        var raw = new ResponseError { StatusCode = 502 };

        var result = await applier.ApplyAsync(UpstreamErrors.Wrap(raw));

        var responseError = Assert.IsType<ResponseError>(result);
        Assert.Equal("upstream_error", responseError.Detail.Type);
        Assert.Null(responseError.Detail.Code);
        Assert.Null(responseError.Detail.RequestId);
    }

    [Fact]
    public async Task CustomModeTrimsTheMessage()
    {
        var applier = NewApplier(RetryPolicy.UpstreamErrorModeCustom, "  try later \t");

        var result = await applier.ApplyAsync(UpstreamErrors.Wrap(new ResponseError()));

        var responseError = Assert.IsType<ResponseError>(result);
        Assert.Equal("try later", responseError.Detail.Message);
    }

    [Fact]
    public async Task CustomModeWithBlankMessageUsesTheDefault()
    {
        var applier = NewApplier(RetryPolicy.UpstreamErrorModeCustom, "   ");

        var result = await applier.ApplyAsync(UpstreamErrors.Wrap(new ResponseError()));

        var responseError = Assert.IsType<ResponseError>(result);
        Assert.Equal(UpstreamErrorPolicyApplier.DefaultMessage, responseError.Detail.Message);
    }

    [Fact]
    public async Task PassthroughReturnsTheOriginalError()
    {
        var applier = NewApplier(RetryPolicy.UpstreamErrorModePassthrough);
        var error = UpstreamErrors.Wrap(new ResponseError { StatusCode = 500 })!;

        Assert.Same(error, await applier.ApplyAsync(error));
    }

    [Fact]
    public async Task EmptyModeReturnsTheOriginalError()
    {
        var applier = NewApplier(string.Empty);
        var error = UpstreamErrors.Wrap(new ResponseError { StatusCode = 500 })!;

        Assert.Same(error, await applier.ApplyAsync(error));
    }

    [Fact]
    public async Task LocalErrorsAreNotRewritten()
    {
        var applier = NewApplier(RetryPolicy.UpstreamErrorModeCustom, "try later");
        var localResponseError = new ResponseError
        {
            StatusCode = 403,
            Detail = new ErrorDetail { Message = "API key quota exceeded", Type = "quota_exceeded_error" },
        };
        var localHttpError = new HttpClientError("POST", "http://local", 400, "400 Bad Request");

        Assert.Same(localResponseError, await applier.ApplyAsync(localResponseError));
        Assert.Same(localHttpError, await applier.ApplyAsync(localHttpError));
    }

    [Fact]
    public async Task UpstreamErrorWithoutStructuredDetailBecomesBadGateway()
    {
        var applier = NewApplier(RetryPolicy.UpstreamErrorModeHidden);
        var error = UpstreamErrors.Wrap(new InvalidOperationException("boom"))!;

        var result = await applier.ApplyAsync(error);

        var responseError = Assert.IsType<ResponseError>(result);
        Assert.Equal(502, responseError.StatusCode);
        Assert.Equal(UpstreamErrorPolicyApplier.DefaultMessage, responseError.Detail.Message);
        Assert.Equal("upstream_error", responseError.Detail.Type);
        Assert.Null(responseError.Detail.Code);
        Assert.Null(responseError.Detail.RequestId);
    }

    [Theory]
    [InlineData("quota_exhausted", "")]
    [InlineData("", "quota_exhausted")]
    public async Task QuotaExhaustedResponseErrorsAreNotRewritten(string code, string type)
    {
        var applier = NewApplier(RetryPolicy.UpstreamErrorModeCustom, "try later");
        var error = UpstreamErrors.Wrap(new ResponseError
        {
            StatusCode = 503,
            Detail = new ErrorDetail { Message = "quota", Code = code, Type = type },
        })!;

        Assert.Same(error, await applier.ApplyAsync(error));
    }

    /// <summary>
    /// Only non-empty strings count. The last two cases pin the port's stricter
    /// body reader (C033): gjson would still scan fields out of a truncated
    /// document, and it resolves duplicate keys to the first occurrence while
    /// System.Text.Json keeps the last.
    /// </summary>
    [Theory]
    [InlineData("""{"error":{"type":7,"code":"c"},"request_id":5}""", "upstream_error", "c")]
    [InlineData("""{"error":{"type":"","code":""}}""", "upstream_error", null)]
    [InlineData("""{"error":"hidden"}""", "upstream_error", null)]
    [InlineData("[]", "upstream_error", null)]
    [InlineData("not json", "upstream_error", null)]
    [InlineData("", "upstream_error", null)]
    [InlineData("{\"error\":{\"type\":\"rate_limit_error\"", "upstream_error", null)]
    [InlineData("""{"error":{"type":"first","type":"second"}}""", "second", null)]
    public async Task HttpBodyFieldsFollowTheGjsonStringChecks(
        string body,
        string expectedType,
        string? expectedCode)
    {
        var applier = NewApplier(RetryPolicy.UpstreamErrorModeHidden);
        var raw = new HttpClientError(
            "POST",
            "http://upstream",
            503,
            "503 Service Unavailable",
            Encoding.UTF8.GetBytes(body));

        var result = await applier.ApplyAsync(UpstreamErrors.Wrap(raw));

        var responseError = Assert.IsType<ResponseError>(result);
        Assert.Equal(503, responseError.StatusCode);
        Assert.Equal(expectedType, responseError.Detail.Type);
        Assert.Equal(expectedCode, responseError.Detail.Code);
        Assert.Null(responseError.Detail.RequestId);
    }

    [Fact]
    public async Task StreamHiddenModeClassifiesATransportError()
    {
        var applier = NewApplier(RetryPolicy.UpstreamErrorModeHidden);
        var raw = new IOException("connection reset");

        var result = await applier.ApplyToStreamErrorAsync(raw);

        var responseError = Assert.IsType<ResponseError>(result);
        Assert.Equal(502, responseError.StatusCode);
        Assert.Equal(UpstreamErrorPolicyApplier.DefaultMessage, responseError.Detail.Message);
        Assert.Equal("upstream_error", responseError.Detail.Type);
        Assert.Equal(UpstreamTransportErrors.StreamInterruptedCode, responseError.Detail.Code);
        Assert.DoesNotContain("connection reset", responseError.Detail.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamCustomModeRewritesATransportError()
    {
        var applier = NewApplier(RetryPolicy.UpstreamErrorModeCustom, "try later");
        var raw = new IOException("connection reset");

        var result = await applier.ApplyToStreamErrorAsync(raw);

        var responseError = Assert.IsType<ResponseError>(result);
        Assert.Equal(502, responseError.StatusCode);
        Assert.Equal("try later", responseError.Detail.Message);
        Assert.Equal(UpstreamTransportErrors.StreamInterruptedCode, responseError.Detail.Code);
    }

    [Fact]
    public async Task StreamPassthroughClassifiesTransportErrors()
    {
        var applier = NewApplier(RetryPolicy.UpstreamErrorModePassthrough);

        var result = await applier.ApplyToStreamErrorAsync(new IOException("connection reset"));

        var responseError = Assert.IsType<ResponseError>(result);
        Assert.Equal(502, responseError.StatusCode);
        Assert.Equal(UpstreamTransportErrors.StreamInterruptedCode, responseError.Detail.Code);
        Assert.Contains("connection reset", responseError.Detail.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamPassthroughPassesPlainErrorsThrough()
    {
        var applier = NewApplier(RetryPolicy.UpstreamErrorModePassthrough);
        var plain = new InvalidOperationException("boom");

        Assert.Same(plain, await applier.ApplyToStreamErrorAsync(plain));
    }

    [Fact]
    public async Task StreamCancellationPassesThrough()
    {
        var applier = NewApplier(RetryPolicy.UpstreamErrorModeHidden);
        var canceled = new OperationCanceledException("client went away");

        Assert.Same(canceled, await applier.ApplyToStreamErrorAsync(canceled));
    }

    [Fact]
    public async Task StreamPipelineResponseErrorIsRewrittenInHiddenMode()
    {
        var applier = NewApplier(RetryPolicy.UpstreamErrorModeHidden);
        var raw = UpstreamErrors.Wrap(new ResponseError
        {
            StatusCode = 400,
            Detail = new ErrorDetail
            {
                Message = "provider secret",
                Type = "invalid_request_error",
                Code = "bad_request",
            },
        })!;

        var result = await applier.ApplyToStreamErrorAsync(raw);

        var responseError = Assert.IsType<ResponseError>(result);
        Assert.Equal(400, responseError.StatusCode);
        Assert.Equal(UpstreamErrorPolicyApplier.DefaultMessage, responseError.Detail.Message);
        Assert.Equal("invalid_request_error", responseError.Detail.Type);
        Assert.Equal("bad_request", responseError.Detail.Code);
    }

    [Fact]
    public async Task StreamLocalErrorBecomesAnUpstreamErrorWhenThePolicyIsActive()
    {
        var applier = NewApplier(RetryPolicy.UpstreamErrorModeCustom, "try later");

        var result = await applier.ApplyToStreamErrorAsync(new InvalidOperationException("boom"));

        var responseError = Assert.IsType<ResponseError>(result);
        Assert.Equal(502, responseError.StatusCode);
        Assert.Equal("try later", responseError.Detail.Message);
        Assert.Equal("upstream_error", responseError.Detail.Type);
    }

    [Fact]
    public async Task StreamPassthroughReadsThePolicyOnce()
    {
        var provider = new StaticRetryPolicyProvider(Policy(RetryPolicy.UpstreamErrorModePassthrough));
        var applier = new UpstreamErrorPolicyApplier(provider);

        await applier.ApplyToStreamErrorAsync(new IOException("connection reset"));

        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task StreamRewriteReadsThePolicyTwiceLikeTheReference()
    {
        var provider = new StaticRetryPolicyProvider(Policy(RetryPolicy.UpstreamErrorModeHidden));
        var applier = new UpstreamErrorPolicyApplier(provider);

        await applier.ApplyToStreamErrorAsync(new IOException("connection reset"));

        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task StoreFailureFallsBackToPassthrough()
    {
        var store = new FakeSystemStore
        {
            GetFailure = new InvalidOperationException("database is down"),
        };
        var applier = new UpstreamErrorPolicyApplier(new SystemRetryPolicyService(store));
        var error = UpstreamErrors.Wrap(new ResponseError { StatusCode = 500 })!;

        Assert.Same(error, await applier.ApplyAsync(error));
    }

    private static UpstreamErrorPolicyApplier NewApplier(string mode, string customMessage = "")
        => new(new StaticRetryPolicyProvider(Policy(mode, customMessage)));

    private static RetryPolicy Policy(string mode, string customMessage = "")
        => new()
        {
            MaxChannelRetries = 0,
            MaxSingleChannelRetries = 0,
            RetryDelayMs = 0,
            UpstreamErrorPolicy = new UpstreamErrorPolicy(mode, customMessage),
        };
}
