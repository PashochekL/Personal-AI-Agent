internal static class ConsoleInputChecks
{
    public static async Task<int> RunAsync()
    {
        var checks = 0;
        void Check(bool ok, string name) { checks++; if (!ok) throw new Exception("Console input: " + name); }
        using var terminal = new StringWriter();
        using var input = new StringReader("\u001b[200~первая\n  вторая\n\u001b[201~\nnext\n");
        var paste = await ConsoleInput.ReadAsync(input, terminal, true, CancellationToken.None);
        Check(paste.IsPaste && paste.Text == "первая\n  вторая\n", "multiline paste is one message preserving whitespace");
        Check(terminal.ToString().StartsWith("\u001b[?2004h") && terminal.ToString().EndsWith("\u001b[?2004l"), "terminal paste mode restored");
        using var modeOutput = new StringWriter();
        using (var mode = new TerminalPasteMode(modeOutput, true))
        {
            mode.SetEnabled(true);
            mode.SetEnabled(true);
            mode.SetEnabled(false);
            mode.SetEnabled(true);
        }
        Check(modeOutput.ToString() == "\u001b[?2004h\u001b[?2004l\u001b[?2004h\u001b[?2004l", "paste mode stays enabled between turns and is restored on exit");
        var next = await ConsoleInput.ReadAsync(input, new StringWriter(), false, CancellationToken.None);
        Check(next.Text == "next" && !next.IsPaste, "next input remains separate");
        var pastedCommand = await ConsoleInput.ReadAsync(new StringReader("\u001b[200~/exit\u001b[201~\n"), new StringWriter(), false, CancellationToken.None);
        Check(pastedCommand.IsPaste && pastedCommand.Text == "/exit", "pasted commands marked as text");
        var incomplete = await ConsoleInput.ReadAsync(new StringReader("\u001b[200~incomplete\n"), new StringWriter(), false, CancellationToken.None);
        Check(incomplete.Incomplete && incomplete.EndOfInput && incomplete.Text is null, "incomplete paste never sent");
        var oversized = await ConsoleInput.ReadAsync(new StringReader("\u001b[200~" + new string('x', 1_000_001) + "\u001b[201~\n"), new StringWriter(), false, CancellationToken.None);
        Check(oversized.Incomplete && !oversized.EndOfInput && oversized.Text is null, "oversized paste rejected");
        using var output = new StringWriter();
        var multi = await ConsoleInput.ReadMultilineAsync(new StringReader("  line one\n\n\\/send\n/exit\n/send\n"), output, CancellationToken.None);
        Check(multi == "  line one\n\n/send\n/exit", "manual multiline and escaped marker");
        Check(await ConsoleInput.ReadMultilineAsync(new StringReader("text\n/cancel\n"), new StringWriter(), CancellationToken.None) is null, "manual multiline cancel");
        Check(await ConsoleInput.ReadMultilineAsync(new StringReader("text\n"), new StringWriter(), CancellationToken.None) is null, "manual multiline EOF");
        Check(await ConsoleInput.ReadMultilineAsync(new StringReader("  \n/send\n"), new StringWriter(), CancellationToken.None) is null, "empty manual message rejected");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try { await ConsoleInput.ReadMultilineAsync(new StringReader("test\n/send\n"), new StringWriter(), cancellation.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { checks++; }
        return checks;
    }
}
