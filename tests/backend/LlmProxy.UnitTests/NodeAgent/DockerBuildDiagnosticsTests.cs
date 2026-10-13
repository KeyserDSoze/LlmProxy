using LlmProxy.NodeAgent;

namespace LlmProxy.UnitTests.NodeAgent;

public sealed class DockerBuildDiagnosticsTests
{
    [Fact]
    public void Network_interruption_is_retryable_and_preserves_last_real_error()
    {
        var result = new CommandResult(1, "", """
            Collecting sympy
            Downloading packaging-24.2.whl
            ERROR: HTTPSConnectionPool(host='pypi.org'): ReadTimeout on wheel download
            #7 ERROR: process pip install returned exit code: 1
            """);
        var diagnosis = DockerBuildDiagnostics.Analyze(result);
        Assert.True(diagnosis.TransientNetworkFailure);
        Assert.Contains("ReadTimeout", diagnosis.Message);
    }

    [Theory]
    [InlineData("ERROR: No matching distribution found for dependency==1.2.3")]
    [InlineData("OSError: [Errno 28] No space left on device")]
    public void Permanent_install_errors_are_not_retried(string output)
    {
        var diagnosis = DockerBuildDiagnostics.Analyze(new CommandResult(1, "", output));
        Assert.False(diagnosis.TransientNetworkFailure);
        Assert.Contains(output, diagnosis.Message);
    }

    [Fact]
    public void Show_tail_of_long_build_output_instead_of_first_dependency_lines()
    {
        var irrelevant = string.Concat(Enumerable.Repeat("Collecting package\n", 200));
        var result = new CommandResult(1, irrelevant, "ERROR: failed to solve: exit code 1");
        var diagnosis = DockerBuildDiagnostics.Analyze(result);
        Assert.Contains("failed to solve", diagnosis.Message);
    }
}
