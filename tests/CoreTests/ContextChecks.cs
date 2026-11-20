using AiAgent.Core;
using AiAgent.Shared;
using System.Text.Json;

static class ContextChecks
{
    public static async Task<int> RunAsync()
    {
        var checks = 0;
        void Check(bool value, string name) { checks++; if (!value) throw new Exception("Context: " + name); }
        var window = new ContextWindow(12000);
        var current = new Message { Role = "user", Content = new string('z', 6000) };
        var oldUser = new Message { Role = "user", Content = new string('x', 2300) };
        var toolCall = new ToolCall { Id = "call1", Name = "test", Arguments = new Dictionary<string, JsonElement>() };
        var oldCall = new Message { Role = "assistant", ToolCalls = [toolCall] };
        var oldResult = new Message { Role = "tool", ToolCallId = "call1", Content = "tool result" };
        var oldUser2 = new Message { Role = "user", Content = new string('y', 2300) };
        var oldAnswer = new Message { Role = "assistant", Content = "done" };
        var system = new Message { Role = "system", Content = "important instructions" };
        var project = new Message { Role = "user", Content = "project context" };
        Message[] request = [system, oldUser, oldCall, oldResult, oldUser2, oldAnswer, project, current];
        var model = new Model(new LlmResponse { Content = "summary one" }, new LlmResponse { Content = "summary two" });
        var compacted = await window.PrepareAsync(model, request, current, project, []);
        Check(model.Requests.Count == 2, "long conversation summarized in bounded chunks");
        Check(model.Requests[0][1].Content!.Contains("call1") && model.Requests[0][1].Content!.Contains("tool result"), "tool call and result stay together");
        Check(model.Requests[1][1].Content!.Contains("summary one"), "summary carries forward between chunks");
        Check(compacted.Count == 4 && ReferenceEquals(compacted[0], system) && ReferenceEquals(compacted[2], project) && ReferenceEquals(compacted[3], current), "instructions and current user turn preserved");
        Check(compacted[1].Content!.Contains("summary two") && request.Length == 8, "summary included without changing history");
        var store = new Store();
        store.Messages.AddRange([oldUser, oldCall, oldResult, oldUser2, oldAnswer]);
        var agentModel = new Model(new LlmResponse { Content = "first summary" }, new LlmResponse { Content = "final summary" }, new LlmResponse { Content = "answer" });
        var agent = new Agent(agentModel, store, new ToolRegistry(), systemPrompt: "important instructions", contextWindow: window);
        var agentResult = await agent.RunAsync("session", current.Content!);
        Check(agentResult.Status == AiAgent.Shared.TaskStatus.COMPLETED && agentResult.Response == "answer", "agent completes after summarizing");
        Check(store.Messages.Count == 7 && ReferenceEquals(store.Messages[0], oldUser) && store.Messages[2].ToolCallId == "call1", "full history remains in store");
        Check(agentModel.Requests[^1].Any(m => m.Content?.Contains("final summary") == true), "answer request receives summary");
        var cache = new MemorySummaryCache();
        var cachedWindow = new ContextWindow(12000, cache: cache);
        var cacheModel = new Model(new LlmResponse { Content = "one" }, new LlmResponse { Content = "two" });
        await cachedWindow.PrepareAsync(cacheModel, request, current, project, [], sessionId: "s");
        var cachedRequest = await cachedWindow.PrepareAsync(cacheModel, request, current, project, [], sessionId: "s");
        Check(cacheModel.Requests.Count == 2 && cachedRequest[1].Content!.Contains("two"), "cached summaries reused on repeated request");
        var manualStore = new Store();
        manualStore.Messages.AddRange([oldUser, oldCall, oldResult, oldUser2, oldAnswer]);
        var manualModel = new Model(new LlmResponse { Content = "first" }, new LlmResponse { Content = "second" }, new LlmResponse { Content = "answer" });
        var manualAgent = new Agent(manualModel, manualStore, new ToolRegistry(), contextWindow: new ContextWindow(12000, cache: new MemorySummaryCache()));
        Check(await manualAgent.CompactAsync("s") && manualStore.Messages.Count == 5, "manual compaction retains full history");
        var manualResult = await manualAgent.RunAsync("s", "short follow-up");
        Check(manualResult.Response == "answer" && manualModel.Requests.Count == 3 && manualModel.Requests[^1].Any(m => m.Content?.Contains("second") == true), "manual summary reused even below automatic limit");
        var previewStore = new Store();
        previewStore.Messages.AddRange([new Message { Role = "user", Content = "remember this" }, new Message { Role = "assistant", Content = "done" }]);
        IReadOnlyList<Message>? measured = null;
        var toolCount = 0;
        Task<int> Counter(IReadOnlyList<Message> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken _) { measured = messages; toolCount = tools.Count; return Task.FromResult(321); }
        var previewModel = new Model();
        var previewRegistry = new ToolRegistry();
        previewRegistry.Register(new Echo());
        var previewAgent = new Agent(previewModel, previewStore, previewRegistry, systemPrompt: "system rule", systemContext: () => "clock", contextWindow: new ContextWindow(12000, Counter));
        var estimate = await previewAgent.EstimateContextAsync("s");
        Check(estimate.Tokens == 321 && measured!.Any(m => m.Content == "remember this") && measured!.Any(m => m.Content!.Contains("system rule") && m.Content.Contains("clock")) && toolCount == 1, "preview counts history, system and tool schemas");
        Check(previewModel.Requests.Count == 0 && previewStore.Messages.Count == 2 && measured!.All(m => m.Content != ""), "preview does not call model, change history, or count marker");
        var previewCache = new MemorySummaryCache();
        var previewSummaryModel = new Model(new LlmResponse { Content = "saved recap" });
        var previewSummaryAgent = new Agent(previewSummaryModel, previewStore, new ToolRegistry(), contextWindow: new ContextWindow(12000, Counter, previewCache));
        await previewSummaryAgent.CompactAsync("s");
        var savedEstimate = await previewSummaryAgent.EstimateContextAsync("s");
        Check(savedEstimate.UsesSavedSummary && !savedEstimate.SummaryUnavailable && previewSummaryModel.Requests.Count == 1 && measured!.Any(m => m.Content!.Contains("saved recap")), "preview reuses cached summary without generating a new one");
        Task<int> OversizeCounter(IReadOnlyList<Message> messages, IReadOnlyList<ToolDefinition> _, CancellationToken __) => Task.FromResult(messages.Any(m => m.Content == "remember this") ? 13000 : 500);
        var noCacheModel = new Model();
        var noCacheAgent = new Agent(noCacheModel, previewStore, new ToolRegistry(), contextWindow: new ContextWindow(12000, OversizeCounter));
        var rawEstimate = await noCacheAgent.EstimateContextAsync("s");
        Check(rawEstimate.SummaryUnavailable && !rawEstimate.UsesSavedSummary && rawEstimate.Tokens == 13000 && noCacheModel.Requests.Count == 0, "preview reports uncompressed history when summary is not cached");
        var hugeOld = new Message { Role = "user", Content = new string('h', 15000) };
        var shortCurrent = new Message { Role = "user", Content = "new task" };
        var chunkModel = new Model(Enumerable.Range(0, 20).Select(_ => new LlmResponse { Content = "short summary" }).ToArray());
        var chunked = await window.PrepareAsync(chunkModel, [hugeOld, shortCurrent], shortCurrent, null, []);
        Check(chunkModel.Requests.Count > 1 && ReferenceEquals(chunked[^1], shortCurrent), "large historical turn split into fragments without changing current request");
        var small = new Message { Role = "user", Content = "hello" };
        var emptyModel = new Model();
        var untouched = await window.PrepareAsync(emptyModel, [small], small, null, []);
        Check(untouched.Count == 1 && emptyModel.Requests.Count == 0, "small request not summarized");
        var oversized = new Message { Role = "user", Content = new string('x', 13000) };
        try { await window.PrepareAsync(emptyModel, [oversized], oversized, null, []); throw new Exception("Oversized request accepted"); }
        catch (ContextLimitException) { checks++; }
        var recoveryStore = new Store();
        var recoveryModel = new Model(new LlmResponse { Content = "recovered" });
        var recoveryAgent = new Agent(recoveryModel, recoveryStore, new ToolRegistry(), contextWindow: window);
        var rejected = await recoveryAgent.RunAsync("recovery", oversized.Content!);
        Check(rejected.Status == AiAgent.Shared.TaskStatus.FAILED && recoveryStore.Messages.Count == 0, "oversized message does not poison stored history");
        var recovered = await recoveryAgent.RunAsync("recovery", "hello");
        Check(recovered.Response == "recovered", "session can continue after oversized request");
        var failingModel = new Model(new LlmResponse { Content = "", FinishReason = "length" });
        try { await window.PrepareAsync(failingModel, request, current, project, []); throw new Exception("Bad summary accepted"); }
        catch (ContextLimitException) { checks++; }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { await window.PrepareAsync(emptyModel, request, current, project, [], cancelled.Token); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { checks++; }
        return checks;
    }
    private sealed class MemorySummaryCache : IContextSummaryCache
    {
        private readonly Dictionary<string, string> _data = [];
        private readonly HashSet<string> _manual = [];
        public Task<string?> GetAsync(string sessionId, string key, CancellationToken cancellationToken = default) => Task.FromResult(_data.GetValueOrDefault(sessionId + key));
        public Task PutAsync(string sessionId, string key, string summary, CancellationToken cancellationToken = default) { _data[sessionId + key] = summary; return Task.CompletedTask; }
        public Task<bool> IsManualAsync(string sessionId, CancellationToken cancellationToken = default) => Task.FromResult(_manual.Contains(sessionId));
        public Task SetManualAsync(string sessionId, CancellationToken cancellationToken = default) { _manual.Add(sessionId); return Task.CompletedTask; }
    }
}
