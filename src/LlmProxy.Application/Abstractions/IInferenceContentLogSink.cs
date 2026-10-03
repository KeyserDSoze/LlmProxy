namespace LlmProxy.Application.Abstractions;

public sealed record InferenceContentLog(
    Guid RequestId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    string Surface,
    string Method,
    string Path,
    string? LogicalModel,
    Guid? ApiCredentialId,
    int StatusCode,
    string? RequestContentType,
    string? ResponseContentType,
    string RequestBody,
    string ResponseBody);

public interface IInferenceContentLogSink
{
    void Write(InferenceContentLog log);
}
