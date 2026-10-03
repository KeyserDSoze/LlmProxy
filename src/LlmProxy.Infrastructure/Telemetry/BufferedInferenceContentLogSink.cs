using System.Threading.Channels;
using LlmProxy.Application.Abstractions;
using LlmProxy.Infrastructure.Persistence;
using LlmProxy.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LlmProxy.Infrastructure.Telemetry;

public sealed class BufferedInferenceContentLogSink(
    IServiceScopeFactory scopeFactory,
    SensitiveDataProtector protector,
    ILogger<BufferedInferenceContentLogSink> logger) : BackgroundService, IInferenceContentLogSink
{
    private readonly Channel<InferenceContentLog> _channel = Channel.CreateBounded<InferenceContentLog>(new BoundedChannelOptions(2_000)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false
    });

    public void Write(InferenceContentLog log)
    {
        if (!_channel.Writer.TryWrite(log))
        {
            logger.LogWarning("Full-body content log {RequestId} could not be queued because the persistence buffer is full.", log.RequestId);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (await _channel.Reader.WaitToReadAsync(stoppingToken))
        {
            while (_channel.Reader.TryRead(out var log))
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
                    dbContext.InferenceContentLogs.Add(new InferenceContentLogRecord
                    {
                        RequestId = log.RequestId,
                        StartedAtUtc = log.StartedAtUtc,
                        CompletedAtUtc = log.CompletedAtUtc,
                        Surface = log.Surface,
                        Method = log.Method,
                        Path = log.Path,
                        LogicalModel = log.LogicalModel,
                        ApiCredentialId = log.ApiCredentialId,
                        StatusCode = log.StatusCode,
                        RequestContentType = log.RequestContentType,
                        ResponseContentType = log.ResponseContentType,
                        RequestBodyCiphertext = protector.Protect(log.RequestBody, $"content-log:{log.RequestId}:request"),
                        ResponseBodyCiphertext = protector.Protect(log.ResponseBody, $"content-log:{log.RequestId}:response")
                    });
                    await dbContext.SaveChangesAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Failed to persist full-body content log {RequestId}.", log.RequestId);
                }
            }
        }
    }
}
