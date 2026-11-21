using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

const string socketPath = "/ipc/sandbox.sock";
const int maxOutput = 16384;
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
if (Environment.GetEnvironmentVariable("DAMIR_GITHUB_MCP") == "1")
{
    await McpStdioBroker.ServeAsync();
    return;
}
await ServeShellAsync();

async Task ServeShellAsync()
{
if (File.Exists(socketPath)) File.Delete(socketPath);
using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
listener.Bind(new UnixDomainSocketEndPoint(socketPath));
listener.Listen(1);
while (true)
{
    using var client = await listener.AcceptAsync();
    await HandleAsync(client);
}
}

async Task HandleAsync(Socket client)
{
    await using var stream = new NetworkStream(client, ownsSocket: false);
    using var reader = new StreamReader(stream, new UTF8Encoding(false, true), leaveOpen: true);
    await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
    try
    {
        // Input is bounded while reading; a peer cannot make the worker buffer an unlimited line.
        var line = new StringBuilder();
        var input = new char[1024];
        while (true)
        {
            var count = await reader.ReadAsync(input.AsMemory(0, 1));
            if (count == 0) return;
            if (input[0] == '\n') break;
            if (line.Length >= 65536) throw new InvalidDataException("Request too large");
            line.Append(input[0]);
        }
        var request = JsonSerializer.Deserialize<SandboxRequest>(line.ToString(), jsonOptions);
        if (request is null || string.IsNullOrWhiteSpace(request.Command) || request.Command.Length > 8192 || request.Timeout is < 1 or > 120)
            throw new InvalidDataException("Invalid request");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(request.Timeout));
        var start = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, WorkingDirectory = "/workspace" };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(request.Command);
        start.Environment.Clear();
        start.Environment["PATH"] = "/usr/local/bin:/usr/bin:/bin";
        start.Environment["HOME"] = "/tmp";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start shell");
        process.StandardInput.Close();
        var stdout = ReadLimitedAsync(process.StandardOutput, maxOutput, timeout.Token);
        var stderr = ReadLimitedAsync(process.StandardError, maxOutput, timeout.Token);
        // Monitor disconnects so cancelling a request also stops its command.
        var disconnect = stream.ReadAsync(new byte[1], timeout.Token).AsTask();
        var finished = process.WaitForExitAsync(timeout.Token);
        var timedOut = false;
        var clientGone = false;
        try
        {
            var first = await Task.WhenAny(finished, disconnect);
            if (first == disconnect && !timeout.IsCancellationRequested) { clientGone = true; timeout.Cancel(); }
            await finished;
            await Task.WhenAll(stdout, stderr).WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            timedOut = !clientGone;
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
        // Reads return captured partial output on cancellation.
        var result = new SandboxResponse(timedOut ? null : process.HasExited ? process.ExitCode : null, await stdout, await stderr, timedOut);
        if (!clientGone) await writer.WriteLineAsync(JsonSerializer.Serialize(result, jsonOptions));
    }
    catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or DecoderFallbackException)
    {
        try { await writer.WriteLineAsync(JsonSerializer.Serialize(new { error = "Sandbox execution failed" }, jsonOptions)); } catch (IOException) { }
    }

}

static async Task<string> ReadLimitedAsync(StreamReader reader, int max, CancellationToken cancellationToken)
{
    var text = new StringBuilder();
    var buffer = new char[4096];
    var truncated = false;
    try
    {
        int n;
        while ((n = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            var keep = Math.Min(n, Math.Max(0, max - text.Length));
            text.Append(buffer, 0, keep);
            truncated |= keep < n;
        }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    return text + (truncated ? "\n[output truncated]" : "");
}
record SandboxRequest(string Command, int Timeout);
record SandboxResponse(int? ExitCode, string Stdout, string Stderr, bool TimedOut);
