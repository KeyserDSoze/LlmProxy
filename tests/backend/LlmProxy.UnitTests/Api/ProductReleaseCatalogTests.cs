using LlmProxy.Api.Product;

namespace LlmProxy.UnitTests.Api;

public sealed class ProductReleaseCatalogTests
{
    [Fact]
    public void Current_product_release_uses_semver_preview_and_contains_versioned_history()
    {
        var product = ProductReleaseCatalog.GetInfo();

        Assert.Equal("LlmProxy", product.Product);
        Assert.StartsWith("0.2.0-preview.4", product.Version);
        Assert.Equal("preview", product.Channel);
        Assert.Equal(5, product.Releases.Count);

        var current = product.Releases[0];
        Assert.Equal(product.Version, current.Version);
        Assert.Contains("Added", current.Sections.Keys);
        Assert.Contains("Changed", current.Sections.Keys);
        Assert.Contains("Security", current.Sections.Keys);
        Assert.Contains(current.Sections["Added"], item => item.Contains("full-stack", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(current.Sections["Changed"], item => item.Contains("deploy script", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(current.Sections["Security"], item => item.Contains("Grafana", StringComparison.OrdinalIgnoreCase));

        var releaseGate = product.Releases[1];
        Assert.Equal("0.2.0-preview.3", releaseGate.Version);
        Assert.Contains(releaseGate.Sections["Added"], item => item.Contains("same source SHA", StringComparison.OrdinalIgnoreCase));

        var supplyChain = product.Releases[2];
        Assert.Equal("0.2.0-preview.2", supplyChain.Version);
        Assert.Contains(supplyChain.Sections["Added"], item => item.Contains("software bill of materials", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(supplyChain.Sections["Added"], item => item.Contains("provenance", StringComparison.OrdinalIgnoreCase));

        var rollups = product.Releases[3];
        Assert.Equal("0.2.0-preview.1", rollups.Version);
        Assert.Contains(rollups.Sections["Added"], item => item.Contains("rollup", StringComparison.OrdinalIgnoreCase));

        var baseline = product.Releases[4];
        Assert.Equal("0.1.0-preview.1", baseline.Version);
        Assert.Contains("Fixed", baseline.Sections.Keys);
        Assert.Contains(baseline.Sections["Added"], item => item.Contains("Safe node maintenance", StringComparison.OrdinalIgnoreCase));
    }
}
