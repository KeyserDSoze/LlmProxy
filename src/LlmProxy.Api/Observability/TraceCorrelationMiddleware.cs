using System.Diagnostics;

namespace LlmProxy.Api.Observability;

public sealed class TraceCorrelationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var activity = Activity.Current;
        if (activity is not null && activity.TraceId != default)
        {
            context.Response.Headers["X-LlmProxy-Trace-Id"] = activity.TraceId.ToString();
        }

        await next(context);
    }
}
