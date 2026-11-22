using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using AiAgent.Api;
using AiAgent.Http;
using AiAgent.Telegram;
using Npgsql;

internal static class CompanionChecks
{
    public static async Task<int> RunAsync(HttpClient api, BackendOptions options, PostgresTaskStore store, NpgsqlDataSource database)
    {
        var count = 0;
        void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); count++; Console.WriteLine("PASS: " + label); }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90)); var ct = deadline.Token;
        var botId = Random.Shared.NextInt64(100000000000, 999999999999);
        using var receiptLoss = new LostReceiptHandler();
        using var transport = new HttpClient(receiptLoss, disposeHandler: false);
        var backend = new BackendClient(transport, api.BaseAddress!, options.ApiKey);
        var session = (await backend.CreateSessionAsync(ct)).SessionId;
        var before = await backend.SubmitAsync(new(session, "before-telegram", RequestKey: "before-tg"), ct);
        await backend.WaitAsync(before.TaskId, ct);
        using var stub = new TelegramTransport(botId); using var telegramHttp = new HttpClient(stub);
        var fixture = Path.Combine(Directory.GetCurrentDirectory(), ".uv-cache", "companion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            await using var companion = new TelegramCompanion(new TelegramApi(telegramHttp, botId + ":synthetic_token_for_testing"), backend, TextWriter.Null);
            var link = await companion.LinkAsync(session, null, null, ct);
            var code = link.Split("?start=")[1];
            Check(link.StartsWith("https://t.me/synthetic_companion?start=") && code.Length == 48, "console creates a Telegram deep link without asking for user ID");
            stub.HoldNextPoll();
            await stub.PollStarted.Task.WaitAsync(ct);
            using (var relinkDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                relinkDeadline.CancelAfter(TimeSpan.FromSeconds(2));
                code = (await companion.LinkAsync(session, null, null, relinkDeadline.Token)).Split("?start=")[1];
            }
            Check(stub.PollsInterrupted > 0, "repeated /tg interrupts a stalled Telegram poll instead of waiting for its network timeout");
            string? lease;
            await using (var command = database.CreateCommand("SELECT lease FROM damir_telegram_companion WHERE bot_id=$1"))
            { command.Parameters.AddWithValue(botId); lease = (string?)await command.ExecuteScalarAsync(ct); }
            async Task<CompanionConnection> State() => await backend.TelegramStateAsync(botId, lease!, ct);
            async Task Send(long update, string text, long user = 900001, string type = "private")
            {
                stub.Incoming.Enqueue(new(update, new(update, new(user, false, null), new(user, type), text)));
                while ((await State()).UpdateOffset <= update) await Task.Delay(20, ct);
            }
            await Send(1, "/start " + new string('0', 48), 900002);
            Check((await State()).UserId is null && !stub.Messages.Any(m => m.User == 900002 && m.Text.Contains("before-telegram")), "invalid link cannot bind a stranger or disclose history");
            await Send(2, "/start " + code, type: "group");
            Check((await State()).UserId is null, "deep links in group chats are ignored");
            await Send(3, "/start " + code);
            Check((await State()) is { UserId: 900001, Channel: "telegram" }, "one click binds actual Telegram ID persistently and transfers input ownership");
            Check(stub.Messages.Any(m => m.User == 900001 && m.Text.Contains("before-telegram")) && stub.HasButtons, "handoff sends recent console history and human-readable buttons");
            await using (var command = database.CreateCommand("SELECT link_hash IS NULL FROM damir_telegram_companion WHERE bot_id=$1"))
            { command.Parameters.AddWithValue(botId); Check((bool)(await command.ExecuteScalarAsync(ct))!, "one-time link is consumed and plaintext code is not retained"); }
            try { await backend.SubmitAsync(new(session, "blocked-console", RequestKey: "blocked"), ct); throw new Exception("Expected channel lock"); }
            catch (BackendException e) { Check(e.Status == (HttpStatusCode)423, "server rejects console submissions while Telegram owns the dialogue"); }
            await Send(4, "/start " + code, 900002);
            Check((await State()).UserId == 900001 && !stub.Messages.Any(m => m.User == 900002 && m.Text.Contains("before-telegram")), "used link cannot reassign owner or disclose conversation");

            await File.WriteAllTextAsync(Path.Combine(fixture, "report.txt"), "synthetic attachment", ct);
            var tool = new TelegramFileTool(fixture, session, store);
            static Dictionary<string, JsonElement> Args(string path) => new() { ["path"] = JsonSerializer.SerializeToElement(path) };
            Check(!(await tool.ExecuteAsync(Args("../outside.txt"), ct)).Success && !(await tool.ExecuteAsync(Args(Path.Combine(fixture, "report.txt")), ct)).Success,
                "Telegram file tool rejects parent traversal and absolute paths");
            Check((await tool.ExecuteAsync(Args("report.txt"), ct)).Success, "Core file tool snapshots a requested workspace file into durable outbox");
            Check((await tool.ExecuteAsync(Args("report.txt"), ct)).Success && (await tool.ExecuteAsync(Args("report.txt"), ct)).Success &&
                !(await tool.ExecuteAsync(Args("report.txt"), ct)).Success, "Telegram attachment queue is bounded to three snapshots");
            Check((await backend.TelegramFilesAsync(session, ct)).Length == 1, "attachment transport reads one file at a time to bound memory");
            await File.WriteAllTextAsync(Path.Combine(fixture, "report.txt"), "changed after queue", ct);
            await using (var oversized = File.Create(Path.Combine(fixture, "large.bin"))) oversized.SetLength(20 * 1024 * 1024 + 1);
            Check(!(await tool.ExecuteAsync(Args("large.bin"), ct)).Success, "oversized Telegram file is rejected before buffering");
            await Send(5, "companion-lost-receipt");
            while (!receiptLoss.Lost) await Task.Delay(20, ct);
            Check((await State()).PendingMessage == "companion-lost-receipt", "submission envelope is durable before a lost backend receipt");
            await companion.LinkAsync(session, "synthetic-not-configured", null, ct);
            Check((await State()).Model is null, "reopening Telegram preserves the frozen selection of an uncertain submission");
            while ((await State()).PendingTask is not null || !stub.Messages.Any(m => m.Text == "answer:companion-lost-receipt")) await Task.Delay(20, ct);
            Check(stub.DocumentCount == 3 && stub.SnapshotsPreserved && (await backend.TelegramFilesAsync(session, ct)).Length == 0, "all workspace snapshots reach Telegram and are acknowledged only after upload");
            Check((await backend.MessagesAsync(session, ct)).Count(m => m.Role == "user" && m.Content == "companion-lost-receipt") == 1,
                "lost submission receipt retries the same update without repeating agent execution");
            await Send(6, "На компьютер");
            Check((await State()).Channel == "console", "Telegram button returns input to console");
            await Send(7, "must-not-execute-in-telegram");
            Check(!(await backend.MessagesAsync(session, ct)).Any(m => m.Content == "must-not-execute-in-telegram"), "Telegram text is blocked after returning to computer");
            Check(!(await tool.ExecuteAsync(Args("report.txt"), ct)).Success, "file delivery cannot be queued from a console-owned session");
            try { await backend.TelegramResumeAsync(botId, lease!, session, 900002, ct); throw new Exception("Expected owner check"); }
            catch (BackendException e) { Check(e.Status == HttpStatusCode.Forbidden, "button resume cannot be requested for another Telegram user"); }
            var otherSession = (await backend.CreateSessionAsync(ct)).SessionId;
            try { await backend.TelegramResumeAsync(botId, lease!, otherSession, 900001, ct); throw new Exception("Expected session check"); }
            catch (BackendException e) { Check(e.Status == HttpStatusCode.Forbidden, "button resume cannot silently select another dialogue"); }
            await Send(8, "Продолжить здесь", 900002);
            Check((await State()).Channel == "console", "a stranger's continue button cannot take ownership of a dialogue");
            await Send(9, "Продолжить здесь");
            Check((await State()).Channel == "telegram", "continue button returns the linked dialogue to Telegram without a new deep link");
            try { await backend.SubmitAsync(new(session, "blocked-after-button", RequestKey: "blocked-after-button"), ct); throw new Exception("Expected channel lock"); }
            catch (BackendException e) { Check(e.Status == (HttpStatusCode)423, "button resume also blocks console admission on the server"); }
            await Send(10, "telegram-after-button");
            while ((await State()).PendingTask is not null || !stub.Messages.Any(m => m.Text == "answer:telegram-after-button")) await Task.Delay(20, ct);
            Check((await backend.MessagesAsync(session, ct)).Any(m => m.Role == "user" && m.Content == "telegram-after-button"), "conversation continues in the same history after the button handoff");
            await Send(11, "На компьютер");
            var busyConsole = await backend.SubmitAsync(new(session, "hold", RequestKey: "busy-console"), ct);
            await Send(12, "Продолжить здесь");
            Check((await State()).Channel == "console", "continue button cannot interrupt an active console task");
            await backend.CancelAsync(busyConsole.TaskId, ct); await backend.WaitAsync(busyConsole.TaskId, ct);
            await Send(13, "Продолжить здесь");
            Check((await State()).Channel == "telegram", "button handoff becomes available after console work has stopped");
            await Send(14, "На компьютер");
            var returned = await backend.SubmitAsync(new(session, "console-after-telegram", RequestKey: "back-on-pc"), ct);
            await backend.WaitAsync(returned.TaskId, ct);
            var nextLink = await companion.LinkAsync(session, null, null, ct);
            await Send(15, "/start " + nextLink.Split("?start=")[1], 900002);
            Check((await State()) is { UserId: 900001, Channel: "console" }, "fresh links preserve the previously bound Telegram owner");
            await Send(16, "/start " + nextLink.Split("?start=")[1]);
            await Send(17, "hold");
            try { await backend.ReturnToConsoleAsync(session, ct); throw new Exception("Expected active task lock"); }
            catch (BackendException e) { Check(e.Status == (HttpStatusCode)423, "handoff waits for an active task instead of interleaving channels"); }
            await Send(18, "Остановить");
            while ((await State()).PendingTask is not null) await Task.Delay(20, ct);
            await backend.ReturnToConsoleAsync(session, ct);
            Check((await State()).Channel == "console", "stop button cancels active work and makes console return possible");
            var expired = await companion.LinkAsync(session, null, null, ct);
            await using (var command = database.CreateCommand("UPDATE damir_telegram_companion SET link_expires=now()-interval '1 second' WHERE bot_id=$1"))
            { command.Parameters.AddWithValue(botId); await command.ExecuteNonQueryAsync(ct); }
            await Send(19, "/start " + expired.Split("?start=")[1]);
            Check((await State()).Channel == "console", "expired link cannot transfer dialogue ownership");
            var competingLease = Guid.NewGuid().ToString("N");
            try { await backend.TelegramLinkAsync(session, botId, competingLease, null, null, ct); throw new Exception("Expected receiver lease"); }
            catch (BackendException e) { Check(e.Status == (HttpStatusCode)423, "a second console cannot steal the active bot receiver"); }
            var foreign = new PostgresTaskStore(database, new BackendOptions { Database = options.Database, Redis = options.Redis, ApiKey = options.ApiKey,
                Scope = new() { OwnerId = options.Scope.OwnerId, ProjectId = "foreign-companion" } });
            try { await foreign.TelegramStateAsync(botId, lease!, ct); throw new Exception("Expected scope isolation"); }
            catch (TaskAdmissionException e) { Check(e.StatusCode == 403, "Telegram connection and checkpoints respect backend project scope"); }
            await backend.SaveConsoleResumeAsync(session, "synthetic-model", "test-skill", ct);
            Check(await backend.ConsoleResumeAsync(ct) == new AiAgent.Http.ConsoleResume(session, "synthetic-model", "test-skill") && await backend.ConsoleResumeAsync(ct) is null,
                "one-time token setup preserves the selected dialogue, model and skill across launcher restart");
            var recoverySession = (await backend.CreateSessionAsync(ct)).SessionId;
            var recoveryBot = botId + 1; var previousLease = Guid.NewGuid().ToString("N");
            var recoveryCode = await store.CreateTelegramLinkAsync(new(recoverySession, recoveryBot, previousLease), ct);
            await store.ConnectTelegramAsync(recoveryBot, previousLease, new(recoveryCode, 900001), ct);
            var recoveryTask = await backend.SubmitAsync(new(recoverySession, "response-before-restart", RequestKey: "recovery", Channel: "telegram"), ct);
            await backend.WaitAsync(recoveryTask.TaskId, ct);
            await store.CheckpointTelegramAsync(recoveryBot, previousLease, new(42, recoveryTask.TaskId, ["already delivered", "remaining after restart"], 1), ct);
            await store.ReleaseTelegramAsync(recoveryBot, previousLease, ct);
            using var recoveredStub = new TelegramTransport(recoveryBot); using var recoveredHttp = new HttpClient(recoveredStub);
            await using (var recovered = new TelegramCompanion(new TelegramApi(recoveredHttp, recoveryBot + ":synthetic_recovery_token"), backend, TextWriter.Null))
            {
                var recoveryLink = await recovered.LinkAsync(recoverySession, null, null, ct);
                await using (var check = database.CreateCommand("SELECT confirmed_lease IS NULL FROM damir_telegram_companion WHERE bot_id=$1"))
                { check.Parameters.AddWithValue(recoveryBot); Check((bool)(await check.ExecuteScalarAsync(ct))!, "a restarted receiver requires its fresh link before delivering saved content"); }
                recoveredStub.Incoming.Enqueue(new(43, new(43, new(900001, false, null), new(900001, "private"), "/start " + recoveryLink.Split("?start=")[1])));
                while (!recoveredStub.Messages.Any(m => m.Text == "remaining after restart")) await Task.Delay(20, ct);
                Check(!recoveredStub.Messages.Any(m => m.Text == "already delivered"), "a fresh receiver resumes an unfinished response after its saved delivery position");
            }
            await using (var cleanup = database.CreateCommand("DELETE FROM damir_telegram_companion WHERE bot_id=$1"))
            { cleanup.Parameters.AddWithValue(recoveryBot); await cleanup.ExecuteNonQueryAsync(ct); }
            return count;
        }
        finally
        {
            File.Delete(Path.Combine(fixture, "report.txt")); File.Delete(Path.Combine(fixture, "large.bin")); Directory.Delete(fixture);
            await using var cleanup = database.CreateCommand("DELETE FROM damir_telegram_companion WHERE bot_id=$1");
            cleanup.Parameters.AddWithValue(botId); await cleanup.ExecuteNonQueryAsync();
        }
    }

    private sealed class LostReceiptHandler : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _inner = new(new SocketsHttpHandler { UseProxy = false });
        private bool _lost;
        public bool Lost => Volatile.Read(ref _lost);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await _inner.SendAsync(request, ct);
            if (!_lost && request.RequestUri!.AbsolutePath == "/tasks" && request.Content is not null &&
                (await request.Content.ReadAsStringAsync(ct)).Contains("companion-lost-receipt"))
            { _lost = true; response.Dispose(); throw new HttpRequestException("synthetic lost receipt"); }
            return response;
        }
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }

    internal sealed class TelegramTransport(long bot) : HttpMessageHandler
    {
        public ConcurrentQueue<TelegramUpdate> Incoming { get; } = new();
        public ConcurrentQueue<(long User, string Text)> Messages { get; } = new();
        public ConcurrentQueue<string> Buttons { get; } = new();
        public bool HasButtons { get; private set; }
        public int DocumentCount { get; private set; }
        public bool SnapshotsPreserved { get; private set; } = true;
        private int _holdNextPoll;
        private int _pollsInterrupted;
        public int PollsInterrupted => Volatile.Read(ref _pollsInterrupted);
        public TaskCompletionSource PollStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void HoldNextPoll() => Volatile.Write(ref _holdNextPoll, 1);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var method = request.RequestUri!.Segments.Last();
            object result;
            if (method == "sendDocument")
            { DocumentCount++; SnapshotsPreserved &= (await request.Content!.ReadAsStringAsync(ct)).Contains("synthetic attachment"); result = new { message_id = 1 }; }
            else
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                switch (method)
                {
                    case "getMe": result = new { id = bot, is_bot = true, username = "synthetic_companion" }; break;
                    case "getWebhookInfo": result = new { url = "" }; break;
                    case "setMyCommands": result = true; break;
                    case "getUpdates":
                        if (Interlocked.Exchange(ref _holdNextPoll, 0) == 1)
                        {
                            PollStarted.TrySetResult();
                            try { await Task.Delay(Timeout.Infinite, ct); }
                            finally { Interlocked.Increment(ref _pollsInterrupted); }
                        }
                        await Task.Delay(30, ct);
                        var offset = body.RootElement.GetProperty("offset").GetInt64();
                        result = Incoming.Where(u => u.UpdateId >= offset).ToArray(); break;
                    case "sendMessage":
                        var text = body.RootElement.GetProperty("text").GetString()!;
                        Messages.Enqueue((body.RootElement.GetProperty("chat_id").GetInt64(), text));
                        if (body.RootElement.TryGetProperty("reply_markup", out var keyboard))
                            foreach (var button in keyboard.GetProperty("keyboard").EnumerateArray().SelectMany(row => row.EnumerateArray())) Buttons.Enqueue(button.GetProperty("text").GetString()!);
                        HasButtons |= body.RootElement.TryGetProperty("reply_markup", out var menu) && menu.GetProperty("keyboard").EnumerateArray()
                            .SelectMany(row => row.EnumerateArray()).Any(button => button.GetProperty("text").GetString() == "На компьютер");
                        result = new { message_id = 1 }; break;
                    default: throw new Exception("Unexpected Telegram method: " + method);
                }
            }
            var json = JsonSerializer.Serialize(new { ok = true, result }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
