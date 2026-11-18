using System.Text.Json;
namespace AiAgent.Shared;

public interface ILlmClient
{
    Task<LlmResponse> CompleteAsync(IReadOnlyList<Message> messages, IReadOnlyList<ToolDefinition> tools);
}
public interface ITool
{
    string Name { get; }
    string Description { get; }
    IReadOnlyDictionary<string, JsonElement> Parameters { get; }
    Task<ToolResult> ExecuteAsync(IReadOnlyDictionary<string, JsonElement> arguments);
}
public interface IContextStore
{
    Task<IReadOnlyList<Message>> GetMessagesAsync(string sessionId);
    Task AppendMessageAsync(string sessionId, Message message);
    Task ReplaceMessagesAsync(string sessionId, IReadOnlyList<Message> messages);
}
public interface ISandboxExecutor
{
    Task<CommandResult> ExecuteAsync(string command, string workspace, int timeout = 30);
}
public interface IAgent
{
    Task<AgentResult> RunAsync(string sessionId, string userMessage);
}
public class AgentError : Exception
{
    public AgentError(string message) : base(message) { }
    public AgentError(string message, Exception innerException) : base(message, innerException) { }
}
