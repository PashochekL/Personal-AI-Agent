using System.Text.Json;
namespace AiAgent.Shared;

public interface ILlmClient
{
    Task<LlmResponse> CompleteAsync(IReadOnlyList<Message> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken = default);
}
public interface ITool
{
    string Name { get; }
    string Description { get; }
    IReadOnlyDictionary<string, JsonElement> Parameters { get; }
    Task<ToolResult> ExecuteAsync(IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken cancellationToken = default);
}
public interface IContextStore
{
    Task<IReadOnlyList<Message>> GetMessagesAsync(string sessionId, CancellationToken cancellationToken = default);
    Task AppendMessageAsync(string sessionId, Message message, CancellationToken cancellationToken = default);
    Task ReplaceMessagesAsync(string sessionId, IReadOnlyList<Message> messages, CancellationToken cancellationToken = default);
}
public interface ISandboxExecutor
{
    Task<CommandResult> ExecuteAsync(string command, string workspace, int timeout = 30, CancellationToken cancellationToken = default);
}
public interface IAgent
{
    Task<AgentResult> RunAsync(string sessionId, string userMessage, CancellationToken cancellationToken = default);
}
public class AgentError : Exception
{
    public AgentError(string message) : base(message) { }
    public AgentError(string message, Exception innerException) : base(message, innerException) { }
}
