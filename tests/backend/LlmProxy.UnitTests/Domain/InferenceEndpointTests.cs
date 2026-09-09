using LlmProxy.Domain.Nodes;

namespace LlmProxy.UnitTests.Domain;

public sealed class InferenceEndpointTests
{
    [Theory]
    [InlineData("http://localhost:3450/primopath/", "http://localhost:3450/primopath")]
    [InlineData("http://localhost:3451/altropath", "http://localhost:3451/altropath")]
    [InlineData("http://127.0.0.1:8000", "http://127.0.0.1:8000")]
    [InlineData("http://10.0.0.25:8000/vllm/", "http://10.0.0.25:8000/vllm")]
    [InlineData("https://dgx.internal:8443/inference/tenant-a/", "https://dgx.internal:8443/inference/tenant-a")]
    public void NormalizeBaseAddress_supports_local_ip_port_and_path_prefix(string input, string expected)
    {
        Assert.Equal(expected, InferenceEndpoint.NormalizeBaseAddress(input));
    }

    [Theory]
    [InlineData("http://localhost:3450/primopath", "/v1/chat/completions", "http://localhost:3450/primopath/v1/chat/completions")]
    [InlineData("http://localhost:3451/altropath/", "v1/responses", "http://localhost:3451/altropath/v1/responses")]
    [InlineData("http://10.0.0.25:8000", "/health", "http://10.0.0.25:8000/health")]
    [InlineData("http://[::1]:8000", "/v1/models", "http://[::1]:8000/v1/models")]
    public void Combine_preserves_the_entire_node_service_root(string baseAddress, string path, string expected)
    {
        Assert.Equal(expected, InferenceEndpoint.Combine(baseAddress, path).AbsoluteUri.TrimEnd('/'));
    }

    [Theory]
    [InlineData("ftp://localhost:8000")]
    [InlineData("http://user:password@localhost:8000")]
    [InlineData("http://localhost:8000?tenant=a")]
    [InlineData("http://localhost:8000/path#fragment")]
    [InlineData("not-an-address")]
    public void NormalizeBaseAddress_rejects_ambiguous_or_unsafe_addresses(string input)
    {
        Assert.Throws<ArgumentException>(() => InferenceEndpoint.NormalizeBaseAddress(input));
    }
}
