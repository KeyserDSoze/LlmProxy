using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LlmProxy.NodeAgent;

public sealed record GgufPreparation(string Repository, string Revision, string ModelFile, string? MmprojFile,
    long TotalBytes);

/// <summary>
/// Resolves a llama.cpp repo[:quant] to exact GGUF files at a pinned Hub commit and downloads them
/// into the shared Hugging Face cache layout (blobs + snapshot links), resumable and checksum-verified.
/// llama-server then starts from a local --model path: no Hub access or lazy download at start time.
/// </summary>
public sealed class GgufModelStore(NodeAgentOptions options, IHttpClientFactory clients, ILogger<GgufModelStore> logger)
{
    public const string ContainerCacheRoot = "/root/.cache/huggingface";
    private const long DiskReserveBytes = 2L * 1024 * 1024 * 1024;
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(120);
    private static readonly Regex Commit = new("^[0-9a-f]{40}$", RegexOptions.CultureInvariant);
    private static readonly Regex Hex = new("^[0-9a-f]{40}([0-9a-f]{24})?$", RegexOptions.CultureInvariant);
    private readonly SemaphoreSlim _downloads = new(1, 1);

    private string HubDirectory => Path.Combine(Path.GetFullPath(options.ModelCacheDirectory), "hub");

    public static string RepositoryFolder(string repository) => "models--" + repository.Replace("/", "--");

    public static string ContainerPath(string repository, string revision, string file) =>
        $"{ContainerCacheRoot}/hub/{RepositoryFolder(repository)}/snapshots/{revision}/{file}";

    private string SnapshotPath(string repository, string revision, string file) =>
        Path.Combine(HubDirectory, RepositoryFolder(repository), "snapshots", revision,
            file.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>True when every file the pinned llama.cpp launch needs is present in the host cache.</summary>
    public bool IsPrepared(ManagedModelRecord record)
    {
        if (record.ModelFile is not { Length: > 0 } file || record.SourceRevision is not { } revision ||
            !Commit.IsMatch(revision) || !GgufFileSelector.IsSafeRelativePath(file)) return false;
        try
        {
            var (repository, _) = GgufFileSelector.SplitRepositoryTag(record.ProviderModelName);
            var required = GgufFileSelector.ExpectedSplitPaths(file).ToList();
            if (record.MmprojFile is { Length: > 0 } mmproj)
            {
                if (!GgufFileSelector.IsSafeRelativePath(mmproj)) return false;
                required.Add(mmproj);
            }
            return required.All(path => File.Exists(SnapshotPath(repository, revision, path)));
        }
        catch (ArgumentException) { return false; }
    }

    public async Task<GgufPreparation> PrepareAsync(string providerModelName, string? pinnedRevision,
        Action<TransferProgress>? report, CancellationToken token)
    {
        var (repository, tag) = GgufFileSelector.SplitRepositoryTag(providerModelName);
        if (pinnedRevision is not null && !Commit.IsMatch(pinnedRevision))
            throw new ArgumentException("Pinned GGUF revision must be a 40-character commit SHA.");
        report?.Invoke(new TransferProgress("model-weights", null,
            "Resolving GGUF files on Hugging Face", null, null));
        var client = clients.CreateClient("huggingface");
        var (revision, files, gated) = await ReadRepositoryAsync(client, repository, pinnedRevision ?? "main", token);
        if (gated && options.HuggingFaceToken is null)
            throw new InvalidOperationException(
                $"'{repository}' is a gated Hugging Face repository. Accept its license on huggingface.co, " +
                "set HF_TOKEN (or NodeAgent__HuggingFaceToken) in the Node Agent environment and retry.");

        var primary = GgufFileSelector.FindBestModel(files, tag) ?? throw new InvalidOperationException(
            NoMatchExplanation(repository, tag, files));
        var parts = GgufFileSelector.SplitFiles(files, primary);
        var expected = GgufFileSelector.GetSplitInfo(primary.Path)?.Count ?? 1;
        if (parts.Count != expected)
            throw new InvalidOperationException(
                $"Split GGUF '{primary.Path}' in '{repository}' is incomplete: {parts.Count} of {expected} parts are published.");
        var mmproj = GgufFileSelector.FindBestMmproj(files, primary.Path);
        if (mmproj is not null && (!GgufFileSelector.IsSafeRelativePath(mmproj.Path) || BlobName(mmproj) is null))
        {
            logger.LogWarning("Skipping unverifiable multimodal projector {File} in {Repository}.", mmproj.Path, repository);
            mmproj = null;
        }
        var plan = parts.Concat(mmproj is null ? [] : [mmproj]).ToArray();
        foreach (var file in parts)
            if (!GgufFileSelector.IsSafeRelativePath(file.Path) || BlobName(file) is null)
                throw new InvalidOperationException(
                    $"Hugging Face returned unsafe or unverifiable metadata for '{file.Path}'; download refused.");

        await _downloads.WaitAsync(token);
        try
        {
            var repoDirectory = Path.Combine(HubDirectory, RepositoryFolder(repository));
            var blobs = Path.Combine(repoDirectory, "blobs");
            Directory.CreateDirectory(blobs);
            EnsureDiskSpace(plan, blobs);

            var total = plan.All(file => file.Size is not null) ? plan.Sum(file => file.Size!.Value) : (long?)null;
            long finished = 0;
            for (var i = 0; i < plan.Length; i++)
            {
                var file = plan[i];
                var blob = Path.Combine(blobs, BlobName(file)!);
                var linked = SnapshotPath(repository, revision, file.Path);
                if (!File.Exists(linked) && !File.Exists(blob) &&
                    !await TryAdoptLegacyDownloadAsync(repository, file, blob, report, token))
                {
                    var label = $"Downloading {file.Path} ({i + 1}/{plan.Length})";
                    await DownloadAsync(client, repository, revision, file, blob, label, finished, total, report, token);
                }
                finished += file.Size ?? new FileInfo(File.Exists(linked) ? linked : blob).Length;
                report?.Invoke(new TransferProgress("model-weights",
                    total is > 0 ? Math.Round(100d * finished / total.Value, 1) : null,
                    $"Verified {file.Path}", finished, total));
                LinkSnapshot(repoDirectory, revision, file, blob);
            }
            if (pinnedRevision is null)
            {
                var refs = Path.Combine(repoDirectory, "refs");
                Directory.CreateDirectory(refs);
                await File.WriteAllTextAsync(Path.Combine(refs, "main"), revision, CancellationToken.None);
            }
            logger.LogInformation("Prepared GGUF {Repository}@{Revision}: {File} ({Count} files).",
                repository, revision, primary.Path, plan.Length);
            return new GgufPreparation(repository, revision, primary.Path, mmproj?.Path, total ?? finished);
        }
        finally { _downloads.Release(); }
    }

    private static string NoMatchExplanation(string repository, string? tag, IReadOnlyList<HubFile> files)
    {
        var quantizations = GgufFileSelector.AvailableQuantizations(files);
        if (!files.Any(file => GgufFileSelector.IsModelFile(file.Path)))
            return $"'{repository}' contains no GGUF model files, so llama.cpp cannot load it. " +
                   "Choose a GGUF repository (for example one ending in -GGUF) or install this checkpoint with vLLM/SGLang.";
        return $"No GGUF file in '{repository}' matches quantization '{tag}'. " +
               (quantizations.Count > 0
                   ? "Available quantizations: " + string.Join(", ", quantizations) + ". "
                   : "") +
               "Choose one of them (repository:QUANT) and retry.";
    }

    private async Task<(string Revision, IReadOnlyList<HubFile> Files, bool Gated)> ReadRepositoryAsync(
        HttpClient client, string repository, string revision, CancellationToken token)
    {
        var url = $"{options.HuggingFaceEndpoint}/api/models/{repository}/revision/{Uri.EscapeDataString(revision)}?blobs=true";
        for (var attempt = 1; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                Authorize(request);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    var failure = HubFailure(response.StatusCode, repository, null);
                    if (IsTransientStatus(response.StatusCode) && attempt < 4)
                    {
                        await Task.Delay(Backoff(attempt, response), token);
                        continue;
                    }
                    throw failure;
                }
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
                return ParseRepository(document.RootElement, repository);
            }
            catch (JsonException)
            {
                throw new InvalidOperationException(
                    $"Hugging Face returned an unexpected (non-JSON) answer for '{repository}'. Check proxy settings and HF_ENDPOINT.");
            }
            catch (Exception error) when (attempt < 4 && !token.IsCancellationRequested &&
                error is HttpRequestException or IOException or OperationCanceledException)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), token);
            }
            catch (Exception error) when (!token.IsCancellationRequested &&
                error is HttpRequestException or IOException or OperationCanceledException)
            {
                var reason = error is OperationCanceledException ? "the request timed out." : error.Message;
                throw new InvalidOperationException(
                    $"Cannot reach Hugging Face ({options.HuggingFaceEndpoint}) from this host: {reason} " +
                    "Check DNS, proxy and firewall rules; the Node Agent downloads GGUF weights itself.", error);
            }
        }
    }

    public static (string Revision, IReadOnlyList<HubFile> Files, bool Gated) ParseRepository(
        JsonElement root, string repository)
    {
        if (root.TryGetProperty("disabled", out var disabled) && disabled.ValueKind == JsonValueKind.True)
            throw new InvalidOperationException($"'{repository}' has been disabled on Hugging Face.");
        var revision = root.TryGetProperty("sha", out var sha) && sha.ValueKind == JsonValueKind.String
            ? sha.GetString() ?? "" : "";
        if (!Commit.IsMatch(revision))
            throw new InvalidOperationException($"Hugging Face did not return a commit SHA for '{repository}'.");
        var gated = root.TryGetProperty("gated", out var gate) &&
            (gate.ValueKind == JsonValueKind.True ||
             gate.ValueKind == JsonValueKind.String && gate.GetString() is { Length: > 0 } value && value != "false");
        var files = new List<HubFile>();
        if (root.TryGetProperty("siblings", out var siblings) && siblings.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in siblings.EnumerateArray().Take(5000))
            {
                if (!item.TryGetProperty("rfilename", out var name) || name.ValueKind != JsonValueKind.String) continue;
                long? size = item.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : null;
                string? sha256 = null;
                if (item.TryGetProperty("lfs", out var lfs) && lfs.ValueKind == JsonValueKind.Object)
                {
                    if (lfs.TryGetProperty("sha256", out var digest) && digest.ValueKind == JsonValueKind.String)
                        sha256 = digest.GetString()?.ToLowerInvariant();
                    if (lfs.TryGetProperty("size", out var lfsSize) && lfsSize.TryGetInt64(out var lfsBytes))
                        size = lfsBytes;
                }
                var blobId = item.TryGetProperty("blobId", out var blob) && blob.ValueKind == JsonValueKind.String
                    ? blob.GetString()?.ToLowerInvariant() : null;
                files.Add(new HubFile(name.GetString()!, size, sha256, blobId));
            }
        }
        files.Sort((left, right) => string.CompareOrdinal(left.Path, right.Path));
        return (revision, files, gated);
    }

    private static string? BlobName(HubFile file)
    {
        var name = file.Sha256 ?? file.BlobId;
        return name is not null && Hex.IsMatch(name) ? name : null;
    }

    private void EnsureDiskSpace(IReadOnlyList<HubFile> plan, string blobs)
    {
        long missing = 0;
        foreach (var file in plan)
        {
            var blob = Path.Combine(blobs, BlobName(file)!);
            if (File.Exists(blob)) continue;
            var partial = blob + ".incomplete";
            missing += Math.Max(0, (file.Size ?? 0) - (File.Exists(partial) ? new FileInfo(partial).Length : 0));
        }
        long available;
        try { available = new DriveInfo(blobs).AvailableFreeSpace; }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException) { return; }
        if (missing + DiskReserveBytes > available)
            throw new InvalidOperationException(
                $"Not enough disk space for the GGUF download: {Gib(missing)} GiB needed plus {Gib(DiskReserveBytes)} GiB reserve, " +
                $"{Gib(available)} GiB available in {options.ModelCacheDirectory}.");
    }

    private static string Gib(long bytes) => (bytes / 1024d / 1024 / 1024).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    // Earlier llama.cpp images cached -hf downloads flat in the mounted directory as
    // "<org>_<repo>_<file>"; reuse a verified copy instead of downloading it again.
    private async Task<bool> TryAdoptLegacyDownloadAsync(string repository, HubFile file, string blob,
        Action<TransferProgress>? report, CancellationToken token)
    {
        try
        {
            var legacy = Path.Combine(Path.GetFullPath(options.ModelCacheDirectory),
                (repository + "_" + file.Path).Replace('/', '_'));
            if (!File.Exists(legacy) || file.Size is not long size || new FileInfo(legacy).Length != size ||
                file.Sha256 is null) return false;
            report?.Invoke(new TransferProgress("model-weights", null,
                $"Verifying previously downloaded {file.Path}", null, null));
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await HashFileAsync(legacy, hash, token);
            if (!string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), file.Sha256, StringComparison.Ordinal))
                return false;
            File.Move(legacy, blob);
            logger.LogInformation("Adopted legacy llama.cpp cache file {File} into the Hugging Face cache.", legacy);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(error, "Could not reuse legacy llama.cpp cache file for {File}.", file.Path);
            return false;
        }
    }

    private async Task DownloadAsync(HttpClient client, string repository, string revision, HubFile file,
        string blob, string label, long finishedBefore, long? total, Action<TransferProgress>? report,
        CancellationToken token)
    {
        var partial = blob + ".incomplete";
        var url = $"{options.HuggingFaceEndpoint}/{repository}/resolve/{revision}/" +
                  string.Join('/', file.Path.Split('/').Select(Uri.EscapeDataString));
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await DownloadAttemptAsync(client, url, repository, file, partial, label, finishedBefore, total, report, token);
                File.Move(partial, blob, overwrite: true);
                return;
            }
            catch (Exception error) when (attempt < 6 && !token.IsCancellationRequested && IsTransient(error))
            {
                logger.LogWarning("GGUF download of {File} attempt {Attempt} failed: {Error}", file.Path, attempt, error.Message);
                report?.Invoke(new TransferProgress("model-weights", null,
                    $"Retrying {file.Path} after: {error.Message}", null, total));
                await Task.Delay(error is TransientHubException { RetryAfter: { } retry } ? retry : TimeSpan.FromSeconds(5 * attempt), token);
            }
        }
    }

    private async Task DownloadAttemptAsync(HttpClient client, string url, string repository, HubFile file,
        string partial, string label, long finishedBefore, long? total, Action<TransferProgress>? report,
        CancellationToken token)
    {
        var existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (file.Size is long known && existing > known)
        {
            File.Delete(partial);
            existing = 0;
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        Authorize(request);
        if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(token);
        stall.CancelAfter(StallTimeout);
        HttpResponseMessage response;
        try { response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new IOException("Hugging Face did not answer within " + StallTimeout.TotalSeconds + " seconds."); }
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                File.Delete(partial);
                throw new IOException("Stale partial download discarded; restarting.");
            }
            if (!response.IsSuccessStatusCode) throw HubFailure(response.StatusCode, repository, file.Path, response);
            var append = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            if (!append) existing = 0;

            using var hash = file.Sha256 is not null
                ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
                : IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            if (file.Sha256 is null)
            {
                // Non-LFS files are verified as git blobs: sha1("blob <size>\0" + content).
                if (file.Size is not long gitSize)
                    throw new InvalidOperationException($"Cannot verify '{file.Path}': Hub did not report its size.");
                hash.AppendData(Encoding.ASCII.GetBytes($"blob {gitSize}\0"));
            }
            if (append) await HashFileAsync(partial, hash, token);

            var buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
            var lastReport = DateTimeOffset.MinValue;
            try
            {
                await using var output = new FileStream(partial, append ? FileMode.Append : FileMode.Create,
                    FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                await using var input = await response.Content.ReadAsStreamAsync(stall.Token);
                while (true)
                {
                    stall.CancelAfter(StallTimeout);
                    int read;
                    try { read = await input.ReadAsync(buffer.AsMemory(), stall.Token); }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    { throw new IOException($"Download stalled for {StallTimeout.TotalSeconds:0} seconds."); }
                    if (read == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    hash.AppendData(buffer, 0, read);
                    existing += read;
                    if (DateTimeOffset.UtcNow - lastReport >= TimeSpan.FromMilliseconds(750))
                    {
                        lastReport = DateTimeOffset.UtcNow;
                        var done = finishedBefore + existing;
                        report?.Invoke(new TransferProgress("model-weights",
                            total is > 0 ? Math.Round(Math.Min(100, 100d * done / total.Value), 1) : null,
                            label, done, total));
                    }
                }
                await output.FlushAsync(token);
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }

            if (file.Size is long expected && existing != expected)
                throw new IOException($"Connection closed after {existing} of {expected} bytes.");
            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!string.Equals(actual, file.Sha256 ?? file.BlobId, StringComparison.Ordinal))
            {
                File.Delete(partial);
                throw new InvalidDataException(
                    $"Checksum mismatch for '{file.Path}': the corrupted download was discarded. Retry the installation.");
            }
        }
    }

    private static async Task HashFileAsync(string path, IncrementalHash hash, CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(), token)) > 0) hash.AppendData(buffer, 0, read);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static void LinkSnapshot(string repoDirectory, string revision, HubFile file, string blob)
    {
        var snapshot = Path.Combine(repoDirectory, "snapshots", revision);
        var pointer = Path.GetFullPath(Path.Combine(snapshot, file.Path.Replace('/', Path.DirectorySeparatorChar)));
        if (!pointer.StartsWith(Path.GetFullPath(snapshot) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException($"Refusing GGUF path outside the snapshot directory: {file.Path}");
        if (File.Exists(pointer)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(pointer)!);
        if (new FileInfo(pointer).LinkTarget is not null) File.Delete(pointer);
        try
        {
            // Relative links keep the cache valid when mounted at another path inside containers.
            File.CreateSymbolicLink(pointer, Path.GetRelativePath(Path.GetDirectoryName(pointer)!, blob));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            File.Move(blob, pointer);
        }
    }

    private void Authorize(HttpRequestMessage request)
    {
        if (options.HuggingFaceToken is { } secret &&
            Uri.TryCreate(options.HuggingFaceEndpoint, UriKind.Absolute, out var endpoint) &&
            request.RequestUri is { } target && string.Equals(target.Host, endpoint.Host, StringComparison.OrdinalIgnoreCase))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
    }

    private Exception HubFailure(HttpStatusCode status, string repository, string? file, HttpResponseMessage? response = null)
    {
        var target = file is null ? $"'{repository}'" : $"'{file}' in '{repository}'";
        var code = (int)status;
        return status switch
        {
            HttpStatusCode.Unauthorized when options.HuggingFaceToken is null => new InvalidOperationException(
                $"Hugging Face requires authentication for {target} (HTTP 401): the repository is private or gated. " +
                "Accept its license on huggingface.co, set HF_TOKEN for the Node Agent and retry."),
            HttpStatusCode.Unauthorized => new InvalidOperationException(
                $"Hugging Face rejected the configured HF_TOKEN for {target} (HTTP 401)."),
            HttpStatusCode.Forbidden => new InvalidOperationException(
                $"Access to {target} is restricted (HTTP 403). Request access on huggingface.co with the account that owns HF_TOKEN."),
            HttpStatusCode.NotFound => new InvalidOperationException(file is null
                ? $"Hugging Face repository '{repository}' was not found (HTTP 404). Check the org/model ID; llama.cpp needs a GGUF repository."
                : $"{target} was not found (HTTP 404); the repository changed after it was resolved. Retry the installation."),
            HttpStatusCode.TooManyRequests => new TransientHubException(
                $"Hugging Face rate limit for {target} (HTTP 429). Retry later or configure HF_TOKEN.",
                response?.Headers.RetryAfter?.Delta),
            _ when code >= 500 => new TransientHubException(
                $"Hugging Face is temporarily unavailable for {target} (HTTP {code}).", null),
            _ => new InvalidOperationException($"Hugging Face returned HTTP {code} for {target}.")
        };
    }

    private static bool IsTransientStatus(HttpStatusCode status) =>
        status == HttpStatusCode.TooManyRequests || (int)status >= 500;

    private static TimeSpan Backoff(int attempt, HttpResponseMessage response) =>
        response.Headers.RetryAfter?.Delta is { } delta && delta < TimeSpan.FromMinutes(2)
            ? delta : TimeSpan.FromSeconds(3 * attempt);

    private static bool IsTransient(Exception error) =>
        error is IOException or HttpRequestException or TransientHubException;

    private sealed class TransientHubException(string message, TimeSpan? retryAfter) : Exception(message)
    {
        public TimeSpan? RetryAfter { get; } = retryAfter is { } delay && delay < TimeSpan.FromMinutes(2) ? delay : null;
    }
}
