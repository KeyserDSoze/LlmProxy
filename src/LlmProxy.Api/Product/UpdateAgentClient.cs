using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace LlmProxy.Api.Product;

public sealed class UpdateAgentClient(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration)
{
    private readonly string _baseAddress = configuration["Updates:AgentBaseAddress"] ?? "http://host.docker.internal:9910";
    private readonly string? _bearerToken = configuration["Updates:AgentBearerToken"];

    public async Task<UpdateAgentStatus?> TryGetStatusAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_bearerToken))
        {
            return null;
        }

        try
        {
            using var request = CreateRequest(HttpMethod.Get, "/v1/status");
            var client = httpClientFactory.CreateClient("update-agent");
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            return await response.Content.ReadFromJsonAsync<UpdateAgentStatus>(cancellationToken);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    public async Task<UpdateJobStatus> ScheduleAsync(
        ScheduleProductUpdateRequest requestBody,
        IReadOnlyList<string> upgradePath,
        CancellationToken cancellationToken)
    {
        EnsureConfigured();
        using var request = CreateRequest(HttpMethod.Post, "/v1/updates");
        request.Content = JsonContent.Create(new
        {
            version = requestBody.Version,
            scheduledForUtc = requestBody.ScheduledForUtc,
            force = requestBody.Force,
            versions = upgradePath
        });
        var client = httpClientFactory.CreateClient("update-agent");
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"Update agent rejected the request with HTTP {(int)response.StatusCode}" +
                (string.IsNullOrWhiteSpace(detail) ? "." : $": {detail}"));
        }

        return await response.Content.ReadFromJsonAsync<UpdateJobStatus>(cancellationToken)
            ?? throw new InvalidOperationException("Update agent returned an empty response.");
    }

    public async Task<bool> CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        using var request = CreateRequest(HttpMethod.Delete, $"/v1/updates/{id}");
        var client = httpClientFactory.CreateClient("update-agent");
        using var response = await client.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(new Uri(_baseAddress.TrimEnd('/') + "/"), path.TrimStart('/')));
        if (!string.IsNullOrWhiteSpace(_bearerToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearerToken);
        }
        return request;
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(_bearerToken))
        {
            throw new InvalidOperationException("The host update agent is not configured for this LlmProxy installation.");
        }
    }
}

public sealed record UpdateAgentStatus(
    string? InstalledVersion,
    UpdateJobStatus? ActiveJob,
    IReadOnlyList<UpdateJobStatus> RecentJobs);

public sealed record UpdateJobStatus(
    Guid Id,
    string Version,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset ScheduledForUtc,
    string Status,
    DateTimeOffset? StartedAtUtc = null,
    DateTimeOffset? CompletedAtUtc = null,
    string? Error = null,
    int? ExitCode = null,
    IReadOnlyList<string>? UpgradePath = null,
    string? CurrentStep = null);

public sealed record ScheduleProductUpdateRequest(
    string Version,
    DateTimeOffset? ScheduledForUtc = null,
    bool Force = false);
