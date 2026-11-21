using AiAgent.Shared;
using AiAgent.Core;
using AiAgent.Memory;
using Npgsql;

/// <summary>Demo composition for shared history and local usage counters.</summary>
sealed class PostgresSessionStore(NpgsqlDataSource database) : IContextStore, IContextSummaryCache
{
    private readonly PostgresContextStore _history = new(database);
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _history.InitializeAsync(cancellationToken);
        await using var command = database.CreateCommand("""
            CREATE TABLE IF NOT EXISTS damir_demo_usage (
                session_id text PRIMARY KEY,
                input_tokens bigint NOT NULL DEFAULT 0,
                output_tokens bigint NOT NULL DEFAULT 0,
                total_tokens bigint NOT NULL DEFAULT 0,
                missing_responses integer NOT NULL DEFAULT 0
            );
            """);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
    public Task<IReadOnlyList<Message>> GetMessagesAsync(string sessionId, CancellationToken cancellationToken = default) => _history.GetMessagesAsync(sessionId, cancellationToken);
    public Task AppendMessageAsync(string sessionId, Message message, CancellationToken cancellationToken = default) => _history.AppendMessageAsync(sessionId, message, cancellationToken);
    public Task ReplaceMessagesAsync(string sessionId, IReadOnlyList<Message> messages, CancellationToken cancellationToken = default) => _history.ReplaceMessagesAsync(sessionId, messages, cancellationToken);
    public Task<string?> GetAsync(string sessionId, string key, CancellationToken cancellationToken = default) => _history.GetSummaryAsync(sessionId, key, cancellationToken);
    public Task PutAsync(string sessionId, string key, string summary, CancellationToken cancellationToken = default) => _history.PutSummaryAsync(sessionId, key, summary, cancellationToken);
    public Task<bool> IsManualAsync(string sessionId, CancellationToken cancellationToken = default) => _history.IsManualSummaryAsync(sessionId, cancellationToken);
    public Task SetManualAsync(string sessionId, CancellationToken cancellationToken = default) => _history.SetManualSummaryAsync(sessionId, cancellationToken);

    public async Task<UsageSnapshot> GetUsageAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await using var command = database.CreateCommand("SELECT input_tokens, output_tokens, total_tokens, missing_responses FROM damir_demo_usage WHERE session_id = $1");
        command.Parameters.AddWithValue(sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new UsageSnapshot(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt32(3), null)
            : new UsageSnapshot(0, 0, 0, 0, null);
    }
    public async Task AddUsageAsync(string sessionId, TokenUsage? usage, CancellationToken cancellationToken = default)
    {
        var total = usage?.TotalTokens is int reported ? (long)reported : usage?.InputTokens is int input && usage.OutputTokens is int output ? (long)input + output : 0;
        var missing = usage?.TotalTokens is null && (usage?.InputTokens is null || usage.OutputTokens is null) ? 1 : 0;
        await using var command = database.CreateCommand("""
            INSERT INTO damir_demo_usage (session_id, input_tokens, output_tokens, total_tokens, missing_responses)
            VALUES ($1, $2, $3, $4, $5)
            ON CONFLICT (session_id) DO UPDATE SET
                input_tokens = damir_demo_usage.input_tokens + EXCLUDED.input_tokens,
                output_tokens = damir_demo_usage.output_tokens + EXCLUDED.output_tokens,
                total_tokens = damir_demo_usage.total_tokens + EXCLUDED.total_tokens,
                missing_responses = damir_demo_usage.missing_responses + EXCLUDED.missing_responses
            """);
        command.Parameters.AddWithValue(sessionId);
        command.Parameters.AddWithValue(usage?.InputTokens is int countedInput ? (long)countedInput : 0L);
        command.Parameters.AddWithValue(usage?.OutputTokens is int countedOutput ? (long)countedOutput : 0L);
        command.Parameters.AddWithValue(total);
        command.Parameters.AddWithValue(missing);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
