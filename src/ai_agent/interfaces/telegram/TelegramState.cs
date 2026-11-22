using System.Text.Json;
using AiAgent.Http;

namespace AiAgent.Telegram;

public sealed record TelegramDelivery(long UpdateId, string SessionId, Submission? Request = null, string? TaskId = null,
    string[]? Parts = null, int NextPart = 0, bool CancelRequested = false);
public sealed record TelegramState(long BotId, long UserId, string Scope, string SessionId, long Offset = 0,
    string? Model = null, string? Skill = null, TelegramDelivery[]? Pending = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public TelegramDelivery[] Deliveries => Pending ?? [];
}

/// <summary>Transport checkpoint and outbox only; conversation history stays on the server.</summary>
public sealed class TelegramStateFile : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly FileStream _lease;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1);
    private TelegramState? _state;
    public TelegramState State => Volatile.Read(ref _state) ?? throw new InvalidOperationException("State is not initialized");

    public TelegramStateFile(TelegramOptions options)
    {
        options.Validate();
        Directory.CreateDirectory(options.StateDirectory);
        _path = Path.Combine(options.StateDirectory, options.IsolatedAccount ? $"{options.BotId}-user-{options.AllowedUserId}.json" : options.BotId + ".json");
        _lease = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            if (File.Exists(_path))
            {
                _state = JsonSerializer.Deserialize<TelegramState>(File.ReadAllText(_path), Json) ?? throw new InvalidDataException();
                if (_state.BotId != options.BotId || _state.UserId != options.AllowedUserId || _state.Scope != options.Scope)
                    throw new InvalidOperationException("Этот бот уже связан с другим владельцем или проектом. Используй отдельного бота и не удаляй состояние незавершённых задач.");
                static bool Id(string id) => id is { Length: 32 } && id.All(char.IsAsciiHexDigit);
                if (_state.Offset < 0 || _state.Deliveries.Length > 100 || !Id(_state.SessionId) ||
                    _state.Deliveries.Select(d => d.UpdateId).Distinct().Count() != _state.Deliveries.Length ||
                    _state.Deliveries.Any(d => d.UpdateId < 0 || d.UpdateId >= _state.Offset || !Id(d.SessionId) || d.NextPart < 0 ||
                        d.TaskId is not null && !Id(d.TaskId) || d.Parts is null && d.Request is null && d.TaskId is null ||
                        d.Parts is not null && (d.Parts.Length == 0 || d.NextPart > d.Parts.Length || d.Parts.Any(p => string.IsNullOrEmpty(p) || p.Length > 4000)) ||
                        d.Request is not null && (d.Request.SessionId != d.SessionId || d.Request.RequestKey != $"telegram:{options.BotId}:{d.UpdateId}")))
                    throw new InvalidDataException();
            }
        }
        catch { _lease.Dispose(); throw; }
    }

    public bool Initialized => _state is not null;
    public async Task ChangeAsync(Func<TelegramState?, TelegramState> change, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var next = change(_state);
            var temporary = _path + ".tmp";
            var bytes = JsonSerializer.SerializeToUtf8Bytes(next, Json);
            await using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { await file.WriteAsync(bytes, ct); await file.FlushAsync(ct); file.Flush(flushToDisk: true); }
            File.Move(temporary, _path, overwrite: true);
            Volatile.Write(ref _state, next);
        }
        finally { _gate.Release(); }
    }
    public void Dispose() { _lease.Dispose(); _gate.Dispose(); }
}
