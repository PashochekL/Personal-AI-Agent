using System.Text.Json;
using AiAgent.Shared;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AiAgent.Core;

/// <summary>Adapts MCP tools to the existing registry without changing the agent loop.</summary>
public sealed class McpTools : IAsyncDisposable
{
    private readonly McpClient _client;
    private readonly IReadOnlyList<ITool> _tools;
    private readonly Stream _input;
    private readonly Stream _output;

    private McpTools(McpClient client, IReadOnlyList<ITool> tools, Stream input, Stream output) { _client = client; _tools = tools; _input = input; _output = output; }

    public static async Task<McpTools> ConnectAsync(Stream input, Stream output, string serverName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverName) || serverName.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new ArgumentException("Invalid MCP server name", nameof(serverName));
        var client = await McpClient.CreateAsync(new StreamClientTransport(input, output), cancellationToken: cancellationToken);
        try
        {
            var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
            return new McpTools(client, tools.Select(t => (ITool)new Adapter(serverName, t)).ToArray(), input, output);
        }
        catch { await client.DisposeAsync(); throw; }
    }

    public void Register(ToolRegistry registry) => registry.RegisterRange(_tools);
    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        await _input.DisposeAsync();
        if (!ReferenceEquals(_input, _output)) await _output.DisposeAsync();
    }

    private sealed class Adapter : ITool
    {
        private readonly McpClientTool _tool;
        public Adapter(string serverName, McpClientTool tool)
        {
            _tool = tool;
            Name = $"mcp.{serverName}.{tool.Name}";
            if (tool.JsonSchema.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid MCP input schema");
            Parameters = tool.JsonSchema.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        }
        public string Name { get; }
        public string Description => _tool.Description;
        public IReadOnlyDictionary<string, JsonElement> Parameters { get; }
        public async Task<ToolResult> ExecuteAsync(IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken cancellationToken = default)
        {
            var result = await _tool.CallAsync(arguments.ToDictionary(p => p.Key, p => (object?)p.Value), cancellationToken: cancellationToken);
            var parts = result.Content.Select(block => block is TextContentBlock text ? text.Text : "[Неподдерживаемый тип данных MCP]").ToList();
            if (result.StructuredContent is { } structured) parts.Add(structured.GetRawText());
            var content = string.Join("\n", parts);
            if (result.IsError == true) return new ToolResult { Success = false, Content = "", Error = string.IsNullOrWhiteSpace(content) ? "Ошибка MCP-инструмента" : content };
            return new ToolResult { Success = true, Content = content };
        }
    }
}
