using AiAgent.Http;
using System.Net;
using TaskStatus = AiAgent.Shared.TaskStatus;

namespace AiAgent.Telegram;

/// <summary>Personal console handoff. All history, binding and delivery checkpoints live in the backend.</summary>
public sealed partial class TelegramCompanion(TelegramApi telegram, BackendClient backend, TextWriter log) : IAsyncDisposable
{
    private readonly string _lease = Guid.NewGuid().ToString("N");
    private readonly CancellationTokenSource _stop = new();
    private long _bot;
    private string? _username;
    private Task? _run;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly object _pollSync = new();
    private CancellationTokenSource? _pollStop;

    public async Task<string> LinkAsync(string session, string? model, string? skill, CancellationToken ct)
    {
        var enter = _gate.WaitAsync(ct);
        while (!enter.IsCompleted)
        {
            InterruptPoll();
            await Task.WhenAny(enter, Task.Delay(100));
        }
        await enter;
        try
        {
            if (_run is { IsCompleted: true }) { await _run; _run = null; }
            if (_bot == 0)
            {
                var identity = await telegram.IdentityAsync(ct);
                if (!identity.IsBot || string.IsNullOrWhiteSpace(identity.Username) || identity.Username.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
                    throw new TelegramException("Не удалось получить адрес бота.", true);
                await telegram.EnsurePollingAsync(ct);
                _bot = identity.Id; _username = identity.Username;
            }
            var code = await backend.TelegramLinkAsync(session, _bot, _lease, model, skill, ct);
            if (_run is null) { await telegram.SetSimpleCommandsAsync(ct, personal: true); _run = RunAsync(_stop.Token); }
            return $"https://t.me/{_username}?start={code}";
        }
        finally { _gate.Release(); }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _gate.WaitAsync(ct);
                try
                {
                    await DeliverOnceAsync(ct);
                    var state = await backend.TelegramStateAsync(_bot, _lease, ct);
                    var updates = await PollAsync(state.UpdateOffset, ct);
                    foreach (var update in updates.OrderBy(u => u.UpdateId)) await HandleAsync(update, ct);
                }
                finally { _gate.Release(); }
            }
            catch (TelegramException e) when (!e.Fatal) { await log.WriteLineAsync(e.Message); await Task.Delay(TimeSpan.FromSeconds(e.RetryAfter), ct); }
            catch (BackendException e) when (e.Status is null or HttpStatusCode.ServiceUnavailable)
            { await log.WriteLineAsync("Telegram ждёт восстановления связи с Дамиром."); await Task.Delay(2000, ct); }
            catch (TelegramException e) { await log.WriteLineAsync(e.Message); return; }
        }
    }

    private void InterruptPoll()
    {
        lock (_pollSync) _pollStop?.Cancel();
    }

    private async Task<TelegramUpdate[]> PollAsync(long offset, CancellationToken ct)
    {
        using var poll = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_pollSync) _pollStop = poll;
        try { return await telegram.UpdatesAsync(offset, poll.Token, timeout: 1); }
        catch (OperationCanceledException) when (poll.IsCancellationRequested && !ct.IsCancellationRequested) { return []; }
        finally { lock (_pollSync) _pollStop = null; }
    }

    public async Task HandleAsync(TelegramUpdate update, CancellationToken ct)
    {
        var state = await backend.TelegramStateAsync(_bot, _lease, ct);
        if (update.UpdateId < state.UpdateOffset || update.UpdateId < 0) return;
        var message = update.Message;
        if (message?.From is not { IsBot: false } user || message.Chat.Type != "private" || message.Chat.Id != user.Id || user.Id <= 0)
        { await CheckpointAsync(state with { UpdateOffset = update.UpdateId + 1 }, ct); return; }
        var text = message.Text?.Trim() ?? "";
        var command = text.Split([' ', '\n', '\t'], 2)[0].Split('@')[0];
        if (command == "/start" && text.Contains(' '))
        {
            try
            {
                await backend.TelegramConnectAsync(_bot, _lease, text[(text.IndexOf(' ') + 1)..].Trim(), user.Id, ct);
                state = await backend.TelegramStateAsync(_bot, _lease, ct);
                await ShowHistoryAsync(state.SessionId, user.Id, ct);
                await telegram.SendMenuAsync(user.Id, $"Проект: {state.WorkspaceName}.\nДиалог открыт здесь. Пиши Дамиру как обычно — я помню разговор на компьютере.\nКонсоль пока приостановлена. Компьютер и окно Дамира должны оставаться включёнными.", ct);
            }
            catch (BackendException e) when (e.Status is HttpStatusCode.Forbidden or (HttpStatusCode)423)
            { await telegram.SendAsync(user.Id, state.UserId == user.Id ? e.Message + $"\nНа компьютере открыт проект: {state.WorkspaceName}. Старая ссылка его не переключает." : e.Message, ct); }
        }
        else if (state.UserId != user.Id)
        {
            // No history, status or file access before a valid one-time link.
            await telegram.SendAsync(user.Id, "Открой ссылку, которую Дамир показывает по /tg в консоли на твоём компьютере.", ct);
        }
        else if (!state.Connected)
            await telegram.SendAsync(user.Id, $"На компьютере открыт проект: {state.WorkspaceName}. Это новый запуск. Открой свежую ссылку из консоли, прежде чем продолжать здесь. Старые сообщения не выполняются в новой папке.", ct);
        else if (text is "Помощь" or "/help" or "/start")
            await telegram.SendMenuAsync(user.Id, $"Проект: {state.WorkspaceName}. Просто напиши сообщение. Можно попросить прислать файл из этой рабочей папки.\n«Команды» — сжатие, контекст, токены, модель и навыки. «Продолжить здесь» возвращает диалог в Telegram. «На компьютер» возвращает его в консоль. «Остановить» прерывает текущий ответ.\nКогда компьютер или Дамир выключен, бот не отвечает.", ct);
        else if (text == "Продолжить здесь")
        {
            try
            {
                if (state.Channel != "telegram") await backend.TelegramResumeAsync(_bot, _lease, state.SessionId, user.Id, ct);
                await ShowHistoryAsync(state.SessionId, user.Id, ct);
                await telegram.SendMenuAsync(user.Id, $"Проект: {state.WorkspaceName}. Диалог снова в Telegram. Можно писать здесь; консоль пока приостановлена.", ct);
            }
            catch (BackendException e) when (e.Status == (HttpStatusCode)423)
            { await telegram.SendMenuAsync(user.Id, "Дамир ещё отвечает на компьютере. Дождись ответа, затем снова нажми «Продолжить здесь».", ct); }
            catch (BackendException e) when (e.Status == HttpStatusCode.Forbidden)
            { await telegram.SendMenuAsync(user.Id, "Подключение диалога изменилось. Получи новую ссылку через /tg в консоли.", ct); }
        }
        else if (text == "На компьютер")
        {
            try { await backend.ReturnToConsoleAsync(state.SessionId, ct); await telegram.SendMenuAsync(user.Id, "Диалог возвращён на компьютер. Можешь писать в открытой консоли Дамира. Для возврата в Telegram нажми «Продолжить здесь».", ct); }
            catch (BackendException e) when (e.Status == (HttpStatusCode)423) { await telegram.SendAsync(user.Id, "Сначала дождись ответа или нажми «Остановить».", ct); }
        }
        else if (text is "Остановить" or "/cancel")
        {
            if (state.PendingTask is { } pending) { await backend.CancelAsync(pending, ct); await telegram.SendAsync(user.Id, "Останавливаю ответ. Уже выполненные действия сохраняются.", ct); }
            else if (state.PendingMessage is not null)
            {
                state = state with { PendingMessage = null, PendingUpdate = null, PendingOperation = "chat" };
                await telegram.SendAsync(user.Id, "Сообщение отменено до отправки Дамиру.", ct);
            }
            else await telegram.SendAsync(user.Id, "Сейчас нечего останавливать.", ct);
        }
        else if (await HandleCommandAsync(update.UpdateId, user.Id, text, command, state, ct)) return;
        else if (text.StartsWith('/')) await telegram.SendMenuAsync(user.Id, "Просто напиши сообщение или выбери кнопку внизу.", ct);
        else if (state.Channel != "telegram") await telegram.SendMenuAsync(user.Id, "Этот диалог сейчас открыт на компьютере. Для возврата в Telegram нажми «Продолжить здесь».", ct);
        else if (state.PendingTask is not null || state.Parts is not null || state.PendingMessage is not null) await telegram.SendAsync(user.Id, "Ещё отвечаю на предыдущее сообщение. Дождись ответа или нажми «Остановить».", ct);
        else if (text.Length is 0 or > 32000) await telegram.SendAsync(user.Id, "Пришли текстовое сообщение длиной до 32000 символов.", ct);
        else
        {
            try
            {
                await CheckpointAsync(state with { PendingUpdate = update.UpdateId, PendingMessage = message.Text!, PendingOperation = "chat", UpdateOffset = update.UpdateId + 1 }, ct);
                return;
            }
            catch (BackendException e) when (e.Status is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Conflict or HttpStatusCode.TooManyRequests or (HttpStatusCode)423)
            { await telegram.SendMenuAsync(user.Id, e.Message, ct); }
        }
        // Preserve changes made by the console; only transport fields are checkpointed.
        await CheckpointAsync(state with { UpdateOffset = update.UpdateId + 1 }, ct);
    }

    private Task CheckpointAsync(CompanionConnection state, CancellationToken ct) => backend.TelegramCheckpointAsync(_bot, _lease, state, ct);

    private async Task ShowHistoryAsync(string session, long user, CancellationToken ct)
    {
        var messages = (await backend.MessagesAsync(session, ct)).Where(m => m.Role is "user" or "assistant" && !string.IsNullOrWhiteSpace(m.Content)).TakeLast(12).ToArray();
        var preview = string.Join("\n\n", messages.Select(m => (m.Role == "user" ? "Ты: " : "Дамир: ") + (m.Content!.Length > 1500 ? m.Content[..1500] + "…" : m.Content)));
        if (preview.Length == 0) preview = "Диалог пока пуст. Напиши первое сообщение.";
        else preview = "Последние сообщения диалога (вся история сохранена у Дамира):\n\n" + preview;
        foreach (var part in TelegramApi.SplitText(preview)) { await telegram.SendAsync(user, part, ct); await Task.Delay(1050, ct); }
    }

    public async Task DeliverOnceAsync(CancellationToken ct)
    {
        var state = await backend.TelegramStateAsync(_bot, _lease, ct);
        if (!state.Connected || state.UserId is not { } user) return;
        if (state.PendingMessage is { } text)
        {
            try
            {
                var task = await backend.SubmitAsync(new(state.SessionId, text, state.Model, state.PendingOperation == "chat" ? state.Skill : null,
                    Operation: state.PendingOperation, RequestKey: $"telegram:{_bot}:{state.PendingUpdate}", Channel: "telegram"), ct);
                state = state with { PendingTask = task.TaskId, PendingMessage = null, PendingUpdate = null, Skill = state.PendingOperation == "chat" ? null : state.Skill };
            }
            catch (BackendException e) when (e.Status is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Conflict or HttpStatusCode.TooManyRequests or (HttpStatusCode)423)
            {
                state = state with { PendingMessage = null, PendingUpdate = null, Parts = TelegramApi.SplitText(e.Message) };
            }
            await CheckpointAsync(state, ct);
        }
        if (state.PendingTask is { } id && state.Parts is null)
        {
            var task = await backend.TaskAsync(id, ct);
            if (task.SessionId != state.SessionId) throw new BackendException("Сервер вернул другой диалог.");
            if (task.Status is TaskStatus.PENDING or TaskStatus.RUNNING) return;
            state = state with { Parts = TelegramApi.SplitText(task.Result?.Response ?? task.Result?.Error ?? "Ответ остановлен."), NextPart = 0 };
            await CheckpointAsync(state, ct);
        }
        if (state.Parts is { } parts)
        {
            while (state.NextPart < parts.Length)
            {
                await telegram.SendAsync(user, parts[state.NextPart], ct);
                state = state with { NextPart = state.NextPart + 1 }; await CheckpointAsync(state, ct);
                await Task.Delay(1050, ct);
            }
            await DeliverFilesAsync(state.SessionId, user, ct);
            await CheckpointAsync(state with { PendingTask = null, Parts = null, NextPart = 0, PendingOperation = "chat" }, ct);
        }
    }

    private async Task DeliverFilesAsync(string session, long user, CancellationToken ct)
    {
        while ((await backend.TelegramFilesAsync(session, ct)).FirstOrDefault() is { } file)
        {
            _ = await backend.TelegramStateAsync(_bot, _lease, ct);
            await telegram.SendDocumentAsync(user, file.Filename, file.Content, ct);
            await backend.AcknowledgeTelegramFileAsync(session, file.FileId, ct);
            await Task.Delay(1050, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { if (_run is not null) await _run; }
        catch (Exception e) when (e is OperationCanceledException or BackendException) { }
        if (_bot != 0)
        {
            using var release = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await backend.TelegramReleaseAsync(_bot, _lease, release.Token); }
            catch (Exception e) when (e is BackendException or OperationCanceledException) { }
        }
        _stop.Dispose();
        _gate.Dispose();
    }
}
