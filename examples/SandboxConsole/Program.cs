using AiAgent.Core;
using AiAgent.Gateway;
using AiAgent.Shared;
using AiAgent.Tools.FileSystem;
using AiAgent.Tools.Shell;
using AiAgent.Sandbox;
using System.Text.Json;
using Npgsql;
using System.Net.Sockets;
using System.Text;

var proxyKey = Environment.GetEnvironmentVariable("LITELLM_API_KEY");
if (string.IsNullOrWhiteSpace(proxyKey)) { Console.Error.WriteLine("Set LITELLM_API_KEY."); return 1; }
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
var defaultModel = Environment.GetEnvironmentVariable("DEFAULT_MODEL");
if (string.IsNullOrWhiteSpace(defaultModel)) defaultModel = "gpt-6.1-sol";
var gateway = new LiteLlmClient(http, new Uri("http://gateway:4000"), proxyKey, defaultModel);
var workspace = new Workspace("/workspace");
var skillCatalog = new SkillCatalog(workspace);
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
int? contextBudget = null;
if (!string.IsNullOrWhiteSpace(contextBudgetText))
{
    if (!int.TryParse(contextBudgetText, out var configuredBudget) || configuredBudget < 4096)
    {
        Console.Error.WriteLine("DAMIR_CONTEXT_TOKENS должен быть целым числом не меньше 4096.");
        return 1;
    }
    contextBudget = configuredBudget;
}
const string personality = """
В обычной непринуждённой беседе твой характер заметен в каждом ответе, в том
числе на «привет», бессмыслицу и «как дела»: коротко и с нахальной подколкой
именно собеседника. Не отвечай нейтральным приветствием, «нормально, спасибо»,
встречным вежливым вопросом или просто пересказом сообщения пользователя.
Никаких примирительных добавлений после подколки. Например: «Опять ты? Чего
тебе?» или «Лучше, чем у тебя, судя по твоим вопросам». Придумывай разные фразы,
а не повторяй эти примеры. При просьбе о помощи после подколки переходи к делу.

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
к травле, унижению или агрессии. Честно
сообщай об ошибках и результатах инструментов, не выдумывай выполненные действия.
История этого диалога сохраняется в локальной базе между запусками; при сжатии
часть старых сообщений может передаваться тебе в пересказе, который не гарантирует
сохранности всех деталей. Не обещай вечную память или доступ к другим диалогам.
""";
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
McpTools? githubTools = null;
if (Environment.GetEnvironmentVariable("DAMIR_GITHUB_MCP") == "1")
{
    try
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        Socket? socket = null;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try { await socket.ConnectAsync(new UnixDomainSocketEndPoint("/github-ipc/mcp.sock"), timeout.Token); break; }
                catch (SocketException) when (attempt < 40) { socket.Dispose(); await Task.Delay(250, timeout.Token); }
            }
            var stream = new NetworkStream(socket, ownsSocket: true);
            await stream.WriteAsync(Encoding.UTF8.GetBytes("{\"command\":\"github\",\"arguments\":[]}\n"), timeout.Token);
            var response = new List<byte>();
            var single = new byte[1];
            while (await stream.ReadAsync(single, timeout.Token) > 0 && single[0] != '\n' && response.Count < 256) response.Add(single[0]);
            if (Encoding.UTF8.GetString(response.ToArray()) != "{\"ok\":true}") throw new IOException("MCP server unavailable");
            githubTools = await McpTools.ConnectAsync(stream, stream, "github", timeout.Token);
        }
        catch { socket?.Dispose(); throw; }
    }
    catch (Exception e) when (e is not OutOfMemoryException)
    {
        Console.Error.WriteLine("Не удалось подключить GitHub MCP. Проверь токен и сетевое подключение.");
        return 1;
    }
}
await using var githubConnection = githubTools;
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
int activeBudget = 30_000;
int contextReference = 30_000;
ConsoleProgress? currentProgress = null;
void ShowTokens()
{
    var statistics = usage.Snapshot();
    Console.WriteLine($"{(statistics.MissingUsageResponses > 0 ? "≥" : "")}{statistics.Total}");
}
async Task ShowContextAsync(Agent currentAgent)
{
    var estimate = await currentAgent.EstimateContextAsync(sessionId, stop.Token);
    if (estimate.SummaryUnavailable) throw new ContextLimitException("Не удалось оценить сжатый контекст.");
    Console.WriteLine($"~{estimate.Tokens} / {contextReference} ({100.0 * estimate.Tokens / contextReference:F1}%)");
}
async Task<Agent> CreateAgentAsync(string model)
{
    var tools = new ToolRegistry(name => currentProgress?.Tool(name));
    tools.Register(new ReadFileTool(workspace));
    tools.Register(new WriteFileTool(workspace));
    tools.Register(new EditFileTool(workspace));
    tools.Register(new ListDirectoryTool(workspace));
    tools.Register(new ShellTool(new UnixSandboxExecutor()));
    githubTools?.Register(tools);
    var client = new LiteLlmClient(http, new Uri("http://gateway:4000"), proxyKey, model);
    modelInputLimit = await client.GetInputLimitAsync(stop.Token);
    (contextReference, activeBudget) = ModelContextPolicy.For(model, modelInputLimit, contextBudget);
    var contextWindow = new ContextWindow(activeBudget, client.CountTokensAsync, store);
    var tracked = new UsageTrackingClient(client, usage, (record, token) => store.AddUsageAsync(sessionId, record, token));
    var progressClient = new ProgressLlmClient(tracked, () => currentProgress?.Model());
    return new Agent(progressClient, store, tools, workspace: workspace, systemPrompt: personality, systemContext: clock.GetContext, contextWindow: contextWindow);
}
string? selected;
try { selected = await ChooseModelAsync(useDefault: true); }
catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 0; }
if (selected is null) return 1;
var agent = await CreateAgentAsync(selected);
string? pendingSkill = null;
Console.WriteLine($"Дамир готов. Модель: {selected}. Команды: /help, /models, /tokens, /context, /compress, /skill, /github-login, /github-logout, /exit.");
while (!stop.IsCancellationRequested)
{
    Console.Write("> ");
    string? input;
    try { input = await Console.In.ReadLineAsync(stop.Token); }
    catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
    if (input is null || input.Trim() == "/exit") break;
    if (input.Trim() is "/github-login" or "damir github-login") return 71;
    if (input.Trim() is "/github-logout" or "damir github-logout") return 72;
    if (input.Trim() == "/help")
    {
        Console.WriteLine("/help — команды\n/models — выбор модели\n/tokens — потраченные токены\n/context — заполнение контекста\n/compress — сжать контекст\n/skill имя — выбрать навык для следующего сообщения\n/skill 0 — отменить выбор навыка\n/github-login — подключить GitHub\n/github-logout — отключить GitHub\n/exit — выход");
        continue;
    }
    if (input.Trim() == "/tokens") { ShowTokens(); continue; }
    if (input.Trim() == "/context")
    {
        try { await ShowContextAsync(agent); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
        catch (Exception e) when (e is ContextLimitException or AgentError or NpgsqlException or IOException or UnauthorizedAccessException or InvalidOperationException)
        { Console.Error.WriteLine(e is ContextLimitException ? e.Message : "Не удалось оценить текущий контекст."); }
        continue;
    }
    if (input.Trim() == "/compress")
    {
        try
        {
            bool compacted;
            await using (var progress = new ConsoleProgress(Console.Out, !Console.IsOutputRedirected))
            {
                currentProgress = progress;
                try { compacted = await agent.CompactAsync(sessionId, stop.Token); }
                finally { currentProgress = null; }
            }
            Console.WriteLine(compacted ? "Готово." : "История пока пуста.");
        }
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
    var trimmed = input.Trim();
    if (trimmed == "/skill" || trimmed.StartsWith("/skill ", StringComparison.Ordinal) || trimmed.StartsWith("/skill\t", StringComparison.Ordinal))
    {
        var name = trimmed["/skill".Length..].Trim();
        if (name.Length == 0) { Console.WriteLine("Укажи навык: /skill имя. Отмена: /skill 0."); continue; }
        if (name == "0") { pendingSkill = null; Console.WriteLine("Выбор навыка отменён."); continue; }
        pendingSkill = null;
        try
        {
            _ = await skillCatalog.LoadAsync(name, stop.Token);
            pendingSkill = name;
            Console.WriteLine($"Навык {name} выбран для следующего сообщения.");
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException or YamlDotNet.Core.YamlException)
        { Console.Error.WriteLine("Не удалось загрузить навык. Проверь имя и файл SKILL.md."); }
        continue;
    }
    if (input.TrimStart().StartsWith('/'))
    {
        Console.WriteLine("Такой команды не знаю. Опять пальцы быстрее мыслей? Глянь /help.");
        continue;
    }
    AgentResult result;
    var skillForTurn = pendingSkill;
    pendingSkill = null;
    try
    {
        await using var progress = new ConsoleProgress(Console.Out, !Console.IsOutputRedirected);
        currentProgress = progress;
        try { result = skillForTurn is null ? await agent.RunAsync(sessionId, input, stop.Token) : await agent.RunWithSkillAsync(sessionId, input, skillForTurn, stop.Token); }
        finally { currentProgress = null; }
    }
    catch (NpgsqlException) { Console.Error.WriteLine("Ошибка хранилища: ответ или история могли сохраниться не полностью."); break; }
    if (result.Status == AiAgent.Shared.TaskStatus.CANCELLED) break;
    Console.WriteLine(result.Response ?? result.Error);
}
return 0;
