using System.Text.Json;

namespace LlmProxy.NodeAgent;

public sealed class ManagedModelRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ManagedModelRegistry(NodeAgentOptions options)
    {
        Directory.CreateDirectory(options.DataDirectory);
        _path = Path.Combine(options.DataDirectory, "models.json");
    }

    public async Task<IReadOnlyList<ManagedModelRecord>> ReadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadUnsafeAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ManagedModelRecord?> FindAsync(string installationId, CancellationToken cancellationToken)
    {
        var rows = await ReadAsync(cancellationToken);
        return rows.FirstOrDefault(item => string.Equals(item.InstallationId, installationId, StringComparison.Ordinal));
    }

    public async Task UpsertAsync(ManagedModelRecord record, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var rows = (await ReadUnsafeAsync(cancellationToken)).ToList();
            var index = rows.FindIndex(item => string.Equals(item.InstallationId, record.InstallationId, StringComparison.Ordinal));
            if (index >= 0) rows[index] = record;
            else rows.Add(record);
            await WriteUnsafeAsync(rows, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(string installationId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var rows = (await ReadUnsafeAsync(cancellationToken))
                .Where(item => !string.Equals(item.InstallationId, installationId, StringComparison.Ordinal))
                .ToList();
            await WriteUnsafeAsync(rows, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<ManagedModelRecord>> ReadUnsafeAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return [];
        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<List<ManagedModelRecord>>(stream, JsonOptions, cancellationToken) ?? [];
    }

    private async Task WriteUnsafeAsync(IReadOnlyList<ManagedModelRecord> rows, CancellationToken cancellationToken)
    {
        var temp = _path + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, rows, JsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        File.Move(temp, _path, overwrite: true);
    }
}
