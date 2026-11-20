using AiAgent.Shared;

namespace AiAgent.Core;

/// <summary>Counts usage reported by the gateway, including summarization calls.</summary>
public sealed class UsageTotals
{
    private readonly object _sync = new();
    private long _input;
    private long _output;
    private long _total;
    private int _missing;
    private int? _lastInput;

    public UsageTotals(UsageSnapshot? initial = null)
    {
        if (initial is null) return;
        _input = initial.Input;
        _output = initial.Output;
        _total = initial.Total;
        _missing = initial.MissingUsageResponses;
        _lastInput = initial.LastInput;
    }

    public void Record(TokenUsage? usage)
    {
        lock (_sync)
        {
            _lastInput = usage?.InputTokens;
            if (usage?.InputTokens is int input) _input += input;
            if (usage?.OutputTokens is int output) _output += output;
            if (usage?.TotalTokens is int total) _total += total;
            else if (usage?.InputTokens is int i && usage.OutputTokens is int o) _total += (long)i + o;
            else _missing++;
        }
    }

    public UsageSnapshot Snapshot()
    {
        lock (_sync) return new(_input, _output, _total, _missing, _lastInput);
    }

    public void ClearLastInput()
    {
        lock (_sync) _lastInput = null;
    }
}

public sealed record UsageSnapshot(long Input, long Output, long Total, int MissingUsageResponses, int? LastInput);

public sealed class UsageTrackingClient(ILlmClient inner, UsageTotals totals, Func<TokenUsage?, CancellationToken, Task>? persist = null) : ILlmClient
{
    public async Task<LlmResponse> CompleteAsync(IReadOnlyList<Message> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken = default)
    {
        var result = await inner.CompleteAsync(messages, tools, cancellationToken);
        if (persist is not null) await persist(result.Usage, cancellationToken);
        totals.Record(result.Usage);
        return result;
    }
}
