using System.Text.Json;
using AiAgent.Core;
using AiAgent.Shared;
using Status = AiAgent.Shared.TaskStatus;

var checks = 0;
void Check(bool condition, string name) { checks++; if (!condition) throw new Exception($"Failed: {name}"); }
ToolCall Call(string id, string name = "echo", string arguments = "{\"text\":\"hello\"}") => new()
{ Id = id, Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments)! };
ToolRegistry Registry() { var registry = new ToolRegistry(); registry.Register(new Echo()); return registry; }

var store = new Store();
var model = new Model(new LlmResponse { ToolCalls = [Call("1"), Call("2", arguments: "{\"text\":\"bye\"}")] }, new LlmResponse { Content = "done" });
var result = await new Agent(model, store, Registry()).RunAsync("s", "hello");
Check(result.Status == Status.COMPLETED && result.Response == "done", "tool cycle result");
Check(store.Messages.Select(m => m.Role).SequenceEqual(["user", "assistant", "tool", "tool", "assistant"]), "history order");
Check(store.Messages[2].ToolCallId == "1" && store.Messages[3].Content == "bye", "tool correlation");
Check(model.Requests[1].Count == 4, "tool results returned to model");

store = new Store();
model = new Model(new LlmResponse { ToolCalls = [Call("persona")] }, new LlmResponse { Content = "done" }, new LlmResponse { Content = "again" });
var personaAgent = new Agent(model, store, Registry(), systemPrompt: "You are Damir");
await personaAgent.RunAsync("s", "first");
await personaAgent.RunAsync("s", "second");
Check(model.Requests.All(r => r[0].Role == "system" && r[0].Content == "You are Damir") && store.Messages.All(m => m.Role != "system"), "system prompt sent every call without polluting history");

var fakeClock = new AdjustableClock { Now = new DateTimeOffset(2026, 11, 20, 16, 59, 59, TimeSpan.Zero) };
var zone = TimeZoneInfo.CreateCustomTimeZone("Test/Tomsk", TimeSpan.FromHours(7), "Test", "Test");
var timeContext = new CurrentTimeContext(fakeClock, zone);
store = new Store();
model = new Model(new LlmResponse { Content = "first" }, new LlmResponse { Content = "second" });
var timedAgent = new Agent(model, store, Registry(), systemPrompt: "Damir", systemContext: timeContext.GetContext);
await timedAgent.RunAsync("s", "first");
fakeClock.Now = fakeClock.Now.AddSeconds(2);
await timedAgent.RunAsync("s", "second");
Check(model.Requests[0][0].Content!.Contains("2026-11-20 23:59:59 +07:00") && model.Requests[1][0].Content!.Contains("2026-11-21 00:00:01 +07:00"), "time updates across requests and uses configured time zone");
Check(model.Requests[1][0].Content!.Contains("Test/Tomsk") && store.Messages.All(m => m.Role != "system"), "time context reports zone and stays out of history");

store = new Store();
model = new Model(new LlmResponse { ToolCalls = [Call("1", "missing"), Call("2", arguments: "{\"text\":1}")] }, new LlmResponse { Content = "failed" });
result = await new Agent(model, store, Registry()).RunAsync("s", "go");
Check(result.Status == Status.COMPLETED && store.Messages[2].Content!.Contains("Unknown tool") && store.Messages[3].Content!.Contains("Invalid tool arguments"), "tool failures returned");

store = new Store();
result = await new Agent(new Model(new LlmResponse { ToolCalls = [Call("1")] }), store, Registry(), 1).RunAsync("s", "go");
Check(result.Status == Status.FAILED && result.Error!.Contains("limit") && store.Messages[^1].Role == "tool", "iteration limit");
result = await new Agent(new Model(new LlmResponse { ToolCalls = [Call("1"), Call("1")] }), new Store(), Registry()).RunAsync("s", "go");
Check(result.Status == Status.FAILED && result.Error!.Contains("Duplicate"), "duplicate calls");
store = new Store();
store.Messages.Add(new Message { Role = "assistant", ToolCalls = [Call("1")] });
result = await new Agent(new Model(), store, Registry()).RunAsync("s", "go");
Check(result.Status == Status.FAILED && store.Messages.Count == 1, "incomplete history");
result = await new Agent(new Model(new LlmResponse { Content = "partial", FinishReason = "length" }), new Store(), Registry()).RunAsync("s", "go");
Check(result.Status == Status.FAILED, "truncated response");
var registry = Registry();
try { registry.Register(new Echo()); throw new Exception("duplicate tool accepted"); } catch (ArgumentException) { checks++; }
Check((await registry.ExecuteAsync(Call("x", arguments: "{}"))).Success == false, "required argument");
Check((await registry.ExecuteAsync(Call("x", arguments: "{\"text\":\"ok\",\"extra\":true}"))).Success == false, "additional properties");
registry = new ToolRegistry();
registry.Register(new BrokenTool());
Check((await registry.ExecuteAsync(Call("1", "broken"))).Error == "Tool execution failed", "tool exception");
result = await new Agent(new BrokenModel(), new Store(), Registry()).RunAsync("s", "go");
Check(result.Status == Status.FAILED && !result.Error!.Contains("secret"), "gateway failure");

using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    store = new Store();
    result = await new Agent(new Model(), store, Registry()).RunAsync("s", "go", cancelled.Token);
    Check(result.Status == Status.CANCELLED && store.Messages.Count == 0, "cancel before start");
}

using (var cancelled = new CancellationTokenSource())
{
    var slow = new CancellableTool();
    registry = new ToolRegistry();
    registry.Register(slow);
    store = new Store();
    var run = new Agent(new Model(new LlmResponse { ToolCalls = [Call("1", "slow"), Call("2")] }), store, registry)
        .RunAsync("s", "go", cancelled.Token);
    await slow.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    cancelled.Cancel();
    result = await run.WaitAsync(TimeSpan.FromSeconds(5));
    Check(result.Status == Status.CANCELLED && slow.Stopped && store.Messages.Count == 2,
        "cancel reaches running tool and prevents subsequent calls");
}

registry = new ToolRegistry();
registry.Register(new UnexpectedCancellationTool());
result = await new Agent(new Model(new LlmResponse { ToolCalls = [Call("1", "unexpected")] }), new Store(), registry).RunAsync("s", "go");
Check(result.Status == Status.FAILED, "unrelated cancellation is not reported as user cancellation");

using (var cancelled = new CancellationTokenSource())
{
    var waiting = new WaitingModel();
    var run = new Agent(waiting, new Store(), Registry()).RunAsync("s", "go", cancelled.Token);
    await waiting.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    cancelled.Cancel();
    result = await run.WaitAsync(TimeSpan.FromSeconds(5));
    Check(result.Status == Status.CANCELLED && waiting.Stopped, "cancel reaches model");
}
using (var cancelled = new CancellationTokenSource())
{
    var waiting = new WaitingStore();
    var run = new Agent(new Model(), waiting, Registry()).RunAsync("s", "go", cancelled.Token);
    await waiting.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    cancelled.Cancel();
    result = await run.WaitAsync(TimeSpan.FromSeconds(5));
    Check(result.Status == Status.CANCELLED && waiting.Stopped, "cancel reaches store");
}
Console.WriteLine($"{checks} core checks passed");
Console.WriteLine($"{await FileSystemChecks.RunAsync()} filesystem checks passed");
Console.WriteLine($"{await InstructionChecks.RunAsync()} instruction checks passed");
Console.WriteLine($"{await GatewayChecks.RunAsync()} gateway checks passed");

sealed class Echo : ITool
{
    public string Name => "echo";
    public string Description => "Echo text";
    public IReadOnlyDictionary<string, JsonElement> Parameters { get; } = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"type":"object","properties":{"text":{"type":"string"}},"required":["text"],"additionalProperties":false}""")!;
    public Task<ToolResult> ExecuteAsync(IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken cancellationToken = default) => Task.FromResult(new ToolResult { Success = true, Content = arguments["text"].GetString()! });
}
sealed class AdjustableClock : TimeProvider
{
    public DateTimeOffset Now { get; set; }
    public override DateTimeOffset GetUtcNow() => Now;
}
sealed class Store : IContextStore
{
    public List<Message> Messages { get; } = [];
    public Task<IReadOnlyList<Message>> GetMessagesAsync(string sessionId, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult<IReadOnlyList<Message>>(Messages.ToArray()); }
    public Task AppendMessageAsync(string sessionId, Message message, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); Messages.Add(message); return Task.CompletedTask; }
    public Task ReplaceMessagesAsync(string sessionId, IReadOnlyList<Message> messages, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); Messages.Clear(); Messages.AddRange(messages); return Task.CompletedTask; }
}
sealed class Model(params LlmResponse[] responses) : ILlmClient
{
    private readonly Queue<LlmResponse> _responses = new(responses);
    public List<IReadOnlyList<Message>> Requests { get; } = [];
    public Task<LlmResponse> CompleteAsync(IReadOnlyList<Message> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken = default)
    { Requests.Add(messages); return Task.FromResult(_responses.Dequeue()); }
}
sealed class BrokenModel : ILlmClient
{
    public Task<LlmResponse> CompleteAsync(IReadOnlyList<Message> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken = default) => throw new AgentError("secret");
}
sealed class BrokenTool : ITool
{
    public string Name => "broken";
    public string Description => "Throws an error";
    public IReadOnlyDictionary<string, JsonElement> Parameters { get; } = new Echo().Parameters;
    public Task<ToolResult> ExecuteAsync(IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken cancellationToken = default) => throw new InvalidOperationException("secret");
}
sealed class CancellableTool : ITool
{
    public string Name => "slow";
    public string Description => "Waits for cancellation";
    public IReadOnlyDictionary<string, JsonElement> Parameters { get; } = new Echo().Parameters;
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Stopped { get; private set; }
    public async Task<ToolResult> ExecuteAsync(IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken cancellationToken = default)
    {
        Started.SetResult();
        try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
        finally { Stopped = true; }
        return new ToolResult { Success = true, Content = "done" };
    }
}
sealed class UnexpectedCancellationTool : ITool
{
    public string Name => "unexpected";
    public string Description => "Cancels independently";
    public IReadOnlyDictionary<string, JsonElement> Parameters { get; } = new Echo().Parameters;
    public Task<ToolResult> ExecuteAsync(IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken cancellationToken = default) => throw new OperationCanceledException();
}
sealed class WaitingModel : ILlmClient
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Stopped { get; private set; }
    public async Task<LlmResponse> CompleteAsync(IReadOnlyList<Message> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken = default)
    {
        Started.SetResult();
        try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
        finally { Stopped = true; }
        return new LlmResponse { Content = "done" };
    }
}
sealed class WaitingStore : IContextStore
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Stopped { get; private set; }
    public async Task<IReadOnlyList<Message>> GetMessagesAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        Started.SetResult();
        try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
        finally { Stopped = true; }
        return [];
    }
    public Task AppendMessageAsync(string sessionId, Message message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task ReplaceMessagesAsync(string sessionId, IReadOnlyList<Message> messages, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
