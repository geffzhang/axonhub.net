using AxonHub.Api.Management;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port of <c>objects.ParseOverrideOperations</c>' numeric handling. Go's
/// <c>*int</c> is 64-bit on the reference platform, so an operation index past
/// <see cref="int.MaxValue"/> must survive decoding (C022).
/// </summary>
public sealed class ChannelOverrideOperationsTests
{
    [Fact]
    public void ParseOverrideOperationsAcceptsIndexBeyondInt32()
    {
        var operations = ChannelOverrideOperations.ParseOverrideOperations(
            """[{"op":"array_insert","path":"items","index":3000000000}]""");

        var operation = Assert.Single(operations!);
        Assert.Equal("array_insert", operation.Op);
        Assert.Equal("items", operation.Path);
        Assert.Equal(3000000000L, operation.Index);
    }
}
