using AiAgent.Core;
using AiAgent.Gateway;
using AiAgent.Shared;
using AiAgent.Tools.FileSystem;

var proxyKey = Environment.GetEnvironmentVariable("LITELLM_API_KEY");
if (string.IsNullOrWhiteSpace(proxyKey)) { Console.Error.WriteLine("Set LITELLM_API_KEY."); return 1; }
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
var defaultModel = Environment.GetEnvironmentVariable("DEFAULT_MODEL");
if (string.IsNullOrWhiteSpace(defaultModel)) defaultModel = "gpt-6.1-sol";
var gateway = new LiteLlmClient(http, new Uri("http://gateway:4000"), proxyKey, defaultModel);
var workspace = new Workspace("/workspace");
var store = new SessionStore();
var timeZoneId = Environment.GetEnvironmentVariable("DAMIR_TIMEZONE") ?? "UTC";
TimeZoneInfo timeZone;
try { timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); }
catch (TimeZoneNotFoundException) { Console.Error.WriteLine($"Неизвестный часовой пояс: {timeZoneId}. Укажи DAMIR_TIMEZONE, например Asia/Tomsk."); return 1; }
catch (InvalidTimeZoneException) { Console.Error.WriteLine($"Некорректные данные часового пояса: {timeZoneId}."); return 1; }
var clock = new CurrentTimeContext(TimeProvider.System, timeZone);
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
Agent CreateAgent(string model)
{
    var tools = new ToolRegistry();
    tools.Register(new ReadFileTool(workspace));
    tools.Register(new WriteFileTool(workspace));
    tools.Register(new EditFileTool(workspace));
    tools.Register(new ListDirectoryTool(workspace));
    return new Agent(new LiteLlmClient(http, new Uri("http://gateway:4000"), proxyKey, model), store, tools, workspace: workspace, systemPrompt: personality, systemContext: clock.GetContext);
}
string? selected;
try { selected = await ChooseModelAsync(useDefault: true); }
catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 0; }
if (selected is null) return 1;
var agent = CreateAgent(selected);
Console.WriteLine($"Дамир готов. Модель: {selected}. Сменить: /models, выйти: /exit.");
while (!stop.IsCancellationRequested)
{
    Console.Write("> ");
    string? input;
    try { input = await Console.In.ReadLineAsync(stop.Token); }
    catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
    if (input is null || input.Trim() == "/exit") break;
    if (input.Trim() == "/models")
    {
        string? choice;
        try { choice = await ChooseModelAsync(); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
        if (choice is null) { Console.WriteLine($"Модель не изменена: {selected}"); continue; }
        selected = choice;
        agent = CreateAgent(selected);
        Console.WriteLine($"Выбрана модель: {selected}");
        continue;
    }
    var result = await agent.RunAsync("local", input, stop.Token);
    if (result.Status == AiAgent.Shared.TaskStatus.CANCELLED) break;
    Console.WriteLine(result.Response ?? result.Error);
}
return 0;

sealed class SessionStore : IContextStore
{
    private readonly Dictionary<string, List<Message>> _sessions = [];
    public Task<IReadOnlyList<Message>> GetMessagesAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<Message>>(_sessions.TryGetValue(sessionId, out var messages) ? messages.ToArray() : []);
    }
    public Task AppendMessageAsync(string sessionId, Message message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_sessions.TryGetValue(sessionId, out var messages)) _sessions[sessionId] = messages = [];
        messages.Add(message);
        return Task.CompletedTask;
    }
    public Task ReplaceMessagesAsync(string sessionId, IReadOnlyList<Message> messages, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _sessions[sessionId] = messages.ToList();
        return Task.CompletedTask;
    }
}
