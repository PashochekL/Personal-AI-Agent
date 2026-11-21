using System.Text.Json;
using AiAgent.Shared;

namespace AiAgent.Core;

/// <summary>The owner/project scope is supplied by trusted composition, never by the model.</summary>
public sealed class MemoryTool : ITool
{
    private readonly ILongTermMemory _memory;
    private readonly MemoryScope _scope;
    private readonly string _operation;
    public MemoryTool(ILongTermMemory memory, MemoryScope scope, string operation)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(scope);
        scope.Validate();
        if (operation is not ("save" or "search" or "list" or "forget")) throw new ArgumentException("Unknown memory operation");
        _memory = memory;
        _scope = scope;
        _operation = operation;
    }
    public string Name => "memory." + _operation;
    public string Description => _operation switch
    {
        "save" => "Remember a concise fact or preference when the user asks. Use the same key to replace an outdated fact. Never store secrets. Scoped to the current owner and project.",
        "search" => "Find stored facts and preferences by meaning in the current owner/project memory.",
        "list" => "List stored facts by key, up to 20 per page. Pass next_after_key as after_key to read another page.",
        _ => "Forget a stored fact by its exact key when the user asks. This does not erase earlier conversation messages."
    };
    public IReadOnlyDictionary<string, JsonElement> Parameters => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(_operation switch
    {
        "save" => """{"type":"object","properties":{"key":{"type":"string","minLength":1,"maxLength":128},"content":{"type":"string","minLength":1,"maxLength":2000}},"required":["key","content"],"additionalProperties":false}""",
        "search" => """{"type":"object","properties":{"query":{"type":"string","minLength":1,"maxLength":4000}},"required":["query"],"additionalProperties":false}""",
        "forget" => """{"type":"object","properties":{"key":{"type":"string","minLength":1,"maxLength":128}},"required":["key"],"additionalProperties":false}""",
        _ => """{"type":"object","properties":{"after_key":{"type":"string","minLength":1,"maxLength":128}},"additionalProperties":false}"""
    })!;
    public async Task<ToolResult> ExecuteAsync(IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            string Field(string name) => arguments.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()! : throw new ArgumentException("Missing memory argument");
            object result;
            switch (_operation)
            {
                case "save":
                    var record = await _memory.SaveAsync(_scope, Field("key"), Field("content"), cancellationToken);
                    record.Validate();
                    CheckScope(record);
                    result = new { saved = true, key = record.Key, content = record.Content };
                    break;
                case "forget":
                    var deleted = await _memory.DeleteAsync(_scope, Field("key"), cancellationToken);
                    result = new { deleted, key = Field("key") };
                    break;
                case "search":
                    var matches = await _memory.SearchAsync(_scope, Field("query"), 5, cancellationToken: cancellationToken);
                    foreach (var match in matches) { match.Validate(); CheckScope(match.Record); }
                    result = matches.Select(x => new { key = x.Record.Key, content = x.Record.Content, similarity = x.Similarity }).Take(5).ToArray();
                    break;
                default:
                    var records = await _memory.ListAsync(_scope, 21, arguments.ContainsKey("after_key") ? Field("after_key") : null, cancellationToken);
                    foreach (var item in records) { item.Validate(); CheckScope(item); }
                    result = new { records = records.Take(20).Select(x => new { key = x.Key, content = x.Content }).ToArray(), next_after_key = records.Count > 20 ? records[19].Key : null };
                    break;
            }
            return new ToolResult { Success = true, Content = JsonSerializer.Serialize(result) };
        }
        catch (MemoryError e) { return new ToolResult { Success = false, Content = "", Error = e.Message }; }
        catch (ArgumentException) { return new ToolResult { Success = false, Content = "", Error = "Invalid memory arguments or response" }; }
    }
    private void CheckScope(MemoryRecord record)
    {
        if (record.Scope.OwnerId != _scope.OwnerId || record.Scope.ProjectId != _scope.ProjectId) throw new MemoryError("Memory scope mismatch");
    }
}
