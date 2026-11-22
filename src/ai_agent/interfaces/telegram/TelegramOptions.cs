using System.Security.Cryptography;
using System.Text;

namespace AiAgent.Telegram;

public sealed class TelegramOptions
{
    public required string Token { get; init; }
    public long AllowedUserId { get; init; }
    public long[]? AllowedUserIds { get; init; }
    public long[] EffectiveUserIds => AllowedUserIds is not null ? AllowedUserIds.Distinct().ToArray() : AllowedUserId > 0 ? [AllowedUserId] : [];
    public bool Allows(long userId) => EffectiveUserIds.Contains(userId);
    public required string Project { get; init; }
    public string Owner { get; init; } = "local";
    public string SessionName { get; init; } = "local";
    public string StateDirectory { get; init; } = ".uv-cache/telegram-state";
    public bool IsolatedAccount { get; init; }
    public long BotId => long.Parse(Token.AsSpan(0, Token.IndexOf(':')));
    public string Scope => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Owner.Length + ":" + Owner + Project))).ToLowerInvariant();
    public string AccountOwner(long userId) => $"telegram:{BotId}:{userId}";
    public string IsolationId => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Owner + "\0" + Project))).ToLowerInvariant();
    public string AccountProject => "telegram-" + IsolationId;
    public TelegramOptions ForAccount(long userId) => new()
    {
        Token = Token, AllowedUserId = userId, Owner = AccountOwner(userId), Project = AccountProject,
        SessionName = SessionName, StateDirectory = StateDirectory, IsolatedAccount = true
    };

    public void Validate()
    {
        var separator = Token.IndexOf(':');
        if (separator is < 1 or > 20 || !long.TryParse(Token.AsSpan(0, separator), out var id) || id <= 0 ||
            Token.Length is < 20 or > 256 || separator >= Token.Length - 1 || Token[(separator + 1)..].Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')))
            throw new ArgumentException("Некорректный токен Telegram-бота.");
        if (EffectiveUserIds.Length is < 1 or > 32 || EffectiveUserIds.Any(id => id <= 0))
            throw new ArgumentException("Укажи от 1 до 32 положительных ID в TELEGRAM_ALLOWED_USER_IDS через запятую.");
        if (string.IsNullOrWhiteSpace(Project) || Project.Length > 256 || string.IsNullOrWhiteSpace(Owner) || Owner.Length > 256)
            throw new ArgumentException("Укажи владельца и проект серверной части.");
        if (string.IsNullOrWhiteSpace(SessionName) || SessionName.Length > 128 || SessionName.Any(char.IsControl)) throw new ArgumentException("Некорректное имя диалога.");
        if (string.IsNullOrWhiteSpace(StateDirectory)) throw new ArgumentException("Не указан каталог состояния Telegram.");
    }

    public static long[] ParseUserIds(string value)
    {
        var parts = value.Split(',');
        if (parts.Length is < 1 or > 32) throw new ArgumentException("Укажи от 1 до 32 Telegram ID через запятую.");
        var result = new List<long>();
        foreach (var part in parts)
        {
            var text = part.Trim();
            if (text.Length == 0 || text.Any(c => !char.IsAsciiDigit(c)) || !long.TryParse(text, out var id) || id <= 0)
                throw new ArgumentException("Список Telegram ID должен содержать положительные числа через запятую.");
            if (!result.Contains(id)) result.Add(id);
        }
        return result.ToArray();
    }

    public static TelegramOptions FromEnvironment() => new()
    {
        Token = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN") ?? "",
        AllowedUserId = long.TryParse(Environment.GetEnvironmentVariable("TELEGRAM_ALLOWED_USER_ID"), out var id) ? id : 0,
        AllowedUserIds = Environment.GetEnvironmentVariable("TELEGRAM_ALLOWED_USER_IDS") is { Length: > 0 } ids ? ParseUserIds(ids) : null,
        Project = Environment.GetEnvironmentVariable("DAMIR_MEMORY_PROJECT") ?? "",
        Owner = Environment.GetEnvironmentVariable("DAMIR_MEMORY_OWNER") ?? "local",
        SessionName = Environment.GetEnvironmentVariable("DAMIR_SESSION_ID") ?? "local",
        StateDirectory = Environment.GetEnvironmentVariable("DAMIR_TELEGRAM_STATE_DIR") ?? ".uv-cache/telegram-state"
    };
}
