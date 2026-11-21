using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

/// <summary>Runs only the GitHub MCP server in its separate networked container.</summary>
internal static class McpStdioBroker
{
    private const string SocketPath = "/ipc/mcp.sock";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly SemaphoreSlim Slots = new(4, 4);

    public static async Task ServeAsync()
    {
        if (File.Exists(SocketPath)) File.Delete(SocketPath);
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
        listener.Listen(4);
        while (true)
        {
            var client = await listener.AcceptAsync();
            _ = HandleAsync(client);
        }
    }

    private static async Task HandleAsync(Socket client)
    {
        using (client)
        await using (var stream = new NetworkStream(client, ownsSocket: false))
        {
            if (!await Slots.WaitAsync(0)) { await SendAsync(stream, "{\"ok\":false}\n"); return; }
            Process? process = null;
            try
            {
                using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var bytes = new List<byte>();
                var one = new byte[1];
                while (await stream.ReadAsync(one, startup.Token) != 0 && one[0] != (byte)'\n')
                {
                    if (bytes.Count >= 16384) throw new InvalidDataException("Request too large");
                    bytes.Add(one[0]);
                }
                if (one[0] != (byte)'\n') throw new InvalidDataException("Incomplete request");
                var request = JsonSerializer.Deserialize<LaunchRequest>(bytes.ToArray(), JsonOptions) ?? throw new InvalidDataException("Invalid request");
                if (string.IsNullOrWhiteSpace(request.Command) || request.Command.Length > 1024 || request.Arguments is null || request.Arguments.Length > 32 || request.Arguments.Any(a => a is null || a.Length > 2048)) throw new InvalidDataException("Invalid command");
                var github = Environment.GetEnvironmentVariable("DAMIR_GITHUB_MCP") == "1";
                if (github && (request.Command != "github" || request.Arguments.Length != 0)) throw new InvalidDataException("Invalid server");
                var start = new ProcessStartInfo(github ? "/server/github-mcp-server" : request.Command) { WorkingDirectory = github ? "/tmp" : "/workspace", UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                if (github) start.ArgumentList.Add("stdio");
                else foreach (var argument in request.Arguments) start.ArgumentList.Add(argument);
                start.Environment.Clear();
                start.Environment["PATH"] = "/usr/local/bin:/usr/bin:/bin";
                start.Environment["HOME"] = "/tmp";
                start.Environment["LANG"] = "C.UTF-8";
                if (github)
                {
                    start.Environment["GITHUB_PERSONAL_ACCESS_TOKEN"] = Environment.GetEnvironmentVariable("GITHUB_PERSONAL_ACCESS_TOKEN") ?? "";
                    start.Environment["GITHUB_READ_ONLY"] = "1";
                }
                process = Process.Start(start) ?? throw new InvalidOperationException("Could not start MCP server");
                await SendAsync(stream, "{\"ok\":true}\n");
                using var stopped = new CancellationTokenSource();
                var fromClient = CopyFramesAsync(stream, process.StandardInput.BaseStream, stopped.Token);
                var fromServer = CopyFramesAsync(process.StandardOutput.BaseStream, stream, stopped.Token);
                var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, stopped.Token);
                await Task.WhenAny(fromClient, fromServer, process.WaitForExitAsync(stopped.Token));
                stopped.Cancel();
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                try { await Task.WhenAll(fromClient, fromServer, stderr); } catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
            }
            catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or OperationCanceledException)
            {
                if (process is null) try { await SendAsync(stream, "{\"ok\":false}\n"); } catch (IOException) { }
            }
            finally
            {
                if (process is not null)
                {
                    try { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
                    catch (InvalidOperationException) { }
                    process.Dispose();
                }
                Slots.Release();
            }
        }
    }

    private static Task SendAsync(Stream stream, string text) => stream.WriteAsync(Encoding.UTF8.GetBytes(text)).AsTask();

    private static async Task CopyFramesAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var frameSize = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            for (var i = 0; i < count; i++)
            {
                frameSize = buffer[i] == (byte)'\n' ? 0 : frameSize + 1;
                if (frameSize > 1_048_576) throw new InvalidDataException("MCP frame too large");
            }
            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            await destination.FlushAsync(cancellationToken);
        }
    }

    private sealed record LaunchRequest(string Command, string[] Arguments);
}
