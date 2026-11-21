using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AiAgent.Core;
using AiAgent.Shared;

internal static class McpChecks
{
    public static async Task GithubSmokeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint("/github-ipc/mcp.sock"), timeout.Token);
        var stream = new NetworkStream(socket, ownsSocket: true);
        await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes("{\"command\":\"github\",\"arguments\":[]}\n"), timeout.Token);
        var response = new byte[12];
        await stream.ReadExactlyAsync(response, timeout.Token);
        if (System.Text.Encoding.UTF8.GetString(response) != "{\"ok\":true}\n") throw new Exception("MCP broker handshake failed");
        await using var tools = await McpTools.ConnectAsync(stream, stream, "github", timeout.Token);
        var registry = new ToolRegistry();
        tools.Register(registry);
        var names = registry.Definitions().Select(x => x.Name).ToArray();
        if (!names.Contains("mcp.github.get_me") || names.Any(x => x.Contains("create_issue", StringComparison.Ordinal))) throw new Exception("GitHub MCP readonly discovery failed");
        var me = await registry.ExecuteAsync(new ToolCall { Id = "github-smoke", Name = "mcp.github.get_me", Arguments = new Dictionary<string, JsonElement>() }, timeout.Token);
        if (me.Success) throw new Exception("Invalid GitHub credential was accepted");
        Console.WriteLine($"GitHub MCP readonly discovery: {names.Length} tools");
    }

    public static async Task<int> RunAsync()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var socket = new TcpClient();
        var connecting = socket.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var peer = await listener.AcceptTcpClientAsync();
        await connecting;
        var server = ServeAsync(peer.GetStream());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using (var tools = await McpTools.ConnectAsync(socket.GetStream(), socket.GetStream(), "test", timeout.Token))
        {
            var registry = new ToolRegistry();
            tools.Register(registry);
            if (registry.Definitions().Single().Name != "mcp.test.echo") throw new Exception("MCP discovery failed");
            var result = await registry.ExecuteAsync(Call("hello"));
            if (!result.Success || result.Content != "hello") throw new Exception("MCP call failed");
            result = await registry.ExecuteAsync(Call("error"));
            if (result.Success || result.Error != "server error") throw new Exception("MCP error not propagated");
            result = await registry.ExecuteAsync(new ToolCall { Id = "bad", Name = "mcp.test.echo", Arguments = new Dictionary<string, JsonElement>() });
            if (result.Success || result.Error != "Invalid tool arguments") throw new Exception("MCP schema validation failed");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try { await registry.ExecuteAsync(Call("hello"), cancelled.Token); throw new Exception("MCP cancellation ignored"); }
            catch (OperationCanceledException) { }
        }
        await server.WaitAsync(TimeSpan.FromSeconds(5));
        return 5;
    }

    private static ToolCall Call(string value) => new() { Id = "1", Name = "mcp.test.echo", Arguments = new Dictionary<string, JsonElement> { ["text"] = JsonSerializer.SerializeToElement(value) } };

    private static async Task ServeAsync(Stream stream)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        await using var writer = new StreamWriter(stream, leaveOpen: true) { AutoFlush = true };
        while (await reader.ReadLineAsync() is { } line)
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("id", out var id)) continue;
            var method = root.GetProperty("method").GetString();
            if (method == "server/discover")
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code = -32601, message = "Method not found" } }));
                continue;
            }
            object result = method switch
            {
                "initialize" => new { protocolVersion = "2025-06-18", capabilities = new { tools = new { } }, serverInfo = new { name = "test", version = "1" } },
                "tools/list" => new { tools = new[] { new { name = "echo", description = "Echo text", inputSchema = new { type = "object", properties = new { text = new { type = "string" } }, required = new[] { "text" } } } } },
                "tools/call" => MakeResult(root.GetProperty("params").GetProperty("arguments").GetProperty("text").GetString()!),
                _ => throw new InvalidOperationException("Unexpected MCP method")
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }));
        }
    }

    private static object MakeResult(string value) => new { content = new[] { new { type = "text", text = value == "error" ? "server error" : value } }, isError = value == "error" };
}
