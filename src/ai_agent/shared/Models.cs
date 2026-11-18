using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiAgent.Shared;

// Validate() also runs automatically after JSON deserialization. Call it before
// passing an object built in code across a component boundary.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public abstract class ExchangeModel : IJsonOnDeserialized
{
    public abstract void Validate();
    void IJsonOnDeserialized.OnDeserialized() => Validate();
    protected static void Require(bool condition, string message)
    {
        if (!condition) throw new ArgumentException(message);
    }
    protected static bool NonBlank(string? value) => !string.IsNullOrWhiteSpace(value);
}

public sealed class ToolDefinition : ExchangeModel
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required Dictionary<string, JsonElement> Parameters { get; init; }
    public override void Validate() => Require(Name is not null && Description is not null && Parameters is not null, "Tool definition fields are required");
}

public sealed class ToolCall : ExchangeModel
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required Dictionary<string, JsonElement> Arguments { get; init; }
    public override void Validate() => Require(NonBlank(Id) && Name is not null && Arguments is not null, "Invalid tool call");
}

public sealed class Message : ExchangeModel
{
    public required string Role { get; init; }
    public string? Content { get; init; }
    public List<ToolCall> ToolCalls { get; init; } = [];
    public string? ToolCallId { get; init; }
    public override void Validate()
    {
        Require(Role is "system" or "user" or "assistant" or "tool", "Invalid message role");
        Require(ToolCalls is not null, "Tool calls are required");
        Require(ToolCallId is null || NonBlank(ToolCallId), "Invalid tool_call_id");
        Require(Role != "tool" || ToolCallId is not null, "Tool messages require tool_call_id");
        Require(ToolCalls!.Count == 0 || Role == "assistant", "Only assistant messages may contain tool calls");
        Require(Content is not null || (Role == "assistant" && ToolCalls.Count > 0), "Message content is required");
        foreach (var call in ToolCalls) { Require(call is not null, "Invalid tool call"); call!.Validate(); }
    }
}

public sealed class ToolResult : ExchangeModel
{
    public required bool Success { get; init; }
    public required string Content { get; init; }
    public string? Error { get; init; }
    public override void Validate() => Require(Content is not null && (Success ? Error is null : NonBlank(Error)), "Invalid tool result");
}

public sealed class TokenUsage : ExchangeModel
{
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public int? TotalTokens { get; init; }
    public override void Validate() => Require((InputTokens is null or >= 0) && (OutputTokens is null or >= 0) && (TotalTokens is null or >= 0), "Token counts must be nonnegative");
}

public sealed class LlmResponse : ExchangeModel
{
    public string? Content { get; init; }
    public List<ToolCall> ToolCalls { get; init; } = [];
    public string? FinishReason { get; init; }
    public TokenUsage? Usage { get; init; }
    public override void Validate()
    {
        Require(FinishReason is null or "stop" or "tool_calls" or "length" or "error" or "unknown", "Invalid finish reason");
        Require(ToolCalls is not null, "Tool calls are required");
        foreach (var call in ToolCalls!) { Require(call is not null, "Invalid tool call"); call!.Validate(); }
        Usage?.Validate();
    }
}

public sealed class CommandResult : ExchangeModel
{
    public required int? ExitCode { get; init; }
    public string Stdout { get; init; } = "";
    public string Stderr { get; init; } = "";
    public bool TimedOut { get; init; }
    public override void Validate() => Require(Stdout is not null && Stderr is not null, "Output must be a string");
}

public enum TaskStatus { PENDING, RUNNING, COMPLETED, FAILED, CANCELLED }

public sealed class AgentResult : ExchangeModel
{
    public required string SessionId { get; init; }
    public string? Response { get; init; }
    public required TaskStatus Status { get; init; }
    public string? Error { get; init; }
    public override void Validate()
    {
        Require(NonBlank(SessionId), "Invalid session_id");
        Require(Status is TaskStatus.COMPLETED or TaskStatus.FAILED or TaskStatus.CANCELLED, "Invalid agent status");
        Require(Error is null || NonBlank(Error), "Invalid error");
        Require(Status != TaskStatus.COMPLETED || (NonBlank(Response) && Error is null), "Completed result requires response and no error");
        Require(Status != TaskStatus.FAILED || NonBlank(Error), "Failed result requires error");
    }
}

public sealed class AgentTask : ExchangeModel
{
    public required string TaskId { get; init; }
    public required string SessionId { get; init; }
    public required string UserMessage { get; init; }
    public TaskStatus Status { get; init; } = TaskStatus.PENDING;
    public AgentResult? Result { get; init; }
    public override void Validate()
    {
        Require(NonBlank(TaskId) && NonBlank(SessionId) && UserMessage is not null, "Invalid task fields");
        Require(Enum.IsDefined(Status), "Invalid task status");
        Require(Result is not null || Status is not (TaskStatus.COMPLETED or TaskStatus.FAILED), "Task requires result");
        if (Result is not null) { Result.Validate(); Require(Result.SessionId == SessionId && Result.Status == Status, "Task result must match session and status"); }
    }
}

public sealed class Session : ExchangeModel
{
    public required string SessionId { get; init; }
    public override void Validate() => Require(NonBlank(SessionId), "Invalid session_id");
}

public static class ContractJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new StrictTaskStatusConverter() }
    };
    public static string Serialize<T>(T value) where T : ExchangeModel
    {
        value.Validate();
        return JsonSerializer.Serialize(value, Options);
    }
    public static T Deserialize<T>(string json) where T : ExchangeModel =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException("Null payload");
}

internal sealed class StrictTaskStatusConverter : JsonConverter<TaskStatus>
{
    public override TaskStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String || !Enum.TryParse<TaskStatus>(reader.GetString(), false, out var value) || !Enum.IsDefined(value) || reader.GetString() != value.ToString())
            throw new JsonException("Invalid task status");
        return value;
    }
    public override void Write(Utf8JsonWriter writer, TaskStatus value, JsonSerializerOptions options)
    {
        if (!Enum.IsDefined(value)) throw new JsonException("Invalid task status");
        writer.WriteStringValue(value.ToString());
    }
}
