using System.Net;
using AiAgent.Http;
using AiAgent.Shared;
using TaskStatus = AiAgent.Shared.TaskStatus;

namespace AiAgent.Telegram;

/// <summary>Telegram delivery and HTTP orchestration. Reasoning and tools execute only in Core.</summary>
public sealed class TelegramBot(TelegramApi telegram, BackendClient backend, TelegramStateFile state, TelegramOptions options, TextWriter log)
{
    public async Task InitializeAsync(CancellationToken ct, bool verifyTelegram = true)
    {
        options.Validate();
        if (verifyTelegram)
        {
            var bot = await telegram.IdentityAsync(ct);
            if (!bot.IsBot || bot.Id != options.BotId) throw new TelegramException("Токен не соответствует Telegram-боту.", true);
            await telegram.EnsurePollingAsync(ct);
        }
        if (!state.Initialized)
        {
            var session = await backend.ResolveSessionAsync(options.SessionName, ct);
            await state.ChangeAsync(_ => new(options.BotId, options.AllowedUserId, options.Scope, session.SessionId), ct);
        }
        else _ = await backend.SessionAsync(state.State.SessionId, ct);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var receive = ReceiveAsync(stop.Token); var deliver = DeliverAsync(stop.Token);
        try { await await Task.WhenAny(receive, deliver); }
        finally
        {
            stop.Cancel();
            try { await Task.WhenAll(receive, deliver); } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }

    private async Task ReceiveAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (state.State.Deliveries.Length >= 100) { await Task.Delay(500, ct); continue; }
                var updates = await telegram.UpdatesAsync(state.State.Offset, ct);
                if (updates.Length == 0) await Task.Delay(150, ct);
                foreach (var update in updates.OrderBy(u => u.UpdateId))
                {
                    if (state.State.Deliveries.Length >= 100) break;
                    await HandleAsync(update, ct);
                }
            }
            catch (TelegramException e) when (!e.Fatal) { await log.WriteLineAsync(e.Message); await Task.Delay(TimeSpan.FromSeconds(e.RetryAfter), ct); }
            catch (BackendException) { await log.WriteLineAsync("Сервер временно недоступен; обновление Telegram будет обработано повторно."); await Task.Delay(2000, ct); }
        }
    }

    // Checkpoint only after storing the command result or a durable submission envelope.
    public async Task HandleAsync(TelegramUpdate update, CancellationToken ct)
    {
        if (update.UpdateId < state.State.Offset || update.UpdateId < 0) return;
        var message = update.Message;
        if (message?.From is null || message.Chat.Type != "private" || message.From.IsBot ||
            message.From.Id != options.AllowedUserId || message.Chat.Id != options.AllowedUserId)
        {
            await state.ChangeAsync(s => s! with { Offset = checked(update.UpdateId + 1) }, ct);
            return;
        }
        var snapshot = state.State;
        var text = message.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text)) { await ReplyAsync(update.UpdateId, "Пока поддерживаю только текстовые сообщения.", ct); return; }
        if (text.Length > 32000) { await ReplyAsync(update.UpdateId, "Сообщение длиннее 32000 символов.", ct); return; }
        var split = text.IndexOfAny([' ', '\n', '\t']);
        var command = split < 0 ? text : text[..split];
        var argument = split < 0 ? "" : text[(split + 1)..].Trim();
        command = command.Split('@')[0];
        switch (command)
        {
            case "/start": case "/help":
                await ReplyAsync(update.UpdateId, "Дамир подключён к общему серверу.\n/session [ID] — текущая или существующая сессия\n/new — новая сессия\n/status [ID] — состояние задачи\n/cancel [ID] — запрос отмены\n/models [номер] — список или выбор модели\n/skill [имя|0] — список, выбор или отмена навыка\n/tokens — расход токенов\n/context — оценка контекста\n/compress — сжатие контекста\nОбычный текст отправляется агенту.", ct); return;
            case "/session":
                if (argument.Length == 0) { await ReplyAsync(update.UpdateId, "Сессия: " + snapshot.SessionId, ct); return; }
                if (!ValidId(argument)) { await ReplyAsync(update.UpdateId, "Нужен ID сессии из консоли Дамира.", ct); return; }
                try
                {
                    var session = await backend.SessionAsync(argument, ct);
                    await ReplyAsync(update.UpdateId, "Сессия: " + session.SessionId, ct, s => s with { SessionId = session.SessionId, Skill = null });
                }
                catch (BackendException e) when (e.Status == HttpStatusCode.NotFound) { await ReplyAsync(update.UpdateId, "Сессия не найдена в этом проекте.", ct); }
                return;
            case "/new":
                var created = await backend.ResolveSessionAsync($"telegram-{options.BotId}-{update.UpdateId}", ct);
                await ReplyAsync(update.UpdateId, "Новая сессия: " + created.SessionId, ct, s => s with { SessionId = created.SessionId, Skill = null }); return;
            case "/models":
                var catalog = await backend.CatalogAsync(ct);
                if (argument.Length == 0)
                {
                    await ReplyAsync(update.UpdateId, "Модели:\n" + string.Join("\n", catalog.Models.Select((m, i) => $"{i + 1}. {m}")) + "\nВыбор: /models номер", ct); return;
                }
                if (!int.TryParse(argument, out var index) || index < 1 || index > catalog.Models.Length)
                { await ReplyAsync(update.UpdateId, "Выбери номер из /models.", ct); return; }
                var model = catalog.Models[index - 1];
                await ReplyAsync(update.UpdateId, "Выбрана модель: " + model, ct, s => s with { Model = model }); return;
            case "/skill":
                if (argument == "0") { await ReplyAsync(update.UpdateId, "Выбор навыка отменён.", ct, s => s with { Skill = null }); return; }
                var skills = (await backend.CatalogAsync(ct)).Skills;
                if (argument.Length == 0)
                {
                    await ReplyAsync(update.UpdateId, skills.Length == 0 ? "Навыков пока нет." : "Навыки:\n" + string.Join("\n", skills.Select(s => s.Name + " — " + s.Description)) + "\nВыбор: /skill имя", ct); return;
                }
                if (!skills.Any(s => s.Name == argument)) { await ReplyAsync(update.UpdateId, "Навык не найден.", ct); return; }
                await ReplyAsync(update.UpdateId, "Навык выбран для следующего сообщения: " + argument, ct, s => s with { Skill = argument }); return;
            case "/tokens":
                var usage = await backend.UsageAsync(snapshot.SessionId, ct);
                await ReplyAsync(update.UpdateId, $"Токены: {(usage.MissingUsageResponses > 0 ? "≥" : "")}{usage.Total}", ct); return;
            case "/status": case "/cancel":
                await TaskCommandAsync(update.UpdateId, command, argument, ct); return;
            case "/context": case "/compress":
                await EnqueueAsync(update.UpdateId, text, command[1..], ct); return;
        }
        if (text.StartsWith('/')) { await ReplyAsync(update.UpdateId, "Неизвестная команда. /help — список команд.", ct); return; }
        await EnqueueAsync(update.UpdateId, message.Text!, "chat", ct);
    }

    private async Task EnqueueAsync(long updateId, string text, string operation, CancellationToken ct)
    {
        await state.ChangeAsync(current =>
        {
            var s = current!;
            if (s.Deliveries.Count(d => d.Request is not null && d.Parts is null) >= 20)
                return AddReply(s, updateId, "Уже ожидают 20 сообщений. Дождись ответов или используй /cancel.");
            var request = new Submission(s.SessionId, text, s.Model, operation == "chat" ? s.Skill : null, operation, $"telegram:{options.BotId}:{updateId}");
            return s with { Offset = checked(updateId + 1), Skill = operation == "chat" ? null : s.Skill,
                Pending = [..s.Deliveries, new(updateId, s.SessionId, request)] };
        }, ct);
    }

    private async Task TaskCommandAsync(long updateId, string command, string argument, CancellationToken ct)
    {
        var s = state.State;
        var local = s.Deliveries.LastOrDefault(d => d.SessionId == s.SessionId && d.Request is not null && d.Parts is null);
        if (argument.Length == 0 && local is { TaskId: null })
        {
            if (command == "/cancel") await UpdateDeliveryAsync(local.UpdateId, d => d with { CancelRequested = true }, ct);
            await ReplyAsync(updateId, command == "/cancel" ? "Запрошена отмена последнего сообщения." : "Последнее сообщение сохранено и ожидает отправки на сервер.", ct); return;
        }
        if (argument.Length > 0 && !ValidId(argument)) { await ReplyAsync(updateId, "Некорректный ID задачи.", ct); return; }
        AgentTask? task;
        try { task = argument.Length > 0 ? await backend.TaskAsync(argument, ct) : local?.TaskId is { } id ? await backend.TaskAsync(id, ct) : await backend.LatestAsync(s.SessionId, ct); }
        catch (BackendException e) when (e.Status == HttpStatusCode.NotFound) { await ReplyAsync(updateId, "Задача не найдена.", ct); return; }
        if (task is null || task.SessionId != s.SessionId) { await ReplyAsync(updateId, "В текущей сессии такой задачи нет.", ct); return; }
        if (command == "/cancel")
        {
            if (local?.TaskId == task.TaskId) await UpdateDeliveryAsync(local.UpdateId, d => d with { CancelRequested = true }, ct);
            task = await backend.CancelAsync(task.TaskId, ct);
            await ReplyAsync(updateId, $"Запрос отмены: {task.TaskId}, {task.Status}. Уже выполненные действия не отменяются.", ct); return;
        }
        await ReplyAsync(updateId, $"Задача: {task.TaskId}\nСостояние: {task.Status}\n" + (task.Result?.Response ?? task.Result?.Error ?? ""), ct);
    }

    private static bool ValidId(string id) => id.Length == 32 && id.All(char.IsAsciiHexDigit);
    private static TelegramState AddReply(TelegramState s, long id, string text) => s with
    { Offset = checked(id + 1), Pending = [..s.Deliveries, new(id, s.SessionId, Parts: TelegramApi.SplitText(text))] };
    private Task ReplyAsync(long id, string text, CancellationToken ct, Func<TelegramState, TelegramState>? change = null) =>
        state.ChangeAsync(s => AddReply(change?.Invoke(s!) ?? s!, id, text), ct);
    public Task UnavailableAsync(long updateId, CancellationToken ct) =>
        ReplyAsync(updateId, "Сервер этого аккаунта временно недоступен. Повтори команду позже. Если запрашивал отмену, проверь /status после восстановления связи.", ct);
    private Task UpdateDeliveryAsync(long id, Func<TelegramDelivery, TelegramDelivery> change, CancellationToken ct) =>
        state.ChangeAsync(s => s! with { Pending = s.Deliveries.Select(d => d.UpdateId == id ? change(d) : d).ToArray() }, ct);

    private async Task DeliverAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await DeliverOnceAsync(ct); }
            catch (TelegramException e) when (!e.Fatal) { await log.WriteLineAsync(e.Message); await Task.Delay(TimeSpan.FromSeconds(e.RetryAfter), ct); }
            catch (BackendException) { await log.WriteLineAsync("Связь с сервером прервана; запросы сохраняются с прежними ключами повтора."); await Task.Delay(2000, ct); }
            await Task.Delay(300, ct);
        }
    }

    public async Task DeliverOnceAsync(CancellationToken ct)
    {
        foreach (var receipt in state.State.Deliveries)
        {
            var delivery = state.State.Deliveries.FirstOrDefault(d => d.UpdateId == receipt.UpdateId);
            if (delivery is null) continue;
            if (delivery.Parts is null && delivery.Request is not null && delivery.TaskId is null)
            {
                if (delivery.CancelRequested)
                { await UpdateDeliveryAsync(delivery.UpdateId, d => d with { Parts = ["Сообщение отменено до отправки на сервер."], Request = null }, ct); continue; }
                try
                {
                    var task = await backend.SubmitAsync(delivery.Request, ct);
                    if (task.SessionId != delivery.SessionId) throw new BackendException("Сервер вернул задачу другой сессии.");
                    await UpdateDeliveryAsync(delivery.UpdateId, d => d with { TaskId = task.TaskId }, ct);
                }
                catch (BackendException e) when (e.Status is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Conflict or HttpStatusCode.TooManyRequests)
                { await UpdateDeliveryAsync(delivery.UpdateId, d => d with { Parts = [e.Message], Request = null }, ct); }
                continue;
            }
            if (delivery.Parts is null && delivery.TaskId is { } taskId)
            {
                if (delivery.CancelRequested) _ = await backend.CancelAsync(taskId, ct);
                var task = await backend.TaskAsync(taskId, ct);
                if (task.SessionId != delivery.SessionId) throw new BackendException("Сервер вернул задачу другой сессии.");
                if (task.Status is TaskStatus.PENDING or TaskStatus.RUNNING) continue;
                var response = task.Result?.Response ?? task.Result?.Error ?? "Задача отменена.";
                await UpdateDeliveryAsync(delivery.UpdateId, d => d with { Parts = TelegramApi.SplitText(response), Request = null }, ct);
                continue;
            }
            if (delivery.Parts is { } parts && delivery.NextPart < parts.Length)
            {
                await telegram.SendAsync(options.AllowedUserId, parts[delivery.NextPart], ct);
                await UpdateDeliveryAsync(delivery.UpdateId, d => d with { NextPart = d.NextPart + 1 }, ct);
                await Task.Delay(1050, ct);
            }
            if (state.State.Deliveries.FirstOrDefault(d => d.UpdateId == delivery.UpdateId) is { Parts: { } sent } done && done.NextPart >= sent.Length)
                await state.ChangeAsync(s => s! with { Pending = s.Deliveries.Where(d => d.UpdateId != delivery.UpdateId).ToArray() }, ct);
        }
    }
}
