using AiAgent.Http;
using AiAgent.Telegram;
using System.Text.Json;
using System.Runtime.InteropServices;

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
using var termination = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; stop.Cancel(); });
try
{
    var options = TelegramOptions.FromEnvironment(); options.Validate();
    using var telegramHttp = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(40) };
    var telegram = new TelegramApi(telegramHttp, options.Token);
    if (args.Contains("--drain-legacy", StringComparer.Ordinal))
    {
        if (options.EffectiveUserIds.Length != 1) throw new ArgumentException("Для завершения старых отправок укажи только прежний Telegram ID.");
        var legacy = new TelegramOptions { Token = options.Token, AllowedUserId = options.EffectiveUserIds.Single(), Owner = options.Owner, Project = options.Project,
            SessionName = options.SessionName, StateDirectory = options.StateDirectory };
        Directory.CreateDirectory(legacy.StateDirectory);
        using var lease = new FileStream(Path.Combine(legacy.StateDirectory, legacy.BotId + ".receiver.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var state = new TelegramStateFile(legacy);
        using var backendHttp = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
        var backend = new BackendClient(backendHttp, new Uri(Environment.GetEnvironmentVariable("DAMIR_BACKEND_URL") ?? "http://127.0.0.1:8080"), Environment.GetEnvironmentVariable("DAMIR_BACKEND_API_KEY") ?? "");
        var oldBot = new TelegramBot(telegram, backend, state, legacy, Console.Error);
        await oldBot.InitializeAsync(stop.Token);
        while (state.State.Deliveries.Length > 0) { await oldBot.DeliverOnceAsync(stop.Token); await Task.Delay(300, stop.Token); }
        Console.WriteLine("Старые отправки завершены. Новые сообщения в общем режиме не принимаются.");
        return 0;
    }
    var profilesPath = Environment.GetEnvironmentVariable("DAMIR_TELEGRAM_BACKENDS_FILE");
    if (string.IsNullOrWhiteSpace(profilesPath)) throw new ArgumentException("Для изолированного Telegram нужен файл серверов аккаунтов. Запусти damir telegram.");
    using var router = new TelegramRouter(telegram, options, TelegramBackendProfile.Load(profilesPath, options), Console.Error);
    while (true)
    {
        try { await router.InitializeAsync(stop.Token); break; }
        catch (Exception e) when (e is BackendException { Status: null or System.Net.HttpStatusCode.ServiceUnavailable } || e is TelegramException { Fatal: false })
        { Console.Error.WriteLine("Не удалось подключиться. Повтор через несколько секунд."); await Task.Delay(3000, stop.Token); }
    }
    Console.WriteLine("Telegram-бот запущен. У разрешённых аккаунтов отдельные серверы, диалоги, память и файлы. Остановка: Ctrl+C.");
    await router.RunAsync(stop.Token);
    return 0;
}
catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 0; }
catch (TelegramException e) { Console.Error.WriteLine(e.Message); return 1; }
catch (BackendException e) { Console.Error.WriteLine(e.Message); return 1; }
catch (ArgumentException e) { Console.Error.WriteLine(e.Message); return 1; }
catch (InvalidOperationException e) when (e.Message.StartsWith("В старом режиме", StringComparison.Ordinal)) { Console.Error.WriteLine(e.Message); return 1; }
catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
{ Console.Error.WriteLine("Не удалось запустить бота. Проверь настройки, состояние и отсутствие второго экземпляра."); return 1; }
