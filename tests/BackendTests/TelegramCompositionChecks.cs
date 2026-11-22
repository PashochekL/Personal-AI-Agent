using System.Net;
using System.Text;
using System.Text.Json;
using AiAgent.Http;
using AiAgent.Shared;
using AiAgent.Telegram;
using TaskStatus = AiAgent.Shared.TaskStatus;

internal static class TelegramCompositionChecks
{
    public static async Task<int> RunAsync(HttpClient http, string key, MemoryScope scope)
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); count++; Console.WriteLine("PASS: " + name); }
        var fixture = Path.Combine(Directory.GetCurrentDirectory(), ".uv-cache", "telegram-composition-" + Guid.NewGuid().ToString("N"));
        var options = new TelegramOptions { Token = "123456:synthetic_token_not_a_real_secret", AllowedUserId = 900001,
            Owner = scope.OwnerId, Project = scope.ProjectId, SessionName = "shared-cli-telegram", StateDirectory = fixture };
        try
        {
            using var transport = new HttpClient(); var backend = new BackendClient(transport, http.BaseAddress!, key);
            using var telegramStub = new TelegramHandler(); using var telegramHttp = new HttpClient(telegramStub);
            using var state = new TelegramStateFile(options);
            var bot = new TelegramBot(new TelegramApi(telegramHttp, options.Token), backend, state, options, TextWriter.Null);
            await bot.InitializeAsync(default);
            var cliSession = await backend.ResolveSessionAsync("shared-cli-telegram", default);
            Check(cliSession.SessionId == state.State.SessionId, "Telegram and CLI resolve one shared server session");
            TelegramUpdate Update(long id, string text, long user) => new(id, new(id, new(user, false, null), new(user, "private"), text));
            await bot.HandleAsync(Update(1, "telegram-common-history", options.AllowedUserId), default);
            await bot.DeliverOnceAsync(default);
            var taskId = state.State.Deliveries.Single().TaskId!;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            Check((await backend.WaitAsync(taskId, deadline.Token)).Status == TaskStatus.COMPLETED, "Telegram message executes through real API, Redis, Worker and Core");
            await bot.DeliverOnceAsync(default); await bot.DeliverOnceAsync(default);
            Check(telegramStub.Sent.Contains("saved through Core"), "Telegram receives the Core result through Bot API transport");
            var cliTask = await backend.SubmitAsync(new(cliSession.SessionId, "cli-follows-telegram", RequestKey: "cli-after-telegram"), default);
            await backend.WaitAsync(cliTask.TaskId, deadline.Token);
            var history = await http.GetStringAsync($"/sessions/{cliSession.SessionId}/messages");
            Check(history.Contains("telegram-common-history") && history.Contains("cli-follows-telegram"), "messages from both interfaces share persistent history");
            await bot.HandleAsync(Update(2, "recall-backend-fact", options.AllowedUserId), default);
            await bot.DeliverOnceAsync(default);
            var memoryTask = state.State.Deliveries.Single().TaskId!;
            await backend.WaitAsync(memoryTask, deadline.Token);
            await bot.DeliverOnceAsync(default); await bot.DeliverOnceAsync(default);
            Check(telegramStub.Sent.Contains("recalled synthetic violet"), "Telegram retrieves long-term memory saved through another interface session");
            var before = await http.GetStringAsync($"/sessions/{cliSession.SessionId}/messages");
            await bot.HandleAsync(Update(3, "must-not-run", 900002), default);
            Check(state.State.Deliveries.Length == 0 && await http.GetStringAsync($"/sessions/{cliSession.SessionId}/messages") == before, "unauthorized Telegram sender cannot reach production Core");
            return count;
        }
        finally
        {
            if (Directory.Exists(fixture)) { foreach (var file in Directory.GetFiles(fixture)) File.Delete(file); Directory.Delete(fixture); }
        }
    }

    internal sealed class TelegramHandler : HttpMessageHandler
    {
        public List<string> Sent { get; } = [];
        public List<(long ChatId, string Text)> SentTo { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var method = request.RequestUri!.Segments.Last();
            var result = method switch
            {
                "getMe" => "{\"id\":123456,\"is_bot\":true,\"username\":\"synthetic_bot\"}",
                "getWebhookInfo" => "{\"url\":\"\"}",
                "setMyCommands" => "true",
                "sendMessage" => "{\"message_id\":1}",
                _ => throw new Exception("Unexpected Telegram method")
            };
            if (method == "sendMessage")
            {
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Sent.Add(json.RootElement.GetProperty("text").GetString()!);
                SentTo.Add((json.RootElement.GetProperty("chat_id").GetInt64(), json.RootElement.GetProperty("text").GetString()!));
            }
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true,\"result\":" + result + "}", Encoding.UTF8, "application/json") };
        }
    }
}
