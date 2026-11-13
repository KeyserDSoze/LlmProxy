using LlmProxy.Api.Admin;

namespace LlmProxy.UnitTests.AgentConnectivity;

public sealed class DirectManagementAddressTests
{
    [Theory]
    [InlineData("http://10.0.0.21:9900")]
    [InlineData("http://192.168.1.50:9900/")]
    [InlineData("http://172.20.4.9:9900")]
    [InlineData("http://spark-1d52.lan:9900")]
    [InlineData("http://[fd00::21]:9900")]
    public void A_lan_agent_address_is_accepted(string address)
        => Assert.True(DirectManagementAddress.IsAcceptable(address, out _));

    [Theory]
    [InlineData("http://169.254.169.254:9900")]            // cloud metadata service
    [InlineData("http://127.0.0.1:9900")]                  // the gateway host itself
    [InlineData("http://localhost:9900")]
    [InlineData("http://api.localhost:9900")]
    [InlineData("http://[::1]:9900")]
    [InlineData("http://0.0.0.0:9900")]
    [InlineData("http://[fe80::1]:9900")]
    [InlineData("http://224.0.0.1:9900")]
    [InlineData("http://255.255.255.255:9900")]
    [InlineData("http://[::ffff:169.254.169.254]:9900")]
    public void Addresses_that_would_make_the_gateway_call_itself_or_infrastructure_are_refused(string address)
        => Assert.False(DirectManagementAddress.IsAcceptable(address, out _));

    [Theory]
    [InlineData("https://10.0.0.21:9900")]
    [InlineData("http://10.0.0.21:8080")]
    [InlineData("http://10.0.0.21")]
    [InlineData("http://user:pass@10.0.0.21:9900")]
    [InlineData("http://10.0.0.21:9900/?x=1")]
    [InlineData("http://10.0.0.21:9900/#f")]
    [InlineData("ftp://10.0.0.21:9900")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData(null)]
    public void Only_a_plain_http_origin_on_the_agent_port_is_accepted(string? address)
        => Assert.False(DirectManagementAddress.IsAcceptable(address, out _));
}
