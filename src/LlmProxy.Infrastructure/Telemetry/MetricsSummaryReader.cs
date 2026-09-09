using System.Data;
using System.Data.Common;
using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LlmProxy.Infrastructure.Telemetry;

public sealed class MetricsSummaryReader(GatewayDbContext dbContext)
{
    public async Task<MetricsSummary> ReadAsync(int requestedHours, CancellationToken cancellationToken)
    {
        var windowHours = Math.Clamp(requestedHours, 1, 168);
        var sinceUtc = DateTimeOffset.UtcNow.AddHours(-windowHours);
        var connection = dbContext.Database.GetDbConnection();
        var closeAfterRead = connection.State != ConnectionState.Open;

        if (closeAfterRead)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            var totals = await ReadTotalsAsync(connection, sinceUtc, cancellationToken);
            var byModel = await ReadModelBreakdownAsync(connection, sinceUtc, cancellationToken);
            var byNode = await ReadNodeBreakdownAsync(connection, sinceUtc, cancellationToken);
            var successRate = totals.RequestCount == 0
                ? 0d
                : Math.Round(totals.SuccessCount * 100d / totals.RequestCount, 2);

            return new MetricsSummary(
                windowHours,
                sinceUtc,
                totals.RequestCount,
                totals.SuccessCount,
                totals.ErrorCount,
                successRate,
                totals.P50DurationMilliseconds,
                totals.P95DurationMilliseconds,
                totals.P50TimeToFirstByteMilliseconds,
                totals.P95TimeToFirstByteMilliseconds,
                totals.AverageUpstreamHeaderMilliseconds,
                totals.InputTokens,
                totals.OutputTokens,
                totals.TotalTokens,
                totals.TokenObservedRequests,
                totals.FailoverRequests,
                totals.StreamingRequests,
                byModel,
                byNode);
        }
        finally
        {
            if (closeAfterRead)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static async Task<Totals> ReadTotalsAsync(
        DbConnection connection,
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                COUNT(*)::bigint,
                COUNT(*) FILTER (WHERE "StatusCode" >= 200 AND "StatusCode" < 400)::bigint,
                COUNT(*) FILTER (WHERE "StatusCode" < 200 OR "StatusCode" >= 400)::bigint,
                (percentile_cont(0.50) WITHIN GROUP (ORDER BY "DurationMilliseconds"))::double precision,
                (percentile_cont(0.95) WITHIN GROUP (ORDER BY "DurationMilliseconds"))::double precision,
                (percentile_cont(0.50) WITHIN GROUP (ORDER BY "TimeToFirstByteMilliseconds") FILTER (WHERE "TimeToFirstByteMilliseconds" IS NOT NULL))::double precision,
                (percentile_cont(0.95) WITHIN GROUP (ORDER BY "TimeToFirstByteMilliseconds") FILTER (WHERE "TimeToFirstByteMilliseconds" IS NOT NULL))::double precision,
                (AVG("UpstreamHeaderMilliseconds") FILTER (WHERE "UpstreamHeaderMilliseconds" IS NOT NULL))::double precision,
                COALESCE(SUM("InputTokens"), 0)::bigint,
                COALESCE(SUM("OutputTokens"), 0)::bigint,
                COALESCE(SUM("TotalTokens"), 0)::bigint,
                COUNT(*) FILTER (WHERE "TotalTokens" IS NOT NULL)::bigint,
                COUNT(*) FILTER (WHERE "AttemptCount" > 1)::bigint,
                COUNT(*) FILTER (WHERE "IsStreaming")::bigint
            FROM request_metrics
            WHERE "StartedAtUtc" >= @sinceUtc;
            """;
        AddSinceParameter(command, sinceUtc);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("PostgreSQL did not return the inference metric aggregate row.");
        }

        return new Totals(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            GetNullableDouble(reader, 3),
            GetNullableDouble(reader, 4),
            GetNullableDouble(reader, 5),
            GetNullableDouble(reader, 6),
            GetNullableDouble(reader, 7),
            reader.GetInt64(8),
            reader.GetInt64(9),
            reader.GetInt64(10),
            reader.GetInt64(11),
            reader.GetInt64(12),
            reader.GetInt64(13));
    }

    private static async Task<IReadOnlyList<ModelMetricSummary>> ReadModelBreakdownAsync(
        DbConnection connection,
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                "LogicalModel",
                COUNT(*)::bigint,
                COUNT(*) FILTER (WHERE "StatusCode" < 200 OR "StatusCode" >= 400)::bigint,
                AVG("DurationMilliseconds")::double precision,
                (AVG("TimeToFirstByteMilliseconds") FILTER (WHERE "TimeToFirstByteMilliseconds" IS NOT NULL))::double precision,
                COALESCE(SUM("OutputTokens"), 0)::bigint
            FROM request_metrics
            WHERE "StartedAtUtc" >= @sinceUtc
            GROUP BY "LogicalModel"
            ORDER BY COUNT(*) DESC, "LogicalModel";
            """;
        AddSinceParameter(command, sinceUtc);

        var rows = new List<ModelMetricSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ModelMetricSummary(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                GetNullableDouble(reader, 3),
                GetNullableDouble(reader, 4),
                reader.GetInt64(5)));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<NodeMetricSummary>> ReadNodeBreakdownAsync(
        DbConnection connection,
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                "NodeId",
                COUNT(*)::bigint,
                COUNT(*) FILTER (WHERE "StatusCode" < 200 OR "StatusCode" >= 400)::bigint,
                AVG("DurationMilliseconds")::double precision,
                (percentile_cont(0.95) WITHIN GROUP (ORDER BY "DurationMilliseconds"))::double precision,
                COALESCE(SUM("OutputTokens"), 0)::bigint
            FROM request_metrics
            WHERE "StartedAtUtc" >= @sinceUtc AND "NodeId" IS NOT NULL
            GROUP BY "NodeId"
            ORDER BY COUNT(*) DESC, "NodeId";
            """;
        AddSinceParameter(command, sinceUtc);

        var rows = new List<NodeMetricSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new NodeMetricSummary(
                reader.GetGuid(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                GetNullableDouble(reader, 3),
                GetNullableDouble(reader, 4),
                reader.GetInt64(5)));
        }

        return rows;
    }

    private static void AddSinceParameter(DbCommand command, DateTimeOffset sinceUtc)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@sinceUtc";
        parameter.Value = sinceUtc;
        command.Parameters.Add(parameter);
    }

    private static double? GetNullableDouble(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);

    private sealed record Totals(
        long RequestCount,
        long SuccessCount,
        long ErrorCount,
        double? P50DurationMilliseconds,
        double? P95DurationMilliseconds,
        double? P50TimeToFirstByteMilliseconds,
        double? P95TimeToFirstByteMilliseconds,
        double? AverageUpstreamHeaderMilliseconds,
        long InputTokens,
        long OutputTokens,
        long TotalTokens,
        long TokenObservedRequests,
        long FailoverRequests,
        long StreamingRequests);
}

public sealed record MetricsSummary(
    int WindowHours,
    DateTimeOffset SinceUtc,
    long RequestCount,
    long SuccessCount,
    long ErrorCount,
    double SuccessRatePercent,
    double? P50DurationMilliseconds,
    double? P95DurationMilliseconds,
    double? P50TimeToFirstByteMilliseconds,
    double? P95TimeToFirstByteMilliseconds,
    double? AverageUpstreamHeaderMilliseconds,
    long InputTokens,
    long OutputTokens,
    long TotalTokens,
    long TokenObservedRequests,
    long FailoverRequests,
    long StreamingRequests,
    IReadOnlyList<ModelMetricSummary> ByModel,
    IReadOnlyList<NodeMetricSummary> ByNode);

public sealed record ModelMetricSummary(
    string LogicalModel,
    long RequestCount,
    long ErrorCount,
    double? AverageDurationMilliseconds,
    double? AverageTimeToFirstByteMilliseconds,
    long OutputTokens);

public sealed record NodeMetricSummary(
    Guid NodeId,
    long RequestCount,
    long ErrorCount,
    double? AverageDurationMilliseconds,
    double? P95DurationMilliseconds,
    long OutputTokens);
