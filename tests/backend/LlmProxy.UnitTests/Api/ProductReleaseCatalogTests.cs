using LlmProxy.Api.Product;

namespace LlmProxy.UnitTests.Api;

public sealed class ProductReleaseCatalogTests
{
    [Fact]
    public void Current_product_release_uses_semver_preview_and_contains_release_notes()
    {
        var product = ProductReleaseCatalog.GetInfo();

        Assert.Equal("LlmProxy", product.Product);
        Assert.StartsWith("0.1.0-preview.1", product.Version);
        Assert.Equal("preview", product.Channel);
        var release = Assert.Single(product.Releases);
        Assert.Equal(product.Version, release.Version);
        Assert.Contains("Added", release.Sections.Keys);
        Assert.Contains("Fixed", release.Sections.Keys);
        Assert.Contains(release.Sections["Added"], item => item.Contains("Safe node maintenance", StringComparison.OrdinalIgnoreCase));
    }
}
