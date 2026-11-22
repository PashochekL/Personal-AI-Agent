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
        try
        {
            using var response = await http.PostAsJsonAsync("https://api.telegram.org/bot" + token + "/" + method, body, Json, ct);
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
    public async Task<TelegramUpdate[]> UpdatesAsync(long offset, CancellationToken ct) =>
        (await CallAsync("getUpdates", new { offset, timeout = 20, limit = 20, allowed_updates = new[] { "message" } }, ct)).Deserialize<TelegramUpdate[]>(Json)
        ?? throw new TelegramException("Telegram не вернул список обновлений.");
    public async Task SendAsync(long chatId, string text, CancellationToken ct)
    {
        _ = await CallAsync("sendMessage", new { chat_id = chatId, text, link_preview_options = new { is_disabled = true } }, ct);
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
