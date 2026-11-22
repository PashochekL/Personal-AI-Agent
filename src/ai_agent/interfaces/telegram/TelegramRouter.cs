using System.Text.Json;
using AiAgent.Http;

namespace AiAgent.Telegram;

// Host-created credentials. A Telegram message can never select these fields.
public sealed record TelegramBackendProfile(long UserId, string Url, string ApiKey, string OwnerId, string ProjectId)
{
    public static TelegramBackendProfile[] Load(string path, TelegramOptions root)
    {
        if (!File.Exists(path))
            throw new ArgumentException("Не найден файл изолированных серверов Telegram. Останови прежний запуск и снова выполни damir telegram-isolated через актуальный launcher.");
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
        return Validate(JsonSerializer.Deserialize<TelegramBackendProfile[]>(File.ReadAllText(path), json) ?? [], root);
    }

    public static TelegramBackendProfile[] Validate(TelegramBackendProfile[] profiles, TelegramOptions root)
    {
        root.Validate();
        if (profiles.Length != root.EffectiveUserIds.Length || profiles.Select(p => p.UserId).Distinct().Count() != profiles.Length ||
            profiles.Select(p => p.ApiKey).Distinct().Count() != profiles.Length || profiles.Select(p => p.Url).Distinct().Count() != profiles.Length ||
            profiles.Any(p => !root.Allows(p.UserId) || p.OwnerId != root.AccountOwner(p.UserId) || p.ProjectId != root.AccountProject ||
                string.IsNullOrWhiteSpace(p.ApiKey) || p.ApiKey.Length is < 24 or > 512 || p.ApiKey.Any(char.IsWhiteSpace) ||
                !Uri.TryCreate(p.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.AbsolutePath != "/" ||
                uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0))
            throw new ArgumentException("Некорректная конфигурация изолированных серверов Telegram. Общий сервер или ключ нельзя назначать нескольким аккаунтам.");
        return profiles;
    }
}

/// <summary>One Telegram poller, isolated backend credentials and durable account state per sender.</summary>
public sealed class TelegramRouter : IDisposable
{
    private readonly TelegramOptions _options;
    private readonly TelegramApi _telegram;
    private readonly TextWriter _log;
    private readonly Dictionary<long, Account> _accounts = [];
    private readonly FileStream _lease;
    private readonly FileStream _legacyLease;
    private readonly string _checkpoint;
    private long _offset;
    private sealed record Account(TelegramBot Bot, TelegramStateFile State, HttpClient Http, BackendClient Backend, TelegramBackendProfile Profile);
    private sealed record Checkpoint(long BotId, string Scope, long Offset);
    public long Offset => _offset;
    public TelegramState AccountState(long userId) => _accounts[userId].State.State;

    public TelegramRouter(TelegramApi telegram, TelegramOptions options, TelegramBackendProfile[] profiles, TextWriter log,
        Func<TelegramBackendProfile, HttpClient>? clients = null)
    {
        _telegram = telegram; _options = options; _log = log;
        TelegramBackendProfile.Validate(profiles, options);
        Directory.CreateDirectory(options.StateDirectory);
        _checkpoint = Path.Combine(options.StateDirectory, options.BotId + ".router.json");
        _lease = new FileStream(Path.Combine(options.StateDirectory, options.BotId + ".receiver.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            // Also excludes the previous single-account receiver using this same state volume.
            _legacyLease = new FileStream(Path.Combine(options.StateDirectory, options.BotId + ".json.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (File.Exists(_checkpoint))
            {
                var saved = JsonSerializer.Deserialize<Checkpoint>(File.ReadAllText(_checkpoint)) ?? throw new InvalidDataException();
                if (saved.BotId != options.BotId || saved.Scope != options.Scope || saved.Offset < 0) throw new InvalidDataException("Router scope mismatch");
                _offset = saved.Offset;
            }
            else
            {
                var legacyPath = Path.Combine(options.StateDirectory, options.BotId + ".json");
                if (File.Exists(legacyPath))
                {
                    var old = JsonSerializer.Deserialize<TelegramState>(File.ReadAllText(legacyPath)) ?? throw new InvalidDataException();
                    if (old.BotId != options.BotId || old.Scope != options.Scope || old.Offset < 0) throw new InvalidDataException("Legacy scope mismatch");
                    if (old.Deliveries.Length > 0) throw new InvalidOperationException("В старом режиме остались недоставленные ответы. Сначала выполни damir telegram-drain с прежним ID и тем же проектом.");
                    _offset = old.Offset;
                }
            }
            foreach (var profile in profiles)
            {
                var http = clients?.Invoke(profile) ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
                TelegramStateFile? state = null;
                try
                {
                    state = new TelegramStateFile(options.ForAccount(profile.UserId));
                    var backend = new BackendClient(http, new Uri(profile.Url), profile.ApiKey);
                    _accounts.Add(profile.UserId, new(new TelegramBot(telegram, backend, state, options.ForAccount(profile.UserId), log), state, http, backend, profile));
                }
                catch { state?.Dispose(); http.Dispose(); throw; }
            }
        }
        catch { Dispose(); throw; }
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        var identity = await _telegram.IdentityAsync(ct);
        if (!identity.IsBot || identity.Id != _options.BotId) throw new TelegramException("Токен не соответствует боту.", true);
        await _telegram.EnsurePollingAsync(ct);
        await _telegram.SetSimpleCommandsAsync(ct);
        foreach (var account in _accounts.Values)
        {
            var serverIdentity = await account.Backend.IdentityAsync(ct);
            if (serverIdentity.OwnerId != account.Profile.OwnerId || serverIdentity.ProjectId != account.Profile.ProjectId)
                throw new ArgumentException("Сервер не соответствует изолированному аккаунту. Запуск Telegram остановлен.");
            await account.Bot.InitializeAsync(ct, verifyTelegram: false);
        }
        await SaveOffsetAsync(_offset, ct);
    }

    public async Task HandleAsync(TelegramUpdate update, CancellationToken ct)
    {
        if (update.UpdateId < _offset || update.UpdateId < 0) return;
        var message = update.Message;
        if (message?.From is { IsBot: false } user && message.Chat.Type == "private" && message.Chat.Id == user.Id && _accounts.TryGetValue(user.Id, out var account))
        {
            if (account.State.State.Deliveries.Length >= 100)
                await _log.WriteLineAsync("Лимит недоставленных ответов одного аккаунта достигнут; новое сообщение не принято.");
            else
            {
                try { await account.Bot.HandleAsync(update, ct); }
                catch (BackendException) { await account.Bot.UnavailableAsync(update.UpdateId, ct); }
            }
        }
        await SaveOffsetAsync(checked(update.UpdateId + 1), ct);
    }

    public Task DeliverAccountAsync(long userId, CancellationToken ct) => _accounts[userId].Bot.DeliverOnceAsync(ct);

    private async Task SaveOffsetAsync(long offset, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Checkpoint(_options.BotId, _options.Scope, offset));
        await using (var file = new FileStream(_checkpoint + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { await file.WriteAsync(bytes, ct); await file.FlushAsync(ct); file.Flush(true); }
        File.Move(_checkpoint + ".tmp", _checkpoint, true);
        _offset = offset;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var jobs = _accounts.Values.Select(a => DeliveryLoopAsync(a.Bot, stop.Token)).ToList();
        jobs.Add(ReceiveLoopAsync(stop.Token));
        try { await await Task.WhenAny(jobs); }
        finally { stop.Cancel(); try { await Task.WhenAll(jobs); } catch (OperationCanceledException) when (stop.IsCancellationRequested) { } }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var updates = await _telegram.UpdatesAsync(_offset, ct);
                foreach (var update in updates.OrderBy(u => u.UpdateId))
                {
                    await HandleAsync(update, ct);
                }
                if (updates.Length == 0) await Task.Delay(150, ct);
            }
            catch (TelegramException e) when (!e.Fatal) { await _log.WriteLineAsync(e.Message); await Task.Delay(TimeSpan.FromSeconds(e.RetryAfter), ct); }
        }
    }

    private async Task DeliveryLoopAsync(TelegramBot bot, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await bot.DeliverOnceAsync(ct); }
            catch (BackendException) { await _log.WriteLineAsync("Один из серверов Telegram недоступен; отправки этого аккаунта сохранены."); await Task.Delay(2000, ct); }
            catch (TelegramException e) when (!e.Fatal || e.StatusCode is 400 or 403)
            { await _log.WriteLineAsync("Доставка одному аккаунту временно недоступна; остальные продолжают работать."); await Task.Delay(TimeSpan.FromSeconds(e.Fatal ? 30 : e.RetryAfter), ct); }
            await Task.Delay(300, ct);
        }
    }

    public void Dispose()
    {
        foreach (var account in _accounts.Values) { account.State.Dispose(); account.Http.Dispose(); }
        _accounts.Clear(); _legacyLease?.Dispose(); _lease.Dispose();
    }
}
