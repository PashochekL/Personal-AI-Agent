using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AiAgent.Shared;
using TaskStatus = AiAgent.Shared.TaskStatus;

namespace AiAgent.Http;

public sealed record ConsoleCatalog(string DefaultModel, string[] Models, ConsoleSkill[] Skills, bool Github);
public sealed record ConsoleSkill(string Name, string Description);
public sealed record ConsoleUsage(long Total, int MissingUsageResponses);
public sealed record BackendIdentity(string OwnerId, string ProjectId);
public sealed record CompanionConnection(string SessionId, long? UserId, long UpdateOffset, string? PendingTask,
    string[]? Parts, int NextPart, string? Model, string? Skill, string Channel, long? PendingUpdate = null, string? PendingMessage = null,
    bool Connected = false, string WorkspaceName = "текущий проект", string PendingOperation = "chat");
public sealed record TelegramFile(string FileId, string Filename, byte[] Content);
public sealed record ConsoleResume(string SessionId, string? Model, string? Skill);
public sealed record Submission(string SessionId, string UserMessage, string? Model = null, string? Skill = null,
    string Operation = "chat", string? RequestKey = null, string Channel = "console");
public sealed class BackendException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>HTTP transport only. Sessions, history and execution belong to the backend.</summary>
public sealed class BackendClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private readonly HttpClient _http;
    private readonly string _key;

    public BackendClient(HttpClient http, Uri address, string key)
    {
        if (!address.IsAbsoluteUri || address.Scheme is not ("http" or "https") || address.AbsolutePath != "/" ||
            address.UserInfo.Length > 0 || address.Query.Length > 0 || address.Fragment.Length > 0)
            throw new ArgumentException("Некорректный адрес backend.");
        if (key.Length is < 24 or > 512 || key.Any(char.IsWhiteSpace)) throw new ArgumentException("Некорректный ключ backend.");
        _http = http; _http.BaseAddress = address; _key = key;
    }

    private static string Id(string id)
    {
        if (id.Length != 32 || id.Any(c => !char.IsAsciiHexDigit(c))) throw new ArgumentException("Некорректный ID.");
        return id;
    }

    private async Task<string> SendAsync(HttpMethod method, string path, object? body, string? key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        try
        {
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var message = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "Backend отклонил ключ доступа.",
                    HttpStatusCode.NotFound => "Сессия или задача не найдена в этом проекте.",
                    HttpStatusCode.Conflict => "Ключ повтора уже связан с другим запросом.",
                    HttpStatusCode.BadRequest => "Backend отклонил запрос. Проверь модель, навык и длину сообщения.",
                    HttpStatusCode.TooManyRequests => "Очередь сессии заполнена. Дождись завершения задач.",
                    (HttpStatusCode)423 => "Диалог открыт в другом месте. /console — продолжить на компьютере; /tg — открыть в Telegram. Если Дамир ещё отвечает, дождись завершения или останови ответ.",
                    HttpStatusCode.Forbidden => "Ссылка больше не действует или этот Telegram-аккаунт не привязан к Дамиру. Получи новую ссылку через /tg в консоли.",
                    _ => $"Backend вернул HTTP {(int)response.StatusCode}."
                };
                throw new BackendException(message, response.StatusCode);
            }
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (HttpRequestException) { throw new BackendException("Не удалось связаться с backend."); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new BackendException("Backend не ответил вовремя."); }
    }

    private static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Json) ?? throw new BackendException("Некорректный ответ backend.");
    public async Task ReadyAsync(CancellationToken ct) => _ = await SendAsync(HttpMethod.Get, "/health/ready", null, null, ct);
    public async Task<BackendIdentity> IdentityAsync(CancellationToken ct) => Read<BackendIdentity>(await SendAsync(HttpMethod.Get, "/identity", null, null, ct));
    public async Task SaveConsoleResumeAsync(string session, string? model, string? skill, CancellationToken ct) =>
        _ = await SendAsync(HttpMethod.Post, "/console/resume", new { session_id = Id(session), model, skill }, null, ct);
    public async Task<ConsoleResume?> ConsoleResumeAsync(CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, "/console/resume", null, null, ct);
        return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<ConsoleResume>(json, Json);
    }
    public async Task<string> TelegramLinkAsync(string session, long bot, string lease, string? model, string? skill, CancellationToken ct) =>
        Read<JsonElement>(await SendAsync(HttpMethod.Post, "/telegram/link", new { session_id = Id(session), bot_id = bot, lease, model, skill }, null, ct)).GetProperty("code").GetString()!;
    private static string CompanionPath(long bot, string lease)
    {
        if (bot <= 0) throw new ArgumentException("Invalid bot.");
        return $"/telegram/{bot}/{Id(lease)}";
    }
    public async Task<CompanionConnection> TelegramStateAsync(long bot, string lease, CancellationToken ct) =>
        Read<CompanionConnection>(await SendAsync(HttpMethod.Get, CompanionPath(bot, lease), null, null, ct));
    public async Task TelegramConnectAsync(long bot, string lease, string code, long user, CancellationToken ct) =>
        _ = await SendAsync(HttpMethod.Post, CompanionPath(bot, lease) + "/connect", new { code, user_id = user }, null, ct);
    public async Task TelegramResumeAsync(long bot, string lease, string session, long user, CancellationToken ct) =>
        _ = await SendAsync(HttpMethod.Post, CompanionPath(bot, lease) + "/resume", new { session_id = Id(session), user_id = user }, null, ct);
    public async Task TelegramSelectAsync(long bot, string lease, string session, long user, string? model, string? skill, CancellationToken ct) =>
        _ = await SendAsync(HttpMethod.Post, CompanionPath(bot, lease) + "/selection", new { session_id = Id(session), user_id = user, model, skill }, null, ct);
    public async Task TelegramCheckpointAsync(long bot, string lease, CompanionConnection state, CancellationToken ct) =>
        _ = await SendAsync(HttpMethod.Post, CompanionPath(bot, lease) + "/checkpoint", new { state.UpdateOffset, state.PendingTask, state.Parts, state.NextPart, state.PendingUpdate, state.PendingMessage, state.PendingOperation }, null, ct);
    public async Task TelegramReleaseAsync(long bot, string lease, CancellationToken ct) =>
        _ = await SendAsync(HttpMethod.Post, CompanionPath(bot, lease) + "/release", null, null, ct);
    public async Task ReturnToConsoleAsync(string session, CancellationToken ct) =>
        _ = await SendAsync(HttpMethod.Post, $"/sessions/{Id(session)}/console", null, null, ct);
    public async Task<TelegramFile[]> TelegramFilesAsync(string session, CancellationToken ct) =>
        Read<TelegramFile[]>(await SendAsync(HttpMethod.Get, $"/sessions/{Id(session)}/telegram-files", null, null, ct));
    public async Task AcknowledgeTelegramFileAsync(string session, string file, CancellationToken ct) =>
        _ = await SendAsync(HttpMethod.Post, $"/sessions/{Id(session)}/telegram-files/{Id(file)}/ack", null, null, ct);
    public async Task<IReadOnlyList<Message>> MessagesAsync(string session, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(await SendAsync(HttpMethod.Get, $"/sessions/{Id(session)}/messages", null, null, ct));
        return document.RootElement.EnumerateArray().Select(m => ContractJson.Deserialize<Message>(m.GetRawText())).ToArray();
    }
    public async Task<ConsoleCatalog> CatalogAsync(CancellationToken ct) => Read<ConsoleCatalog>(await SendAsync(HttpMethod.Get, "/console", null, null, ct));
    public async Task<ConsoleUsage> UsageAsync(string session, CancellationToken ct) => Read<ConsoleUsage>(await SendAsync(HttpMethod.Get, $"/sessions/{Id(session)}/usage", null, null, ct));
    public async Task<Session> SessionAsync(string id, CancellationToken ct) => ContractJson.Deserialize<Session>(await SendAsync(HttpMethod.Get, "/sessions/" + Id(id), null, null, ct));
    public async Task<Session> CreateSessionAsync(CancellationToken ct) => ContractJson.Deserialize<Session>(await SendAsync(HttpMethod.Post, "/sessions", null, null, ct));
    public async Task<Session> ResolveSessionAsync(string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(char.IsControl)) throw new ArgumentException("Некорректное имя сессии.");
        return ContractJson.Deserialize<Session>(await SendAsync(HttpMethod.Post, "/sessions/named/" + Uri.EscapeDataString(name), null, null, ct));
    }
    public async Task<AgentTask> SubmitAsync(Submission submission, CancellationToken ct)
    {
        _ = Id(submission.SessionId);
        if (string.IsNullOrWhiteSpace(submission.RequestKey)) throw new ArgumentException("Для отправки нужен ключ повтора.");
        return ContractJson.Deserialize<AgentTask>(await SendAsync(HttpMethod.Post, "/tasks",
            new { session_id = submission.SessionId, user_message = submission.UserMessage, model = submission.Model, skill = submission.Skill, operation = submission.Operation, channel = submission.Channel }, submission.RequestKey, ct));
    }
    public async Task<AgentTask> TaskAsync(string id, CancellationToken ct) => ContractJson.Deserialize<AgentTask>(await SendAsync(HttpMethod.Get, "/tasks/" + Id(id), null, null, ct));
    public async Task<AgentTask?> LatestAsync(string session, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, $"/sessions/{Id(session)}/tasks/latest", null, null, ct);
        return json.Length == 0 ? null : ContractJson.Deserialize<AgentTask>(json);
    }
    public async Task<AgentTask> CancelAsync(string id, CancellationToken ct) => ContractJson.Deserialize<AgentTask>(await SendAsync(HttpMethod.Post, $"/tasks/{Id(id)}/cancel", null, null, ct));
    public async Task<AgentTask> WaitAsync(string id, CancellationToken ct)
    {
        while (true)
        {
            var task = await TaskAsync(id, ct);
            if (task.Status is not (TaskStatus.PENDING or TaskStatus.RUNNING)) return task;
            await Task.Delay(250, ct);
        }
    }
}
