using AxonHub.Api.Http;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of api.transformOrchestratorError's policy step and
/// api.upstreamErrorStream (internal/server/api/upstream_error_policy.go):
/// rewrites client-visible upstream failures according to
/// <c>retry_policy.upstream_error_policy</c>.
/// </summary>
public interface IUpstreamErrorPolicyApplier
{
    /// <summary>
    /// Applies the policy to a non-stream error after transport classification
    /// (api.applyUpstreamErrorPolicy).
    /// </summary>
    Task<Exception?> ApplyAsync(Exception? error, CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of upstreamErrorStream.Err: classifies transport failures first, then
    /// applies the policy when it is not passthrough.
    /// </summary>
    Task<Exception?> ApplyToStreamErrorAsync(Exception error, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads the policy per call (the reference has no settings cache, C029) and
/// rewrites errors that travelled through the pipeline's upstream marker.
/// </summary>
public sealed class UpstreamErrorPolicyApplier : IUpstreamErrorPolicyApplier
{
    /// <summary>Port of biz.DefaultUpstreamErrorMessage (biz/system.go:296).</summary>
    public const string DefaultMessage = "Upstream provider request failed. Please try again later.";

    private const string UpstreamErrorType = "upstream_error";

    private const string QuotaExhausted = "quota_exhausted";

    private readonly IRetryPolicyProvider _retryPolicyProvider;

    public UpstreamErrorPolicyApplier(IRetryPolicyProvider retryPolicyProvider)
    {
        ArgumentNullException.ThrowIfNull(retryPolicyProvider);
        _retryPolicyProvider = retryPolicyProvider;
    }

    public async Task<Exception?> ApplyAsync(
        Exception? error,
        CancellationToken cancellationToken = default)
    {
        if (error is null)
        {
            return null;
        }

        // A classified transport failure is already the client-visible 502;
        // hidden/custom modes must not rewrite it back into a generic message
        // (the classifier deliberately drops the upstream retry marker).
        if (error is ResponseError { Detail.Code: UpstreamTransportErrors.StreamInterruptedCode })
        {
            return error;
        }

        // api.transformOrchestratorError classifies transport failures before
        // the policy runs, so a bare classified error is not an upstream-marked
        // pipeline error and hidden/custom modes leave it alone.
        var transformed = UpstreamTransportErrors.IsUpstreamTransportError(error)
            ? UpstreamTransportErrors.Classify(error)
            : error;

        var policy = await _retryPolicyProvider
            .GetRetryPolicyAsync(cancellationToken)
            .ConfigureAwait(false);

        return Apply(transformed, policy.UpstreamErrorPolicy);
    }

    public async Task<Exception?> ApplyToStreamErrorAsync(
        Exception error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(error);

        // upstreamErrorStream.Err returns cancellations before the policy runs.
        if (HasCancellationCause(error))
        {
            return error;
        }

        // Classify transport-level interruptions before the policy runs so the
        // stable code and 502 semantics survive a hidden/custom rewrite.
        var transformed = UpstreamTransportErrors.IsUpstreamTransportError(error)
            ? UpstreamTransportErrors.Classify(error)
            : error;

        // The reference reads the policy once to decide whether to rewrite and a
        // second time inside applyUpstreamErrorPolicy; both reads observe the
        // uncached setting, so keep the same two reads.
        var policy = await _retryPolicyProvider
            .GetRetryPolicyAsync(cancellationToken)
            .ConfigureAwait(false);

        var mode = policy.UpstreamErrorPolicy.Mode;
        if (mode.Length == 0 || mode == RetryPolicy.UpstreamErrorModePassthrough)
        {
            return transformed;
        }

        return await ApplyAsync(UpstreamErrors.Wrap(transformed), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Port of api.wrapQuotaExhaustedAsResponseError (chat.go:855-875): the
    /// selector's quota exhaustion becomes a 503 ResponseError before the
    /// transport classifier and the upstream error policy run.
    /// </summary>
    internal static Exception? WrapQuotaExhausted(Exception? error)
    {
        if (error is not QuotaExhaustedException quotaError)
        {
            return error;
        }

        return new ResponseError
        {
            StatusCode = 503,
            Detail = new ErrorDetail
            {
                Message = quotaError.Message,
                Type = QuotaExhausted,
                Code = QuotaExhausted,
            },
        };
    }

    /// <summary>Port of applyUpstreamErrorPolicy's rewrite (chat quota aside).</summary>
    internal static Exception Apply(Exception error, UpstreamErrorPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(policy);

        if (policy.Mode is RetryPolicy.UpstreamErrorModePassthrough or "")
        {
            return error;
        }

        // Only errors that travelled through the pipeline's upstream marker are
        // provider failures; local errors (invalid model, decode failures) keep
        // their own envelope.
        if (!UpstreamErrors.Is(error))
        {
            return error;
        }

        var message = DefaultMessage;
        if (policy.Mode == RetryPolicy.UpstreamErrorModeCustom)
        {
            message = policy.CustomMessage.Trim();
            if (message.Length == 0)
            {
                message = DefaultMessage;
            }
        }

        if (GoErrors.As<ResponseError>(error) is { } responseError)
        {
            // Quota exhaustion is reported by the gateway itself, never rewritten.
            if (responseError.Detail.Code == QuotaExhausted
                || responseError.Detail.Type == QuotaExhausted)
            {
                return error;
            }

            return new ResponseError
            {
                StatusCode = responseError.StatusCode,
                CauseException = error,
                Detail = new ErrorDetail
                {
                    Message = message,
                    Type = FirstNonEmpty(responseError.Detail.Type, UpstreamErrorType),
                    Code = NullIfEmpty(responseError.Detail.Code),
                    RequestId = NullIfEmpty(responseError.Detail.RequestId),
                },
            };
        }

        if (GoErrors.As<HttpClientError>(error) is { } httpError)
        {
            var fields = HttpErrorBodyFields.Parse(httpError.Body);

            return new ResponseError
            {
                StatusCode = httpError.StatusCode,
                CauseException = error,
                Detail = new ErrorDetail
                {
                    Message = message,
                    Type = FirstNonEmpty(fields.Type, UpstreamErrorType),
                    Code = NullIfEmpty(fields.Code),
                    RequestId = NullIfEmpty(fields.RequestId),
                },
            };
        }

        return new ResponseError
        {
            StatusCode = 502,
            CauseException = error,
            Detail = new ErrorDetail
            {
                Message = message,
                Type = UpstreamErrorType,
            },
        };
    }

    private static string FirstNonEmpty(string value, string fallback)
        => value.Length == 0 ? fallback : value;

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrEmpty(value) ? null : value;

    private static bool HasCancellationCause(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is OperationCanceledException)
            {
                return true;
            }
        }

        return false;
    }
}
