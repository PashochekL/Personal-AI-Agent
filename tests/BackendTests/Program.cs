using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using AiAgent.Api;
using AiAgent.Memory;
using AiAgent.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using StackExchange.Redis;
using TaskStatus = AiAgent.Shared.TaskStatus;

var database = Environment.GetEnvironmentVariable("DAMIR_BACKEND_TEST_DATABASE_URL");
var redisAddress = Environment.GetEnvironmentVariable("DAMIR_BACKEND_TEST_REDIS");
if (string.IsNullOrWhiteSpace(database) || string.IsNullOrWhiteSpace(redisAddress))
{ Console.WriteLine("Backend integration checks skipped: set DAMIR_BACKEND_TEST_DATABASE_URL and DAMIR_BACKEND_TEST_REDIS, or use run-integration.ps1."); return 0; }
var scope = new MemoryScope { OwnerId = "backend-test-" + Guid.NewGuid().ToString("N"), ProjectId = "synthetic" };
var options = new BackendOptions { Database = database, Redis = redisAddress, ApiKey = Guid.NewGuid().ToString("N"),
    Scope = scope, Url = "http://127.0.0.1:0", PollInterval = TimeSpan.FromMilliseconds(40), TaskTimeout = TimeSpan.FromSeconds(2) };
await using var db = NpgsqlDataSource.Create(database);
var history = new PostgresContextStore(db);
var factory = new ProbeAgent(history);
var count = 0;
void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); count++; Console.WriteLine("PASS: " + name); }
static HttpClient Client(WebApplication app, string? key)
{
    var server = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>();
    var address = server.Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(10) };
    if (key is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    return client;
}
static async Task<AgentTask> TaskBody(HttpResponseMessage response)
{
    response.EnsureSuccessStatusCode(); return ContractJson.Deserialize<AgentTask>(await response.Content.ReadAsStringAsync());
}
static async Task<AgentTask> Submit(HttpClient client, string session, string message, string? key = null)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, "/tasks") { Content = JsonContent.Create(new { session_id = session, user_message = message }) };
    if (key is not null) request.Headers.Add("Idempotency-Key", key);
    using var response = await client.SendAsync(request); return await TaskBody(response);
}
static async Task<string> Session(HttpClient client)
{
    using var response = await client.PostAsync("/sessions", null); response.EnsureSuccessStatusCode();
    return ContractJson.Deserialize<Session>(await response.Content.ReadAsStringAsync()).SessionId;
}
static async Task<AgentTask> Wait(HttpClient client, string taskId, Func<AgentTask, bool>? condition = null)
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
    while (true)
    {
        using var response = await client.GetAsync("/tasks/" + taskId, deadline.Token);
        var task = await TaskBody(response);
        if (condition?.Invoke(task) ?? task.Status is TaskStatus.COMPLETED or TaskStatus.FAILED or TaskStatus.CANCELLED) return task;
        await Task.Delay(30, deadline.Token);
    }
}

WebApplication? app = null;
try
{
    app = await BackendApplication.BuildAsync(options, factory); await app.StartAsync();
    using var client = Client(app, options.ApiKey);
    using var unauthorized = Client(app, null);
    Check((await unauthorized.GetAsync("/health/live")).StatusCode == HttpStatusCode.OK, "public liveness");
    Check((await unauthorized.PostAsync("/sessions", null)).StatusCode == HttpStatusCode.Unauthorized, "authentication required");
    using var wrong = Client(app, new string('x', 32));
    Check((await wrong.GetAsync("/health/ready")).StatusCode == HttpStatusCode.Unauthorized, "wrong key rejected");
    Check((await client.GetAsync("/health/ready")).StatusCode == HttpStatusCode.OK, "database and Redis readiness");
    var session = await Session(client);
    Check((await client.GetAsync("/sessions/" + session)).StatusCode == HttpStatusCode.OK, "create and retrieve session");
    Check((await client.PostAsJsonAsync("/tasks", new { session_id = "missing", user_message = "x" })).StatusCode == HttpStatusCode.NotFound, "unknown session rejected");
    Check((await client.PostAsJsonAsync("/tasks", new { session_id = session, user_message = " ", owner_id = "untrusted" })).StatusCode == HttpStatusCode.BadRequest, "unknown request fields rejected");
    Check((await client.PostAsJsonAsync("/tasks", new { session_id = session, user_message = " " })).StatusCode == HttpStatusCode.BadRequest, "empty message rejected");
    Check((await client.PostAsJsonAsync("/tasks", new { session_id = session, user_message = new string('x', 32001) })).StatusCode == HttpStatusCode.BadRequest, "message limit enforced");
    Check((await client.PostAsync("/tasks", new StringContent("{", Encoding.UTF8, "application/json"))).StatusCode == HttpStatusCode.BadRequest, "malformed JSON rejected");
    Check((await client.PostAsJsonAsync("/tasks", new { session_id = session })).StatusCode == HttpStatusCode.BadRequest, "missing required field rejected");
    var first = await Submit(client, session, "hello", "first");
    var done = await Wait(client, first.TaskId);
    Check(done.Status == TaskStatus.COMPLETED && done.Result?.Response == "answer:hello", "API through Redis Worker returns result");
    var same = await Submit(client, session, "hello", "first");
    Check(same.TaskId == first.TaskId && factory.Calls["hello"] == 1, "idempotency returns existing execution");
    using (var conflict = new HttpRequestMessage(HttpMethod.Post, "/tasks") { Content = JsonContent.Create(new { session_id = session, user_message = "different" }) })
    { conflict.Headers.Add("Idempotency-Key", "first"); Check((await client.SendAsync(conflict)).StatusCode == HttpStatusCode.Conflict, "idempotency payload conflict rejected"); }
    var messages = await client.GetStringAsync("/sessions/" + session + "/messages");
    Check(messages.Contains("answer:hello") && messages.Contains("user"), "common persistent history exposed");
    await client.PostAsync("/tasks/" + first.TaskId + "/cancel", null);
    Check((await Wait(client, first.TaskId)).Status == TaskStatus.COMPLETED, "cancel cannot overwrite completed result");

    var hold = await Submit(client, session, "hold"); await Wait(client, hold.TaskId, t => t.Status == TaskStatus.RUNNING);
    var pending = await Submit(client, session, "must-not-run");
    Check((await TaskBody(await client.PostAsync("/tasks/" + pending.TaskId + "/cancel", null))).Status == TaskStatus.CANCELLED, "pending cancellation is immediate");
    var after = await Submit(client, session, "after");
    await Task.Delay(150);
    Check((await TaskBody(await client.GetAsync("/tasks/" + after.TaskId))).Status == TaskStatus.PENDING, "one session executes serially");
    var otherSession = await Session(client);
    var parallel = await Submit(client, otherSession, "parallel");
    Check((await Wait(client, parallel.TaskId)).Status == TaskStatus.COMPLETED, "different sessions execute concurrently");
    await client.PostAsync("/tasks/" + hold.TaskId + "/cancel", null);
    Check((await Wait(client, hold.TaskId)).Status == TaskStatus.CANCELLED, "running cancellation reaches IAgent");
    Check((await Wait(client, after.TaskId)).Status == TaskStatus.COMPLETED && !factory.Calls.ContainsKey("must-not-run"), "queue continues after cancellation");
    Check(factory.MaximumPerSession == 1, "no overlapping writes in one session");

    var failure = await Submit(client, session, "fail");
    var failed = await Wait(client, failure.TaskId);
    Check(failed.Status == TaskStatus.FAILED && !failed.Result!.Error!.Contains("secret"), "agent exception sanitized and persisted");
    var timed = await Submit(client, session, "timeout");
    Check((await Wait(client, timed.TaskId)).Result?.Error?.Contains("timed out") == true, "task deadline enforced");

    var foreignOptions = new BackendOptions { Database = database, Redis = redisAddress, ApiKey = options.ApiKey,
        Scope = new MemoryScope { OwnerId = scope.OwnerId, ProjectId = "other-project" } };
    var foreign = new PostgresTaskStore(db, foreignOptions);
    Check(!await foreign.HasSessionAsync(session) && await foreign.GetAsync(first.TaskId) is null && await foreign.CancelAsync(first.TaskId) is null, "task and history scope isolation");
    Check(await foreign.LatestAsync(session, default) is null, "latest task receipt respects project scope");
    var ownNamed = await app.Services.GetRequiredService<PostgresTaskStore>().ResolveSessionAsync("same-name", default);
    var foreignNamed = await foreign.ResolveSessionAsync("same-name", default);
    Check(ownNamed.SessionId != foreignNamed.SessionId, "named conversations are isolated by owner/project");
    var store = app.Services.GetRequiredService<PostgresTaskStore>();
    count += await CompanionChecks.RunAsync(client, options, store, db);
    await using (var duplicate = await store.TryWorkerLeaseAsync(default)) Check(duplicate is null, "second Worker excluded by PostgreSQL lease");

    // Stop cleanly, inject the exact persisted states left by an abrupt process death, then start a fresh host.
    var graceful = await Submit(client, session, "shutdown"); await Wait(client, graceful.TaskId, t => t.Status == TaskStatus.RUNNING);
    await app.StopAsync();
    Check((await store.GetAsync(graceful.TaskId))!.Status == TaskStatus.CANCELLED, "graceful stop cancels active execution");
    var crashed = await store.SubmitAsync(new TaskRequest { SessionId = session, UserMessage = "crashed-never-replay" }, null);
    var claimed = await store.ClaimAsync(session, default);
    Check(claimed?.TaskId == crashed.TaskId, "crash fixture contains RUNNING task");
    var waiting = await store.SubmitAsync(new TaskRequest { SessionId = session, UserMessage = "resume-pending" }, null);
    await app.DisposeAsync(); app = null;
    app = await BackendApplication.BuildAsync(options, factory); await app.StartAsync();
    using var resumed = Client(app, options.ApiKey);
    Check((await Wait(resumed, crashed.TaskId)).Status == TaskStatus.FAILED && !factory.Calls.ContainsKey("crashed-never-replay"), "interrupted RUNNING task fails without replay");
    Check((await Wait(resumed, waiting.TaskId)).Status == TaskStatus.COMPLETED, "durable PENDING task resumes after restart");

    // Redis notification loss must not lose an accepted task. A foreign session lease blocks claiming while the queue is cleared.
    var repairSession = await Session(resumed);
    var nextStore = app.Services.GetRequiredService<PostgresTaskStore>();
    var sessionLock = await nextStore.TrySessionLeaseAsync(repairSession, default);
    Check(sessionLock is not null, "shared session lease acquired");
    try
    {
        var repair = await Submit(resumed, repairSession, "redis-repair");
        using var redis = await ConnectionMultiplexer.ConnectAsync(redisAddress);
        var endpoint = redis.GetEndPoints().Single();
        var prefix = "damir:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(
            scope.OwnerId.Length + ":" + scope.OwnerId + scope.ProjectId))).ToLowerInvariant();
        foreach (var key in redis.GetServer(endpoint).Keys(pattern: prefix + ":*")) await redis.GetDatabase().KeyDeleteAsync(key);
        await sessionLock!.DisposeAsync(); sessionLock = null;
        Check((await Wait(resumed, repair.TaskId)).Status == TaskStatus.COMPLETED && factory.Calls["redis-repair"] == 1, "SQL reconciliation repairs lost Redis notification");
    }
    finally { if (sessionLock is not null) await sessionLock.DisposeAsync(); }

    await app.StopAsync(); await app.DisposeAsync(); app = null;
    var offlineOptions = new BackendOptions { Database = database, Redis = "127.0.0.1:1,connectRetry=0", ApiKey = options.ApiKey,
        Scope = scope, Url = options.Url, PollInterval = options.PollInterval };
    app = await BackendApplication.BuildAsync(offlineOptions, factory); await app.StartAsync();
    using var offline = Client(app, options.ApiKey);
    var durable = await Submit(offline, session, "redis-was-offline", "offline-submit");
    Check(durable.Status == TaskStatus.PENDING, "submission stays durable while Redis unavailable");
    Check((await offline.GetAsync("/health/ready")).StatusCode == HttpStatusCode.ServiceUnavailable, "readiness reports Redis outage");
    await app.StopAsync(); await app.DisposeAsync(); app = null;
    app = await BackendApplication.BuildAsync(options, factory); await app.StartAsync();
    using var recovered = Client(app, options.ApiKey);
    Check((await Wait(recovered, durable.TaskId)).Status == TaskStatus.COMPLETED && factory.Calls["redis-was-offline"] == 1, "pending task executes once after Redis reconnect");

    await app.StopAsync(); await app.DisposeAsync(); app = null;
    count += await TelemetryChecks.RunAsync(options);
    Console.WriteLine($"Backend checks passed: {count}");
    return 0;
}
finally
{
    if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
    foreach (var table in new[] { "damir_telegram_files", "damir_telegram_companion", "damir_console_resume", "damir_demo_summaries", "damir_demo_context", "damir_demo_history", "damir_api_tasks", "damir_api_sessions" })
    {
        await using var cleanup = db.CreateCommand($"DELETE FROM {table} WHERE session_id IN (SELECT session_id FROM damir_api_sessions WHERE owner_id=$1 AND project_id=$2)");
        cleanup.Parameters.AddWithValue(scope.OwnerId); cleanup.Parameters.AddWithValue(scope.ProjectId); await cleanup.ExecuteNonQueryAsync();
    }
}

sealed class ProbeAgent(PostgresContextStore history) : IBackendAgentFactory, IAgent
{
    public ConcurrentDictionary<string, int> Calls { get; } = new();
    private readonly ConcurrentDictionary<string, int> _active = new();
    private int _maximum;
    public int MaximumPerSession => _maximum;
    public Task<IAgent> CreateAsync(CancellationToken cancellationToken) => Task.FromResult<IAgent>(this);
    public async Task<AgentResult> RunAsync(string sessionId, string userMessage, CancellationToken cancellationToken = default)
    {
        Calls.AddOrUpdate(userMessage, 1, (_, value) => value + 1);
        var active = _active.AddOrUpdate(sessionId, 1, (_, value) => value + 1);
        InterlockedExtensions.Max(ref _maximum, active);
        try
        {
            if (userMessage == "fail") throw new InvalidOperationException("secret-provider-body");
            if (userMessage is "hold" or "timeout" or "shutdown") await Task.Delay(Timeout.Infinite, cancellationToken);
            await history.AppendMessageAsync(sessionId, new Message { Role = "user", Content = userMessage }, cancellationToken);
            await history.AppendMessageAsync(sessionId, new Message { Role = "assistant", Content = "answer:" + userMessage }, cancellationToken);
            return new AgentResult { SessionId = sessionId, Status = TaskStatus.COMPLETED, Response = "answer:" + userMessage };
        }
        finally { _active.AddOrUpdate(sessionId, 0, (_, value) => value - 1); }
    }
}

static class InterlockedExtensions
{
    public static void Max(ref int target, int value)
    {
        var previous = Volatile.Read(ref target);
        while (previous < value) { var observed = Interlocked.CompareExchange(ref target, value, previous); if (observed == previous) return; previous = observed; }
    }
}
