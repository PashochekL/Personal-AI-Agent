using System.Net;
using System.Text.Json;
using AiAgent.Core;
using AiAgent.Gateway;
using AiAgent.Memory;
using AiAgent.Shared;
using Npgsql;
using Status = AiAgent.Shared.TaskStatus;

var checks = 0;
void Check(bool value, string name) { checks++; if (!value) throw new Exception("Memory check failed: " + name); }
void Reject(Action action, string name)
{
    checks++;
    try { action(); }
    catch (Exception e) when (e is ArgumentException or JsonException) { return; }
    throw new Exception("Invalid memory value accepted: " + name);
}
async Task RejectAsync(Func<Task> action, string name)
{
    checks++;
    try { await action(); }
    catch (Exception e) when (e is MemoryError or ArgumentException) { return; }
    throw new Exception("Invalid memory operation accepted: " + name);
}
var scope = new MemoryScope { OwnerId = "alice", ProjectId = "project-a" };
var otherOwner = new MemoryScope { OwnerId = "bob", ProjectId = "project-a" };
var otherProject = new MemoryScope { OwnerId = "alice", ProjectId = "project-b" };
Reject(() => new MemoryScope { OwnerId = " ", ProjectId = "p" }.Validate(), "blank owner");
Reject(() => new MemoryScope { OwnerId = "u", ProjectId = "" }.Validate(), "blank project");
Reject(() => MemoryRecord.ValidateText(" ", "x"), "blank key");
Reject(() => MemoryRecord.ValidateText("key", new string('x', 2001)), "oversized content");
Reject(() => new Embedding { Model = "model", Values = [0, 0] }.Validate(), "zero vector");
Reject(() => new Embedding { Model = "model", Values = [float.NaN] }.Validate(), "nonfinite vector");
Reject(() => ContractJson.Deserialize<MemoryScope>("""{"owner_id":"u","project_id":"p","extra":1}"""), "unknown field");
var now = DateTimeOffset.UtcNow;
var record = new MemoryRecord { Scope = scope, Key = "drink", Content = "Предпочитаю чай", CreatedAt = now, UpdatedAt = now };
Check(ContractJson.Deserialize<MemoryRecord>(ContractJson.Serialize(record)).Content == record.Content, "contract roundtrip");
Reject(() => new MemoryMatch { Record = record, Similarity = double.NaN }.Validate(), "invalid similarity");
Reject(() => MemoryOptions.FromEnvironment(_ => null), "project must be supplied");
Check(!MemoryOptions.FromEnvironment(key => key == "DAMIR_MEMORY_ENABLED" ? "0" : null).Enabled, "explicit opt out");
Check(MemoryOptions.FromEnvironment(key => key == "DAMIR_MEMORY_PROJECT" ? "p" : null).Scope!.ProjectId == "p", "project configuration");
Reject(() => MemoryOptions.FromEnvironment(key => key switch { "DAMIR_MEMORY_PROJECT" => "p", "DAMIR_EMBEDDING_DIMENSIONS" => "0", _ => null }), "dimension configuration");

var handler = new EmbeddingHttp();
using var http = new HttpClient(handler);
var client = new LiteLlmEmbeddingClient(http, new Uri("http://gateway:4000"), "test-key", "embedding-deployment", "test-model", 3);
var embedding = await client.EmbedAsync("Предпочитаю чай");
Check(embedding.Values.SequenceEqual(new float[] { 1, 0, 0 }) && embedding.Model == "test-model", "embedding response");
var payload = handler.Payload ?? throw new Exception("Missing embedding request");
Check(handler.Authorized && payload.GetProperty("model").GetString() == "embedding-deployment"
    && payload.GetProperty("input").GetString() == "Предпочитаю чай" && handler.Path == "/v1/embeddings", "proxy endpoint, authentication and deployment");
handler.Body = """{"data":[{"index":0,"embedding":[1,0]}]}""";
await RejectAsync(async () => { await client.EmbedAsync("x"); }, "dimension mismatch");
handler.Body = """{"data":[{"index":0,"embedding":[0,0,0]}]}""";
await RejectAsync(async () => { await client.EmbedAsync("x"); }, "empty direction");
handler.Body = """{"data":[{"index":1,"embedding":[1,0,0]}]}""";
await RejectAsync(async () => { await client.EmbedAsync("x"); }, "wrong response index");
handler.Status = HttpStatusCode.Unauthorized;
handler.Body = "SECRET provider error";
try { await client.EmbedAsync("x"); throw new Exception("HTTP failure ignored"); }
catch (MemoryError e) { Check(!e.ToString().Contains("SECRET"), "provider error body is not leaked"); }
using (var cancellation = new CancellationTokenSource())
{
    cancellation.Cancel();
    try { await client.EmbedAsync("x", cancellation.Token); throw new Exception("Embedding cancellation ignored"); }
    catch (OperationCanceledException) { checks++; }
}

var memory = new FakeMemory();
var store = new ChatStore();
var saveCall = Call("memory.save", new { key = "drink", content = "Предпочитаю чай" });
var model = new ScriptedModel(new LlmResponse { ToolCalls = [saveCall] }, new LlmResponse { Content = "Запомнил" });
var result = await new Agent(model, store, new ToolRegistry(), memory: memory, memoryScope: scope).RunAsync("session-1", "Запомни: предпочитаю чай");
Check(result.Status == Status.COMPLETED && (await memory.ListAsync(scope)).Single().Content == record.Content, "agent saves a memory through a tool");
Check(model.Requests[1].Any(m => m.Content?.Contains("Relevant stored facts (JSON)") == true && m.Content.Contains("drink")), "recall refreshed after save");
Check(store.Messages.All(m => m.Content?.Contains("Relevant stored facts (JSON)") != true), "recall does not pollute stored history");
var secondModel = new ScriptedModel(new LlmResponse { Content = "Ты предпочитаешь чай" });
var secondStore = new ChatStore();
result = await new Agent(secondModel, secondStore, new ToolRegistry(), memory: memory, memoryScope: scope).RunAsync("session-2", "Какой напиток я предпочитаю?");
Check(result.Status == Status.COMPLETED && secondModel.Requests[0].Any(m => m.Content?.Contains("drink") == true), "recall works with a new session and empty history");
Check(secondModel.Requests[0][^1].Content == "Какой напиток я предпочитаю?", "current request follows recalled data");
var foreignModel = new ScriptedModel(new LlmResponse { Content = "Не знаю" });
await new Agent(foreignModel, new ChatStore(), new ToolRegistry(), memory: memory, memoryScope: otherOwner).RunAsync("s", "напиток");
Check(foreignModel.Requests[0].All(m => m.Content?.Contains("\"drink\"") != true), "no recall across owners");
foreignModel = new ScriptedModel(new LlmResponse { Content = "Не знаю" });
await new Agent(foreignModel, new ChatStore(), new ToolRegistry(), memory: memory, memoryScope: otherProject).RunAsync("s", "напиток");
Check(foreignModel.Requests[0].All(m => m.Content?.Contains("\"drink\"") != true), "no recall across projects");

var registry = new ToolRegistry();
registry.Register(new MemoryTool(memory, scope, "save"));
var forbiddenScope = await registry.ExecuteAsync(Call("memory.save", new { key = "drink", content = "кофе", owner_id = "bob" }));
Check(!forbiddenScope.Success, "model cannot override scope");
var saveTool = new MemoryTool(memory, scope, "save");
await saveTool.ExecuteAsync(Args(new { key = "drink", content = "Теперь предпочитаю кофе" }));
Check((await memory.ListAsync(scope)).Count == 1 && (await memory.ListAsync(scope))[0].Content.Contains("кофе"), "same key updates instead of duplicating");
model = new ScriptedModel(new LlmResponse { ToolCalls = [Call("memory.forget", new { key = "drink" })] }, new LlmResponse { Content = "Забыл" });
await new Agent(model, new ChatStore(), new ToolRegistry(), memory: memory, memoryScope: scope).RunAsync("s", "Забудь мой напиток");
Check(!model.Requests[1].Any(m => m.Content?.Contains("Relevant stored facts (JSON)") == true && m.Content.Contains("\"drink\"")), "deleted fact removed from current recall");
Check((await memory.ListAsync(scope)).Count == 0, "forget removes a record");
Check((await new MemoryTool(memory, scope, "forget").ExecuteAsync(Args(new { key = "drink" }))).Content.Contains("\"deleted\":false"), "missing fact not reported as deleted");
memory.Fail = true;
model = new ScriptedModel(new LlmResponse { Content = "Могу продолжить" });
result = await new Agent(model, new ChatStore(), new ToolRegistry(), memory: memory, memoryScope: scope).RunAsync("s", "привет");
Check(result.Status == Status.COMPLETED && model.Requests[0].Any(m => m.Content?.Contains("search is unavailable") == true), "memory outage does not stop unrelated conversation");
Check(!(await saveTool.ExecuteAsync(Args(new { key = "x", content = "y" }))).Success, "save failure is explicit");
memory.Fail = false;
memory.Leak = record;
Check(!(await new MemoryTool(memory, otherOwner, "list").ExecuteAsync(Args(new { }))).Success, "foreign record rejected at tool boundary");
model = new ScriptedModel(new LlmResponse { Content = "ok" });
await new Agent(model, new ChatStore(), new ToolRegistry(), memory: memory, memoryScope: otherOwner).RunAsync("s", "напиток");
Check(model.Requests[0].All(m => m.Content?.Contains("\"drink\"") != true), "foreign record rejected at context boundary");
memory.Leak = null;
Reject(() => new Agent(new ScriptedModel(), new ChatStore(), new ToolRegistry(), memory: memory), "scope required with memory");
var boundedMemory = new FakeMemory();
for (var i = 0; i < 5; i++) await boundedMemory.SaveAsync(scope, "large-" + i, new string('x', 2000));
model = new ScriptedModel(new LlmResponse { Content = "ok" });
await new Agent(model, new ChatStore(), new ToolRegistry(), memory: boundedMemory, memoryScope: scope).RunAsync("s", new string('q', 5000));
var boundedContext = model.Requests[0].Single(m => m.Content?.Contains("Relevant stored facts (JSON)") == true).Content!;
Check(boundedContext.Contains("large-2") && !boundedContext.Contains("large-3") && boundedMemory.LastQuery.Length == 4000, "recall and embedding input are bounded");
var pagedMemory = new FakeMemory();
for (var i = 0; i < 23; i++) await pagedMemory.SaveAsync(scope, $"entry-{i:D2}", "stored fact");
var listTool = new MemoryTool(pagedMemory, scope, "list");
using (var page = JsonDocument.Parse((await listTool.ExecuteAsync(Args(new { }))).Content))
{
    var next = page.RootElement.GetProperty("next_after_key").GetString();
    using var tail = JsonDocument.Parse((await listTool.ExecuteAsync(Args(new { after_key = next }))).Content);
    Check(page.RootElement.GetProperty("records").GetArrayLength() == 20 && tail.RootElement.GetProperty("records").GetArrayLength() == 3
        && tail.RootElement.GetProperty("next_after_key").ValueKind == JsonValueKind.Null, "memory.list pages expose every record");
}
using (var cancellation = new CancellationTokenSource())
{
    cancellation.Cancel();
    try { await saveTool.ExecuteAsync(Args(new { key = "x", content = "y" }), cancellation.Token); throw new Exception("Memory tool cancellation ignored"); }
    catch (OperationCanceledException) { checks++; }
    result = await new Agent(new ScriptedModel(), new ChatStore(), new ToolRegistry(), memory: memory, memoryScope: scope).RunAsync("s", "x", cancellation.Token);
    Check(result.Status == Status.CANCELLED, "cancelled recall does not call the model");
}

var databaseUrl = Environment.GetEnvironmentVariable("DAMIR_MEMORY_TEST_DATABASE_URL");
if (databaseUrl is not null)
{
    await using var database = NpgsqlDataSource.Create(databaseUrl);
    var contextChecks = await ContextStoreChecks.RunAsync(database);
    checks += contextChecks;
    Console.WriteLine($"{contextChecks} shared history checks passed");
    var persisted = new PostgresLongTermMemory(database, new TestEmbeddings());
    await persisted.InitializeAsync();
    var testScope = new MemoryScope { OwnerId = "memory-test-" + Guid.NewGuid().ToString("N"), ProjectId = "a" };
    var testOther = new MemoryScope { OwnerId = testScope.OwnerId, ProjectId = "b" };
    var testOwner = new MemoryScope { OwnerId = testScope.OwnerId + "-other", ProjectId = "a" };
    try
    {
        var original = await persisted.SaveAsync(testScope, "drink", "чай");
        await persisted.SaveAsync(testScope, "editor", "редактор");
        await persisted.SaveAsync(testOther, "drink", "кофе");
        await persisted.SaveAsync(testOwner, "drink", "кофе");
        var reopened = new PostgresLongTermMemory(database, new TestEmbeddings());
        Check((await reopened.ListAsync(testScope)).Count == 2, "PostgreSQL persistence across service instances");
        var firstPage = await reopened.ListAsync(testScope, 1);
        var secondPage = await reopened.ListAsync(testScope, 1, firstPage[0].Key);
        Check(firstPage[0].Key == "drink" && secondPage.Single().Key == "editor", "keyset pagination visits older facts without duplicates");
        var matches = await reopened.SearchAsync(testScope, "напиток", 5, 0.5);
        Check(matches.Count == 1 && matches[0].Record.Content == "чай", "pgvector semantic ranking and threshold");
        Check((await reopened.SearchAsync(testOther, "кофе", 5, 0.5)).Single().Record.Content == "кофе", "PostgreSQL project isolation");
        Check((await reopened.ListAsync(testOwner)).Count == 1, "PostgreSQL owner isolation");
        await RejectAsync(async () => { await new PostgresLongTermMemory(database, new FailingEmbeddings()).SaveAsync(testScope, "drink", "новый факт"); }, "embedding failure before write");
        Check((await reopened.ListAsync(testScope)).Single(x => x.Key == "drink").Content == "чай", "failed embedding preserves existing fact");
        var updated = await reopened.SaveAsync(testScope, "drink", "кофе");
        Check(updated.CreatedAt == original.CreatedAt && updated.UpdatedAt >= original.UpdatedAt && (await reopened.ListAsync(testScope)).Count == 2, "atomic upsert preserves creation time");
        Check((await reopened.SearchAsync(testScope, "напиток", 5, 0.5)).Count == 0, "old vector replaced with content");
        Check(await reopened.DeleteAsync(testScope, "drink") && !await reopened.DeleteAsync(testScope, "drink"), "persistent deletion is idempotent");
        Check((await reopened.ListAsync(testOther)).Count == 1, "deletion cannot cross scope");
        await RejectAsync(async () => { await new PostgresLongTermMemory(database, new TestEmbeddings("different-model")).SearchAsync(testScope, "редактор"); }, "model drift requires reindexing");
        var embeddingUrl = Environment.GetEnvironmentVariable("DAMIR_MEMORY_TEST_EMBEDDING_URL");
        if (embeddingUrl is not null)
        {
            using var liveHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            var liveClient = new LiteLlmEmbeddingClient(liveHttp, new Uri(embeddingUrl), Environment.GetEnvironmentVariable("DAMIR_MEMORY_TEST_EMBEDDING_KEY"),
                "damir-memory-embedding", "ollama/embeddinggemma:300m");
            var liveMemory = new PostgresLongTermMemory(database, liveClient);
            await liveMemory.SaveAsync(testScope, "editor", "Предпочитаю зелёный чай без сахара.");
            var liveResults = await liveMemory.SearchAsync(testScope, "Какой чай я предпочитаю?", minimumSimilarity: 0);
            Check(liveResults.Single().Record.Content.Contains("зелёный чай"), "real Ollama through LiteLLM and pgvector");
            var recallModel = new ScriptedModel(new LlmResponse { Content = "Зелёный чай без сахара" });
            var recallResult = await new Agent(recallModel, new ChatStore(), new ToolRegistry(), memory: liveMemory, memoryScope: testScope)
                .RunAsync("a-new-session", "Какой чай я предпочитаю?");
            Check(recallResult.Status == Status.COMPLETED && recallModel.Requests[0].Any(m => m.Content?.Contains("Relevant stored facts (JSON)") == true && m.Content.Contains("editor")), "live semantic recall reaches Core in a new session");
            Console.WriteLine("Ollama -> LiteLLM -> pgvector -> Core checks passed (scripted response model)");
        }
        Console.WriteLine("PostgreSQL/pgvector integration checks passed");
    }
    finally
    {
        foreach (var cleanupScope in new[] { testScope, testOther, testOwner })
            foreach (var key in new[] { "drink", "editor" }) await persisted.DeleteAsync(cleanupScope, key);
    }
}
else Console.WriteLine("PostgreSQL integration not run: set DAMIR_MEMORY_TEST_DATABASE_URL for an isolated pgvector database.");
Console.WriteLine($"{checks} memory checks passed");

static Dictionary<string, JsonElement> Args(object value) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!;
static ToolCall Call(string name, object value) => new() { Id = Guid.NewGuid().ToString("N"), Name = name, Arguments = Args(value) };

sealed class EmbeddingHttp : HttpMessageHandler
{
    public string Body { get; set; } = """{"data":[{"index":0,"embedding":[1,0,0]}]}""";
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    public JsonElement? Payload { get; private set; }
    public bool Authorized { get; private set; }
    public string? Path { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Payload = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(cancellationToken));
        // The pinned LiteLLM/Ollama route rejects this optional OpenAI parameter.
        if (Payload.Value.TryGetProperty("encoding_format", out _))
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("Unsupported embedding parameter") };
        Authorized = request.Headers.Authorization?.Parameter == "test-key";
        Path = request.RequestUri!.AbsolutePath;
        return new HttpResponseMessage(Status) { Content = new StringContent(Body) };
    }
}
sealed class ChatStore : IContextStore
{
    public List<Message> Messages { get; } = [];
    public Task<IReadOnlyList<Message>> GetMessagesAsync(string sessionId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Message>>(Messages.ToArray());
    public Task AppendMessageAsync(string sessionId, Message message, CancellationToken cancellationToken = default) { Messages.Add(message); return Task.CompletedTask; }
    public Task ReplaceMessagesAsync(string sessionId, IReadOnlyList<Message> messages, CancellationToken cancellationToken = default) { Messages.Clear(); Messages.AddRange(messages); return Task.CompletedTask; }
}
sealed class ScriptedModel(params LlmResponse[] responses) : ILlmClient
{
    private readonly Queue<LlmResponse> _responses = new(responses);
    public List<IReadOnlyList<Message>> Requests { get; } = [];
    public Task<LlmResponse> CompleteAsync(IReadOnlyList<Message> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken = default)
    { Requests.Add(messages.ToArray()); return Task.FromResult(_responses.Dequeue()); }
}
sealed class FakeMemory : ILongTermMemory
{
    private readonly Dictionary<(string, string, string), MemoryRecord> _records = [];
    public bool Fail { get; set; }
    public MemoryRecord? Leak { get; set; }
    public string LastQuery { get; private set; } = "";
    public Task<MemoryRecord> SaveAsync(MemoryScope scope, string key, string content, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Fail) throw new MemoryError("Memory unavailable");
        MemoryRecord.ValidateText(key, content);
        var id = (scope.OwnerId, scope.ProjectId, key);
        var now = DateTimeOffset.UtcNow;
        var record = new MemoryRecord { Scope = scope, Key = key, Content = content, CreatedAt = _records.TryGetValue(id, out var old) ? old.CreatedAt : now, UpdatedAt = now };
        _records[id] = record;
        return Task.FromResult(record);
    }
    public async Task<IReadOnlyList<MemoryMatch>> SearchAsync(MemoryScope scope, string query, int limit = 5, double minimumSimilarity = 0.3, CancellationToken cancellationToken = default)
    {
        LastQuery = query;
        return (await ListAsync(scope, limit, cancellationToken: cancellationToken)).Select(record => new MemoryMatch { Record = record, Similarity = 1 }).ToArray();
    }
    public Task<IReadOnlyList<MemoryRecord>> ListAsync(MemoryScope scope, int limit = 20, string? afterKey = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Fail) throw new MemoryError("Memory unavailable");
        return Task.FromResult<IReadOnlyList<MemoryRecord>>(Leak is { } leak ? [leak] : _records.Values.Where(x => x.Scope.OwnerId == scope.OwnerId && x.Scope.ProjectId == scope.ProjectId && (afterKey is null || string.CompareOrdinal(x.Key, afterKey) > 0)).OrderBy(x => x.Key, StringComparer.Ordinal).Take(limit).ToArray());
    }
    public Task<bool> DeleteAsync(MemoryScope scope, string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Fail) throw new MemoryError("Memory unavailable");
        return Task.FromResult(_records.Remove((scope.OwnerId, scope.ProjectId, key)));
    }
}
sealed class TestEmbeddings(string model = "test-three-dimensional") : IEmbeddingClient
{
    public string Model => model;
    public int Dimensions => 3;
    public Task<Embedding> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new Embedding { Model = Model, Values = text.Contains("кофе") ? [1, 0, 0] : text.Contains("чай") || text.Contains("напиток") ? [0, 1, 0] : [0, 0, 1] });
    }
}
sealed class FailingEmbeddings : IEmbeddingClient
{
    public string Model => "test-three-dimensional";
    public int Dimensions => 3;
    public Task<Embedding> EmbedAsync(string text, CancellationToken cancellationToken = default) => throw new MemoryError("Embedding unavailable");
}
