using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AiAgent.Api;
using AiAgent.Observability;
using AiAgent.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using TaskStatus = AiAgent.Shared.TaskStatus;

static class TelemetryChecks
{
    public static async Task<int> RunAsync(BackendOptions options)
    {
        var count = 0;
        void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); count++; Console.WriteLine("PASS: " + label); }
        var spans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener { ShouldListenTo = source => source.Name is AgentTelemetry.SourceName or AgentTelemetry.CoreSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded, ActivityStopped = spans.Enqueue };
        ActivitySource.AddActivityListener(listener);
        var receiveCount = 0;
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        await using var receiver = builder.Build();
        receiver.MapPost("/{**path}", async (HttpContext context) => { await context.Request.Body.CopyToAsync(Stream.Null); Interlocked.Increment(ref receiveCount); context.Response.ContentType = "application/x-protobuf"; });
        await receiver.StartAsync();
        var settings = new Dictionary<string, string?> { ["DAMIR_OBSERVABILITY_ENABLED"] = "1", ["DAMIR_OTEL_ENDPOINT"] = Address(receiver),
            ["DAMIR_LANGFUSE_ENDPOINT"] = null, ["LANGFUSE_PUBLIC_KEY"] = null, ["LANGFUSE_SECRET_KEY"] = null, ["DAMIR_TRACE_SAMPLE_RATIO"] = "1" };
        var previous = settings.ToDictionary(p => p.Key, p => Environment.GetEnvironmentVariable(p.Key));
        WebApplication? app = null;
        NpgsqlConnection? lease = null;
        try
        {
            foreach (var item in settings) Environment.SetEnvironmentVariable(item.Key, item.Value);
            app = await BackendApplication.BuildAsync(options, new TelemetryProbe()); await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new(Address(app)) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
            using var sessionResponse = await client.PostAsync("/sessions/named/synthetic-private-session-name?key=synthetic-private-query", null);
            sessionResponse.EnsureSuccessStatusCode();
            var session = ContractJson.Deserialize<Session>(await sessionResponse.Content.ReadAsStringAsync());
            var store = app.Services.GetRequiredService<PostgresTaskStore>();
            lease = await store.TrySessionLeaseAsync(session.SessionId, default);
            Check(lease is not null, "telemetry restart test holds its session lease");
            using var request = new HttpRequestMessage(HttpMethod.Post, "/tasks") { Content = JsonContent.Create(new { session_id = session.SessionId, user_message = "synthetic-private-request-body" }) };
            request.Headers.Add("Idempotency-Key", "synthetic-private-request-key");
            using var response = await client.SendAsync(request); response.EnsureSuccessStatusCode();
            var task = ContractJson.Deserialize<AgentTask>(await response.Content.ReadAsStringAsync());
            var traceParent = await store.TraceParentAsync(task.TaskId, default);
            Check(traceParent is { Length: 55 } && spans.Any(a => a.OperationName == "http.request" && a.Id == traceParent), "admission persists the request's trace context in PostgreSQL");
            Check((await store.GetAsync(task.TaskId))?.Status == TaskStatus.PENDING, "telemetry does not bypass the durable queue or session lease");
            await using var db = NpgsqlDataSource.Create(options.Database);
            var foreign = new PostgresTaskStore(db, new BackendOptions { Database = options.Database, Redis = options.Redis, ApiKey = options.ApiKey,
                Scope = new() { OwnerId = "synthetic-private-foreign", ProjectId = options.Scope.ProjectId } });
            Check(await foreign.TraceParentAsync(task.TaskId, default) is null, "trace context lookup respects owner/project isolation");
            await app.StopAsync(); await app.DisposeAsync(); app = null;
            await lease!.DisposeAsync(); lease = null;
            app = await BackendApplication.BuildAsync(options, new TelemetryProbe()); await app.StartAsync();
            using var resumed = new HttpClient { BaseAddress = new(Address(app)) };
            resumed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
            AgentTask done;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            do
            {
                using var result = await resumed.GetAsync("/tasks/" + task.TaskId, deadline.Token); result.EnsureSuccessStatusCode();
                done = ContractJson.Deserialize<AgentTask>(await result.Content.ReadAsStringAsync(deadline.Token));
                if (done.Status is TaskStatus.PENDING or TaskStatus.RUNNING) await Task.Delay(30, deadline.Token);
            } while (done.Status is TaskStatus.PENDING or TaskStatus.RUNNING);
            Check(done.Status == TaskStatus.COMPLETED, "pending task completes after backend restart with telemetry enabled");
            using var replay = new HttpRequestMessage(HttpMethod.Post, "/tasks") { Content = JsonContent.Create(new { session_id = session.SessionId, user_message = "synthetic-private-request-body" }) };
            replay.Headers.Add("Idempotency-Key", "synthetic-private-request-key");
            using var replayResponse = await resumed.SendAsync(replay); replayResponse.EnsureSuccessStatusCode();
            Check(await app.Services.GetRequiredService<PostgresTaskStore>().TraceParentAsync(task.TaskId, default) == traceParent, "idempotent replay preserves the original trace parent");
            await app.StopAsync(); await app.DisposeAsync(); app = null;
            var execution = spans.Single(a => a.OperationName == "task.execute" && a.GetTagItem("damir.task.id") as string == task.TaskId);
            Check(execution.ParentId == traceParent && execution.GetTagItem("damir.task.status") as string == "COMPLETED", "worker resumes the same trace after process lifecycle restart");
            Check(spans.Any(a => a.GetTagItem("http.route") as string == "/sessions/named/{name}"), "HTTP telemetry contains route templates");
            var exported = string.Join("\n", spans.Select(a => a.DisplayName + a.StatusDescription + string.Join(';', a.TagObjects)));
            Check(!exported.Contains("synthetic-private") && !exported.Contains(options.ApiKey), "HTTP and worker spans exclude messages, query, headers, session names and credentials");
            Check(receiveCount > 0, "production backend exports OTLP over HTTP to the test receiver");
            // Run the merged Telegram and real Core composition with production exporters enabled.
            count += await WorkspaceHandoffChecks.RunAsync(options);
            count += await CompositionChecks.RunAsync(options);
            var agent = spans.First(a => a.OperationName == "agent.run");
            Check(spans.Any(a => a.OperationName == "task.execute" && a.TraceId == agent.TraceId && a.SpanId == agent.ParentSpanId),
                "production Core execution is a child of its durable worker task");
            Check(spans.Any(a => a.OperationName == "llm.complete" && a.TraceId == agent.TraceId && a.ParentSpanId == agent.SpanId),
                "production gateway calls remain inside the agent trace");
            Check(spans.Any(a => a.OperationName == "tool.execute" && a.TraceId == agent.TraceId && a.ParentSpanId == agent.SpanId && a.GetTagItem("gen_ai.tool.name") as string == "memory.save"),
                "production tool execution shares the task and model trace with Telegram telemetry enabled");
        }
        finally
        {
            if (lease is not null) await lease.DisposeAsync();
            if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
            foreach (var item in previous) Environment.SetEnvironmentVariable(item.Key, item.Value);
            await receiver.StopAsync();
        }
        return count;
    }
    private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    private sealed class TelemetryProbe : IBackendAgentFactory, IAgent
    {
        public Task<IAgent> CreateAsync(CancellationToken cancellationToken) => Task.FromResult<IAgent>(this);
        public Task<AgentResult> RunAsync(string sessionId, string userMessage, CancellationToken cancellationToken = default) => Task.FromResult(new AgentResult { SessionId = sessionId, Status = TaskStatus.COMPLETED, Response = "synthetic-private-answer" });
    }
}
