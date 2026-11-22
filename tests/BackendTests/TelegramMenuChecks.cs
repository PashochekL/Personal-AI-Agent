using AiAgent.Http;
using AiAgent.Telegram;
using Npgsql;

internal static class TelegramMenuChecks
{
    public static async Task<int> RunAsync(HttpClient http, string key, string database)
    {
        var count = 0;
        void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); count++; Console.WriteLine("PASS: " + label); }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90)); var ct = deadline.Token;
        using var transport = new HttpClient(); var backend = new BackendClient(transport, http.BaseAddress!, key);
        var session = (await backend.ResolveSessionAsync("telegram-command-menu", ct)).SessionId;
        for (var i = 0; i < 4; i++)
        { var task = await backend.SubmitAsync(new(session, "menu-history-" + i, RequestKey: "menu-prime-" + i), ct); await backend.WaitAsync(task.TaskId, ct); }
        var before = await backend.MessagesAsync(session, ct);
        var bot = Random.Shared.NextInt64(100000000000, 999999999999);
        using var stub = new CompanionChecks.TelegramTransport(bot); using var telegram = new HttpClient(stub);
        await using var companion = new TelegramCompanion(new TelegramApi(telegram, bot + ":synthetic_menu_token"), backend, TextWriter.Null);
        var link = await companion.LinkAsync(session, "synthetic-other", "merged-skill", ct);
        await using var db = NpgsqlDataSource.Create(database);
        await using var leaseQuery = db.CreateCommand("SELECT lease FROM damir_telegram_companion WHERE bot_id=$1");
        leaseQuery.Parameters.AddWithValue(bot); var lease = (string)(await leaseQuery.ExecuteScalarAsync(ct))!;
        async Task<CompanionConnection> State() => await backend.TelegramStateAsync(bot, lease, ct);
        async Task Send(long id, string text)
        {
            stub.Incoming.Enqueue(new(id, new(id, new(900001, false, null), new(900001, "private"), text)));
            while ((await State()).UpdateOffset <= id) await Task.Delay(20, ct);
        }
        async Task Finish()
        {
            while ((await State()) is { } state && (state.PendingMessage is not null || state.PendingTask is not null || state.Parts is not null)) await Task.Delay(20, ct);
        }
        await Send(1, "/start " + link.Split("?start=")[1]);
        Check(stub.Buttons.Contains("Команды"), "personal Telegram keyboard offers a commands button");
        await Send(2, "Команды");
        Check(stub.Buttons.Contains("Сжать контекст") && stub.Buttons.Contains("Заполнение контекста") && stub.Buttons.Contains("Выбрать навык"), "commands menu exposes compression, context and selections without IDs");
        await Send(3, "Выбрать модель");
        Check(stub.Buttons.Contains("Модель: synthetic-chat") && !stub.Buttons.Contains("Модель: synthetic-vector"), "model menu uses the backend chat catalog");
        await Send(4, "Модель: synthetic-chat");
        Check((await State()).Model == "synthetic-chat", "model button persists the selected model");
        await Send(5, "Выбрать навык"); await Send(6, "Навык: merged-skill");
        Check((await State()).Skill == "merged-skill", "skill button selects a project skill for the next message");
        await Send(7, "Заполнение контекста"); await Finish();
        Check(stub.Messages.Any(m => m.Text.Contains("~100")) && (await State()).Skill == "merged-skill", "context button runs the real backend operation without consuming a pending skill");
        await Send(8, "Сжать контекст"); await Finish();
        Check(stub.Messages.Any(m => m.Text == "Готово.") && (await State()).Skill == "merged-skill", "compression button runs Core compaction and preserves the selected skill");
        Check((await backend.MessagesAsync(session, ct)).Count == before.Count, "context and compression controls do not become user messages or replace original history");
        await Send(9, "Расход токенов"); await Send(10, "Текущий проект"); await Send(11, "Состояние ответа");
        Check(stub.Messages.Any(m => m.Text.StartsWith("Расход этого диалога:")) && stub.Messages.Any(m => m.Text.StartsWith("Рабочая папка:")), "menu displays durable usage, current workspace and answer status");
        await Send(12, "История диалога");
        Check(stub.Messages.Any(m => m.Text.Contains("menu-history-0")), "history button shows the current dialogue");
        await Send(13, "/compress"); await Finish();
        Check((await backend.LatestAsync(session, ct))?.UserMessage == "/compress", "existing compression command remains available as a menu alias");
        await Send(14, "message-after-menu"); await Finish();
        Check((await State()).Skill is null && (await backend.MessagesAsync(session, ct)).Any(m => m.Content == "message-after-menu"), "selected skill is consumed by the next actual chat message");
        var last = (await backend.LatestAsync(session, ct))!.TaskId;
        await Send(15, "На компьютер"); await Send(16, "Сжать контекст");
        Check((await backend.LatestAsync(session, ct))?.TaskId == last, "Telegram menu cannot enqueue compaction while the console owns the dialogue");
        return count;
    }
}
