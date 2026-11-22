using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AiAgent.Core;
using AiAgent.Observability;
using AiAgent.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using TaskStatus = AiAgent.Shared.TaskStatus;

var count = 0;
void Check(bool ok, string label) { if (!ok) throw new Exception("FAIL: " + label); count++; Console.WriteLine("PASS: " + label); }
void Reject(Dictionary<string, string?> values, string label)
{
    try { _ = ObservabilityOptions.FromEnvironment(key => values.GetValueOrDefault(key)); }
    catch (ArgumentException e) { Check(!e.Message.Contains("synthetic-secret"), label); return; }
    throw new Exception("Invalid configuration accepted: " + label);
}
Check(!ObservabilityOptions.FromEnvironment(_ => null).Enabled, "export is disabled by default");
Reject(new() { ["DAMIR_OBSERVABILITY_ENABLED"] = "yes" }, "enabled flag is validated");
Reject(new() { ["DAMIR_OBSERVABILITY_ENABLED"] = "1" }, "enabled export requires a destination");
Reject(new() { ["DAMIR_OBSERVABILITY_ENABLED"] = "1", ["DAMIR_OTEL_ENDPOINT"] = "http://localhost", ["DAMIR_TRACE_SAMPLE_RATIO"] = "NaN" }, "sampling rejects non-finite values");
Reject(new() { ["DAMIR_OBSERVABILITY_ENABLED"] = "1", ["DAMIR_OTEL_ENDPOINT"] = "http://localhost", ["DAMIR_TRACE_SAMPLE_RATIO"] = "1.1" }, "sampling rejects values above one");
foreach (var uri in new[] { "file:///tmp/otel", "https://user:synthetic-secret@localhost", "https://localhost/?key=synthetic-secret", "https://localhost/#synthetic-secret" })
    Reject(new() { ["DAMIR_OBSERVABILITY_ENABLED"] = "1", ["DAMIR_OTEL_ENDPOINT"] = uri }, "unsafe endpoint configuration is sanitized");
Reject(new() { ["DAMIR_OBSERVABILITY_ENABLED"] = "1", ["DAMIR_LANGFUSE_ENDPOINT"] = "http://localhost" }, "Langfuse requires both keys");
var disabled = new ServiceCollection(); disabled.AddDamirTelemetry(new());
using (var services = disabled.BuildServiceProvider()) Check(services.GetService<TracerProvider>() is null, "disabled setup registers no trace exporter");

var captures = new ConcurrentQueue<Capture>();
var sinkCalls = 0;
var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
await using var receiver = builder.Build();
receiver.MapPost("/{**path}", async (HttpContext context) =>
{
    using var body = new MemoryStream(); await context.Request.Body.CopyToAsync(body);
    captures.Enqueue(new(context.Request.Path, context.Request.ContentType ?? "", context.Request.Headers.Authorization.ToString(),
        context.Request.Headers["x-langfuse-ingestion-version"].ToString(), body.ToArray()));
    if (context.Request.Path.StartsWithSegments("/redirect"))
    { context.Response.StatusCode = 307; context.Response.Headers.Location = "/sink/v1/traces"; }
    else if (context.Request.Path.StartsWithSegments("/sink")) Interlocked.Increment(ref sinkCalls);
    else if (context.Request.Path.StartsWithSegments("/unavailable")) context.Response.StatusCode = 503;
    else context.Response.ContentType = "application/x-protobuf";
});
await receiver.StartAsync();
var address = receiver.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
var options = new ObservabilityOptions { Enabled = true, CollectorEndpoint = new(address + "/otel"), LangfuseEndpoint = new(address + "/langfuse/api/public/otel"), PublicKey = "pk-lf-synthetic", SecretKey = "sk-lf-synthetic-secret" };
Check(!options.ToString().Contains("synthetic-secret") && !JsonSerializer.Serialize(options).Contains("synthetic-secret"), "options do not print or serialize credentials");
var spans = new ConcurrentQueue<Activity>();
ActivityTraceId traceId;
var registrations = new ServiceCollection(); registrations.AddLogging(); registrations.AddDamirTelemetry(options);
registrations.ConfigureOpenTelemetryTracerProvider(traces => traces.AddProcessor(new SimpleActivityExportProcessor(new CaptureExporter(spans))));
using (var services = registrations.BuildServiceProvider())
{
    var provider = services.GetRequiredService<TracerProvider>();
    var metrics = services.GetRequiredService<MeterProvider>();
    string parent;
    using (var admission = AgentTelemetry.Source.StartActivity("http.request")!)
    {
        admission.TraceStateString = "private=synthetic-sensitive-tracestate";
        parent = AgentTelemetry.CaptureTraceParent()!; traceId = admission.TraceId;
    }
    var scope = new MemoryScope { OwnerId = "synthetic-private-owner", ProjectId = "synthetic-private-project" };
    using (var task = AgentTelemetry.StartTask("synthetic-task", "synthetic-private-session", scope, parent)!)
    {
        Check(task.TraceId == traceId && task.ParentSpanId.ToString() == parent.Split('-')[2], "worker continues saved admission context after request completion");
        var registry = new ToolRegistry(); registry.Register(new ProbeTool());
        var agent = new Agent(new ObservedLlmClient(new ProbeLlm(), "synthetic-model"), new MemoryHistory(), registry);
        var result = await agent.RunAsync("synthetic-private-session", "synthetic-private-message");
        Check(result.Status == TaskStatus.COMPLETED && result.Response == "synthetic-private-answer", "real Core and tool behavior survives telemetry");
        AgentTelemetry.TaskFinished(task, result.Status.ToString(), 0.1);
        var unknown = await registry.ExecuteAsync(new ToolCall { Id = "unknown", Name = "synthetic-private-tool", Arguments = [] });
        Check(!unknown.Success, "unknown tool still returns normal failure");
        registry.Register(new ProbeTool("filesystem.send_to_telegram"));
        _ = await registry.ExecuteAsync(new ToolCall { Id = "telegram-file", Name = "filesystem.send_to_telegram", Arguments = [] });
        try { await new ObservedLlmClient(new FailedLlm(), "synthetic-model").CompleteAsync([], []); }
        catch (InvalidOperationException) { }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await new ObservedLlmClient(new CancelledLlm(), "synthetic-model").CompleteAsync([], [], cancelled.Token); }
        catch (OperationCanceledException) { }
    }
    Check(provider.ForceFlush(10000), "trace exporter flush completes");
    Check(metrics.ForceFlush(10000), "metrics exporter flush completes");
}
var all = spans.ToArray();
var llm = all.First(a => a.OperationName == "llm.complete" && a.Status != ActivityStatusCode.Error && a.GetTagItem("damir.cancelled") is null);
var tool = all.Single(a => a.OperationName == "tool.execute" && (string?)a.GetTagItem("gen_ai.tool.name") == "filesystem.read_file");
Check(llm.TraceId == tool.TraceId && tool.TraceId == traceId, "task, model and tool share a trace");
Check(llm.GetTagItem("langfuse.observation.type") as string == "generation" && tool.GetTagItem("langfuse.observation.type") as string == "tool", "Langfuse generation and tool attributes are present");
Check(all.Any(a => a.OperationName == "tool.execute" && a.GetTagItem("gen_ai.tool.name") as string == "filesystem.send_to_telegram"), "Telegram file delivery is recognized as a built-in tool");
Check((int?)llm.GetTagItem("gen_ai.usage.input_tokens") == 4 && (int?)llm.GetTagItem("gen_ai.usage.output_tokens") == 2, "actual token usage is recorded");
Check(tool.GetTagItem("langfuse.session.id") is string hash && hash.Length == 64 && llm.GetTagItem("langfuse.session.id") as string == hash, "pseudonymous session correlation reaches child spans");
Check(all.Any(a => a.OperationName == "llm.complete" && a.Status == ActivityStatusCode.Error && string.IsNullOrEmpty(a.StatusDescription)), "provider error records status without its exception message");
Check(all.Any(a => a.GetTagItem("damir.cancelled") is true), "cancellation is distinguished from provider error");
var sent = captures.ToArray();
Check(sent.Any(c => c.Path == "/otel/v1/traces" && c.Body.Length > 0 && c.Type == "application/x-protobuf"), "collector receives real OTLP protobuf traces");
Check(sent.Any(c => c.Path == "/otel/v1/metrics" && Encoding.UTF8.GetString(c.Body).Contains("damir.llm.tokens")), "collector receives token and duration metrics");
Check(sent.Where(c => c.Path.StartsWith("/otel/", StringComparison.Ordinal)).All(c => c.Authorization == ""), "Langfuse credentials are not sent to the generic collector");
Check(sent.Any(c => c.Path == "/langfuse/api/public/otel/v1/traces" && c.Authorization == "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("pk-lf-synthetic:sk-lf-synthetic-secret")) && c.Ingestion == "4"), "Langfuse export uses its authenticated HTTP endpoint and ingestion header");
var text = string.Join("\n", sent.Select(c => Encoding.UTF8.GetString(c.Body))) + string.Join("\n", all.Select(a => a.DisplayName + a.StatusDescription + string.Join(";", a.TagObjects)));
Check(!text.Contains("synthetic-private") && !text.Contains("synthetic-secret") && !text.Contains("synthetic-sensitive-tracestate"), "payloads, raw identities, exception content, credentials and tracestate are absent");

foreach (var target in new[] { "/redirect", "/unavailable" })
{
    var services = new ServiceCollection(); services.AddLogging();
    services.AddDamirTelemetry(new ObservabilityOptions { Enabled = true, LangfuseEndpoint = new(address + target), PublicKey = "synthetic-public", SecretKey = "synthetic-secret" });
    using var provider = services.BuildServiceProvider();
    var tracer = provider.GetRequiredService<TracerProvider>();
    var model = new ObservedLlmClient(new ProbeLlm(), "synthetic-model");
    var result = await model.CompleteAsync([], []);
    Check(result.ToolCalls.Count == 1, "unavailable or redirecting exporter does not change model execution");
    _ = tracer.ForceFlush(5000);
}
Check(sinkCalls == 0, "exporter never forwards credentials through redirects");
var zero = new ServiceCollection(); zero.AddLogging();
zero.AddDamirTelemetry(new ObservabilityOptions { Enabled = true, CollectorEndpoint = new(address), SamplingRatio = 0 });
using (var services = zero.BuildServiceProvider())
{
    _ = services.GetRequiredService<TracerProvider>();
    using var unsampled = AgentTelemetry.Source.StartActivity("zero-sample");
    Check(unsampled is null || !unsampled.Recorded, "zero sampling disables recorded root traces");
}
await receiver.StopAsync();
Console.WriteLine($"Observability checks passed: {count}");

sealed record Capture(string Path, string Type, string Authorization, string Ingestion, byte[] Body);
sealed class CaptureExporter(ConcurrentQueue<Activity> spans) : BaseExporter<Activity>
{
    public override ExportResult Export(in Batch<Activity> batch) { foreach (var span in batch) spans.Enqueue(span); return ExportResult.Success; }
}
sealed class ProbeLlm : ILlmClient
{
    private int _calls;
    public Task<LlmResponse> CompleteAsync(IReadOnlyList<Message> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken = default) => Task.FromResult(++_calls == 1
        ? new LlmResponse { FinishReason = "tool_calls", Usage = new() { InputTokens = 4, OutputTokens = 2 }, ToolCalls = [new() { Id = "synthetic-call", Name = "filesystem.read_file", Arguments = new Dictionary<string, JsonElement> { ["path"] = JsonSerializer.SerializeToElement("synthetic-private-file") } }] }
        : new LlmResponse { Content = "synthetic-private-answer", FinishReason = "stop", Usage = new() { InputTokens = 4, OutputTokens = 2 } });
}
sealed class FailedLlm : ILlmClient
{
    public Task<LlmResponse> CompleteAsync(IReadOnlyList<Message> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken = default) => throw new InvalidOperationException("synthetic-private-provider-error");
}
sealed class CancelledLlm : ILlmClient
{
    public Task<LlmResponse> CompleteAsync(IReadOnlyList<Message> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken = default) => Task.FromCanceled<LlmResponse>(cancellationToken);
}
sealed class ProbeTool(string name = "filesystem.read_file") : ITool
{
    public string Name => name;
    public string Description => "synthetic-private-description";
    public IReadOnlyDictionary<string, JsonElement> Parameters => new Dictionary<string, JsonElement> { ["type"] = JsonSerializer.SerializeToElement("object") };
    public Task<ToolResult> ExecuteAsync(IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken cancellationToken = default) => Task.FromResult(new ToolResult { Success = true, Content = "synthetic-private-tool-result" });
}
sealed class MemoryHistory : IContextStore
{
    private readonly List<Message> _messages = [];
    public Task<IReadOnlyList<Message>> GetMessagesAsync(string sessionId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Message>>(_messages.ToArray());
    public Task AppendMessageAsync(string sessionId, Message message, CancellationToken cancellationToken = default) { _messages.Add(message); return Task.CompletedTask; }
    public Task ReplaceMessagesAsync(string sessionId, IReadOnlyList<Message> messages, CancellationToken cancellationToken = default) { _messages.Clear(); _messages.AddRange(messages); return Task.CompletedTask; }
}
