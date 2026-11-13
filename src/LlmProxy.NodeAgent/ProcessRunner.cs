using System.Diagnostics;

namespace LlmProxy.NodeAgent;

public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Success => ExitCode == 0;

}

public sealed class ProcessRunner
{
    public async Task<CommandResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start '{executable}'.");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var stdoutTask = ReadBoundedAsync(process.StandardOutput, timeoutCts.Token);
        var stderrTask = ReadBoundedAsync(process.StandardError, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            return new CommandResult(process.ExitCode, await stdoutTask, await stderrTask);
        }
        catch (OperationCanceledException)
        {
            // Caller cancellation must not leave docker/nvidia-smi children running.
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException($"'{executable}' exceeded the {timeout} timeout.");
        }
    }
    /// <summary>Largest output kept per stream. `docker logs` of a crashing container can emit very long lines.</summary>
    internal const int MaxCapturedCharacters = 4 * 1024 * 1024;

    /// <summary>Keeps the first and discards the rest beyond the limit, but keeps draining so the child never blocks on a full pipe.</summary>
    internal static async Task<string> ReadBoundedAsync(TextReader reader, CancellationToken cancellationToken, int limit = MaxCapturedCharacters)
    {
        var text = new System.Text.StringBuilder();
        var buffer = new char[8192];
        var truncated = false;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            var room = limit - text.Length;
            if (room > 0) text.Append(buffer, 0, Math.Min(read, room));
            if (read > room) truncated = true;
        }
        if (truncated) text.Append("\n[output truncated by the Agent]");
        return text.ToString();
    }

    // Streams carriage-return progress (docker, huggingface tqdm) without buffering multi-GB downloads.
    public async Task<CommandResult> RunStreamingAsync(string executable, IEnumerable<string> arguments,
        TimeSpan timeout, CancellationToken cancellationToken, Action<string> onLine,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo {
                FileName = executable, RedirectStandardOutput = true,
                RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
            }
        };
        if (environment is not null)
            foreach (var (key, value) in environment) process.StartInfo.Environment[key] = value;
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) throw new InvalidOperationException("Failed to start download operation.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var stdoutTail = new System.Text.StringBuilder();
        var stderrTail = new System.Text.StringBuilder();
        async Task DrainAsync(StreamReader reader, System.Text.StringBuilder tail)
        {
            var buffer = new char[4096];
            var line = new System.Text.StringBuilder();
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), deadline.Token);
                if (read == 0) break;
                for (var i = 0; i < read; i++)
                {
                    var c = buffer[i];
                    if (c is '\r' or '\n')
                    {
                        if (line.Length == 0) continue;
                        var entry = line.ToString();
                        onLine(entry);
                        tail.AppendLine(entry);
                        if (tail.Length > 8192) tail.Remove(0, tail.Length - 8192);
                        line.Clear();
                    }
                    else if (line.Length < 16384) line.Append(c);
                }
            }
            if (line.Length > 0) { onLine(line.ToString()); tail.AppendLine(line.ToString()); }
        }
        var stdout = DrainAsync(process.StandardOutput, stdoutTail);
        var stderr = DrainAsync(process.StandardError, stderrTail);
        try
        {
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(deadline.Token));
            return new CommandResult(process.ExitCode, stdoutTail.ToString(), stderrTail.ToString());
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) {}
            if (!cancellationToken.IsCancellationRequested)
                throw new TimeoutException("Download command exceeded its configured time limit.");
            throw;
        }
    }
}
