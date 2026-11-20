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
    Console.WriteLine("Доступные модели шлюза:");
    for (var i = 0; i < models.Count; i++) Console.WriteLine($"{i + 1}. {models[i]}");
    while (!stop.IsCancellationRequested)
    {
        Console.Write("Выбери номер модели (или /exit): ");
        var choice = await Console.In.ReadLineAsync(stop.Token);
        if (choice is null || choice.Trim() == "/exit") return null;
        if (int.TryParse(choice, out var index) && index >= 1 && index <= models.Count) return models[index - 1];
        Console.WriteLine("Введи номер из списка.");
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
    return new Agent(new LiteLlmClient(http, new Uri("http://gateway:4000"), proxyKey, model), store, tools, workspace: workspace);
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
        try { selected = await ChooseModelAsync(); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
        if (selected is null) break;
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
