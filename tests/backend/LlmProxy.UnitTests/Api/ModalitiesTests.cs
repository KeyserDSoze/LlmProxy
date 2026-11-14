using System.Text.Json;
using LlmProxy.Api.OpenAi;
using LlmProxy.Application.Routing;
using LlmProxy.Domain.Models;

namespace LlmProxy.UnitTests.Api;

public sealed class ModalitiesTests
{
    private static PublicModel Model(string name, ModelSurface surface, bool vision = false)
        => new(Guid.NewGuid(), name, false, false, surface, SupportsVision: vision);

    private static JsonElement Build(params PublicModel[] models)
        => JsonSerializer.SerializeToElement(ModelDetails.Modalities(models), ModelDetails.Json);

    [Fact]
    public void Every_modality_is_listed_even_when_nothing_runs_for_it()
    {
        var data = Build().GetProperty("data").EnumerateArray().ToArray();

        Assert.Equal(["chat", "image", "speech", "transcription", "embedding", "realtime", "classifier"],
            data.Select(item => item.GetProperty("type").GetString()));
        Assert.All(data, item =>
        {
            Assert.Equal(0, item.GetProperty("model_count").GetInt32());
            Assert.Empty(item.GetProperty("models").EnumerateArray());
            Assert.NotEmpty(item.GetProperty("endpoints").EnumerateArray());
        });
    }

    [Fact]
    public void Models_are_grouped_under_their_own_modality_only()
    {
        var data = Build(Model("chat-b", ModelSurface.OpenAi), Model("chat-a", ModelSurface.OpenAi), Model("pic", ModelSurface.ImageGeneration),
                Model("laya", ModelSurface.SystemOne), Model("bge", ModelSurface.Embedding))
            .GetProperty("data").EnumerateArray()
            .ToDictionary(item => item.GetProperty("type").GetString()!, item => item.GetProperty("models").EnumerateArray().Select(model => model.GetProperty("id").GetString()).ToArray());

        Assert.Equal(["chat-a", "chat-b"], data["chat"]);
        Assert.Equal(["pic"], data["image"]);
        Assert.Equal(["laya"], data["classifier"]);
        Assert.Equal(["bge"], data["embedding"]);
        Assert.Empty(data["speech"]);
    }

    [Fact]
    public void Chat_accepts_images_only_when_a_running_model_sees_them()
    {
        static string[] ChatInput(JsonElement root) => root.GetProperty("data").EnumerateArray()
            .Single(item => item.GetProperty("type").GetString() == "chat").GetProperty("input_modalities").EnumerateArray().Select(value => value.GetString()!).ToArray();

        Assert.Equal(["text"], ChatInput(Build(Model("plain", ModelSurface.OpenAi))));
        Assert.Equal(["text", "image"], ChatInput(Build(Model("plain", ModelSurface.OpenAi), Model("vision", ModelSurface.OpenAi, vision: true))));
    }
}
