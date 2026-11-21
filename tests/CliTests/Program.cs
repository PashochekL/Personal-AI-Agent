using System.Net;
using System.Text;
using System.Text.Json;
using AiAgent.Cli;
using AiAgent.Shared;
using TaskStatus = AiAgent.Shared.TaskStatus;

var count = 0;
void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); count++; Console.WriteLine("PASS: " + name); }
var id = new string('a', 32);
var taskId = new string('b', 32);
const string key = "synthetic-backend-key-for-tests";
var saved = new AgentTask { TaskId = taskId, SessionId = id, UserMessage = "Привет\n/exit", Status = TaskStatus.COMPLETED,
    Result = new AgentResult { SessionId = id, Status = TaskStatus.COMPLETED, Response = "Привет из Worker" } };
var bodies = new List<string>(); var keys = new List<string>();
var submissionCalls = 0; var pollCalls = 0;
using var http = new HttpClient(new Handler(async (request, ct) =>
{
    Check(request.Headers.Authorization?.ToString() == "Bearer " + key, "authenticated request");
    var path = request.RequestUri!.AbsolutePath;
    if (path == "/tasks" && request.Method == HttpMethod.Post)
    {
        bodies.Add(await request.Content!.ReadAsStringAsync(ct));
        keys.Add(request.Headers.GetValues("Idempotency-Key").Single());
        if (submissionCalls++ == 0) throw new HttpRequestException("secret-provider-response"); // Accepted, receipt lost.
        return Reply(ContractJson.Serialize(saved));
    }
    if (path == "/tasks/" + taskId)
    {
        pollCalls++;
        if (pollCalls == 1) return Reply(ContractJson.Serialize(new AgentTask { TaskId = taskId, SessionId = id, UserMessage = saved.UserMessage }));
        return Reply(ContractJson.Serialize(saved));
    }
    if (path.EndsWith("/cancel")) return Reply(ContractJson.Serialize(new AgentTask { TaskId = taskId, SessionId = id, UserMessage = saved.UserMessage, Status = TaskStatus.CANCELLED }));
    if (path == "/console") return Reply("{\"default_model\":\"synthetic-chat\",\"models\":[\"synthetic-chat\"],\"skills\":[{\"name\":\"test-skill\",\"description\":\"test\"}],\"github\":true}");
    if (path.EndsWith("/usage")) return Reply("{\"total\":123,\"missing_usage_responses\":2}");
    if (path.EndsWith("/latest")) return new HttpResponseMessage(HttpStatusCode.NoContent);
    return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("secret-backend-body") };
}));
var client = new BackendClient(http, new Uri("http://synthetic-backend/"), key);
var submission = new Submission(id, saved.UserMessage, "synthetic-chat", "test-skill", RequestKey: "stable-request-key");
try { await client.SubmitAsync(submission, default); throw new Exception("Expected lost response"); }
catch (BackendException e) { Check(!e.Message.Contains("secret"), "connection failures do not expose server data"); }
var accepted = await client.SubmitAsync(submission, default);
Check(accepted.TaskId == taskId && bodies.Count == 2 && bodies[0] == bodies[1] && keys.Distinct().Single() == "stable-request-key", "lost receipt retry preserves payload and idempotency key");
using (var body = JsonDocument.Parse(bodies[0]))
    Check(body.RootElement.GetProperty("user_message").GetString() == saved.UserMessage && body.RootElement.GetProperty("skill").GetString() == "test-skill", "Unicode multiline message and manual skill survive transport");
Check((await client.WaitAsync(taskId, default)).Result?.Response == "Привет из Worker" && pollCalls == 2, "pending tasks polled through terminal result");
Check((await client.CancelAsync(taskId, default)).Status == TaskStatus.CANCELLED, "cancellation goes to server task");
var catalog = await client.CatalogAsync(default);
Check(catalog.DefaultModel == "synthetic-chat" && catalog.Skills[0].Name == "test-skill" && catalog.Github, "console catalog read from backend");
var usage = await client.UsageAsync(id, default);
Check(usage.Total == 123 && usage.MissingUsageResponses == 2, "incomplete usage remains distinguishable");
Check(await client.LatestAsync(id, default) is null, "empty session has no receipt");
try { await client.ReadyAsync(default); throw new Exception("Expected unauthorized"); }
catch (BackendException e) { Check(e.Status == HttpStatusCode.Unauthorized && !e.Message.Contains("secret"), "authentication error sanitized"); }
try { await client.TaskAsync("../console", default); throw new Exception("Expected invalid ID"); }
catch (ArgumentException) { Check(true, "IDs cannot alter route"); }
using var cancel = new CancellationTokenSource(); cancel.Cancel();
try { await client.WaitAsync(taskId, cancel.Token); throw new Exception("Expected cancellation"); }
catch (OperationCanceledException) { Check(true, "polling responds to cancellation"); }
Console.WriteLine($"CLI checks passed: {count}");

static HttpResponseMessage Reply(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
}
