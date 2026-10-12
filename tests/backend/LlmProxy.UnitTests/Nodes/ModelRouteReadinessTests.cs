using LlmProxy.Api.Admin;
using LlmProxy.Domain.Deployments;
using LlmProxy.Domain.Models;
using LlmProxy.Domain.Nodes;

namespace LlmProxy.UnitTests.Nodes;

public sealed class ModelRouteReadinessTests
{
    private static ModelDefinition Model() => new("qwen3-8b-airllm", "Qwen/Qwen3-8B");
    private static InferenceNode Node() => new("spark-1d52", "http://127.0.0.1:18000");

    [Fact]
    public void Missing_logical_model_and_missing_deployment_are_distinct()
    {
        Assert.Equal("model_not_registered",
            ModelRouteReadiness.Inspect("qwen3-8b-airllm", null, [], []).Code);
        Assert.Equal("deployment_missing",
            ModelRouteReadiness.Inspect("qwen3-8b-airllm", Model(), [], []).Code);
    }

    [Fact]
    public void Disabled_model_or_wrong_surface_never_claims_routability()
    {
        var model = Model();
        model.Disable();
        Assert.Equal("model_disabled",
            ModelRouteReadiness.Inspect(model.PublicName, model, [], []).Code);
        var stt = new ModelDefinition("whisper", "Systran/faster-whisper-tiny",
            false, false, ModelSurface.AudioTranscription);
        Assert.Equal("wrong_model_surface",
            ModelRouteReadiness.Inspect(stt.PublicName, stt, [], []).Code);
    }

    [Fact]
    public void Installed_but_disabled_route_requires_explicit_start_or_health_activation()
    {
        var node = Node();
        var model = Model();
        var deployment = new ModelDeployment(node.Id, model.Id);
        deployment.ConfigureRuntime("http://127.0.0.1:18000", "qwen3-8b-airllm", "managed-airllm-1");
        deployment.Disable();
        var diagnosis = ModelRouteReadiness.Inspect(model.PublicName, model, [deployment], [node]);
        Assert.Equal("deployment_disabled", diagnosis.Code);
        Assert.Equal("installations", diagnosis.NextAction);
        Assert.Single(diagnosis.Deployments);
        Assert.True(diagnosis.Deployments[0].Managed);
    }

    [Fact]
    public void Bad_node_health_prevents_routing_even_when_container_health_is_up()
    {
        var node = Node();
        var model = Model();
        var deployment = new ModelDeployment(node.Id, model.Id);
        for (var i = 0; i < 3; i++)
            node.RecordHealthFailure(DateTimeOffset.UtcNow, 1, "Health check timeout");
        Assert.Equal("node_unavailable",
            ModelRouteReadiness.Inspect(model.PublicName, model, [deployment], [node]).Code);
        node.RecordHealthSuccess(DateTimeOffset.UtcNow, 2, 1);
        Assert.Equal("candidate_configured",
            ModelRouteReadiness.Inspect(model.PublicName, model, [deployment], [node]).Code);
    }

    [Fact]
    public void Stopped_managed_runtime_without_endpoint_needs_start()
    {
        var node = Node();
        var model = Model();
        var deployment = new ModelDeployment(node.Id, model.Id);
        deployment.ConfigureRuntime(null, "qwen3-8b-airllm", "managed-airllm-1");
        Assert.Equal("runtime_address_missing",
            ModelRouteReadiness.Inspect(model.PublicName, model, [deployment], [node]).Code);
    }
}
