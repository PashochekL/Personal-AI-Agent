using AiAgent.Cli;
using AiAgent.Shared;
using System.Net;
using System.Text.Json;
using TaskStatus = AiAgent.Shared.TaskStatus;

StartupBanner.Show(Console.Out, !Console.IsOutputRedirected, () => Console.WindowWidth);
using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
using var stop = new CancellationTokenSource();
CancellationTokenSource? turn = null;
Console.CancelKeyPress += (_, e) => { e.Cancel = true; if (turn is not null) turn.Cancel(); else stop.Cancel(); };
try
{
    var address = new Uri(Environment.GetEnvironmentVariable("DAMIR_BACKEND_URL") ?? "http://127.0.0.1:8080");
    var client = new BackendClient(http, address, Environment.GetEnvironmentVariable("DAMIR_BACKEND_API_KEY") ?? "");
    using (var startup = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
    {
        startup.CancelAfter(TimeSpan.FromSeconds(60));
        while (true)
        {
            try { await client.ReadyAsync(startup.Token); break; }
            catch (BackendException e) when (e.Status is null or HttpStatusCode.ServiceUnavailable)
            { await Task.Delay(500, startup.Token); }
        }
    }
    ConsoleCatalog catalog;
    try { catalog = await client.CatalogAsync(stop.Token); }
    catch (BackendException e) when (e.Status is null or HttpStatusCode.ServiceUnavailable)
    {
        catalog = new ConsoleCatalog("", [], [], false);
        Console.Error.WriteLine("Каталог моделей недоступен. Можно проверить задачи; /models повторит загрузку каталога.");
    }
    var selected = catalog.Models.Contains(catalog.DefaultModel, StringComparer.Ordinal) ? catalog.DefaultModel : catalog.Models.FirstOrDefault();
    var session = await client.ResolveSessionAsync(Environment.GetEnvironmentVariable("DAMIR_SESSION_ID") ?? "local", stop.Token);
    string? pendingSkill = null;
    Submission? pending = null;
    string? activeTask = null;
    using var paste = new TerminalPasteMode(Console.Out, !Console.IsInputRedirected && !Console.IsOutputRedirected);
    paste.SetEnabled(true);
    Console.WriteLine($"Дамир готов. Модель: {selected ?? "не выбрана"}. Сессия: {session.SessionId}. Команды: /help.");
    if (await client.LatestAsync(session.SessionId, stop.Token) is { } latest)
        Console.WriteLine($"Последняя задача: {latest.TaskId}, {latest.Status}. /task {latest.TaskId} — посмотреть результат.");

    async Task ShowTaskAsync(string id, CancellationToken ct)
    {
        activeTask = id;
        Console.WriteLine($"Задача: {id}. Ctrl+C — запросить отмену.");
        var task = await client.WaitAsync(id, ct);
        activeTask = null;
        Console.WriteLine(task.Result?.Response ?? task.Result?.Error ?? (task.Status == TaskStatus.CANCELLED ? "Задача отменена." : task.Status.ToString()));
    }

    while (!stop.IsCancellationRequested)
    {
        Console.Write("> ");
        var read = await ConsoleInput.ReadAsync(Console.In, Console.Out, false, stop.Token);
        if (read.EndOfInput) break;
        if (read.Incomplete) { Console.Error.WriteLine("Вставка не принята: сообщение слишком большое или неполное."); continue; }
        var input = read.Text!; var isPaste = read.IsPaste;
        if (!isPaste && input.Trim() is "/multi" or "/multiline")
        {
            paste.SetEnabled(false);
            string? multiline;
            try { multiline = await ConsoleInput.ReadMultilineAsync(Console.In, Console.Out, stop.Token); }
            finally { paste.SetEnabled(true); }
            if (multiline is null) continue;
            input = multiline; isPaste = true;
        }
        var command = input.Trim();
        if (!isPaste && command == "/exit") break;
        turn = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        try
        {
            if (!isPaste && command == "/help")
            {
                Console.WriteLine("/models — выбрать модель\n/tokens — расход токенов\n/context — заполнение контекста\n/compress — сжать контекст\n/skill [имя|0] — выбрать или отменить навык\n/multi — многострочное сообщение\n/session [ID] — показать или открыть сессию\n/new — новая сессия\n/task ID — дождаться задачи\n/cancel ID — отменить задачу\n/retry — повторить отправку с тем же ключом\n/discard — забыть неясную отправку (задача могла быть принята)\n/github-login, /github-logout — подключить или отключить GitHub\n/exit — выход");
                continue;
            }
            if (!isPaste && command == "/tokens")
            {
                var usage = await client.UsageAsync(session.SessionId, turn.Token);
                Console.WriteLine($"{(usage.MissingUsageResponses > 0 ? "≥" : "")}{usage.Total}"); continue;
            }
            if (!isPaste && command == "/session") { Console.WriteLine(session.SessionId); continue; }
            if (!isPaste && command.StartsWith("/task ", StringComparison.Ordinal)) { await ShowTaskAsync(command[6..].Trim(), turn.Token); continue; }
            if (!isPaste && command.StartsWith("/cancel ", StringComparison.Ordinal))
            { var task = await client.CancelAsync(command[8..].Trim(), turn.Token); Console.WriteLine($"Запрос отмены: {task.TaskId}, {task.Status}."); continue; }
            if (!isPaste && command == "/discard") { pending = null; Console.WriteLine("Ключ забыт. Уже принятую задачу это не отменяет."); continue; }
            if (pending is not null && (isPaste || command != "/retry"))
            { Console.WriteLine("Сначала /retry для проверки прежней отправки или /discard. Её задача могла быть принята."); continue; }
            if (!isPaste && command is "/github-login" or "damir github-login") return 71;
            if (!isPaste && command is "/github-logout" or "damir github-logout") return 72;
            if (!isPaste && (command == "/new" || command.StartsWith("/session ", StringComparison.Ordinal)))
            {
                session = command == "/new" ? await client.CreateSessionAsync(turn.Token) : await client.SessionAsync(command[9..].Trim(), turn.Token);
                pendingSkill = null; Console.WriteLine($"Сессия: {session.SessionId}"); continue;
            }
            if (!isPaste && command == "/models")
            {
                catalog = await client.CatalogAsync(turn.Token);
                for (var i = 0; i < catalog.Models.Length; i++) Console.WriteLine($"{i + 1}. {catalog.Models[i]}");
                Console.Write("Номер модели или 0 для возврата: ");
                paste.SetEnabled(false);
                string? choice;
                try { choice = await Console.In.ReadLineAsync(turn.Token); } finally { paste.SetEnabled(true); }
                if (int.TryParse(choice, out var number) && number >= 1 && number <= catalog.Models.Length) selected = catalog.Models[number - 1];
                Console.WriteLine($"Модель: {selected}"); continue;
            }
            if (!isPaste && (command == "/skill" || command.StartsWith("/skill ", StringComparison.Ordinal) || command.StartsWith("/skill\t", StringComparison.Ordinal)))
            {
                var name = command[6..].Trim();
                if (name == "0") { pendingSkill = null; Console.WriteLine("Выбор навыка отменён."); continue; }
                catalog = await client.CatalogAsync(turn.Token);
                if (name.Length == 0)
                {
                    for (var i = 0; i < catalog.Skills.Length; i++)
                        Console.WriteLine($"{i + 1}. {catalog.Skills[i].Name} — {new string(catalog.Skills[i].Description.Where(c => !char.IsControl(c)).Take(160).ToArray())}");
                    if (catalog.Skills.Length == 0) { Console.WriteLine("Навыков пока нет. Добавь их в .agents/skills/."); continue; }
                    Console.Write("Номер навыка или 0 для возврата: ");
                    paste.SetEnabled(false);
                    string? choice;
                    try { choice = await Console.In.ReadLineAsync(turn.Token); } finally { paste.SetEnabled(true); }
                    if (!int.TryParse(choice, out var number) || number < 1 || number > catalog.Skills.Length) continue;
                    name = catalog.Skills[number - 1].Name;
                }
                if (!catalog.Skills.Any(s => s.Name == name)) { Console.Error.WriteLine("Навык не найден."); continue; }
                pendingSkill = name; Console.WriteLine($"Навык {name} выбран для следующего сообщения."); continue;
            }
            var operation = !isPaste && command == "/compress" ? "compress" : !isPaste && command == "/context" ? "context" : "chat";
            if (!isPaste && command.StartsWith('/') && operation == "chat" && command != "/retry")
            { Console.WriteLine("Неизвестная команда. Глянь /help."); continue; }
            if (!isPaste && command == "/retry" && pending is null) { Console.WriteLine("Неясной отправки нет."); continue; }
            if (pending is null)
            {
                if (operation == "chat" && selected is null) { Console.Error.WriteLine("Сначала выбери модель через /models."); continue; }
                if (string.IsNullOrWhiteSpace(input)) continue;
                if (input.Length > 32000) { Console.Error.WriteLine("Сообщение длиннее 32000 символов."); continue; }
                pending = new Submission(session.SessionId, input, selected, operation == "chat" ? pendingSkill : null, operation, Guid.NewGuid().ToString("N"));
            }
            // Keep the receipt request alive briefly after Ctrl+C so we can cancel the actual server task.
            var submittedOperation = pending.Operation;
            var accepted = await client.SubmitAsync(pending, CancellationToken.None);
            pending = null;
            if (submittedOperation == "chat") pendingSkill = null;
            await ShowTaskAsync(accepted.TaskId, turn.Token);
        }
        catch (OperationCanceledException) when (turn.IsCancellationRequested)
        {
            if (activeTask is not null)
            {
                using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await client.CancelAsync(activeTask, cancel.Token); Console.WriteLine($"Запрошена отмена {activeTask}. /task {activeTask} — проверить завершение."); }
                catch (Exception e) when (e is BackendException or OperationCanceledException) { Console.Error.WriteLine($"Не удалось подтвердить отмену. /task {activeTask} — проверить состояние."); }
                activeTask = null;
            }
        }
        catch (Exception e) when (e is BackendException or JsonException or ArgumentException or InvalidOperationException)
        {
            if (e is BackendException { Status: HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Conflict or HttpStatusCode.TooManyRequests }) pending = null;
            Console.Error.WriteLine(e is BackendException or ArgumentException ? e.Message : "Некорректный ответ backend.");
            if (pending is not null) Console.Error.WriteLine($"Результат отправки неизвестен. /retry использует прежний ключ: {pending.RequestKey}");
            if (activeTask is not null) { Console.Error.WriteLine($"Задача могла продолжить работу. /task {activeTask}"); activeTask = null; }
        }
        finally { turn.Dispose(); turn = null; }
    }
    if (pending is not null) Console.Error.WriteLine($"Осталась неясная отправка. Сессия: {pending.SessionId}; Idempotency-Key: {pending.RequestKey}. Проверь backend перед повторной отправкой.");
    return 0;
}
catch (OperationCanceledException)
{ if (!stop.IsCancellationRequested) Console.Error.WriteLine("Не удалось дождаться готовности backend."); return stop.IsCancellationRequested ? 0 : 1; }
catch (UriFormatException) { Console.Error.WriteLine("Некорректный адрес backend."); return 1; }
catch (Exception e) when (e is BackendException or ArgumentException or JsonException or InvalidOperationException)
{ Console.Error.WriteLine(e is BackendException or ArgumentException ? e.Message : "Не удалось запустить HTTP-консоль."); return 1; }
