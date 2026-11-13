using System.Text.Json;
using LlmProxy.NodeAgent;
using Microsoft.Extensions.Logging.Abstractions;

namespace LlmProxy.UnitTests.NodeAgent;

public sealed class AgentStateFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "llmproxy-state-" + Guid.NewGuid().ToString("N"));
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public AgentStateFileTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string File_(string name) => Path.Combine(_root, name);

    [Fact]
    public void A_missing_file_is_an_empty_list()
        => Assert.Empty(AgentStateFile.ReadList<string>(File_("jobs.json"), Json, null));

    [Fact]
    public void A_valid_file_is_read()
    {
        File.WriteAllText(File_("jobs.json"), "[\"a\",\"b\"]");
        Assert.Equal(["a", "b"], AgentStateFile.ReadList<string>(File_("jobs.json"), Json, null));
    }

    [Fact]
    public void A_truncated_file_is_moved_aside_and_does_not_stop_the_agent()
    {
        File.WriteAllText(File_("jobs.json"), "[{\"id\":\"half-writ");

        var result = AgentStateFile.ReadList<string>(File_("jobs.json"), Json, NullLogger.Instance);

        Assert.Empty(result);
        Assert.False(File.Exists(File_("jobs.json")));
        Assert.Single(Directory.GetFiles(_root, "jobs.json.corrupt-*"));
    }

    [Fact]
    public async Task The_async_reader_quarantines_too()
    {
        File.WriteAllText(File_("models.json"), "not json at all");

        var result = await AgentStateFile.ReadListAsync<string>(File_("models.json"), Json, null, CancellationToken.None);

        Assert.Empty(result);
        Assert.Single(Directory.GetFiles(_root, "models.json.corrupt-*"));
    }

    [Fact]
    public void Only_the_latest_quarantined_copies_are_kept()
    {
        for (var i = 0; i < 8; i++)
        {
            File.WriteAllText(File_("jobs.json"), "{broken");
            AgentStateFile.ReadList<string>(File_("jobs.json"), Json, null);
            Thread.Sleep(5);
        }

        Assert.Equal(5, Directory.GetFiles(_root, "jobs.json.corrupt-*").Length);
    }

    [Fact]
    public async Task Process_output_is_bounded_but_fully_drained()
    {
        using var reader = new StringReader(new string('x', 50_000));

        var text = await ProcessRunner.ReadBoundedAsync(reader, CancellationToken.None, limit: 1000);

        Assert.StartsWith(new string('x', 1000), text);
        Assert.Contains("[output truncated by the Agent]", text);
        Assert.True(text.Length < 1100);
        Assert.Equal(-1, reader.Peek());
    }

    [Fact]
    public async Task Short_process_output_is_returned_unchanged()
    {
        using var reader = new StringReader("hello\nworld");
        Assert.Equal("hello\nworld", await ProcessRunner.ReadBoundedAsync(reader, CancellationToken.None, limit: 1000));
    }
}
