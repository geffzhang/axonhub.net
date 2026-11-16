using System.Text;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Port of llm/transformer/shared/responses_session_test.go: the recovery error
/// keeps the sentinel and its wire 400, the lane budget refuses aggregate
/// overflow and can be released, and a provider 400 with
/// <c>previous_response_not_found</c> classifies as recovery.
/// </summary>
public sealed class ResponsesLaneTests
{
    [Fact]
    public void RecoveryPreservesHttpError()
    {
        var error = ResponsesRecovery.Error();

        Assert.True(GoErrors.Is(error, ResponsesRecovery.Sentinel));

        var wire = Assert.IsType<HttpClientError>(GoErrors.As<HttpClientError>(error));
        Assert.Equal(400, wire.StatusCode);
    }

    [Fact]
    public void BudgetRejectsAggregateOverflow()
    {
        var budget = new ResponsesBudget(64 << 20);
        Assert.True(budget.Replace(0, 63 << 20));

        var accepted = budget.Replace(0, (1 << 20) + 1);

        Assert.False(accepted);
        Assert.True(budget.Replace(63 << 20, 0));
        Assert.True(budget.Replace(0, 64 << 20));
    }

    [Fact]
    public void ProviderErrorIsRecovery()
    {
        var error = new HttpClientError(
            "POST",
            "http://upstream.test/v1/responses",
            400,
            "400 status",
            Encoding.UTF8.GetBytes("""{"error":{"code":"previous_response_not_found"}}"""));

        Assert.True(ResponsesRecovery.IsRecoveryError(error));
    }
}
