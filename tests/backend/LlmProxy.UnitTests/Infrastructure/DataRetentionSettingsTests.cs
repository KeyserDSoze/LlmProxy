using LlmProxy.Infrastructure.Retention;
using Microsoft.Extensions.Configuration;

namespace LlmProxy.UnitTests.Infrastructure;

public sealed class DataRetentionSettingsTests
{
    [Fact]
    public void Uses_operational_defaults()
    {
        var configuration = new ConfigurationBuilder().Build();

        var settings = DataRetentionSettings.FromConfiguration(configuration);

        Assert.True(settings.Enabled);
        Assert.Equal(90, settings.RequestMetricsDays);
        Assert.Equal(365, settings.AuditEventsDays);
        Assert.Equal(30, settings.RuntimeStateOutboxDays);
        Assert.Equal(24, settings.IntervalHours);
        Assert.Equal(5000, settings.BatchSize);
    }

    [Fact]
    public void Reads_configuration_and_clamps_unsafe_values()
    {
        var values = new Dictionary<string, string?>
        {
            ["Retention:Enabled"] = "false",
            ["Retention:RequestMetricsDays"] = "0",
            ["Retention:AuditEventsDays"] = "99999",
            ["Retention:RuntimeStateOutboxDays"] = "0",
            ["Retention:IntervalHours"] = "999",
            ["Retention:BatchSize"] = "1"
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var settings = DataRetentionSettings.FromConfiguration(configuration);

        Assert.False(settings.Enabled);
        Assert.Equal(1, settings.RequestMetricsDays);
        Assert.Equal(3650, settings.AuditEventsDays);
        Assert.Equal(1, settings.RuntimeStateOutboxDays);
        Assert.Equal(168, settings.IntervalHours);
        Assert.Equal(100, settings.BatchSize);
    }
}
