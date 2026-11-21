using AiAgent.Shared;

/// <summary>Displays a single temporary status line while a turn is running.</summary>
internal sealed class ConsoleProgress : IAsyncDisposable
{
    private static readonly string[] Frames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
    private readonly TextWriter _writer;
    private readonly bool _enabled;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _animation;
    private string _status = "Подготавливает контекст";
    private int _frame;
    private bool _disposed;

    public ConsoleProgress(TextWriter writer, bool enabled)
    {
        _writer = writer;
        _enabled = enabled;
        if (enabled) Draw();
        _animation = enabled ? AnimateAsync() : Task.CompletedTask;
    }

    public void Model() => SetStatus("Обращается к модели");
    public void SkillLoaded(string name)
    {
        var safe = new string(name.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').Take(64).ToArray());
        if (safe.Length == 0) return;
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                if (_enabled) _writer.Write("\r\u001b[2K");
                _writer.WriteLine($"Скилл загружен: {safe}");
                _writer.Flush();
                if (_enabled) DrawLocked();
            }
            catch (IOException) { }
        }
    }
    public void Tool(string name)
    {
        var safe = new string(name.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-').Take(48).ToArray());
        SetStatus("Вызывает: " + (safe.Length == 0 ? "инструмент" : safe));
    }

    private void SetStatus(string status)
    {
        lock (_gate)
        {
            if (_disposed || !_enabled) return;
            _status = status;
            DrawLocked();
        }
    }

    private void Draw() { lock (_gate) DrawLocked(); }
    private void DrawLocked()
    {
        try
        {
            var width = 80;
            try { if (Console.WindowWidth > 0) width = Console.WindowWidth; } catch (IOException) { }
            var length = Math.Max(0, width - 3);
            var text = _status.Length <= length ? _status : _status[..length];
            _writer.Write($"\r\u001b[2K{Frames[_frame++ % Frames.Length]} {text}");
            _writer.Flush();
        }
        catch (IOException) { }
    }

    private async Task AnimateAsync()
    {
        try
        {
            while (true) { await Task.Delay(120, _stopping.Token); Draw(); }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        await _animation;
        lock (_gate)
        {
            _disposed = true;
            if (_enabled) try { _writer.Write("\r\u001b[2K"); _writer.Flush(); } catch (IOException) { }
        }
        _stopping.Dispose();
    }
}

internal sealed class ProgressLlmClient(ILlmClient inner, Action onRequest) : ILlmClient
{
    public Task<LlmResponse> CompleteAsync(IReadOnlyList<Message> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken = default)
    {
        try { onRequest(); } catch (Exception) { }
        return inner.CompleteAsync(messages, tools, cancellationToken);
    }
}
