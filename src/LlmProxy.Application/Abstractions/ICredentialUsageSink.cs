namespace LlmProxy.Application.Abstractions;

public interface ICredentialUsageSink
{
    void RecordUsage(Guid credentialId, DateTimeOffset usedAtUtc);
}
