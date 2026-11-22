using System.Diagnostics;
using AiAgent.Shared;

namespace AiAgent.Observability;

/// <summary>Records only model, timing and token counts. Payloads and exceptions are never recorded.</summary>
public sealed class ObservedLlmClient(ILlmClient inner, string model) : ILlmClient
{
    public async Task<LlmResponse> CompleteAsync(IReadOnlyList<Message> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken = default)
    {
        using var activity = AgentTelemetry.Source.StartActivity("llm.complete", ActivityKind.Client);
        activity?.SetTag("langfuse.observation.type", "generation");
        activity?.SetTag("gen_ai.operation.name", "chat");
        activity?.SetTag("gen_ai.provider.name", "litellm");
        activity?.SetTag("gen_ai.request.model", model.Length <= 128 && model.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '/' or ':') ? model : "custom");
        var start = Stopwatch.GetTimestamp();
        try
        {
            var result = await inner.CompleteAsync(messages, tools, cancellationToken);
            if (result.Usage?.InputTokens is int input) { activity?.SetTag("gen_ai.usage.input_tokens", input); AgentTelemetry.Tokens.Add(input, new KeyValuePair<string, object?>("direction", "input")); }
            if (result.Usage?.OutputTokens is int output) { activity?.SetTag("gen_ai.usage.output_tokens", output); AgentTelemetry.Tokens.Add(output, new KeyValuePair<string, object?>("direction", "output")); }
            if (result.FinishReason == "error") activity?.SetStatus(ActivityStatusCode.Error);
            return result;
        }
        catch (OperationCanceledException) { activity?.SetTag("damir.cancelled", true); throw; }
        catch { activity?.SetStatus(ActivityStatusCode.Error); throw; }
        finally { AgentTelemetry.LlmDuration.Record(Stopwatch.GetElapsedTime(start).TotalSeconds); }
    }
}
