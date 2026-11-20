using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AiAgent.Shared;

namespace AiAgent.Sandbox;

/// <summary>Connects to a single-command, network-isolated worker over a Unix socket.</summary>
public sealed class UnixSandboxExecutor(string socketPath = "/sandbox-ipc/sandbox.sock") : ISandboxExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public async Task<CommandResult> ExecuteAsync(string command, string workspace, int timeout = 30, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (command.Length > 8192) throw new ArgumentException("Command is too long", nameof(command));
        if (workspace != "/workspace") throw new ArgumentException("Only the configured workspace is supported", nameof(workspace));
        if (timeout is < 1 or > 120) throw new ArgumentOutOfRangeException(nameof(timeout));
        try
        {
            // Only retry connection before sending. Retrying a sent command could execute it twice.
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            Socket? connected = null;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try { await candidate.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken); connected = candidate; break; }
                // Docker increases restart backoff after many rapid commands (up to about a minute).
                catch (SocketException) when (elapsed.Elapsed < TimeSpan.FromSeconds(75)) { candidate.Dispose(); await Task.Delay(200, cancellationToken); }
                catch { candidate.Dispose(); throw; }
            }
            using var socket = connected;
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), leaveOpen: true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { command, timeout }, JsonOptions).AsMemory(), cancellationToken);
            var response = new StringBuilder();
            var one = new char[1];
            while (await reader.ReadAsync(one.AsMemory(), cancellationToken) > 0 && one[0] != '\n')
            {
                if (response.Length >= 250_000) throw new AgentError("Sandbox response exceeds size limit");
                response.Append(one[0]);
            }
            var result = JsonSerializer.Deserialize<CommandResult>(response.ToString(), JsonOptions) ?? throw new AgentError("Empty sandbox response");
            result.Validate();
            // Wait for the worker to close the connection before allowing another command.
            using var closeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            closeTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            try { await reader.ReadAsync(one.AsMemory(), closeTimeout.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new AgentError("Sandbox worker did not stop"); }
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (AgentError) { throw; }
        catch (Exception e) when (e is SocketException or IOException or JsonException or ArgumentException or InvalidOperationException or DecoderFallbackException)
        { throw new AgentError("Sandbox is unavailable or returned invalid data", e); }
    }
}
