using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace AiAgent.Core;

// Core emits standard diagnostics; exporter configuration belongs to infrastructure.
internal static class CoreDiagnostics
{
    private static readonly ActivitySource Source = new("AiAgent.Core");
    private static readonly Meter Meter = new("AiAgent.Core");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("damir.tool.duration", "s");
    private static readonly Counter<long> Completed = Meter.CreateCounter<long>("damir.tool.completed", "{call}");
    private static readonly HashSet<string> Builtins = new(StringComparer.Ordinal)
    {
        "filesystem.read_file", "filesystem.write_file", "filesystem.edit_file", "filesystem.list_directory", "filesystem.send_to_telegram",
        "shell.execute", "web.search", "memory.save", "memory.search", "memory.list", "memory.forget", "skills.list", "skills.load"
    };

    public sealed class ToolObservation : IDisposable
    {
        private readonly string _name;
        private readonly long _start = Stopwatch.GetTimestamp();
        private readonly Activity? _activity;
        private string _status = "failed";
        public ToolObservation(string? registeredName)
        {
            _name = registeredName is null ? "unknown" : Builtins.Contains(registeredName) ? registeredName : registeredName.StartsWith("mcp.", StringComparison.Ordinal) ? "mcp" : "custom";
            _activity = Source.StartActivity("tool.execute");
            _activity?.SetTag("langfuse.observation.type", "tool");
            _activity?.SetTag("gen_ai.tool.name", _name);
        }
        public void Complete(bool success) => _status = success ? "completed" : "failed";
        public void Cancel() => _status = "cancelled";
        public void Dispose()
        {
            _activity?.SetTag("damir.tool.status", _status);
            if (_status == "failed") _activity?.SetStatus(ActivityStatusCode.Error);
            var tags = new TagList { { "tool", _name }, { "status", _status } };
            Duration.Record(Stopwatch.GetElapsedTime(_start).TotalSeconds, tags);
            Completed.Add(1, tags);
            _activity?.Dispose();
        }
    }
}
