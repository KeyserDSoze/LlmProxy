using System.Text;
using LlmProxy.Api.Multimodal;
using LlmProxy.Domain.Models;

namespace LlmProxy.UnitTests.Api;

public sealed class MultimodalGatewayContractTests
{
    [Theory]
    [InlineData(ModelSurface.ImageGeneration)]
    [InlineData(ModelSurface.SpeechSynthesis)]
    [InlineData(ModelSurface.AudioTranscription)]
    public void Distinct_multimodal_surfaces_are_valid_logical_models(ModelSurface surface)
    {
        var model = new ModelDefinition("example-model", "org/model", false, false, surface);
        Assert.Equal(surface, model.Surface);
        Assert.False(model.SupportsStreaming);
        Assert.False(model.SupportsTools);
    }

    [Fact]
    public void Invalid_surfaces_are_rejected_in_the_domain()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ModelDefinition("fake", "org/model", false, false, (ModelSurface)999));
    }

    [Theory]
    [InlineData(32, 32, false)]
    [InlineData(33, 32, true)]
    [InlineData(0, 10, false)]
    public async Task Bounded_body_reading_refuses_oversized_unknown_length_uploads(
        int length, int limit, bool shouldFail)
    {
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes(new string('a', length)));
        if (shouldFail)
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                MultimodalEndpoints.ReadBoundedAsync(source, limit, CancellationToken.None));
        else
            Assert.Equal(length, (await MultimodalEndpoints.ReadBoundedAsync(
                source, limit, CancellationToken.None)).Length);
    }

    [Fact]
    public void Audio_file_binary_bytes_are_not_utf8_decoded_or_modified()
    {
        var bytes = new byte[] { 0, 255, 254, 13, 10, 5, 128, 0 };
        using var source = new MemoryStream(bytes);
        Assert.Equal(bytes, MultimodalEndpoints.ReadBoundedAsync(source, 512, CancellationToken.None).GetAwaiter().GetResult());
    }
}
