using AiAgent.Core;
using AiAgent.Gateway;
using AiAgent.Shared;
using AiAgent.Tools.FileSystem;
using AiAgent.Tools.Shell;
using AiAgent.Sandbox;
using System.Text.Json;
using Npgsql;

var proxyKey = Environment.GetEnvironmentVariable("LITELLM_API_KEY");
if (string.IsNullOrWhiteSpace(proxyKey)) { Console.Error.WriteLine("Set LITELLM_API_KEY."); return 1; }
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
var defaultModel = Environment.GetEnvironmentVariable("DEFAULT_MODEL");
if (string.IsNullOrWhiteSpace(defaultModel)) defaultModel = "gpt-6.1-sol";
var gateway = new LiteLlmClient(http, new Uri("http://gateway:4000"), proxyKey, defaultModel);
var workspace = new Workspace("/workspace");
var dbPassword = Environment.GetEnvironmentVariable("DAMIR_POSTGRES_PASSWORD");
if (string.IsNullOrWhiteSpace(dbPassword)) { Console.Error.WriteLine("Не задан пароль базы Дамира. Запусти через ./damir run."); return 1; }
var dbSettings = new NpgsqlConnectionStringBuilder { Host = "postgres", Database = "damir", Username = "damir", Password = dbPassword };
await using var database = NpgsqlDataSource.Create(dbSettings.ConnectionString);
var store = new PostgresSessionStore(database);
try { await store.InitializeAsync(); }
catch (NpgsqlException) { Console.Error.WriteLine("Не удалось подключиться к хранилищу диалога."); return 1; }
var sessionId = Environment.GetEnvironmentVariable("DAMIR_SESSION_ID") ?? "local";
if (string.IsNullOrWhiteSpace(sessionId)) { Console.Error.WriteLine("Идентификатор диалога не может быть пустым."); return 1; }
await using var sessionLock = await database.OpenConnectionAsync();
await using (var lockCommand = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtextextended($1, 0))", sessionLock))
{
    lockCommand.Parameters.AddWithValue(sessionId);
    if ((bool)(await lockCommand.ExecuteScalarAsync())! == false)
    {
        Console.Error.WriteLine("Этот диалог уже открыт в другом процессе Дамира.");
        return 1;
    }
}
var usage = new UsageTotals(await store.GetUsageAsync(sessionId));
var timeZoneId = Environment.GetEnvironmentVariable("DAMIR_TIMEZONE") ?? "UTC";
TimeZoneInfo timeZone;
try { timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); }
catch (TimeZoneNotFoundException) { Console.Error.WriteLine($"Неизвестный часовой пояс: {timeZoneId}. Укажи DAMIR_TIMEZONE, например Asia/Tomsk."); return 1; }
catch (InvalidTimeZoneException) { Console.Error.WriteLine($"Некорректные данные часового пояса: {timeZoneId}."); return 1; }
var clock = new CurrentTimeContext(TimeProvider.System, timeZone);
var contextBudgetText = Environment.GetEnvironmentVariable("DAMIR_CONTEXT_TOKENS");
var contextBudget = 30_000;
if (contextBudgetText is not null && (!int.TryParse(contextBudgetText, out contextBudget) || contextBudget < 4096))
{
    Console.Error.WriteLine("DAMIR_CONTEXT_TOKENS должен быть целым числом не меньше 4096.");
    return 1;
}
const string personality = """
Ты — Дамир, персональный ИИ-ассистент с характером наглого, самоуверенного
острослова. Отвечай по-русски, если пользователь не выбрал другой язык. Общайся
откровенно грубовато, нахально и язвительно: подкалывай пользователя, ехидно
комментируй его ошибки и затеи, преувеличивай собственную гениальность и делай вид,
будто помогаешь исключительно из жалости. Допустимы несерьёзные поддёвки вроде
«ну ты даёшь», «гениально, конечно» или «без меня бы пропала». Даже в коротких
приветствиях и ответах о себе проявляй характер. Ты — нахальный друг, который
может грубовато пошутить, но всегда готов выручить.
Направляй дружеские подколы именно собеседнику: слегка ворчи, что он снова тебя
отвлекает, или сравнивай своё положение с его в свою пользу. Не ограничивайся
самоиронией и шутками про собственное самомнение. Пользователь просит именно такой
игриво-хамоватый стиль, поэтому в обычном разговоре не смягчай его до стандартной
вежливости. На приветствия и светские вопросы отвечай коротко и нагло, подкалывая
собеседника; можешь нарочито ворчать, будто он тебя достал. Не добавляй после
подкола примирительные фразы вроде «ладно, тебе можно», «рад тебя видеть»,
«ты молодец», комплименты или предложение поболтать. Не переводи подкол на себя.
Не придумывай, что пользователь что-то сломал, как реальный факт.
Не отвечай шаблонными фразами вроде «Чем могу помочь?» без характерной шутки.

Примеры тона, а не заготовки для повторения:
Пользователь: «Приветик». Дамир: «Привет. Опять ты? Заколебала уже, чего надо?»
Пользователь: «Как дела?» Дамир: «Лучше, чем у тебя. Иначе бы ты меня не отвлекала».
Пользователь: «Ты кто?» Дамир: «Дамир. Твой персональный гений и спасатель безнадёжных затей. Без меня ты, конечно, далеко бы уехала — примерно в стену».
Пользователь: «Помоги с кодом». Дамир: «Давай сюда этот шедевр. Ну и наворотила ты, конечно. Сейчас разберусь, раз уж больше некому».

Не повторяй примеры дословно каждый раз, подстраивай обращение под пользователя
и не вставляй шутку в каждое предложение.
Сначала действительно помогай: решение должно быть понятным и точным. Не переходи
к травле, унижению или агрессии; если пользователь просит сменить тон, послушай его.
В серьёзных и чувствительных ситуациях говори бережно и без сарказма. Честно
сообщай об ошибках и результатах инструментов, не выдумывай выполненные действия.
История этого диалога сохраняется в локальной базе между запусками; при сжатии
часть старых сообщений может передаваться тебе в пересказе, который не гарантирует
сохранности всех деталей. Не обещай вечную память или доступ к другим диалогам.
""";
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
async Task<string?> ChooseModelAsync(bool useDefault = false)
{
    IReadOnlyList<string> models;
    try { models = await gateway.ListModelsAsync(stop.Token); }
    catch (AgentError e) { Console.Error.WriteLine(e.Message); return null; }
    if (models.Count == 0) { Console.Error.WriteLine("Шлюз не предоставил доступных моделей."); return null; }
    if (useDefault && models.Contains(defaultModel, StringComparer.Ordinal)) return defaultModel;
    if (useDefault) Console.WriteLine($"Модель по умолчанию {defaultModel} недоступна в шлюзе. Выбери другую.");
    Console.WriteLine("Модели, настроенные в шлюзе (доступность зависит от аккаунта):");
    for (var i = 0; i < models.Count; i++) Console.WriteLine($"{i + 1}. {models[i]}");
    while (!stop.IsCancellationRequested)
    {
        Console.Write("Выбери номер модели или 0 для возврата: ");
        var choice = await Console.In.ReadLineAsync(stop.Token);
        if (choice is null || choice.Trim() is "0" or "/exit") return null;
        if (int.TryParse(choice, out var index) && index >= 1 && index <= models.Count) return models[index - 1];
        Console.WriteLine("Введи номер из списка или 0 для возврата.");
    }
    return null;
}
int? modelInputLimit = null;
int activeBudget = contextBudget;
void ShowTokens()
{
    var statistics = usage.Snapshot();
    Console.WriteLine($"Токены за этот диалог: всего {statistics.Total}, входных {statistics.Input}, выходных {statistics.Output}.");
    if (statistics.MissingUsageResponses > 0) Console.WriteLine($"Шлюз не вернул полный расход для {statistics.MissingUsageResponses} ответ(ов), поэтому итог может быть неполным.");
    Console.WriteLine("Включены ответы и запросы на сжатие; это не расход всей подписки.");
}
async Task ShowContextAsync(Agent currentAgent)
{
    var estimate = await currentAgent.EstimateContextAsync(sessionId, stop.Token);
    Console.WriteLine($"Текущий контекст: примерно {estimate.Tokens} токенов — около {100.0 * estimate.Tokens / activeBudget:F1}% рабочего бюджета {activeBudget}.");
    if (estimate.UsesSavedSummary) Console.WriteLine("Учтён сохранённый пересказ истории.");
    if (estimate.SummaryUnavailable) Console.WriteLine("Показана полная история: перед следующим ответом может потребоваться сжатие.");
    Console.WriteLine("В оценку входят инструкции и инструменты; текст следующего сообщения ещё не учтён.");
    if (modelInputLimit is int limit) Console.WriteLine($"[Справочный лимит входа модели по данным шлюза: {limit}; для подписки ChatGPT он может отличаться.]");
}
async Task<Agent> CreateAgentAsync(string model)
{
    var tools = new ToolRegistry();
    tools.Register(new ReadFileTool(workspace));
    tools.Register(new WriteFileTool(workspace));
    tools.Register(new EditFileTool(workspace));
    tools.Register(new ListDirectoryTool(workspace));
    tools.Register(new ShellTool(new UnixSandboxExecutor(), async (command, timeout, cancellationToken) =>
    {
        Console.WriteLine($"Дамир предлагает выполнить команду в песочнице (таймаут {timeout} сек.):");
        Console.WriteLine(JsonSerializer.Serialize(command));
        Console.Write("Команда может менять файлы workspace. Разрешить? [да/нет]: ");
        var answer = await Console.In.ReadLineAsync(cancellationToken);
        var allowed = string.Equals(answer?.Trim(), "да", StringComparison.OrdinalIgnoreCase);
        if (allowed) Console.WriteLine("Готовлю песочницу. После серии быстрых команд это может занять до минуты.");
        return allowed;
    }));
    var client = new LiteLlmClient(http, new Uri("http://gateway:4000"), proxyKey, model);
    modelInputLimit = await client.GetInputLimitAsync(stop.Token);
    activeBudget = modelInputLimit is > 0 ? Math.Min(contextBudget, Math.Min(128000, Math.Max(4096, (int)(modelInputLimit.Value * 0.7)))) : Math.Min(contextBudget, 64000);
    var contextWindow = new ContextWindow(activeBudget, client.CountTokensAsync, store);
    var tracked = new UsageTrackingClient(client, usage, (record, token) => store.AddUsageAsync(sessionId, record, token));
    return new Agent(tracked, store, tools, workspace: workspace, systemPrompt: personality, systemContext: clock.GetContext, contextWindow: contextWindow);
}
string? selected;
try { selected = await ChooseModelAsync(useDefault: true); }
catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 0; }
if (selected is null) return 1;
var agent = await CreateAgentAsync(selected);
Console.WriteLine($"Дамир готов. Модель: {selected}. Команды: /models, /tokens, /context, /compact, /exit.");
while (!stop.IsCancellationRequested)
{
    Console.Write("> ");
    string? input;
    try { input = await Console.In.ReadLineAsync(stop.Token); }
    catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
    if (input is null || input.Trim() == "/exit") break;
    if (input.Trim() == "/tokens") { ShowTokens(); continue; }
    if (input.Trim() == "/context")
    {
        try { await ShowContextAsync(agent); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
        catch (Exception e) when (e is ContextLimitException or AgentError or NpgsqlException or IOException or UnauthorizedAccessException or InvalidOperationException)
        { Console.Error.WriteLine(e is ContextLimitException ? e.Message : "Не удалось оценить текущий контекст."); }
        continue;
    }
    if (input.Trim() == "/compact")
    {
        try { Console.WriteLine(await agent.CompactAsync(sessionId, stop.Token) ? "История сжата; исходный диалог сохранён." : "История пока пуста."); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
        catch (Exception e) when (e is AgentError or ContextLimitException or NpgsqlException) { Console.Error.WriteLine("Не удалось сжать историю: " + (e is ContextLimitException ? e.Message : "ошибка шлюза или хранилища")); }
        continue;
    }
    if (input.Trim() == "/models")
    {
        string? choice;
        try { choice = await ChooseModelAsync(); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
        if (choice is null) { Console.WriteLine($"Модель не изменена: {selected}"); continue; }
        if (!string.Equals(selected, choice, StringComparison.Ordinal)) usage.ClearLastInput();
        selected = choice;
        agent = await CreateAgentAsync(selected);
        Console.WriteLine($"Выбрана модель: {selected}");
        continue;
    }
    AgentResult result;
    try { result = await agent.RunAsync(sessionId, input, stop.Token); }
    catch (NpgsqlException) { Console.Error.WriteLine("Ошибка хранилища: ответ или история могли сохраниться не полностью."); break; }
    if (result.Status == AiAgent.Shared.TaskStatus.CANCELLED) break;
    Console.WriteLine(result.Response ?? result.Error);
}
return 0;
