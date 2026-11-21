using System.Text;

internal sealed record ConsoleInputResult(string? Text, bool IsPaste = false, bool Incomplete = false, bool EndOfInput = false);

internal sealed class TerminalPasteMode(TextWriter output, bool available) : IDisposable
{
    private bool _enabled;

    public void SetEnabled(bool enabled)
    {
        if (!available || _enabled == enabled) return;
        output.Write(enabled ? "\u001b[?2004h" : "\u001b[?2004l");
        output.Flush();
        _enabled = enabled;
    }

    public void Dispose() => SetEnabled(false);
}

internal static class ConsoleInput
{
    private const string PasteStart = "\u001b[200~";
    private const string PasteEnd = "\u001b[201~";
    private const int MaxCharacters = 1_000_000;

    public static async Task<ConsoleInputResult> ReadAsync(TextReader input, TextWriter output, bool enablePaste, CancellationToken cancellationToken)
    {
        if (enablePaste) { await output.WriteAsync("\u001b[?2004h"); await output.FlushAsync(cancellationToken); }
        try
        {
            var first = await input.ReadLineAsync(cancellationToken);
            if (first is null) return new(null, EndOfInput: true);
            var start = first.IndexOf(PasteStart, StringComparison.Ordinal);
            if (start < 0) return new(first);
            var text = new StringBuilder(first[..start]);
            var line = first[(start + PasteStart.Length)..];
            var tooLarge = false;
            while (true)
            {
                var end = line.IndexOf(PasteEnd, StringComparison.Ordinal);
                var part = end < 0 ? line : line.Remove(end, PasteEnd.Length);
                if ((long)text.Length + part.Length > MaxCharacters) tooLarge = true;
                if (!tooLarge) text.Append(part);
                if (end >= 0) return tooLarge ? new(null, true, true) : new(text.ToString(), true);
                if (text.Length >= MaxCharacters) tooLarge = true;
                if (!tooLarge) text.Append('\n');
                line = await input.ReadLineAsync(cancellationToken);
                if (line is null) return new(null, true, true, true);
            }
        }
        finally
        {
            if (enablePaste) { await output.WriteAsync("\u001b[?2004l"); await output.FlushAsync(CancellationToken.None); }
        }
    }

    public static async Task<string?> ReadMultilineAsync(TextReader input, TextWriter output, CancellationToken cancellationToken)
    {
        await output.WriteLineAsync("Вставь или введи текст. Отдельная строка /send отправит его, /cancel отменит.");
        var text = new StringBuilder();
        var hasLine = false;
        var tooLarge = false;
        while (true)
        {
            var line = await input.ReadLineAsync(cancellationToken);
            if (line is null || line == "/cancel") return null;
            if (line == "/send")
            {
                if (tooLarge) { await output.WriteLineAsync("Сообщение слишком большое."); return null; }
                if (!hasLine || string.IsNullOrWhiteSpace(text.ToString())) { await output.WriteLineAsync("Сообщение пустое."); return null; }
                return text.ToString();
            }
            if (line is "\\/send" or "\\/cancel") line = line[1..];
            if ((long)text.Length + line.Length + (hasLine ? 1 : 0) > MaxCharacters) tooLarge = true;
            if (!tooLarge) { if (hasLine) text.Append('\n'); text.Append(line); }
            hasLine = true;
        }
    }
}
