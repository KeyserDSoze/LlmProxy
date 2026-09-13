namespace LlmProxy.Application.Abstractions;

public interface IRequestLoadTracker
{
    int GetActive(Guid deploymentId);

    int GetNodeActive(Guid nodeId) => 0;

    IDisposable Enter(Guid deploymentId);

    bool TryEnter(
        Guid deploymentId,
        Guid nodeId,
        int deploymentMaxConcurrency,
        int nodeMaxConcurrency,
        out IDisposable? lease)
    {
        if (GetActive(deploymentId) >= deploymentMaxConcurrency || GetNodeActive(nodeId) >= nodeMaxConcurrency)
        {
            lease = null;
            return false;
        }

        lease = Enter(deploymentId);
        return true;
    }
}
