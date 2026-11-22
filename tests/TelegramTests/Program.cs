using System.Net;
using System.Text;
using System.Text.Json;
using AiAgent.Http;
using AiAgent.Shared;
using AiAgent.Telegram;
using TaskStatus = AiAgent.Shared.TaskStatus;

var fixture = Path.Combine(Directory.GetCurrentDirectory(), ".uv-cache", "telegram-check-" + Guid.NewGuid().ToString("N"));
var options = new TelegramOptions { Token = "123456:synthetic_token_not_a_real_secret", AllowedUserId = 900001, Project = "synthetic", StateDirectory = fixture };
var count = AllowedUsersChecks.Run() + await RouterChecks.RunAsync();
void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); count++; Console.WriteLine("PASS: " + name); }
TelegramUpdate Update(long id, string? text, long? user = null, string type = "private") => new(id, new(id, new(user ?? options.AllowedUserId, false, null), new(user ?? options.AllowedUserId, type), text));
var backendStub = new BackendStub(); var telegramStub = new TelegramStub();
using var backendHttp = new HttpClient(backendStub); using var telegramHttp = new HttpClient(telegramStub);
var backend = new BackendClient(backendHttp, new Uri("http://synthetic-backend/"), "synthetic-backend-access-key");
var api = new TelegramApi(telegramHttp, options.Token);
TelegramStateFile? state = null;
try
{
    options.Validate();
    state = new TelegramStateFile(options);
    var bot = new TelegramBot(api, backend, state, options, TextWriter.Null);
    await bot.InitializeAsync(default);
    Check(state.State.SessionId == BackendStub.Session, "bot resolves the same named server session as CLI");
    await bot.HandleAsync(Update(1, "stranger", 900002), default);
    await bot.HandleAsync(Update(2, "group", type: "group"), default);
    Check(state.State.Offset == 3 && state.State.Deliveries.Length == 0 && backendStub.Submissions == 0, "strangers and groups produce no tasks or replies");
    await bot.HandleAsync(Update(3, "/models 2"), default);
    await bot.HandleAsync(Update(4, "/skill synthetic-skill"), default);
    await bot.HandleAsync(Update(5, "Привет\n/exit"), default);
    Check(state.State.Deliveries.Last().Request is { Model: "synthetic-other", Skill: "synthetic-skill", UserMessage: "Привет\n/exit" } && state.State.Skill is null, "selected model and one-turn skill frozen in durable request");
    await bot.HandleAsync(Update(5, "duplicate should be ignored"), default);
    Check(state.State.Deliveries.Length == 3, "repeated update ignored after checkpoint");
    backendStub.LoseNextReceipt = true;
    try { await bot.DeliverOnceAsync(default); throw new Exception("Expected lost receipt"); }
    catch (BackendException e) { Check(!e.Message.Contains("secret"), "lost submission receipt does not expose secrets"); }
    Check(backendStub.Submissions == 1 && state.State.Deliveries.Last().TaskId is null, "uncertain submission envelope retained");
    state.Dispose();
    state = new TelegramStateFile(options);
    bot = new TelegramBot(api, backend, state, options, TextWriter.Null);
    await bot.InitializeAsync(default);
    await bot.DeliverOnceAsync(default);
    Check(backendStub.Submissions == 1 && backendStub.RequestKeys.Distinct().Count() == 1, "restart retries the same idempotency key without another execution");
    Check(state.State.Deliveries.Last().TaskId == BackendStub.TaskId, "server receipt recovered after restart");
    backendStub.WrongSession = true;
    try { await bot.DeliverOnceAsync(default); throw new Exception("Expected mismatched session rejection"); }
    catch (BackendException) { Check(!telegramStub.Sent.Contains("synthetic final answer"), "mismatched server session cannot leak another task result"); }
    backendStub.WrongSession = false;
    await bot.DeliverOnceAsync(default); await bot.DeliverOnceAsync(default);
    Check(telegramStub.Sent.Contains("synthetic final answer") && state.State.Deliveries.Length == 0, "terminal result delivered and transport envelope removed");
    Check(telegramStub.NoParseMode && telegramStub.Sent.All(s => s.Length <= 4096), "Telegram replies are plain text within message limit");
    await bot.HandleAsync(Update(6, "will be cancelled"), default);
    await bot.HandleAsync(Update(7, "/cancel"), default);
    await bot.DeliverOnceAsync(default); await bot.DeliverOnceAsync(default);
    Check(backendStub.Submissions == 1, "cancellation before admission does not execute a task");
    await bot.HandleAsync(Update(8, "hold"), default);
    await bot.DeliverOnceAsync(default);
    await bot.HandleAsync(Update(9, "/cancel"), default);
    Check(backendStub.Cancellations > 0, "running task cancellation reaches backend while result is pending");
    await bot.HandleAsync(Update(10, "/session " + new string('f', 32)), default);
    Check(state.State.SessionId == BackendStub.Session, "unknown server session cannot replace current session");
    await bot.HandleAsync(Update(11, "/new"), default);
    Check(state.State.SessionId == BackendStub.OtherSession, "new session uses deterministic named admission");
    await bot.HandleAsync(Update(12, "/status " + BackendStub.TaskId), default);
    Check(state.State.Deliveries.Last().Parts!.Single().Contains("такой задачи нет"), "task from another current session is not exposed");
    var joined = new string('x', 3999) + "😀" + new string('я', 5000);
    var chunks = TelegramApi.SplitText(joined);
    Check(string.Concat(chunks) == joined && chunks.All(p => p.Length <= 4000 && !char.IsHighSurrogate(p[^1])), "long Unicode replies split without breaking surrogate pairs");
    Check(TelegramApi.SplitText(new string(' ', 5000) + "answer").All(p => !string.IsNullOrWhiteSpace(p)), "empty whitespace chunks are not sent to Telegram");
    telegramStub.RateLimit = true;
    try { await api.SendAsync(options.AllowedUserId, "test", default); throw new Exception("Expected rate limit"); }
    catch (TelegramException e) { Check(!e.Fatal && e.RetryAfter == 5 && !e.Message.Contains(options.Token), "rate limit preserves retry_after without exposing token"); }
    try { await api.SendDocumentAsync(options.AllowedUserId, "synthetic.txt", [1, 2, 3], default); throw new Exception("Expected upload rate limit"); }
    catch (TelegramException e) { Check(!e.Fatal && e.RetryAfter == 5 && !e.Message.Contains(options.Token), "file uploads preserve retry_after without exposing the token"); }
    telegramStub.RateLimit = false; telegramStub.Webhook = true;
    try { await api.EnsurePollingAsync(default); throw new Exception("Expected webhook refusal"); }
    catch (TelegramException e) { Check(e.Fatal && !e.Message.Contains("synthetic-webhook"), "existing webhook is not overwritten or logged"); }
    try { using var duplicate = new TelegramStateFile(options); throw new Exception("Expected second-instance rejection"); }
    catch (IOException) { Check(true, "second local bot instance excluded by state lease"); }
    state.Dispose(); state = null;
    var foreign = new TelegramOptions { Token = options.Token, AllowedUserId = options.AllowedUserId, Project = "other", StateDirectory = fixture };
    try { using var wrong = new TelegramStateFile(foreign); throw new Exception("Expected scope mismatch"); }
    catch (InvalidOperationException) { Check(true, "saved delivery state cannot be silently moved to another project"); }
    try { new TelegramOptions { Token = options.Token, AllowedUserId = 0, Project = "x" }.Validate(); throw new Exception("Expected allowlist requirement"); }
    catch (ArgumentException) { Check(true, "owner allowlist is mandatory"); }
    state = new TelegramStateFile(options);
    bot = new TelegramBot(api, backend, state, options, TextWriter.Null);
    telegramStub.Webhook = false;
    telegramStub.Incoming = [Update(20, "polling-round-trip")];
    using (var stop = new CancellationTokenSource())
    {
        var run = bot.RunAsync(stop.Token);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (state.State.Offset < 21 || !telegramStub.Sent.Contains("synthetic final answer", StringComparer.Ordinal) || state.State.Deliveries.Any(d => d.UpdateId == 20))
            await Task.Delay(50, deadline.Token);
        stop.Cancel();
        try { await run; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        Check(telegramStub.LastOffset >= 21, "receive loop acknowledges only durable processed updates");
        Check(state.State.Deliveries.All(d => d.UpdateId != 20), "concurrent receiving and delivery complete a polling round-trip");
    }
    Console.WriteLine($"Telegram checks passed: {count}");
}
finally
{
    state?.Dispose();
    if (Directory.Exists(fixture)) { foreach (var file in Directory.GetFiles(fixture)) File.Delete(file); Directory.Delete(fixture); }
}

sealed class TelegramStub : HttpMessageHandler
{
    public List<string> Sent { get; } = [];
    public List<(long ChatId, string Text)> SentTo { get; } = [];
    public bool NoParseMode { get; private set; } = true;
    public bool RateLimit { get; set; }
    public bool Webhook { get; set; }
    public TelegramUpdate[] Incoming { get; set; } = [];
    public long LastOffset { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var method = request.RequestUri!.Segments.Last();
        if (RateLimit) return Response("{\"ok\":false,\"parameters\":{\"retry_after\":5},\"description\":\"secret-response\"}", HttpStatusCode.TooManyRequests);
        if (method == "getMe") return Response("{\"ok\":true,\"result\":{\"id\":123456,\"is_bot\":true,\"username\":\"synthetic_bot\"}}");
        if (method == "getWebhookInfo") return Response(JsonSerializer.Serialize(new { ok = true, result = new { url = Webhook ? "https://synthetic-webhook/secret" : "" } }));
        if (method == "setMyCommands") return Response("{\"ok\":true,\"result\":true}");
        if (method == "getUpdates")
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            LastOffset = body.RootElement.GetProperty("offset").GetInt64();
            var incoming = Incoming.Where(u => u.UpdateId >= LastOffset).ToArray();
            if (incoming.Length == 0) await Task.Delay(200, ct);
            return Response(JsonSerializer.Serialize(new { ok = true, result = incoming }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
        }
        if (method == "sendMessage")
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Sent.Add(body.RootElement.GetProperty("text").GetString()!);
            SentTo.Add((body.RootElement.GetProperty("chat_id").GetInt64(), body.RootElement.GetProperty("text").GetString()!));
            NoParseMode &= !body.RootElement.TryGetProperty("parse_mode", out _);
            return Response("{\"ok\":true,\"result\":{\"message_id\":1}}");
        }
        throw new Exception("Unexpected Telegram method");
    }
    private static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

sealed class BackendStub : HttpMessageHandler
{
    public static readonly string Session = new('a', 32), OtherSession = new('d', 32), TaskId = new('b', 32);
    public int Submissions { get; private set; }
    public int Cancellations { get; private set; }
    public bool LoseNextReceipt { get; set; }
    public bool WrongSession { get; set; }
    public List<string> RequestKeys { get; } = [];
    private readonly Dictionary<string, AgentTask> _tasks = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.StartsWith("/sessions/named/")) return Model(new Session { SessionId = path.Contains("telegram-") ? OtherSession : Session });
        if (path == "/sessions/" + Session || path == "/sessions/" + OtherSession) return Model(new Session { SessionId = path.EndsWith(Session) ? Session : OtherSession });
        if (path == "/console") return Reply("{\"default_model\":\"synthetic-chat\",\"models\":[\"synthetic-chat\",\"synthetic-other\"],\"skills\":[{\"name\":\"synthetic-skill\",\"description\":\"test\"}],\"github\":false}");
        if (path == "/tasks")
        {
            var key = request.Headers.GetValues("Idempotency-Key").Single(); RequestKeys.Add(key);
            if (!_tasks.TryGetValue(key, out var task))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var text = body.RootElement.GetProperty("user_message").GetString()!;
                var sessionId = body.RootElement.GetProperty("session_id").GetString()!;
                task = new AgentTask { TaskId = _tasks.Count == 0 ? TaskId : Guid.NewGuid().ToString("N"), SessionId = sessionId, UserMessage = text,
                    Status = text == "hold" ? TaskStatus.RUNNING : TaskStatus.COMPLETED,
                    Result = text == "hold" ? null : new AgentResult { SessionId = sessionId, Status = TaskStatus.COMPLETED, Response = "synthetic final answer" } };
                _tasks.Add(key, task); Submissions++;
            }
            if (LoseNextReceipt) { LoseNextReceipt = false; throw new HttpRequestException("secret-url"); }
            return Model(task);
        }
        if (path.StartsWith("/tasks/"))
        {
            var task = _tasks.Values.FirstOrDefault(t => path.Contains(t.TaskId));
            if (task is null) return new(HttpStatusCode.NotFound);
            if (path.EndsWith("/cancel")) Cancellations++;
            if (WrongSession) return Model(new AgentTask { TaskId = task.TaskId, SessionId = OtherSession, UserMessage = task.UserMessage,
                Status = TaskStatus.COMPLETED, Result = new AgentResult { SessionId = OtherSession, Status = TaskStatus.COMPLETED, Response = "foreign answer" } });
            return Model(task);
        }
        return new(HttpStatusCode.NotFound);
    }
    private static HttpResponseMessage Model<T>(T value) where T : ExchangeModel => Reply(ContractJson.Serialize(value));
    private static HttpResponseMessage Reply(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
