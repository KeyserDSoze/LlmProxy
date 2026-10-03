using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LlmProxy.Infrastructure.Retention;

public sealed class ContentLogRetentionService(GatewayDbContext dbContext)
{
    public const int MinimumRetentionDays = 10;
    public const int MaximumRetentionDays = 11 * 365;
    public const int DefaultRetentionDays = 30;
    public const int CleanupIntervalHours = 4;

    public async Task<ContentLogSettingsRecord> GetSettingsAsync(CancellationToken cancellationToken = default)
        => await dbContext.ContentLogSettings.SingleAsync(item => item.Id == ContentLogSettingsRecord.SingletonId, cancellationToken);

    public async Task<ContentLogSettingsRecord> UpdateRetentionDaysAsync(
        int retentionDays,
        CancellationToken cancellationToken = default)
    {
        if (retentionDays is < MinimumRetentionDays or > MaximumRetentionDays)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retentionDays),
                $"Content-log retention must be between {MinimumRetentionDays} and {MaximumRetentionDays} days.");
        }

        var settings = await GetSettingsAsync(cancellationToken);
        settings.RetentionDays = retentionDays;
        settings.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return settings;
    }

    public async Task<ContentLogCleanupResult> RunAsync(
        DateTimeOffset? nowUtc = null,
        CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken);
        var startedAtUtc = nowUtc ?? DateTimeOffset.UtcNow;
        var cutoffUtc = startedAtUtc.AddDays(-settings.RetentionDays);
        var deleted = await dbContext.InferenceContentLogs
            .Where(item => item.StartedAtUtc < cutoffUtc)
            .ExecuteDeleteAsync(cancellationToken);
        return new ContentLogCleanupResult(startedAtUtc, DateTimeOffset.UtcNow, settings.RetentionDays, cutoffUtc, deleted);
    }
}

public sealed record ContentLogCleanupResult(
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    int RetentionDays,
    DateTimeOffset CutoffUtc,
    int DeletedLogs);
