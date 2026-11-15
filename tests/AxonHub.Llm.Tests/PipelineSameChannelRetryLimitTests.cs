using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using PipelineType = AxonHub.Llm.Pipeline.Pipeline;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Port of llm/pipeline/same_channel_retry_limit_test.go: a channel-level
/// maxSingleChannelRetries override adjusts the same-channel retry count but
/// can never re-enable retries disabled by the global policy.
/// </summary>
public sealed class PipelineSameChannelRetryLimitTests
{
    [Fact]
    public void NoOverrideUsesTheGlobalValue()
    {
        var pipeline = Pipeline(new MockOutbound(), globalLimit: 2);

        Assert.Equal(2, pipeline.MaxSameChannelRetriesFor());
    }

    [Fact]
    public void AnOverrideReplacesTheGlobalValue()
    {
        var outbound = new MockOutbound { SameChannelRetryLimitOverride = 7 };
        var pipeline = Pipeline(outbound, globalLimit: 2);

        Assert.Equal(7, pipeline.MaxSameChannelRetriesFor());
    }

    [Fact]
    public void AGlobalDisableIgnoresTheOverride()
    {
        var outbound = new MockOutbound { SameChannelRetryLimitOverride = 7 };
        var pipeline = Pipeline(outbound, globalLimit: 0, retriesDisabled: true);

        Assert.Equal(0, pipeline.MaxSameChannelRetriesFor());
    }

    [Fact]
    public void AZeroOverrideStopsSameChannelRetriesDespiteTheGlobalValue()
    {
        var execCalls = 0;
        var executor = new MockExecutor
        {
            DoFn = (_, _) =>
            {
                execCalls++;
                throw new InvalidOperationException("temporary error");
            },
        };
        var outbound = new MockOutbound
        {
            SameChannelRetryLimitOverride = 0,
            CanRetryFn = _ => true,
            HasMoreChannelsFn = () => false,
        };
        var pipeline = Pipeline(executor, outbound, globalLimit: 3);

        Assert.ThrowsAny<Exception>(
            () => pipeline.Process(new HttpRequestData()));
        Assert.Equal(1, execCalls);
    }

    [Fact]
    public void AnOverrideRaisesTheRetryCountAboveTheGlobalValue()
    {
        var execCalls = 0;
        var executor = new MockExecutor
        {
            DoFn = (_, _) =>
            {
                execCalls++;
                if (execCalls > 3)
                {
                    return new HttpResponseData();
                }

                throw new InvalidOperationException("temporary error");
            },
        };
        var outbound = new MockOutbound
        {
            SameChannelRetryLimitOverride = 3,
            CanRetryFn = _ => true,
            HasMoreChannelsFn = () => false,
        };
        var pipeline = Pipeline(executor, outbound, globalLimit: 1);

        var result = pipeline.Process(new HttpRequestData());

        Assert.NotNull(result);
        Assert.Equal(4, execCalls);
    }

    private static PipelineType Pipeline(
        MockOutbound outbound,
        int globalLimit,
        bool retriesDisabled = false)
        => Pipeline(new MockExecutor(), outbound, globalLimit, retriesDisabled);

    private static PipelineType Pipeline(
        MockExecutor executor,
        MockOutbound outbound,
        int globalLimit,
        bool retriesDisabled = false)
        => new(
            executor,
            new MockInbound(),
            outbound,
            new PipelineOptions
            {
                MaxSameChannelRetries = globalLimit,
                RetriesDisabled = retriesDisabled,
            });
}
