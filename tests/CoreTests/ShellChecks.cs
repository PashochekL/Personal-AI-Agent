using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AiAgent.Sandbox;
using AiAgent.Shared;
using AiAgent.Tools.Shell;

static class ShellChecks
{
    public static async Task<int> RunAsync()
    {
        var checks = 0;
        void Check(bool value, string name) { checks++; if (!value) throw new Exception("Shell: " + name); }
        var sandbox = new FakeSandbox();
        var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"command":"printf hello","timeout":3}""")!;
        var rejected = new ShellTool(sandbox, (_, _, _) => Task.FromResult(false));
        Check(!(await rejected.ExecuteAsync(args)).Success && sandbox.Calls == 0, "rejection prevents execution");
        var tool = new ShellTool(sandbox, (_, _, _) => Task.FromResult(true));
        Check((await tool.ExecuteAsync(args)).Content.Contains("hello") && sandbox.Calls == 1 && sandbox.Timeout == 3, "approved command uses executor and timeout");
        sandbox.Result = new CommandResult { ExitCode = null, TimedOut = true, Stdout = "partial" };
        var timeout = await tool.ExecuteAsync(args);
        Check(!timeout.Success && timeout.Content.Contains("partial") && timeout.Error == "Command timed out", "partial output on timeout");
        Check(!(await tool.ExecuteAsync(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"command":" "}""")!)).Success, "empty command rejected");
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            try { await tool.ExecuteAsync(args, cts.Token); throw new Exception("Cancellation ignored"); }
            catch (OperationCanceledException) { checks++; }
        }
        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
        {
            try { await new UnixSandboxExecutor(Path.Combine("/tmp", "missing-" + Guid.NewGuid().ToString("N"))).ExecuteAsync("echo", "/workspace", cancellationToken: cts.Token); throw new Exception("Connection cancellation ignored"); }
            catch (OperationCanceledException) { checks++; }
        }
        var path = Path.Combine(Path.GetTempPath(), "ds-" + Guid.NewGuid().ToString("N")[..12] + ".sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(1);
            var server = Task.Run(async () =>
            {
                using var connection = await listener.AcceptAsync();
                await using var stream = new NetworkStream(connection);
                using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                using var request = JsonDocument.Parse((await reader.ReadLineAsync())!);
                if (request.RootElement.GetProperty("command").GetString() != "printf hi" || request.RootElement.GetProperty("timeout").GetInt32() != 5) throw new Exception("Invalid IPC request");
                await writer.WriteLineAsync("""{"exitCode":0,"stdout":"hi","stderr":"","timedOut":false}""");
            });
            var response = await new UnixSandboxExecutor(path).ExecuteAsync("printf hi", "/workspace", 5).WaitAsync(TimeSpan.FromSeconds(10));
            await server;
            Check(response.ExitCode == 0 && response.Stdout == "hi", "framed Unix socket response");
            try { await new UnixSandboxExecutor(path).ExecuteAsync("echo", "/outside"); throw new Exception("Outside workspace accepted"); }
            catch (ArgumentException) { checks++; }
        }
        finally { listener.Dispose(); if (File.Exists(path)) File.Delete(path); }
        return checks;
    }
    private sealed class FakeSandbox : ISandboxExecutor
    {
        public int Calls { get; private set; }
        public int Timeout { get; private set; }
        public CommandResult Result { get; set; } = new() { ExitCode = 0, Stdout = "hello" };
        public Task<CommandResult> ExecuteAsync(string command, string workspace, int timeout = 30, CancellationToken cancellationToken = default)
        { Calls++; Timeout = timeout; return Task.FromResult(Result); }
    }
}
