using AiAgent.Shared;
using TaskStatus = AiAgent.Shared.TaskStatus;

namespace AiAgent.Core;

/// <summary>Runs one session turn. The caller must serialize runs for the same session.</summary>
public sealed class Agent : IAgent
{
    private readonly ILlmClient _llm;
    private readonly IContextStore _store;
    private readonly ToolRegistry _tools;
    private readonly int _maxIterations;

    public Agent(ILlmClient llm, IContextStore store, ToolRegistry tools, int maxIterations = 20)
    {
        ArgumentNullException.ThrowIfNull(llm);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(tools);
        if (maxIterations < 1) throw new ArgumentOutOfRangeException(nameof(maxIterations));
        _llm = llm;
        _store = store;
        _tools = tools;
        _maxIterations = maxIterations;
    }

    public async Task<AgentResult> RunAsync(string sessionId, string userMessage, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("Session ID is required", nameof(sessionId));
        ArgumentNullException.ThrowIfNull(userMessage);
        AgentResult Fail(string error) => new() { SessionId = sessionId, Status = TaskStatus.FAILED, Error = error };
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var messages = (await _store.GetMessagesAsync(sessionId, cancellationToken)).ToList();
            cancellationToken.ThrowIfCancellationRequested();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var pending = new HashSet<string>(StringComparer.Ordinal);
            foreach (var message in messages)
            {
                message.Validate();
                if (pending.Count > 0 && message.Role != "tool") return Fail("Incomplete tool history");
                foreach (var call in message.ToolCalls)
                {
                    if (!seen.Add(call.Id)) return Fail("Duplicate tool call ID");
                    pending.Add(call.Id);
                }
                if (message.Role == "tool" && !pending.Remove(message.ToolCallId!)) return Fail("Invalid tool history");
            }
            if (pending.Count > 0) return Fail("Incomplete tool history");
            var user = new Message { Role = "user", Content = userMessage };
            await _store.AppendMessageAsync(sessionId, user, cancellationToken);
            messages.Add(user);
            for (var i = 0; i < _maxIterations; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var response = await _llm.CompleteAsync(messages.ToArray(), _tools.Definitions(), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                response.Validate();
                if (response.FinishReason is "error" or "length") return Fail($"LLM finished with {response.FinishReason}");
                if (response.ToolCalls.Count > 0)
                {
                    foreach (var call in response.ToolCalls)
                        if (!seen.Add(call.Id)) return Fail("Duplicate tool call ID");
                    var assistant = new Message { Role = "assistant", Content = response.Content, ToolCalls = response.ToolCalls };
                    await _store.AppendMessageAsync(sessionId, assistant, cancellationToken);
                    messages.Add(assistant);
                    foreach (var call in response.ToolCalls)
                    {
                        var result = await _tools.ExecuteAsync(call, cancellationToken);
                        var content = result.Success ? result.Content : $"Error: {result.Error}\n{result.Content}";
                        var toolMessage = new Message { Role = "tool", Content = content, ToolCallId = call.Id };
                        await _store.AppendMessageAsync(sessionId, toolMessage, cancellationToken);
                        messages.Add(toolMessage);
                    }
                    continue;
                }
                if (response.FinishReason == "tool_calls" || string.IsNullOrWhiteSpace(response.Content)) return Fail("LLM returned no final answer");
                var final = new Message { Role = "assistant", Content = response.Content };
                await _store.AppendMessageAsync(sessionId, final, cancellationToken);
                return new AgentResult { SessionId = sessionId, Status = TaskStatus.COMPLETED, Response = response.Content };
            }
            return Fail("Agent iteration limit reached");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new AgentResult { SessionId = sessionId, Status = TaskStatus.CANCELLED };
        }
        catch (OperationCanceledException) { return Fail("Component operation cancelled unexpectedly"); }
        catch (AgentError) { return Fail("Agent infrastructure failure"); }
        catch (ArgumentException) { return Fail("Invalid data from a component"); }
    }
}
