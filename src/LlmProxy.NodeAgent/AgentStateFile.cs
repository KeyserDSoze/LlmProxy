using System.Text.Json;

namespace LlmProxy.NodeAgent;

/// <summary>
/// Reads the Agent's JSON state files. A truncated or hand-edited file must not stop the Agent from starting (the
/// operator would lose remote management of the machine): the unreadable file is moved aside for inspection and the
/// Agent continues with an empty list.
/// </summary>
internal static class AgentStateFile
{
    private const int MaxQuarantinedCopies = 5;

    public static List<T> ReadList<T>(string path, JsonSerializerOptions options, ILogger? logger)
    {
        if (!File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path), options) ?? [];
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            Quarantine(path, error, logger);
            return [];
        }
    }

    public static async Task<List<T>> ReadListAsync<T>(string path, JsonSerializerOptions options, ILogger? logger, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return [];
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<List<T>>(stream, options, cancellationToken) ?? [];
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            Quarantine(path, error, logger);
            return [];
        }
    }

    internal static void Quarantine(string path, Exception error, ILogger? logger)
    {
        var moved = path + ".corrupt-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff");
        try
        {
            File.Move(path, moved, overwrite: true);
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(moved, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Prune(path);
            logger?.LogError("State file {File} is unreadable ({Error}). It was moved to {Moved} and the Agent continues with an empty list.",
                Path.GetFileName(path), error.GetType().Name, Path.GetFileName(moved));
        }
        catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException)
        {
            logger?.LogError("State file {File} is unreadable ({Error}) and could not be moved aside ({MoveError}).",
                Path.GetFileName(path), error.GetType().Name, moveError.GetType().Name);
        }
    }

    private static void Prune(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory)) return;
        foreach (var stale in Directory.EnumerateFiles(directory, Path.GetFileName(path) + ".corrupt-*")
                     .Order(StringComparer.Ordinal).SkipLast(MaxQuarantinedCopies))
        {
            try { File.Delete(stale); } catch (IOException) { }
        }
    }
}
