using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using LlmProxy.Api.Multimodal;
using LlmProxy.Api.OpenAi;
using LlmProxy.Application.Routing;
using LlmProxy.Domain.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace LlmProxy.UnitTests.Api;

public sealed class EmbeddingAndRealtimeTests
{
    [Theory]
    [InlineData("\"hello\"", true)]
    [InlineData("[\"a\",\"b\"]", true)]
    [InlineData("[1,2,3]", true)]
    [InlineData("[[1,2],[3,4]]", true)]
    [InlineData("\"\"", false)]
    [InlineData("\"   \"", false)]
    [InlineData("[]", false)]
    [InlineData("[\"a\",\"\"]", false)]
    [InlineData("[\"a\",1]", false)]
    [InlineData("[[]]", false)]
    [InlineData("{\"x\":1}", false)]
    [InlineData("42", false)]
    public void Embedding_input_follows_the_openai_shapes(string json, bool valid)
        => Assert.Equal(valid, EmbeddingInput.IsValid(JsonNode.Parse(json), out _));

    [Fact]
    public void A_missing_input_is_refused_with_a_reason()
    {
        Assert.False(EmbeddingInput.IsValid(null, out var problem));
        Assert.Contains("required", problem);
    }

    [Fact]
    public void An_oversized_batch_is_refused()
    {
        var batch = new JsonArray(Enumerable.Range(0, EmbeddingInput.MaxItems + 1).Select(i => (JsonNode)JsonValue.Create("text " + i)).ToArray());
        Assert.False(EmbeddingInput.IsValid(batch, out var problem));
        Assert.Contains("at most", problem);
    }

    [Theory]
    [InlineData("http://10.0.0.5:8000", "ws://10.0.0.5:8000/v1/realtime?model=org%2Fvoice")]
    [InlineData("https://voice.example/prefix/", "wss://voice.example/prefix/v1/realtime?model=org%2Fvoice")]
    public void The_runtime_address_becomes_its_realtime_websocket_url(string baseAddress, string expected)
        => Assert.Equal(expected, RealtimeEndpoints.UpstreamUri(new Uri(baseAddress), "org/voice").ToString());

    [Theory]
    [InlineData(ModelSurface.OpenAi, "chat")]
    [InlineData(ModelSurface.Embedding, "embedding")]
    [InlineData(ModelSurface.ImageGeneration, "image")]
    [InlineData(ModelSurface.SpeechSynthesis, "speech")]
    [InlineData(ModelSurface.AudioTranscription, "transcription")]
    [InlineData(ModelSurface.Realtime, "realtime")]
    public void Every_surface_has_a_client_facing_type(ModelSurface surface, string type)
        => Assert.Equal(type, ModelDetails.TypeOf(surface));

    [Fact]
    public void Embedding_and_realtime_models_describe_their_modalities_and_endpoints()
    {
        var embedding = System.Text.Json.JsonSerializer.SerializeToElement(
            ModelDetails.From(new PublicModel(Guid.NewGuid(), "bge", false, false, ModelSurface.Embedding, ContextWindowTokens: 8192)), ModelDetails.Json);
        var realtime = System.Text.Json.JsonSerializer.SerializeToElement(
            ModelDetails.From(new PublicModel(Guid.NewGuid(), "voice", false, false, ModelSurface.Realtime)), ModelDetails.Json);

        Assert.Equal("embedding", embedding.GetProperty("type").GetString());
        Assert.Equal(8192, embedding.GetProperty("context_window").GetInt32());
        Assert.Equal("embedding", embedding.GetProperty("output_modalities")[0].GetString());
        Assert.Equal("/v1/embeddings", embedding.GetProperty("endpoints")[0].GetString());
        Assert.Equal("/v1/realtime", realtime.GetProperty("endpoints")[0].GetString());
        Assert.Equal(2, realtime.GetProperty("input_modalities").GetArrayLength());
    }

    // --- relay: real sockets on loopback -------------------------------------------------------------------------

    private static async Task<(WebApplication App, Uri Address)> Start(Action<WebApplication> map)
    {
        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.UseWebSockets();
        map(app);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return (app, new Uri(address.Replace("http://", "ws://")));
    }

    private static async Task<string> ReceiveText(WebSocket socket)
    {
        var buffer = new byte[4096];
        var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    [Fact]
    public async Task Frames_travel_both_ways_through_the_relay_and_a_client_close_closes_the_runtime()
    {
        var upstreamClosed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (echo, echoAddress) = await Start(app => app.Map("/echo", async (HttpContext context) =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[4096];
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close) { upstreamClosed.TrySetResult(true); return; }
                await socket.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage, CancellationToken.None);
            }
        }));
        var (proxy, proxyAddress) = await Start(app => app.Map("/relay", async (HttpContext context) =>
        {
            using var client = await context.WebSockets.AcceptWebSocketAsync();
            using var upstream = new ClientWebSocket();
            await upstream.ConnectAsync(new Uri(echoAddress, "/echo"), CancellationToken.None);
            await RealtimeEndpoints.RelayAsync(client, upstream, context.RequestAborted);
        }));

        var ct = TestContext.Current.CancellationToken;
        try
        {
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri(proxyAddress, "/relay"), ct);

            await socket.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"session.update\"}"), WebSocketMessageType.Text, true, ct);
            Assert.Equal("{\"type\":\"session.update\"}", await ReceiveText(socket));

            var audio = new byte[] { 1, 2, 3, 4, 5, 250 };
            await socket.SendAsync(audio, WebSocketMessageType.Binary, true, ct);
            var buffer = new byte[64];
            var echoed = await socket.ReceiveAsync(buffer, ct);
            Assert.Equal(WebSocketMessageType.Binary, echoed.MessageType);
            Assert.Equal(audio, buffer.AsSpan(0, echoed.Count).ToArray());

            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);
            Assert.True(await upstreamClosed.Task.WaitAsync(TimeSpan.FromSeconds(10), ct));
        }
        finally
        {
            await proxy.StopAsync(ct); await echo.StopAsync(ct);
        }
    }
}
