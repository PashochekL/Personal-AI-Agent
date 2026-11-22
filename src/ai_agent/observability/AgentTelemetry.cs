using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using AiAgent.Shared;

namespace AiAgent.Observability;

public static class AgentTelemetry
{
    public const string SourceName = "AiAgent.Backend";
    public const string CoreSourceName = "AiAgent.Core";
    public static readonly ActivitySource Source = new(SourceName);
    private static readonly Meter Meter = new(SourceName);
    private static readonly Counter<long> Tasks = Meter.CreateCounter<long>("damir.task.completed", "{task}");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("damir.task.duration", "s");
    internal static readonly Counter<long> Tokens = Meter.CreateCounter<long>("damir.llm.tokens", "{token}");
    internal static readonly Histogram<double> LlmDuration = Meter.CreateHistogram<double>("damir.llm.duration", "s");

    public static string? CaptureTraceParent() => Source.HasListeners() && Activity.Current?.IdFormat == ActivityIdFormat.W3C ? Activity.Current.Id : null;

    public static Activity? StartTask(string taskId, string session, MemoryScope scope, string? traceParent)
    {
        ActivityContext.TryParse(traceParent, null, isRemote: true, out var parent);
        var activity = Source.StartActivity("task.execute", ActivityKind.Consumer, parent);
        activity?.SetTag("langfuse.observation.type", "agent");
        activity?.SetTag("langfuse.trace.name", "damir.task");
        activity?.SetTag("damir.task.id", taskId);
        activity?.SetTag("langfuse.session.id", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            scope.OwnerId + "\0" + scope.ProjectId + "\0" + session))));
        return activity;
    }

    public static void TaskFinished(Activity? activity, string status, double seconds)
    {
        activity?.SetTag("damir.task.status", status);
        if (status == "FAILED") activity?.SetStatus(ActivityStatusCode.Error);
        Tasks.Add(1, new KeyValuePair<string, object?>("status", status));
        Duration.Record(seconds, new KeyValuePair<string, object?>("status", status));
    }
}
