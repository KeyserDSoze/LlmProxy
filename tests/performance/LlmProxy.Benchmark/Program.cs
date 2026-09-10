using LlmProxy.Benchmarking;

if (args.Any(argument => argument.Equals("--help", StringComparison.OrdinalIgnoreCase) || argument.Equals("-h", StringComparison.OrdinalIgnoreCase)))
{
    Console.WriteLine(BenchmarkOptions.HelpText);
}
else
{
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };

    try
    {
        var options = BenchmarkOptions.Parse(args);
        using var httpClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        var runner = new BenchmarkRunner(httpClient);
        var report = await runner.RunAsync(options, cancellation.Token);
        BenchmarkConsoleWriter.Write(report);

        var files = await BenchmarkReportWriter.WriteAsync(report, options.OutputDirectory, cancellation.Token);
        Console.WriteLine();
        Console.WriteLine($"JSON: {files.JsonPath}");
        Console.WriteLine($"CSV:  {files.CsvPath}");
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
    {
        Console.Error.WriteLine("Benchmark cancelled.");
        Environment.ExitCode = 130;
    }
    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"Benchmark failed: {exception.Message}");
        Console.Error.WriteLine("Use --help for usage.");
        Environment.ExitCode = 2;
    }
}
