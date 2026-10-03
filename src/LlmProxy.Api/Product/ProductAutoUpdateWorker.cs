using LlmProxy.Domain.Audit;
using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LlmProxy.Api.Product;

public sealed class ProductAutoUpdateWorker(
    IServiceScopeFactory scopeFactory,
    ReleaseDiscoveryService releases,
    UpdateAgentClient agent,
    ILogger<ProductAutoUpdateWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);
    private const long AdvisoryLockKey = 5497851493419667796;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        await CheckAsync(stoppingToken);

        using var timer = new PeriodicTimer(PollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await CheckAsync(stoppingToken);
        }
    }

    private async Task CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            var claimed = await TryClaimDueCheckAsync(cancellationToken);
            if (claimed is null) return;

            var status = await agent.TryGetStatusAsync(cancellationToken);
            if (status is null)
            {
                await RecordResultAsync(claimed, null, "The host update agent is not reachable.", cancellationToken);
                return;
            }

            if (status.ActiveJob is { Status: "Pending" or "Running" })
            {
                await RecordResultAsync(claimed, null, null, cancellationToken);
                return;
            }

            var current = ProductReleaseCatalog.GetInfo().Version;
            var available = await releases.GetAvailableAsync(current, cancellationToken);
            var target = available.Where(item => item.IsNewer)
                .OrderByDescending(item => Version.TryParse(item.Version, out var parsed) ? parsed : new Version())
                .FirstOrDefault();

            if (target is null)
            {
                await RecordResultAsync(claimed, null, null, cancellationToken);
                return;
            }

            var targetVersion = Version.Parse(target.Version);
            var upgradePath = available
                .Where(item => item.IsNewer && Version.TryParse(item.Version, out var parsed) && parsed <= targetVersion)
                .OrderBy(item => Version.Parse(item.Version))
                .Select(item => item.Version)
                .ToArray();

            var job = await agent.ScheduleAsync(
                new ScheduleProductUpdateRequest(target.Version, DateTimeOffset.UtcNow, Force: false),
                upgradePath,
                cancellationToken);
            await RecordResultAsync(claimed, job, null, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogError(exception, "Automatic product update check failed.");
        }
    }

    private async Task<ProductUpdatePolicySnapshot?> TryClaimDueCheckAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await dbContext.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({AdvisoryLockKey})", cancellationToken);

        var policy = await dbContext.ProductUpdatePolicies.SingleOrDefaultAsync(
            item => item.Id == ProductUpdatePolicyRecord.SingletonId, cancellationToken);
        if (policy is null || !ProductUpdateSchedule.IsDue(policy, DateTimeOffset.UtcNow))
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        policy.LastCheckedAtUtc = DateTimeOffset.UtcNow;
        policy.LastError = null;
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ProductUpdatePolicySnapshot.From(policy);
    }

    private async Task RecordResultAsync(ProductUpdatePolicySnapshot claimed, UpdateJobStatus? job, string? error, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var policy = await dbContext.ProductUpdatePolicies.SingleOrDefaultAsync(
            item => item.Id == ProductUpdatePolicyRecord.SingletonId, cancellationToken);
        if (policy is null) return;

        policy.LastError = string.IsNullOrWhiteSpace(error) ? null : error[..Math.Min(error.Length, 1200)];
        if (job is not null)
        {
            policy.LastScheduledAtUtc = DateTimeOffset.UtcNow;
            policy.LastScheduledVersion = job.Version;
            dbContext.AuditEvents.Add(new AuditEvent(
                "system:auto-update",
                "product.update.auto_schedule",
                "product_update",
                job.Id.ToString(),
                null,
                System.Text.Json.JsonSerializer.Serialize(new { claimed.Mode, job.Version, job.UpgradePath, job.ScheduledForUtc })));
        }
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

public static class ProductUpdateSchedule
{
    private static readonly TimeSpan AsapInterval = TimeSpan.FromMinutes(5);

    public static bool IsDue(ProductUpdatePolicyRecord policy, DateTimeOffset nowUtc)
    {
        if (policy.Mode == ProductUpdatePolicyRecord.ManualMode) return false;
        if (policy.Mode == ProductUpdatePolicyRecord.AsapMode)
            return policy.LastCheckedAtUtc is null || nowUtc - policy.LastCheckedAtUtc >= AsapInterval;

        var occurrence = GetMostRecentOccurrenceUtc(policy, nowUtc);
        return occurrence is not null && occurrence <= nowUtc &&
               (policy.LastCheckedAtUtc is null || policy.LastCheckedAtUtc < occurrence);
    }

    private static DateTimeOffset? GetMostRecentOccurrenceUtc(ProductUpdatePolicyRecord policy, DateTimeOffset nowUtc)
    {
        TimeZoneInfo timeZone;
        try { timeZone = TimeZoneInfo.FindSystemTimeZoneById(policy.TimeZoneId); }
        catch (TimeZoneNotFoundException) { return null; }
        catch (InvalidTimeZoneException) { return null; }

        var localNow = TimeZoneInfo.ConvertTime(nowUtc, timeZone);
        var time = new TimeSpan(Math.Clamp(policy.LocalHour, 0, 23), Math.Clamp(policy.LocalMinute, 0, 59), 0);
        DateTime candidate;

        if (policy.Mode == ProductUpdatePolicyRecord.NightlyMode)
        {
            candidate = localNow.Date + time;
            if (candidate > localNow.DateTime) candidate = candidate.AddDays(-1);
        }
        else if (policy.Mode == ProductUpdatePolicyRecord.WeeklyMode)
        {
            var day = Math.Clamp(policy.DayOfWeek, 0, 6);
            var daysBack = ((int)localNow.DayOfWeek - day + 7) % 7;
            candidate = localNow.Date.AddDays(-daysBack) + time;
            if (candidate > localNow.DateTime) candidate = candidate.AddDays(-7);
        }
        else if (policy.Mode == ProductUpdatePolicyRecord.MonthlyMode)
        {
            candidate = MonthlyCandidate(localNow.Year, localNow.Month, policy.DayOfMonth, time);
            if (candidate > localNow.DateTime)
            {
                var previous = localNow.AddMonths(-1);
                candidate = MonthlyCandidate(previous.Year, previous.Month, policy.DayOfMonth, time);
            }
        }
        else return null;

        while (timeZone.IsInvalidTime(candidate)) candidate = candidate.AddMinutes(30);
        var utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(candidate, DateTimeKind.Unspecified), timeZone);
        return new DateTimeOffset(utc);
    }

    private static DateTime MonthlyCandidate(int year, int month, int requestedDay, TimeSpan time)
    {
        var day = Math.Clamp(requestedDay, 1, DateTime.DaysInMonth(year, month));
        return new DateTime(year, month, day) + time;
    }
}

public sealed record ProductUpdatePolicySnapshot(
    string Mode,
    string TimeZoneId,
    int LocalHour,
    int LocalMinute,
    int DayOfWeek,
    int DayOfMonth,
    DateTimeOffset? LastCheckedAtUtc,
    DateTimeOffset? LastScheduledAtUtc,
    string? LastScheduledVersion,
    string? LastError,
    DateTimeOffset UpdatedAtUtc)
{
    public static ProductUpdatePolicySnapshot From(ProductUpdatePolicyRecord record) =>
        new(record.Mode, record.TimeZoneId, record.LocalHour, record.LocalMinute, record.DayOfWeek, record.DayOfMonth,
            record.LastCheckedAtUtc, record.LastScheduledAtUtc, record.LastScheduledVersion, record.LastError, record.UpdatedAtUtc);
}
