using System.Text.Json;
using System.Text.Json.Nodes;
using AiAgent.Shared;
using Json.Schema;

namespace AiAgent.Core;

/// <summary>Stores tool definitions and validates calls before execution.</summary>
public sealed class ToolRegistry
{
    private sealed record Registration(ITool Tool, ToolDefinition Definition, JsonSchema Schema);
    private readonly Dictionary<string, Registration> _tools = new(StringComparer.Ordinal);
    private readonly Action<string>? _onExecute;

    public ToolRegistry(Action<string>? onExecute = null) => _onExecute = onExecute;

    public void Register(ITool tool) => RegisterRange([tool]);

    /// <summary>Validates every tool before registering any, so connection failures leave no partial set.</summary>
    public void RegisterRange(IEnumerable<ITool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var pending = new Dictionary<string, Registration>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            ArgumentNullException.ThrowIfNull(tool);
            if (string.IsNullOrWhiteSpace(tool.Name)) throw new ArgumentException("Tool name must not be blank");
            if (_tools.ContainsKey(tool.Name) || pending.ContainsKey(tool.Name)) throw new ArgumentException("Tool name is already registered");
            var parameters = tool.Parameters.ToDictionary(pair => pair.Key, pair => pair.Value.Clone());
            var schema = JsonSchema.FromText(JsonSerializer.Serialize(parameters));
            var definition = new ToolDefinition { Name = tool.Name, Description = tool.Description, Parameters = parameters };
            definition.Validate();
            pending.Add(tool.Name, new Registration(tool, definition, schema));
        }
        foreach (var (name, registration) in pending) _tools.Add(name, registration);
    }

    public IReadOnlyList<ToolDefinition> Definitions() => _tools.Values.Select(entry => new ToolDefinition
    {
        Name = entry.Definition.Name,
        Description = entry.Definition.Description,
        Parameters = entry.Definition.Parameters.ToDictionary(pair => pair.Key, pair => pair.Value.Clone())
    }).ToArray();

    public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        call.Validate();
        if (!_tools.TryGetValue(call.Name, out var entry)) return Failure("Unknown tool");
        try
        {
            var arguments = JsonSerializer.SerializeToNode(call.Arguments);
            if (!entry.Schema.Evaluate(arguments).IsValid) return Failure("Invalid tool arguments");
        }
        catch (Exception) { return Failure("Tool schema evaluation failed"); }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { _onExecute?.Invoke(call.Name); } catch (Exception) { /* Progress reporting must not affect tools. */ }
            var result = await entry.Tool.ExecuteAsync(call.Arguments, cancellationToken);
            result.Validate();
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return Failure("Tool execution failed"); }
    }

    private static ToolResult Failure(string error) => new() { Success = false, Content = "", Error = error };
}
