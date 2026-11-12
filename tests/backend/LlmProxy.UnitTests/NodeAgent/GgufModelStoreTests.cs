using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LlmProxy.NodeAgent;
using Microsoft.Extensions.Logging.Abstractions;

namespace LlmProxy.UnitTests.NodeAgent;

public sealed class GgufModelStoreTests : IDisposable
{
    private const string Revision = "0123456789abcdef0123456789abcdef01234567";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "llmproxy-gguf-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static HubFile Entry(string path, long? size = 10) => new(path, size, null, null);

    [Theory]
    [InlineData("ggml-org/Qwen3-4B-GGUF:Q4_K_M", "ggml-org/Qwen3-4B-GGUF", "Q4_K_M")]
    [InlineData("unsloth/gemma-3-4b-it-GGUF", "unsloth/gemma-3-4b-it-GGUF", null)]
    public void Repository_and_quant_tag_are_split_like_llama_cpp(string input, string repository, string? tag)
    {
        var parsed = GgufFileSelector.SplitRepositoryTag(input);
        Assert.Equal(repository, parsed.Repository);
        Assert.Equal(tag, parsed.Tag);
    }

    [Theory]
    [InlineData("https://huggingface.co/org/model")]
    [InlineData("org/../model")]
    [InlineData("org--x/model")]
    [InlineData("org/model:Q4 K")]
    [InlineData("only-name")]
    public void Unsafe_repository_names_are_rejected_before_network_access(string input)
    {
        Assert.Throws<ArgumentException>(() => GgufFileSelector.SplitRepositoryTag(input));
    }

    [Fact]
    public void Default_selection_prefers_Q4_K_M_then_Q8_0_and_skips_sidecars()
    {
        HubFile[] files =
        [
            Entry("mmproj-model-Q4_K_M.gguf"), Entry("model-Q8_0.gguf"),
            Entry("model-imatrix-Q4_K_M.gguf"), Entry("model-Q4_K_M.gguf"), Entry("model-F16.gguf")
        ];
        Assert.Equal("model-Q4_K_M.gguf", GgufFileSelector.FindBestModel(files, null)?.Path);
        Assert.Equal("model-Q8_0.gguf", GgufFileSelector.FindBestModel(files[..2], null)?.Path);
        Assert.Equal("model-F16.gguf", GgufFileSelector.FindBestModel([Entry("model-F16.gguf")], null)?.Path);
    }

    [Fact]
    public void Explicit_tag_matches_full_token_case_insensitively_and_never_falls_back()
    {
        HubFile[] files = [Entry("model-BF16.gguf"), Entry("model-q5_k_m.gguf")];
        Assert.Equal("model-q5_k_m.gguf", GgufFileSelector.FindBestModel(files, "Q5_K_M")?.Path);
        Assert.Null(GgufFileSelector.FindBestModel(files, "F16"));
        Assert.Null(GgufFileSelector.FindBestModel(files, "Q4_K_M"));
        Assert.Equal(new[] { "BF16", "Q5_K_M" }, GgufFileSelector.AvailableQuantizations(files));
    }

    [Fact]
    public void Split_models_select_first_part_and_all_shards_from_subdirectories()
    {
        HubFile[] files =
        [
            Entry("Q4_K_M/big-Q4_K_M-00002-of-00003.gguf"), Entry("Q4_K_M/big-Q4_K_M-00001-of-00003.gguf"),
            Entry("Q4_K_M/big-Q4_K_M-00003-of-00003.gguf"), Entry("Q8_0/big-Q8_0-00001-of-00002.gguf")
        ];
        var primary = GgufFileSelector.FindBestModel(files, "Q4_K_M");
        Assert.Equal("Q4_K_M/big-Q4_K_M-00001-of-00003.gguf", primary?.Path);
        Assert.Equal(
            new[] { "Q4_K_M/big-Q4_K_M-00001-of-00003.gguf", "Q4_K_M/big-Q4_K_M-00002-of-00003.gguf", "Q4_K_M/big-Q4_K_M-00003-of-00003.gguf" },
            GgufFileSelector.SplitFiles(files, primary!).Select(file => file.Path));
        Assert.Equal(
            new[] { "Q4_K_M/big-Q4_K_M-00001-of-00003.gguf", "Q4_K_M/big-Q4_K_M-00002-of-00003.gguf", "Q4_K_M/big-Q4_K_M-00003-of-00003.gguf" },
            GgufFileSelector.ExpectedSplitPaths(primary!.Path));
    }

    [Fact]
    public void Multimodal_projector_closest_to_model_precision_is_selected()
    {
        HubFile[] files = [Entry("mmproj-F32.gguf"), Entry("mmproj-F16.gguf"), Entry("model-Q8_0.gguf")];
        Assert.Equal("mmproj-F16.gguf", GgufFileSelector.FindBestMmproj(files, "model-Q8_0.gguf")?.Path);
        Assert.Null(GgufFileSelector.FindBestMmproj([Entry("model-Q8_0.gguf")], "model-Q8_0.gguf"));
    }

    [Theory]
    [InlineData("model.gguf", true)]
    [InlineData("Q4/model-00001-of-00002.gguf", true)]
    [InlineData("../model.gguf", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("a//b.gguf", false)]
    [InlineData("a\\b.gguf", false)]
    [InlineData("C:model.gguf", false)]
    public void Repository_file_paths_must_stay_inside_snapshot(string path, bool safe)
    {
        Assert.Equal(safe, GgufFileSelector.IsSafeRelativePath(path));
    }

    [Fact]
    public void Hub_metadata_is_parsed_with_lfs_digests_and_gated_flag()
    {
        using var json = JsonDocument.Parse($$$"""
        {"sha":"{{{Revision}}}","gated":"manual","siblings":[
          {"rfilename":"b.gguf","size":134,"blobId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
           "lfs":{"sha256":"ABCDEF0000000000000000000000000000000000000000000000000000000000","size":4096}},
          {"rfilename":"README.md","size":12,"blobId":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}]}
        """);
        var (revision, files, gated) = GgufModelStore.ParseRepository(json.RootElement, "org/repo");
        Assert.Equal(Revision, revision);
        Assert.True(gated);
        Assert.Equal(new[] { "README.md", "b.gguf" }, files.Select(file => file.Path));
        var gguf = files.Single(file => file.Path == "b.gguf");
        Assert.Equal(4096, gguf.Size);
        Assert.Equal("abcdef0000000000000000000000000000000000000000000000000000000000", gguf.Sha256);
    }

    [Fact]
    public async Task Prepare_downloads_verifies_and_links_the_pinned_gguf()
    {
        var content = Encoding.UTF8.GetBytes(new string('x', 4096));
        var hub = new FakeHub(Revision).AddLfs("model-Q4_K_M.gguf", content).AddLfs("model-Q8_0.gguf", [1, 2, 3]);
        var store = Store(hub);

        var prepared = await store.PrepareAsync("org/repo-GGUF", null, null, TestContext.Current.CancellationToken);

        Assert.Equal(Revision, prepared.Revision);
        Assert.Equal("model-Q4_K_M.gguf", prepared.ModelFile);
        Assert.Null(prepared.MmprojFile);
        var snapshot = Path.Combine(_root, "hub", "models--org--repo-GGUF", "snapshots", Revision, "model-Q4_K_M.gguf");
        Assert.Equal(content, await System.IO.File.ReadAllBytesAsync(snapshot, TestContext.Current.CancellationToken));
        Assert.Equal(Revision, await System.IO.File.ReadAllTextAsync(
            Path.Combine(_root, "hub", "models--org--repo-GGUF", "refs", "main"), TestContext.Current.CancellationToken));
        Assert.DoesNotContain(hub.Requests, url => url.Contains("model-Q8_0.gguf"));
        Assert.True(store.IsPrepared(Record("org/repo-GGUF", prepared)));
        Assert.Equal("/root/.cache/huggingface/hub/models--org--repo-GGUF/snapshots/" + Revision + "/model-Q4_K_M.gguf",
            GgufModelStore.ContainerPath("org/repo-GGUF", Revision, prepared.ModelFile));
    }

    [Fact]
    public async Task Prepare_resumes_a_partial_download_with_a_range_request()
    {
        var content = Encoding.UTF8.GetBytes(new string('y', 8192));
        var hub = new FakeHub(Revision).AddLfs("model-Q4_K_M.gguf", content);
        var blobs = Path.Combine(_root, "hub", "models--org--repo", "blobs");
        Directory.CreateDirectory(blobs);
        await System.IO.File.WriteAllBytesAsync(Path.Combine(blobs, Sha256(content) + ".incomplete"), content[..3000], TestContext.Current.CancellationToken);

        await Store(hub).PrepareAsync("org/repo", null, null, TestContext.Current.CancellationToken);

        Assert.Contains("bytes=3000-", hub.Ranges);
        Assert.Equal(content, await System.IO.File.ReadAllBytesAsync(Path.Combine(blobs, Sha256(content)), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Corrupted_download_is_discarded_instead_of_starting_llama_cpp()
    {
        var hub = new FakeHub(Revision).AddLfs("model-Q4_K_M.gguf", [1, 2, 3, 4], servedContent: [9, 9, 9, 9]);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            Store(hub).PrepareAsync("org/repo", null, null, TestContext.Current.CancellationToken));
        Assert.Contains("Checksum mismatch", error.Message);
    }

    [Fact]
    public async Task Missing_quantization_lists_the_available_choices()
    {
        var hub = new FakeHub(Revision).AddLfs("model-Q8_0.gguf", [1]).AddLfs("model-Q5_K_M.gguf", [2]);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Store(hub).PrepareAsync("org/repo:Q4_K_M", null, null, TestContext.Current.CancellationToken));
        Assert.Contains("matches quantization 'Q4_K_M'", error.Message);
        Assert.Contains("Q5_K_M, Q8_0", error.Message);
    }

    [Fact]
    public async Task Repository_without_gguf_explains_the_runtime_mismatch()
    {
        var hub = new FakeHub(Revision).AddLfs("model.safetensors", [1]);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Store(hub).PrepareAsync("Qwen/Qwen3-8B", null, null, TestContext.Current.CancellationToken));
        Assert.Contains("contains no GGUF model files", error.Message);
    }

    [Fact]
    public async Task Gated_repository_requires_a_token_before_any_download()
    {
        var hub = new FakeHub(Revision, gated: true).AddLfs("model-Q4_K_M.gguf", [1]);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Store(hub).PrepareAsync("org/repo", null, null, TestContext.Current.CancellationToken));
        Assert.Contains("access token", error.Message);
        Assert.DoesNotContain(hub.Requests, url => url.Contains("/resolve/"));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "was not found")]
    [InlineData(HttpStatusCode.Unauthorized, "requires authentication")]
    public async Task Hub_errors_are_translated_into_actionable_messages(HttpStatusCode status, string expected)
    {
        var hub = new FakeHub(Revision) { MetadataStatus = status };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Store(hub).PrepareAsync("org/missing", null, null, TestContext.Current.CancellationToken));
        Assert.Contains(expected, error.Message);
    }

    private GgufModelStore Store(FakeHub hub)
    {
        var options = new NodeAgentOptions(null, "docker", "vllm", _root, _root, "localhost", 18000, true, true, 60, 20,
            "llama", "sglang");
        var clients = new SingleClientFactory(new HttpClient(hub));
        return new(options, new HuggingFaceCredentialStore(options, clients, NullLogger<HuggingFaceCredentialStore>.Instance),
            clients, NullLogger<GgufModelStore>.Instance);
    }

    private static ManagedModelRecord Record(string provider, GgufPreparation prepared) => new()
    {
        InstallationId = "custom-1", CatalogModelId = "custom", ProviderModelName = provider,
        Runtime = "llama.cpp", Port = 18000, TensorParallelSize = 1,
        SourceRevision = prepared.Revision, ModelFile = prepared.ModelFile, MmprojFile = prepared.MmprojFile
    };

    private static string Sha256(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class FakeHub(string revision, bool gated = false) : HttpMessageHandler
    {
        private readonly List<(string Path, byte[] Content, byte[] Served)> _files = [];
        public List<string> Requests { get; } = [];
        public List<string> Ranges { get; } = [];
        public HttpStatusCode MetadataStatus { get; init; } = HttpStatusCode.OK;

        public FakeHub AddLfs(string path, byte[] content, byte[]? servedContent = null)
        {
            _files.Add((path, content, servedContent ?? content));
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            if (url.Contains("/api/models/", StringComparison.Ordinal))
            {
                if (MetadataStatus != HttpStatusCode.OK) return Task.FromResult(new HttpResponseMessage(MetadataStatus));
                var body = JsonSerializer.Serialize(new
                {
                    sha = revision,
                    gated = gated ? (object)"auto" : false,
                    siblings = _files.Select(file => new
                    {
                        rfilename = file.Path,
                        size = file.Content.Length,
                        lfs = new { sha256 = Sha256(file.Content), size = file.Content.Length }
                    })
                });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
            }
            var match = _files.First(file => url.EndsWith("/" + file.Path, StringComparison.Ordinal));
            var range = request.Headers.Range?.Ranges.FirstOrDefault();
            if (range?.From is long from)
            {
                Ranges.Add($"bytes={from}-");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent)
                    { Content = new ByteArrayContent(match.Served[(int)from..]) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(match.Served) });
        }
    }
}
