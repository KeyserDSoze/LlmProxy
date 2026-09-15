using System.Diagnostics;

namespace LlmProxy.Application.Observability;

public static class LlmProxyActivity
{
    public const string SourceName = "Agic.LlmProxy";

    private static readonly ActivitySource Source = new(SourceName);

    public static Activity? Start(string name, ActivityKind kind = ActivityKind.Internal)
        => Source.StartActivity(name, kind);

    public static void SetGuid(Activity? activity, string key, Guid? value)
    {
        if (value is Guid id && id != Guid.Empty)
        {
            activity?.SetTag(key, id.ToString());
        }
    }

    public static void MarkError(Activity? activity, string errorType)
    {
        activity?.SetTag("error.type", errorType);
        activity?.SetStatus(ActivityStatusCode.Error, errorType);
    }
}
