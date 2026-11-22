using System.Net;
using System.Text;
using System.Text.Json;
using AiAgent.Http;
using AiAgent.Shared;
using AiAgent.Telegram;
using TaskStatus = AiAgent.Shared.TaskStatus;

internal static class RouterChecks
{
    public static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); count++; Console.WriteLine("PASS: " + name); }
        var fixture = Path.Combine(Directory.GetCurrentDirectory(), ".uv-cache", "telegram-router-" + Guid.NewGuid().ToString("N"));
        var root = Options(fixture, [900001, 900002]);
        var profiles = Profiles(root);
        var stores = profiles.ToDictionary(p => p.UserId, p => new AccountBackend(p));
        using var tg = new TelegramStub(); using var tgHttp = new HttpClient(tg);
        var api = new TelegramApi(tgHttp, root.Token);
        TelegramRouter? router = null;
        TelegramUpdate Update(long id, long user, string text) => new(id, new(id, new(user, false, null), new(user, "private"), text));
        Func<TelegramBackendProfile, HttpClient> clients = p => new HttpClient(stores[p.UserId], disposeHandler: false);
        try
        {
            try { TelegramBackendProfile.Load(Path.Combine(fixture, "missing-profiles.json"), root); throw new Exception("Expected missing manifest rejection"); }
            catch (ArgumentException e) { Check(e.Message.Contains("актуальный launcher"), "missing backend manifest explains how to restart the correct launcher"); }
            Directory.CreateDirectory(fixture);
            try { TelegramBackendProfile.Load(fixture, root); throw new Exception("Expected directory manifest rejection"); }
            catch (ArgumentException e) { Check(e.Message.Contains("Не найден файл"), "directory created by an old bind mount is rejected with an actionable error"); }
            router = new(api, root, profiles, TextWriter.Null, clients);
            await router.InitializeAsync(default);
            var sessionA = router.AccountState(900001).SessionId;
            var sessionB = router.AccountState(900002).SessionId;
            Check(sessionA != sessionB, "isolated accounts resolve different server sessions");
            await router.HandleAsync(Update(1, 900001, "private-A"), default);
            await router.HandleAsync(Update(2, 900002, "private-B"), default);
            for (var i = 0; i < 3; i++) { await router.DeliverAccountAsync(900001, default); await router.DeliverAccountAsync(900002, default); }
            Check(stores[900001].Messages.SequenceEqual(new[] { "private-A" }) && stores[900002].Messages.SequenceEqual(new[] { "private-B" }), "sender identity chooses its own backend and credentials");
            Check(tg.SentTo.Contains((900001, "answer:private-A")) && tg.SentTo.Contains((900002, "answer:private-B")) &&
                !tg.SentTo.Contains((900002, "answer:private-A")), "answers delivered only to the originating account");
            await router.HandleAsync(Update(3, 900002, "/session " + sessionA), default);
            Check(router.AccountState(900002).SessionId == sessionB, "another account cannot attach a known session ID");
            var taskA = stores[900001].Tasks.Values.Single().TaskId;
            await router.HandleAsync(Update(4, 900002, "/status " + taskA), default);
            await router.HandleAsync(Update(5, 900002, "/cancel " + taskA), default);
            Check(stores[900001].Cancellations == 0 && router.AccountState(900002).Deliveries.All(d => d.Parts?.Any(p => p.Contains("private-A")) != true), "foreign task status and cancellation cannot cross routing boundary");
            await router.HandleAsync(Update(6, 900002, "/models 2"), default);
            Check(router.AccountState(900002).Model == "other" && router.AccountState(900001).Model is null, "model selection is independent per account");
            await router.HandleAsync(Update(7, 900001, "/new"), default);
            Check(router.AccountState(900001).SessionId != sessionA && router.AccountState(900002).SessionId == sessionB, "new conversation affects only its sender");
            await router.HandleAsync(Update(8, 900003, "unauthorized"), default);
            Check(router.Offset == 9 && stores.Values.All(s => !s.Messages.Contains("unauthorized")), "unknown sender acknowledged without reaching any backend");
            await router.HandleAsync(Update(9, 900001, "pending-A"), default);
            router.Dispose(); router = null;
            var onlyB = Options(fixture, [900002]);
            router = new(api, onlyB, profiles.Where(p => p.UserId == 900002).ToArray(), TextWriter.Null, clients);
            await router.InitializeAsync(default);
            await router.HandleAsync(Update(10, 900001, "revoked-A"), default);
            Check(stores[900001].Messages.Count == 1 && router.Offset == 11, "removing an ID stops admission while its state stays on disk");
            router.Dispose(); router = new(api, root, profiles, TextWriter.Null, clients);
            await router.InitializeAsync(default);
            Check(router.AccountState(900001).Deliveries.Any(d => d.Request?.UserMessage == "pending-A"), "re-adding an ID recovers its own unfinished envelope");
            stores[900001].Offline = true;
            await router.HandleAsync(Update(11, 900001, "/tokens"), default);
            await router.HandleAsync(Update(12, 900002, "B-continues"), default);
            await router.DeliverAccountAsync(900002, default);
            Check(stores[900002].Messages.Contains("B-continues"), "unavailable account backend does not block another account's admission");
            stores[900001].Offline = false;
            router.Dispose(); router = null;
            try { TelegramBackendProfile.Validate([profiles[0], profiles[1] with { ApiKey = profiles[0].ApiKey }], root); throw new Exception("Expected shared key rejection"); }
            catch (ArgumentException) { Check(true, "profiles cannot share backend API credentials"); }
            try { TelegramBackendProfile.Validate([profiles[0], profiles[1] with { Url = profiles[0].Url }], root); throw new Exception("Expected shared server rejection"); }
            catch (ArgumentException) { Check(true, "profiles cannot share a server URL"); }
            stores[900001].WrongIdentity = true;
            router = new(api, root, profiles, TextWriter.Null, clients);
            try { await router.InitializeAsync(default); throw new Exception("Expected identity mismatch"); }
            catch (ArgumentException) { Check(true, "server identity handshake rejects incorrect owner mapping"); }
            stores[900001].WrongIdentity = false;
        }
        finally
        {
            router?.Dispose(); foreach (var store in stores.Values) store.Dispose();
            Clean(fixture);
        }
        var legacyFixture = fixture + "-legacy";
        var legacyRoot = Options(legacyFixture, [900001, 900002]);
        var legacySingle = new TelegramOptions { Token = legacyRoot.Token, AllowedUserId = 900001, Project = legacyRoot.Project, StateDirectory = legacyFixture };
        try
        {
            using (var old = new TelegramStateFile(legacySingle))
                await old.ChangeAsync(_ => new(legacyRoot.BotId, 900001, legacyRoot.Scope, new string('f', 32), 20), default);
            using (var migrated = new TelegramRouter(api, legacyRoot, Profiles(legacyRoot), TextWriter.Null, p => new HttpClient(new AccountBackend(p))))
                Check(migrated.Offset == 20, "legacy migration preserves update offset without copying the old shared session");
            using (var old = new TelegramStateFile(legacySingle))
                await old.ChangeAsync(s => s! with { Pending = [new(19, s.SessionId, new(s.SessionId, "pending legacy", RequestKey: "telegram:123456:19"))] }, default);
            try { using var blocked = new TelegramRouter(api, legacyRoot, Profiles(legacyRoot), TextWriter.Null); throw new Exception("Expected pending legacy refusal"); }
            catch (InvalidOperationException e) { Check(e.Message.Contains("telegram-drain"), "unfinished legacy deliveries require explicit draining rather than reassignment"); }
        }
        finally { Clean(legacyFixture); }
        return count;
    }

    private static TelegramOptions Options(string directory, long[] users) => new()
    { Token = "123456:synthetic_token_not_a_real_secret", AllowedUserIds = users, Project = "isolated-test", StateDirectory = directory };
    private static TelegramBackendProfile[] Profiles(TelegramOptions root) => root.EffectiveUserIds.Select(id =>
        new TelegramBackendProfile(id, $"http://account-{id}/", "synthetic-access-key-for-account-" + id, root.AccountOwner(id), root.AccountProject)).ToArray();
    private static void Clean(string directory)
    { if (Directory.Exists(directory)) { foreach (var file in Directory.GetFiles(directory)) File.Delete(file); Directory.Delete(directory); } }

    private sealed class AccountBackend(TelegramBackendProfile profile) : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _sessions = [];
        public Dictionary<string, AgentTask> Tasks { get; } = [];
        public List<string> Messages { get; } = [];
        public int Cancellations { get; private set; }
        public bool Offline { get; set; }
        public bool WrongIdentity { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Headers.Authorization?.Parameter != profile.ApiKey) return new(HttpStatusCode.Unauthorized);
            if (Offline) throw new HttpRequestException("synthetic outage");
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/identity") return Json(new { owner_id = WrongIdentity ? "foreign" : profile.OwnerId, project_id = profile.ProjectId });
            if (path.StartsWith("/sessions/named/"))
            {
                if (!_sessions.TryGetValue(path, out var id)) _sessions[path] = id = Guid.NewGuid().ToString("N");
                return Model(new Session { SessionId = id });
            }
            if (path == "/console") return Json(new { default_model = "default", models = new[] { "default", "other" }, skills = Array.Empty<object>(), github = false });
            if (path.StartsWith("/sessions/"))
            {
                var id = path.Split('/')[2];
                if (!_sessions.Values.Contains(id)) return new(HttpStatusCode.NotFound);
                if (path.EndsWith("/usage")) return Json(new { total = 1, missing_usage_responses = 0 });
                return Model(new Session { SessionId = id });
            }
            if (path == "/tasks")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var session = body.RootElement.GetProperty("session_id").GetString()!;
                if (!_sessions.Values.Contains(session)) return new(HttpStatusCode.NotFound);
                var key = request.Headers.GetValues("Idempotency-Key").Single();
                if (!Tasks.TryGetValue(key, out var task))
                {
                    var text = body.RootElement.GetProperty("user_message").GetString()!; Messages.Add(text);
                    task = new AgentTask { TaskId = Guid.NewGuid().ToString("N"), SessionId = session, UserMessage = text, Status = TaskStatus.COMPLETED,
                        Result = new AgentResult { SessionId = session, Status = TaskStatus.COMPLETED, Response = "answer:" + text } };
                    Tasks.Add(key, task);
                }
                return Model(task);
            }
            if (path.StartsWith("/tasks/"))
            {
                var task = Tasks.Values.FirstOrDefault(t => path.Contains(t.TaskId));
                if (task is null) return new(HttpStatusCode.NotFound);
                if (path.EndsWith("/cancel")) Cancellations++;
                return Model(task);
            }
            return new(HttpStatusCode.NotFound);
        }
        private static HttpResponseMessage Model<T>(T value) where T : ExchangeModel => new(HttpStatusCode.OK) { Content = new StringContent(ContractJson.Serialize(value), Encoding.UTF8, "application/json") };
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
