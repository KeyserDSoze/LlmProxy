using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LlmProxy.NodeAgent;

/// <summary>Agent-initiated registration and heartbeat: no open inbound port is needed for visibility.</summary>
public sealed class OutboundNodeConnectionWorker(
    NodeAgentOptions options, HardwareInventoryReader inventory,
    IHttpClientFactory factory, ProcessRunner runner,
    ILogger<OutboundNodeConnectionWorker> logger) : BackgroundService
{
    private sealed record Connection(Guid NodeId, string AgentSecret);
    private readonly string _stateFile = Path.Combine(options.DataDirectory, "gateway-connection.json");
    private bool _recoveryCompleted;
    private string? _lastUpdateAttempt;
    private DateTimeOffset _lastUpdateAt;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(options.GatewayBaseAddress)) return;
        if (!Uri.TryCreate(options.GatewayBaseAddress, UriKind.Absolute, out var gateway) ||
            (gateway.Scheme != Uri.UriSchemeHttps &&
                !(gateway.Scheme == Uri.UriSchemeHttp && gateway.IsLoopback)))
            throw new InvalidOperationException("NodeAgent:GatewayBaseAddress must be HTTPS (HTTP only for local loopback).");
        if (options.ConnectionMode is not ("direct" or "outbound"))
            throw new InvalidOperationException("NodeAgent:ConnectionMode must be direct or outbound.");
        var client = factory.CreateClient("gateway");
        var address = options.GatewayBaseAddress.TrimEnd('/');
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var connection = await ReadStateAsync(stoppingToken);
                var currentInventory = await inventory.ReadAsync(stoppingToken);
                // An explicit recovery operation reuses the existing gateway node ID.
                // A restart without a recovery request always uses the durable state.
                if (!_recoveryCompleted && !string.IsNullOrWhiteSpace(options.RecoveryToken) &&
                    options.RecoveryNodeId.HasValue && (options.ForceRecovery || connection is null))
                {
                    using var response = await client.PostAsJsonAsync(
                        address + "/api/agent-connection/recover",
                        new
                        {
                            nodeId = options.RecoveryNodeId.Value, recoveryToken = options.RecoveryToken,
                            mode = options.ConnectionMode, inventory = currentInventory,
                            managementBaseAddress = options.ConnectionMode == "direct" ?
                                "http://" + options.AdvertiseHost + ":9900" : null,
                            agentBearer = options.ConnectionMode == "direct" ? options.BearerToken : null,
                            agentVersion = typeof(OutboundNodeConnectionWorker).Assembly.GetName().Version?.ToString(3)
                        }, stoppingToken);
                    if (!response.IsSuccessStatusCode)
                        throw new HttpRequestException($"Node recovery rejected: HTTP {(int)response.StatusCode}. Open Fleet > Recover agent and verify the per-node recovery code.");
                    connection = await response.Content.ReadFromJsonAsync<Connection>(stoppingToken)
                        ?? throw new InvalidOperationException("Node recovery returned no credential.");
                    if (connection.NodeId != options.RecoveryNodeId.Value ||
                        !connection.AgentSecret.StartsWith("lpa_", StringComparison.Ordinal))
                        throw new InvalidOperationException("Unexpected node identity in recovery response.");
                    await SaveStateAsync(connection, stoppingToken);
                    await ClearBootstrapTokensAsync(stoppingToken);
                    _recoveryCompleted = true;
                    logger.LogInformation("Restored registered node {NodeId} without creating a duplicate.", connection.NodeId);
                }
                if (connection is null)
                {
                    if (string.IsNullOrWhiteSpace(options.EnrollmentToken))
                    {
                        logger.LogWarning("Set NodeAgent__EnrollmentToken from the Admin invitation to pair this node.");
                    }
                    else
                    {
                        using var response = await client.PostAsJsonAsync(address + "/api/agent-connection/enroll",
                            new {
                                token = options.EnrollmentToken, hostname = currentInventory.Hostname,
                                mode = options.ConnectionMode, inventory = currentInventory,
                                managementBaseAddress = options.ConnectionMode == "direct" ?
                                    "http://" + options.AdvertiseHost + ":9900" : null,
                                agentBearer = options.ConnectionMode == "direct" ? options.BearerToken : null,
                                agentVersion = typeof(OutboundNodeConnectionWorker).Assembly.GetName().Version?.ToString(3)
                            }, stoppingToken);
                        if (!response.IsSuccessStatusCode)
                            throw new HttpRequestException($"Node enrollment rejected: HTTP {(int)response.StatusCode}");
                        connection = await response.Content.ReadFromJsonAsync<Connection>(stoppingToken)
                            ?? throw new InvalidOperationException("Node enrollment returned no credential.");
                        await SaveStateAsync(connection, stoppingToken);
                        await ClearBootstrapTokensAsync(stoppingToken);
                        logger.LogInformation("Paired node {NodeId} in {Mode} mode.", connection.NodeId, options.ConnectionMode);
                    }
                }
                if (connection is not null)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post,
                        address + "/api/agent-connection/" + connection.NodeId + "/heartbeat")
                    {
                        Content = JsonContent.Create(new
                        {
                            inventory = currentInventory,
                            agentVersion = typeof(OutboundNodeConnectionWorker).Assembly.GetName().Version?.ToString(3),
                            agentUpdateStatus = ReadUpdateStatus()
                        })
                    };
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.AgentSecret);
                    using var response = await client.SendAsync(request, stoppingToken);
                    if (!response.IsSuccessStatusCode)
                        logger.LogWarning("Node heartbeat returned HTTP {StatusCode}.", (int)response.StatusCode);
                    else
                    {
                        var instruction = await response.Content.ReadFromJsonAsync<HeartbeatInstruction>(stoppingToken);
                        if (instruction?.DesiredAgentVersion is string desired)
                            await TryStartUpdateAsync(desired, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogWarning("Agent outbound registration/heartbeat failed: {Error}", exception.Message);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private string? ReadUpdateStatus()
    {
        var path = Path.Combine(options.DataDirectory, "update-status.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var data = JsonDocument.Parse(File.ReadAllText(path));
            var status = data.RootElement.GetProperty("status").GetString();
            var version = data.RootElement.GetProperty("version").GetString();
            if (status is not ("running" or "failed" or "succeeded") ||
                version is null || !System.Text.RegularExpressions.Regex.IsMatch(version, @"^\d+\.\d+\.\d+$"))
                return null;
            // Optional phase/percentage is informational; never pretend progress when
            // a remote registry omits Content-Length.
            var stage = data.RootElement.TryGetProperty("stage", out var stageJson) ? stageJson.GetString() : null;
            var percent = data.RootElement.TryGetProperty("percent", out var pctJson) &&
                pctJson.ValueKind == JsonValueKind.Number && pctJson.TryGetInt32(out var p) && p is >= 0 and <= 100
                ? p.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
            if (stage is null || !System.Text.RegularExpressions.Regex.IsMatch(stage, @"^[a-z]{3,24}$"))
                return status + ":" + version;
            return status + ":" + version + ":" + stage + (percent is null ? "" : ":" + percent);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException)
        { return null; }
    }

    private sealed record HeartbeatInstruction(string? DesiredAgentVersion);

    private async Task TryStartUpdateAsync(string version, CancellationToken cancellationToken)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(version, @"^\d+\.\d+\.\d+$") ||
            version == typeof(OutboundNodeConnectionWorker).Assembly.GetName().Version?.ToString(3))
            return;
        if (_lastUpdateAttempt == version && DateTimeOffset.UtcNow - _lastUpdateAt < TimeSpan.FromMinutes(10))
            return;
        _lastUpdateAttempt = version;
        _lastUpdateAt = DateTimeOffset.UtcNow;
        const string updater = "/opt/llmproxy-node-agent/update-node-agent.sh";
        if (!File.Exists(updater))
        {
            logger.LogWarning("Node Agent updater not installed; latest Node Agent archive must be installed once.");
            return;
        }
        var task = await runner.RunAsync("systemd-run",
            ["--unit=llmproxy-node-agent-update", "--collect", "--no-block", "/bin/bash", updater, version],
            TimeSpan.FromSeconds(15), cancellationToken);
        if (!task.Success) logger.LogWarning("Agent update scheduling failed: {Reason}", task.StandardError);
        else logger.LogInformation("Scheduled verified Node Agent update to {Version}.", version);
    }

    private static async Task ClearBootstrapTokensAsync(CancellationToken token)
    {
        const string envFile = "/etc/llmproxy/node-agent.env";
        if (!File.Exists(envFile)) return;
        var lines = await File.ReadAllLinesAsync(envFile, token);
        for (var index = 0; index < lines.Length; index++)
        {
            if (lines[index].StartsWith("NodeAgent__EnrollmentToken=", StringComparison.Ordinal) ||
                lines[index].StartsWith("NodeAgent__RecoveryToken=", StringComparison.Ordinal) ||
                lines[index].StartsWith("NodeAgent__RecoveryNodeId=", StringComparison.Ordinal))
                lines[index] = lines[index][..lines[index].IndexOf('=') + 1];
            if (lines[index].StartsWith("NodeAgent__ForceRecovery=", StringComparison.Ordinal))
                lines[index] = "NodeAgent__ForceRecovery=false";
        }
        await File.WriteAllLinesAsync(envFile, lines, token);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(envFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private async Task<Connection?> ReadStateAsync(CancellationToken token) =>
        File.Exists(_stateFile)
            ? JsonSerializer.Deserialize<Connection>(await File.ReadAllTextAsync(_stateFile, token),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)) : null;

    private async Task SaveStateAsync(Connection state, CancellationToken token)
    {
        Directory.CreateDirectory(options.DataDirectory);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(options.DataDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temp = _stateFile + ".tmp";
        var optionsForFile = new FileStreamOptions
        {
            Mode = FileMode.Create, Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        };
        await using (var stream = new FileStream(temp, optionsForFile))
            await JsonSerializer.SerializeAsync(stream, state, cancellationToken: token);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, _stateFile, overwrite: true);
    }
}
