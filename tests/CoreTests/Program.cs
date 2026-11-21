using System.Text.Json;
using AiAgent.Core;
using AiAgent.Shared;
using Status = AiAgent.Shared.TaskStatus;

if (Environment.GetEnvironmentVariable("DAMIR_TEST_GITHUB_MCP") == "1") { await McpChecks.GithubSmokeAsync(); return; }
if (Environment.GetEnvironmentVariable("DAMIR_TEST_TERMINAL_PASTE") == "1")
{
    var terminalInput = await ConsoleInput.ReadAsync(Console.In, Console.Out, !Console.IsInputRedirected && !Console.IsOutputRedirected, CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(new { terminalInput.Text, terminalInput.IsPaste, terminalInput.Incomplete }));
    return;
}

var checks = 0;
void Check(bool condition, string name) { checks++; if (!condition) throw new Exception($"Failed: {name}"); }
ToolCall Call(string id, string name = "echo", string arguments = "{\"text\":\"hello\"}") => new()
{ Id = id, Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments)! };
ToolRegistry Registry() { var registry = new ToolRegistry(); registry.Register(new Echo()); return registry; }

var store = new Store();
var continuation = JsonSerializer.SerializeToElement(new { provider = "test", items = new[] { "opaque" } });
var model = new Model(new LlmResponse { ToolCalls = [Call("1"), Call("2", arguments: "{\"text\":\"bye\"}")], Continuation = continuation }, new LlmResponse { Content = "done" });
var result = await new Agent(model, store, Registry()).RunAsync("s", "hello");
Check(result.Status == Status.COMPLETED && result.Response == "done", "tool cycle result");
Check(store.Messages.Select(m => m.Role).SequenceEqual(["user", "assistant", "tool", "tool", "assistant"]), "history order");
Check(store.Messages[2].ToolCallId == "1" && store.Messages[3].Content == "bye", "tool correlation");
Check(model.Requests[1].Count == 4, "tool results returned to model");
Check(store.Messages[1].Continuation?.GetProperty("provider").GetString() == "test" && model.Requests[1][1].Continuation is not null, "continuation saved and sent after tool calls");

var usageTotals = new UsageTotals();
var usageModel = new UsageTrackingClient(new Model(
    new LlmResponse { Content = "one", Usage = new TokenUsage { InputTokens = 3, OutputTokens = 2, TotalTokens = 5 } },
    new LlmResponse { Content = "two", Usage = new TokenUsage { InputTokens = 4, OutputTokens = 1 } },
    new LlmResponse { Content = "three" }), usageTotals);
await usageModel.CompleteAsync([], []);
await usageModel.CompleteAsync([], []);
await usageModel.CompleteAsync([], []);
var usageSnapshot = usageTotals.Snapshot();
Check(usageSnapshot.Input == 7 && usageSnapshot.Output == 3 && usageSnapshot.Total == 10 && usageSnapshot.MissingUsageResponses == 1 && usageSnapshot.LastInput is null, "reported usage sums and missing usage is explicit");
usageTotals.Record(new TokenUsage { InputTokens = 8, OutputTokens = 2, TotalTokens = 10 });
usageTotals.ClearLastInput();
Check(usageTotals.Snapshot().LastInput is null && usageTotals.Snapshot().Total == 20, "switching model clears last context but preserves total usage");

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
var contextCalls = 0;
var stableModel = new Model(new LlmResponse { ToolCalls = [Call("stable")] }, new LlmResponse { Content = "done" });
await new Agent(stableModel, new Store(), Registry(), systemContext: () => $"clock {++contextCalls}").RunAsync("s", "test");
Check(contextCalls == 1 && stableModel.Requests[0][0].Content == stableModel.Requests[1][0].Content, "dynamic system context stays stable across tool cycle");

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
store.Messages.Add(new Message { Role = "assistant", ToolCalls = [Call("1"), Call("2")] });
store.Messages.Add(new Message { Role = "tool", ToolCallId = "1", Content = "already completed" });
model = new Model(new LlmResponse { Content = "continuing" });
result = await new Agent(model, store, Registry()).RunAsync("s", "go");
Check(result.Status == Status.COMPLETED && store.Messages[1].Content == "already completed" && store.Messages[2].ToolCallId == "2" && store.Messages[2].Content!.Contains("outcome is unknown") && model.Requests[0].Any(m => m.Content?.Contains("Do not assume success") == true), "interrupted calls repaired without repeating completed or missing tools");
store = new Store();
store.Messages.Add(new Message { Role = "assistant", ToolCalls = [Call("1")] });
store.Messages.Add(new Message { Role = "user", Content = "out of order" });
result = await new Agent(new Model(), store, Registry()).RunAsync("s", "go");
Check(result.Status == Status.FAILED && store.Messages.Count == 2, "corrupt nonterminal history not repaired");
result = await new Agent(new Model(new LlmResponse { Content = "partial", FinishReason = "length" }), new Store(), Registry()).RunAsync("s", "go");
Check(result.Status == Status.FAILED, "truncated response");
var registry = Registry();
try { registry.Register(new Echo()); throw new Exception("duplicate tool accepted"); } catch (ArgumentException) { checks++; }
try { registry.RegisterRange([new NamedEcho("new-tool"), new Echo()]); throw new Exception("duplicate batch accepted"); }
catch (ArgumentException) { Check(registry.Definitions().All(x => x.Name != "new-tool"), "duplicate in batch leaves registry unchanged"); }
try { registry.RegisterRange([new NamedEcho("same"), new NamedEcho("same")]); throw new Exception("duplicate batch names accepted"); }
catch (ArgumentException) { Check(registry.Definitions().All(x => x.Name != "same"), "duplicates within batch leave registry unchanged"); }
Check((await registry.ExecuteAsync(Call("x", arguments: "{}"))).Success == false, "required argument");
Check((await registry.ExecuteAsync(Call("x", arguments: "{\"text\":\"ok\",\"extra\":true}"))).Success == false, "additional properties");
registry = new ToolRegistry();
registry.Register(new BrokenTool());
Check((await registry.ExecuteAsync(Call("1", "broken"))).Error == "Tool execution failed", "tool exception");
result = await new Agent(new BrokenModel(), new Store(), Registry()).RunAsync("s", "go");
Check(result.Status == Status.FAILED && !result.Error!.Contains("secret"), "gateway failure");
store = new Store();
result = await new Agent(new ToolThenGatewayFailure(), store, Registry()).RunAsync("s", "list skills");
Check(result.Status == Status.FAILED && result.Error!.Contains("формат") && store.Messages.Select(m => m.Role).SequenceEqual(["user", "assistant", "tool"]), "gateway format failure after tool is explained without losing its result");
result = await new Agent(new SpecificGatewayFailure(new AgentError("LLM gateway returned HTTP 429")), new Store(), Registry()).RunAsync("s", "go");
Check(result.Error!.Contains("HTTP 429"), "safe gateway HTTP status shown");
result = await new Agent(new SpecificGatewayFailure(new AgentError("LLM gateway returned HTTP 429 secret")), new Store(), Registry()).RunAsync("s", "go");
Check(!result.Error!.Contains("secret") && !result.Error.Contains("429"), "untrusted gateway error text not exposed");

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
    var recoveryModel = new Model(new LlmResponse { Content = "continued" });
    var recovered = await new Agent(recoveryModel, store, registry).RunAsync("s", "continue");
    Check(recovered.Status == Status.COMPLETED && store.Messages.Count(m => m.Role == "tool" && m.Content!.Contains("outcome is unknown")) == 2 && recoveryModel.Requests[0].Count(m => m.Role == "tool") == 2, "cancelled batch can continue with unknown results without rerunning tools");
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
Console.WriteLine($"{await ContextChecks.RunAsync()} context checks passed");
Console.WriteLine($"{await ShellChecks.RunAsync()} shell checks passed");
Console.WriteLine($"{await McpChecks.RunAsync()} MCP checks passed");
Console.WriteLine($"{await ProgressChecks.RunAsync()} progress checks passed");
Console.WriteLine($"{await ConsoleInputChecks.RunAsync()} console input checks passed");
Console.WriteLine($"{StartupBannerChecks.Run()} startup banner checks passed");
Console.WriteLine($"{await SkillMenuChecks.RunAsync()} skill menu checks passed");
Console.WriteLine($"{ContextPolicyChecks.Run()} context policy checks passed");
Console.WriteLine($"{await WebSearchChecks.RunAsync()} web search checks passed");

sealed class Echo : ITool
{
    public string Name => "echo";
    public string Description => "Echo text";
    public IReadOnlyDictionary<string, JsonElement> Parameters { get; } = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"type":"object","properties":{"text":{"type":"string"}},"required":["text"],"additionalProperties":false}""")!;
    public Task<ToolResult> ExecuteAsync(IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken cancellationToken = default) => Task.FromResult(new ToolResult { Success = true, Content = arguments["text"].GetString()! });
}
sealed class NamedEcho(string name) : ITool
{
    public string Name => name;
    public string Description => "Echo";
    public IReadOnlyDictionary<string, JsonElement> Parameters => new Echo().Parameters;
    public Task<ToolResult> ExecuteAsync(IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken cancellationToken = default) => new Echo().ExecuteAsync(arguments, cancellationToken);
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
sealed class ToolThenGatewayFailure : ILlmClient
{
    private int _calls;
    public Task<LlmResponse> CompleteAsync(IReadOnlyList<Message> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken = default)
    {
        if (_calls++ > 0) throw new AgentError("LLM gateway request failed", new JsonException("private data"));
        return Task.FromResult(new LlmResponse { ToolCalls = [new ToolCall { Id = "1", Name = "echo", Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("{\"text\":\"ok\"}")! }] });
    }
}
sealed class SpecificGatewayFailure(AgentError failure) : ILlmClient
{
    public Task<LlmResponse> CompleteAsync(IReadOnlyList<Message> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken = default) => throw failure;
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
