using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiAgent.Telegram;

public sealed record TelegramUser(long Id, bool IsBot, string? Username);
public sealed record TelegramChat(long Id, string Type);
public sealed record TelegramMessage(long MessageId, TelegramUser? From, TelegramChat Chat, string? Text);
public sealed record TelegramUpdate(long UpdateId, TelegramMessage? Message);
public sealed class TelegramException(string message, bool fatal = false, int retryAfter = 2, int? statusCode = null) : Exception(message)
{
    public bool Fatal { get; } = fatal;
    public int RetryAfter { get; } = Math.Clamp(retryAfter, 1, 300);
    public int? StatusCode { get; } = statusCode;
}

/// <summary>Official Bot API over HTTPS. Errors never retain URLs, tokens or response bodies.</summary>
public sealed class TelegramApi(HttpClient http, string token)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private async Task<JsonElement> CallAsync(string method, object body, CancellationToken ct)
    {
        using var content = JsonContent.Create(body, options: Json);
        return await CallContentAsync(method, content, ct);
    }

    private async Task<JsonElement> CallContentAsync(string method, HttpContent body, CancellationToken ct)
    {
        try
        {
            using var response = await http.PostAsync("https://api.telegram.org/bot" + token + "/" + method, body, ct);
            if ((int)response.StatusCode is 400 or 401 or 403 or 409)
                throw new TelegramException($"Telegram отклонил запрос ({(int)response.StatusCode}). Проверь токен, доступ бота и отсутствие второго процесса.", fatal: true, statusCode: (int)response.StatusCode);
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = json.RootElement;
            if (!response.IsSuccessStatusCode || !root.GetProperty("ok").GetBoolean())
            {
                var retry = root.TryGetProperty("parameters", out var parameters) && parameters.TryGetProperty("retry_after", out var value) && value.TryGetInt32(out var seconds) ? seconds : 2;
                throw new TelegramException("Telegram временно не принял запрос.", retryAfter: retry);
            }
            return root.GetProperty("result").Clone();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (TelegramException) { throw; }
        catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException or OperationCanceledException)
        { throw new TelegramException("Не удалось получить корректный ответ Telegram."); }
    }


    public async Task<TelegramUser> IdentityAsync(CancellationToken ct) =>
        (await CallAsync("getMe", new { }, ct)).Deserialize<TelegramUser>(Json) ?? throw new TelegramException("Telegram не вернул данные бота.", true);
    public async Task EnsurePollingAsync(CancellationToken ct)
    {
        var webhook = await CallAsync("getWebhookInfo", new { }, ct);
        if (webhook.GetProperty("url").GetString() is { Length: > 0 })
            throw new TelegramException("У бота настроен webhook. Отключи его перед запуском получения сообщений.", true);
    }
    public async Task<TelegramUpdate[]> UpdatesAsync(long offset, CancellationToken ct, int timeout = 20) =>
        (await CallAsync("getUpdates", new { offset, timeout, limit = 20, allowed_updates = new[] { "message" } }, ct)).Deserialize<TelegramUpdate[]>(Json)
        ?? throw new TelegramException("Telegram не вернул список обновлений.");
    public async Task SendAsync(long chatId, string text, CancellationToken ct)
    {
        _ = await CallAsync("sendMessage", new { chat_id = chatId, text, link_preview_options = new { is_disabled = true } }, ct);
    }
    public async Task SendMenuAsync(long chatId, string text, CancellationToken ct, bool personal = true)
    {
        var keyboard = personal ? new[] { new[] { new { text = "Продолжить здесь" }, new { text = "На компьютер" } }, new[] { new { text = "Остановить" }, new { text = "Команды" } }, new[] { new { text = "Помощь" } } }
            : new[] { new[] { new { text = "Новый диалог" }, new { text = "Остановить" } }, new[] { new { text = "Помощь" } } };
        _ = await CallAsync("sendMessage", new { chat_id = chatId, text, link_preview_options = new { is_disabled = true },
            reply_markup = new { keyboard, resize_keyboard = true, is_persistent = true } }, ct);
    }
    public async Task SetSimpleCommandsAsync(CancellationToken ct, bool personal = false)
    {
        var commands = new[] { new { command = "start", description = "Открыть Дамира" }, new { command = "help", description = "Помощь" } };
        if (personal) commands = [..commands, new { command = "commands", description = "Меню команд" }];
        _ = await CallAsync("setMyCommands", new { commands }, ct);
    }
    public Task SendCommandsAsync(long chatId, CancellationToken ct) => SendButtonsAsync(chatId, "Выбери действие:",
        [["Сжать контекст", "Заполнение контекста"], ["Расход токенов", "Состояние ответа"], ["Выбрать модель", "Выбрать навык"], ["История диалога", "Текущий проект"], ["Назад"]], ct);
    public async Task SendButtonsAsync(long chatId, string text, string[][] buttons, CancellationToken ct)
    {
        _ = await CallAsync("sendMessage", new { chat_id = chatId, text,
            reply_markup = new { keyboard = buttons.Select(row => row.Select(label => new { text = label }).ToArray()).ToArray(), resize_keyboard = true } }, ct);
    }

    public async Task SendDocumentAsync(long chatId, string filename, byte[] content, CancellationToken ct)
    {
        using var body = new MultipartFormDataContent();
        body.Add(new StringContent(chatId.ToString(System.Globalization.CultureInfo.InvariantCulture)), "chat_id");
        body.Add(new ByteArrayContent(content), "document", filename);
        _ = await CallContentAsync("sendDocument", body, ct);
    }

    public static string[] SplitText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) text = "Ответ пуст.";
        var parts = new List<string>();
        for (var offset = 0; offset < text.Length;)
        {
            var length = Math.Min(4000, text.Length - offset);
            if (offset + length < text.Length && char.IsHighSurrogate(text[offset + length - 1])) length--;
            var part = text.Substring(offset, length);
            if (!string.IsNullOrWhiteSpace(part)) parts.Add(part);
            offset += length;
        }
        return parts.ToArray();
    }
}
