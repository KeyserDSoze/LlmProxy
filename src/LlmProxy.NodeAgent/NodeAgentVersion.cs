using System.Reflection;

namespace LlmProxy.NodeAgent;

/// <summary>
/// The stable AssemblyVersion is intentionally 0.2.0.0 for binary compatibility.
/// Releases MUST advertise the MSBuild -p:Version informational version instead:
/// it is embedded as 0.2.46[+sourceSha], for example, in the Agent artifact.
/// </summary>
public static class NodeAgentVersion
{
    public static string Current => Resolve(typeof(OutboundNodeConnectionWorker).Assembly);

    public static string Resolve(Assembly assembly)
    {
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
            return informational.Split('+', 2, StringSplitOptions.None)[0];
        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
