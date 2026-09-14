using System.Threading.Channels;
using LlmProxy.Application.Abstractions;
using LlmProxy.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LlmProxy.Infrastructure.Telemetry;

public sealed class BufferedRequestMetricsSink(
    IServiceScopeFactory scopeFactory,
    ILogger<BufferedRequestMetricsSink> logger) : BackgroundService, IRequestMetricsSink
{
    private readonly Channel<GatewayRequestMetric> _channel = Channel.CreateBounded<GatewayRequestMetric>(new BoundedChannelOptions(10_000)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false
    });

    public void Write(GatewayRequestMetric metric)
    {
        if (!_channel.Writer.TryWrite(metric))
        {
            logger.LogWarning("Request metric {RequestId} could not be queued.", metric.RequestId);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<GatewayRequestMetric>(100);

        while (await _channel.Reader.WaitToReadAsync(stoppingToken))
        {
            batch.Clear();
            while (batch.Count < 100 && _channel.Reader.TryRead(out var metric))
            {
                batch.Add(metric);
            }

            if (batch.Count == 0)
            {
                continue;
            }

            try
            {
                using var scope = scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
                dbContext.RequestMetrics.AddRange(batch.Select(metric => new RequestMetricRecord
                {
                    RequestId = metric.RequestId,
                    StartedAtUtc = metric.StartedAtUtc,
                    LogicalModel = metric.LogicalModel,
                    Surface = metric.Surface,
                    DeploymentId = metric.DeploymentId,
                    NodeId = metric.NodeId,
                    ApiCredentialId = metric.ApiCredentialId,
                    UsageGroupId = metric.UsageGroupId,
                    StatusCode = metric.StatusCode,
                    DurationMilliseconds = metric.DurationMilliseconds,
                    AttemptCount = metric.AttemptCount,
                    IsStreaming = metric.IsStreaming,
                    UpstreamHeaderMilliseconds = metric.UpstreamHeaderMilliseconds,
                    TimeToFirstByteMilliseconds = metric.TimeToFirstByteMilliseconds,
                    InputTokens = metric.InputTokens,
                    OutputTokens = metric.OutputTokens,
                    TotalTokens = metric.TotalTokens,
                    ErrorCode = metric.ErrorCode
                }));
                await dbContext.SaveChangesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failed to persist {MetricCount} request metrics.", batch.Count);
            }
        }
    }
}
