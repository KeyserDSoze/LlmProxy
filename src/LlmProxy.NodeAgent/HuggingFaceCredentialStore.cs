using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LlmProxy.NodeAgent;

public sealed record HuggingFaceTokenRequest(string? Token);

/// <summary>Never contains the token: only where it comes from and whom it was verified for.</summary>
public sealed record HuggingFaceAccessStatus(
    bool Configured, string Source, string? Account, bool Verified, DateTimeOffset? UpdatedAtUtc);

/// <summary>
/// Hugging Face read token used for gated/private repositories. An Admin-supplied token is stored
/// owner-only in the Agent data directory and overrides the HF_TOKEN environment fallback. It is
/// write-only through the management API and is sent only to the configured Hub host.
/// </summary>
public sealed partial class HuggingFaceCredentialStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly NodeAgentOptions _options;
    private readonly IHttpClientFactory _clients;
    private readonly ILogger<HuggingFaceCredentialStore> _logger;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile StoredCredential? _stored;

    private sealed record StoredCredential(string Token, string? Account, bool Verified, DateTimeOffset UpdatedAtUtc);

    public HuggingFaceCredentialStore(NodeAgentOptions options, IHttpClientFactory clients,
        ILogger<HuggingFaceCredentialStore> logger)
    {
        _options = options;
        _clients = clients;
        _logger = logger;
        Directory.CreateDirectory(options.DataDirectory);
        _path = Path.Combine(options.DataDirectory, "huggingface-credential.json");
        _stored = Load();
    }

    public string? Token => _stored?.Token ?? _options.HuggingFaceToken;

    public HuggingFaceAccessStatus Status()
    {
        if (_stored is { } stored)
            return new(true, "admin", stored.Account, stored.Verified, stored.UpdatedAtUtc);
        return _options.HuggingFaceToken is null
            ? new(false, "none", null, false, null)
            : new(true, "environment", null, false, null);
    }

    /// <summary>Environment for child processes, so the secret never appears in a command line.</summary>
    public IReadOnlyDictionary<string, string>? ProcessEnvironment() =>
        Token is { } token ? new Dictionary<string, string> { ["HF_TOKEN"] = token } : null;

    public async Task<HuggingFaceAccessStatus> SetAsync(string? raw, CancellationToken cancellationToken)
    {
        var token = raw?.Trim();
        if (string.IsNullOrEmpty(token) || !TokenShape().IsMatch(token))
            throw new ArgumentException("The value is not a Hugging Face access token (it normally starts with 'hf_').");

        var (outcome, account) = await VerifyAsync(token, cancellationToken);
        if (outcome == Verification.Rejected)
            throw new ArgumentException("Hugging Face rejected this token (HTTP 401). Create a new read token and try again.");

        var credential = new StoredCredential(token, account, outcome == Verification.Verified, DateTimeOffset.UtcNow);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await WriteOwnerOnlyAsync(JsonSerializer.Serialize(credential, JsonOptions), cancellationToken);
            _stored = credential;
        }
        finally { _gate.Release(); }
        _logger.LogInformation("Hugging Face access token stored for account {Account} (verified: {Verified}).",
            account ?? "unknown", credential.Verified);
        return Status();
    }

    public async Task<HuggingFaceAccessStatus> ClearAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
            _stored = null;
        }
        finally { _gate.Release(); }
        _logger.LogInformation("Admin-supplied Hugging Face access token removed.");
        return Status();
    }

    private enum Verification { Verified, Rejected, Unverifiable }

    // whoami-v2 proves the token is live and names the account the Admin should request access with.
    private async Task<(Verification, string?)> VerifyAsync(string token, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _options.HuggingFaceEndpoint + "/api/whoami-v2");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await _clients.CreateClient("huggingface").SendAsync(request, timeout.Token);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return (Verification.Rejected, null);
            if (!response.IsSuccessStatusCode) return (Verification.Unverifiable, null);
            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            var name = document.RootElement.TryGetProperty("name", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
            return (Verification.Verified, name is { Length: <= 100 } ? name : null);
        }
        catch (Exception error) when ((error is HttpRequestException or TaskCanceledException or JsonException)
            && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Hugging Face token could not be verified online: {Error}", error.GetType().Name);
            return (Verification.Unverifiable, null);
        }
    }

    private StoredCredential? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var credential = JsonSerializer.Deserialize<StoredCredential>(File.ReadAllText(_path), JsonOptions);
            return credential is { Token.Length: > 0 } ? credential : null;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Stored Hugging Face credential is unreadable and was ignored: {Error}", error.GetType().Name);
            return null;
        }
    }

    private async Task WriteOwnerOnlyAsync(string content, CancellationToken cancellationToken)
    {
        var temp = _path + ".tmp";
        var create = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
            create.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using (var stream = new FileStream(temp, create))
        await using (var writer = new StreamWriter(stream))
            await writer.WriteAsync(content.AsMemory(), cancellationToken);
        File.Move(temp, _path, overwrite: true);
    }

    [GeneratedRegex("^[A-Za-z0-9_\\-]{8,256}$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenShape();

    /// <summary>
    /// Maps huggingface_hub failures from the prefetch container to the same guided wording the GGUF
    /// downloader uses, or null when the output is not an access problem.
    /// </summary>
    public static string? ExplainAccessFailure(string repository, string output, bool hasToken)
    {
        if (GatedFailure().IsMatch(output))
            return hasToken
                ? $"Access to '{repository}' is restricted (HTTP 403). Request access on huggingface.co with the account that owns the access token."
                : $"Hugging Face requires authentication for '{repository}' (HTTP 401): the repository is private or gated. " +
                  "Accept its license on huggingface.co, add a Hugging Face access token for this server in LlmProxy Admin and retry.";
        if (UnauthorizedFailure().IsMatch(output))
            return hasToken
                ? $"Hugging Face rejected the configured access token for '{repository}' (HTTP 401), or the repository does not exist. Check the ID or replace the token in LlmProxy Admin."
                : $"Hugging Face requires authentication for '{repository}' (HTTP 401): the repository is private or gated. " +
                  "Accept its license on huggingface.co, add a Hugging Face access token for this server in LlmProxy Admin and retry.";
        return null;
    }

    [GeneratedRegex("GatedRepoError|Cannot access gated repo|(?:403|Forbidden) Client Error", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GatedFailure();

    [GeneratedRegex("401 Client Error|RepositoryNotFoundError|Invalid (?:credentials|user token)|Repository Not Found for url", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnauthorizedFailure();
}
