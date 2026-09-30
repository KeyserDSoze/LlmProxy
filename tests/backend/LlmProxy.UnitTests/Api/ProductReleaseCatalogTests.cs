using LlmProxy.Api.Product;

namespace LlmProxy.UnitTests.Api;

public sealed class ProductReleaseCatalogTests
{
    [Fact]
    public void Current_product_release_uses_semver_preview_and_contains_versioned_history()
    {
        var product = ProductReleaseCatalog.GetInfo();

        Assert.Equal("LlmProxy", product.Product);
        Assert.StartsWith("0.2.0-preview.8", product.Version);
        Assert.Equal("preview", product.Channel);
        Assert.Equal(9, product.Releases.Count);

        var current = product.Releases[0];
        Assert.Equal(product.Version, current.Version);
        Assert.Contains("Added", current.Sections.Keys);
        Assert.Contains("Changed", current.Sections.Keys);
        Assert.Contains("Security", current.Sections.Keys);
        Assert.Contains(current.Sections["Added"], item => item.Contains("llmproxyctl", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(current.Sections["Added"], item => item.Contains("linux/arm64", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(current.Sections["Security"], item => item.Contains("SHA-256", StringComparison.OrdinalIgnoreCase));

        var quotas = product.Releases[1];
        Assert.Equal("0.2.0-preview.7", quotas.Version);
        Assert.Contains(quotas.Sections["Added"], item => item.Contains("across all personal API keys", StringComparison.OrdinalIgnoreCase));

        var identity = product.Releases[2];
        Assert.Equal("0.2.0-preview.6", identity.Version);
        Assert.Contains(identity.Sections["Added"], item => item.Contains("personal", StringComparison.OrdinalIgnoreCase));

        var acceptance = product.Releases[3];
        Assert.Equal("0.2.0-preview.5", acceptance.Version);
        Assert.Contains(acceptance.Sections["Added"], item => item.Contains("acceptance", StringComparison.OrdinalIgnoreCase));

        var deployment = product.Releases[4];
        Assert.Equal("0.2.0-preview.4", deployment.Version);
        Assert.Contains(deployment.Sections["Added"], item => item.Contains("full-stack", StringComparison.OrdinalIgnoreCase));

        var releaseGate = product.Releases[5];
        Assert.Equal("0.2.0-preview.3", releaseGate.Version);
        Assert.Contains(releaseGate.Sections["Added"], item => item.Contains("same source SHA", StringComparison.OrdinalIgnoreCase));

        var supplyChain = product.Releases[6];
        Assert.Equal("0.2.0-preview.2", supplyChain.Version);
        Assert.Contains(supplyChain.Sections["Added"], item => item.Contains("software bill of materials", StringComparison.OrdinalIgnoreCase));

        var rollups = product.Releases[7];
        Assert.Equal("0.2.0-preview.1", rollups.Version);
        Assert.Contains(rollups.Sections["Added"], item => item.Contains("rollup", StringComparison.OrdinalIgnoreCase));

        var baseline = product.Releases[8];
        Assert.Equal("0.1.0-preview.1", baseline.Version);
        Assert.Contains("Fixed", baseline.Sections.Keys);
        Assert.Contains(baseline.Sections["Added"], item => item.Contains("Safe node maintenance", StringComparison.OrdinalIgnoreCase));
    }
}
