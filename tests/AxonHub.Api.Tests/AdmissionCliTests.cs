using AxonHub.Api.Cli;

namespace AxonHub.Api.Tests;

public sealed class AdmissionCliTests
{
    [Theory]
    [InlineData("release")]
    [InlineData("invalid")]
    public void InvalidCommandsNeverStartTheServerOrOpenADatabase(string subcommand)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var result = CliCommand.TryRun(["admission", subcommand], output, error);
        Assert.Equal(1, result);
        Assert.Contains("Usage:", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ReleasingRequiresExplicitUpstreamConfirmation()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var result = CliCommand.TryRun(
            ["admission", "release", Guid.NewGuid().ToString()], output, error);
        Assert.Equal(1, result);
        Assert.Contains("--confirm-upstream-stopped", error.ToString(), StringComparison.Ordinal);
    }
}
