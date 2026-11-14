using System.Runtime.InteropServices;

namespace LlmProxy.NodeAgent;

/// <summary>
/// The serving engines on this server: which are installed, which version, what uses them, and how to install, update or
/// remove them. An engine is a Docker image; models keep their weights separately, so removing or updating an engine never
/// touches downloaded model files. A running model keeps the image it started from; a new image applies from its next start.
/// </summary>
public sealed class RuntimeEngineService(NodeAgentOptions options, ProcessRunner runner, ManagedModelRegistry registry)
{
    private static readonly HttpClient Registry = new() { Timeout = TimeSpan.FromSeconds(30) };

    public async Task<IReadOnlyList<RuntimeEngineState>> ListAsync(CancellationToken token)
    {
        var records = await registry.ReadAsync(token);
        var states = new List<RuntimeEngineState>();
        foreach (var engine in RuntimeEngineCatalog.All)
        {
            var image = RuntimeEngineCatalog.Image(engine, options);
            var inspected = await InspectAsync(image, token);
            var running = inspected is null ? 0 : await RunningContainersAsync(image, token);
            states.Add(new RuntimeEngineState(engine.Id, engine.Label, engine.Description, engine.Built ? "built" : "pulled", image,
                inspected is not null, inspected is null ? null : RuntimeEngineParsing.VersionOf(image, inspected),
                inspected is null ? null : RuntimeEngineParsing.RevisionOf(inspected), inspected?.Id, inspected?.CreatedAtUtc, inspected?.SizeBytes,
                running, records.Count(record => string.Equals(record.Runtime, engine.Id, StringComparison.Ordinal)), engine.Serves));
        }
        return states;
    }

    /// <summary>Asks each registry whether a newer image exists, without downloading it. Adapters the Agent builds have no registry.</summary>
    public async Task<IReadOnlyList<RuntimeEngineUpdateCheck>> CheckUpdatesAsync(CancellationToken token)
    {
        var checks = RuntimeEngineCatalog.All.Select(engine => CheckOneAsync(engine, token));
        return await Task.WhenAll(checks);
    }

    private async Task<RuntimeEngineUpdateCheck> CheckOneAsync(RuntimeEngineDefinition engine, CancellationToken token)
    {
        var image = RuntimeEngineCatalog.Image(engine, options);
        var inspected = await InspectAsync(image, token);
        if (inspected is null) return new(engine.Id, "not_installed", null);
        if (engine.Built)
            return new(engine.Id, "not_applicable", "Built by this Agent from its bundled adapter; Update rebuilds it with fresh base layers.");
        if (inspected.RepoDigests.Count == 0) return new(engine.Id, "unknown", "This image was not pulled from a registry, so it cannot be compared.");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            var remote = await RegistryDigestClient.GetDigestAsync(Registry, image, timeout.Token);
            if (remote is null) return new(engine.Id, "unknown", "The registry did not give a digest for this tag (private repository or no access).");
            return RuntimeEngineParsing.IsBehindRegistry(inspected.RepoDigests, remote)
                ? new(engine.Id, "update_available", "The registry has a newer image for this tag.")
                : new(engine.Id, "up_to_date", null);
        }
        catch (Exception error) when (!token.IsCancellationRequested && error is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            return new(engine.Id, "unknown", "The registry could not be reached from this server.");
        }
    }

    /// <summary>Pulls (or rebuilds) the engine image and reports whether it actually changed.</summary>
    public async Task<RuntimeEngineResult> InstallOrUpdateAsync(string engineId, string action, Action<string> progress, CancellationToken token)
    {
        var engine = RuntimeEngineCatalog.Find(engineId) ?? throw new KeyNotFoundException($"Unknown engine '{engineId}'.");
        var image = RuntimeEngineCatalog.Image(engine, options);
        var before = await InspectAsync(image, token);

        if (engine.Built)
        {
            var directory = Path.Combine(AppContext.BaseDirectory, engine.BuildFolder!);
            var dockerfile = Path.Combine(directory, RuntimeEngineCatalog.DockerfileName(engine,
                RuntimeInformation.OSArchitecture == Architecture.Arm64));
            if (!File.Exists(dockerfile))
                throw new InvalidOperationException($"The {engine.Label} adapter is not part of this Agent release ({engine.BuildFolder}).");
            progress($"Building {engine.Label} for this host");
            await RunStreamingAsync(["build", "--pull", "--file", dockerfile, "-t", image, directory], progress, token);
        }
        else
        {
            progress($"Downloading {image}");
            await RunStreamingAsync(["pull", image], progress, token);
        }

        var after = await InspectAsync(image, token) ?? throw new InvalidOperationException("The image is not present after the operation.");
        var changed = before is null || !string.Equals(before.Id, after.Id, StringComparison.Ordinal);
        var message = before is null ? $"{engine.Label} installed."
            : changed ? $"{engine.Label} updated; models use it the next time they start."
            : $"{engine.Label} is already up to date.";
        return new RuntimeEngineResult(changed, before?.Id, after.Id, message);
    }

    public async Task<string> RemoveAsync(string engineId, CancellationToken token)
    {
        var engine = RuntimeEngineCatalog.Find(engineId) ?? throw new KeyNotFoundException($"Unknown engine '{engineId}'.");
        var image = RuntimeEngineCatalog.Image(engine, options);
        if (await InspectAsync(image, token) is null) return $"{engine.Label} was not installed.";
        var running = await RunningContainersAsync(image, token);
        if (running > 0)
            throw new InvalidOperationException($"{running} running model{(running == 1 ? "" : "s")} use {engine.Label}. Stop {(running == 1 ? "it" : "them")} first.");
        var result = await runner.RunAsync(options.DockerExecutable, ["image", "rm", image], TimeSpan.FromMinutes(2), token);
        if (!result.Success) throw new InvalidOperationException("Docker could not remove the image: " + Trim(result.StandardError));
        return $"{engine.Label} removed. Downloaded model files were kept; models using it install the engine again when they start.";
    }

    private async Task<InspectedImage?> InspectAsync(string image, CancellationToken token)
    {
        var result = await runner.RunAsync(options.DockerExecutable, ["image", "inspect", "--format", "{{json .}}", image], TimeSpan.FromSeconds(15), token);
        return result.Success ? RuntimeEngineParsing.ParseInspect(result.StandardOutput) : null;
    }

    private async Task<int> RunningContainersAsync(string image, CancellationToken token)
    {
        var result = await runner.RunAsync(options.DockerExecutable, ["ps", "--filter", "ancestor=" + image, "--format", "{{.ID}}"], TimeSpan.FromSeconds(15), token);
        return result.Success ? result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length : 0;
    }

    private async Task RunStreamingAsync(IReadOnlyList<string> arguments, Action<string> progress, CancellationToken token)
    {
        var result = await runner.RunStreamingAsync(options.DockerExecutable, arguments, TimeSpan.FromMinutes(options.CommandTimeoutMinutes), token, line =>
        {
            var text = line.Trim();
            if (text.Length > 0) progress(text.Length > 200 ? text[..200] : text);
        });
        if (!result.Success) throw new InvalidOperationException($"Docker failed ({result.ExitCode}): {Trim(result.StandardError)}");
    }

    private static string Trim(string text) { var value = text.Trim(); return value.Length > 600 ? value[..600] : value; }
}
