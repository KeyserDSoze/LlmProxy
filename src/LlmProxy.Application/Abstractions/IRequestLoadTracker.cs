namespace LlmProxy.Application.Abstractions;

public interface IRequestLoadTracker
{
    int GetActive(Guid deploymentId);
    IDisposable Enter(Guid deploymentId);
}
