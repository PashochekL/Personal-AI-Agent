using System.Text.Json;
using AiAgent.Shared;
using AiAgent.Core;
using Npgsql;

/// <summary>Local demonstration adapter; not the team's shared memory service.</summary>
sealed class PostgresSessionStore(NpgsqlDataSource database) : IContextStore, IContextSummaryCache
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var command = database.CreateCommand("""
            CREATE TABLE IF NOT EXISTS damir_demo_history (
                session_id text PRIMARY KEY,
                messages jsonb NOT NULL DEFAULT '[]'::jsonb
            );
            CREATE TABLE IF NOT EXISTS damir_demo_summaries (
                session_id text NOT NULL,
                cache_key text NOT NULL,
                summary text NOT NULL,
                PRIMARY KEY (session_id, cache_key)
            );
            CREATE TABLE IF NOT EXISTS damir_demo_usage (
                session_id text PRIMARY KEY,
                input_tokens bigint NOT NULL DEFAULT 0,
                output_tokens bigint NOT NULL DEFAULT 0,
                total_tokens bigint NOT NULL DEFAULT 0,
                missing_responses integer NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS damir_demo_context (
                session_id text PRIMARY KEY,
                prefer_summary boolean NOT NULL DEFAULT false
            )
            """);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> IsManualAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await using var command = database.CreateCommand("SELECT prefer_summary FROM damir_demo_context WHERE session_id = $1");
        command.Parameters.AddWithValue(sessionId);
        return (bool?)await command.ExecuteScalarAsync(cancellationToken) ?? false;
    }

    public async Task SetManualAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await using var command = database.CreateCommand("INSERT INTO damir_demo_context (session_id, prefer_summary) VALUES ($1, true) ON CONFLICT (session_id) DO UPDATE SET prefer_summary = true");
        command.Parameters.AddWithValue(sessionId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

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
        command.Parameters.AddWithValue((long)(usage?.InputTokens ?? 0));
        command.Parameters.AddWithValue((long)(usage?.OutputTokens ?? 0));
        command.Parameters.AddWithValue(total);
        command.Parameters.AddWithValue(missing);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<string?> GetAsync(string sessionId, string key, CancellationToken cancellationToken = default)
    {
        await using var command = database.CreateCommand("SELECT summary FROM damir_demo_summaries WHERE session_id = $1 AND cache_key = $2");
        command.Parameters.AddWithValue(sessionId);
        command.Parameters.AddWithValue(key);
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    public async Task PutAsync(string sessionId, string key, string summary, CancellationToken cancellationToken = default)
    {
        await using var command = database.CreateCommand("INSERT INTO damir_demo_summaries (session_id, cache_key, summary) VALUES ($1, $2, $3) ON CONFLICT DO NOTHING");
        command.Parameters.AddWithValue(sessionId);
        command.Parameters.AddWithValue(key);
        command.Parameters.AddWithValue(summary);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Message>> GetMessagesAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await using var command = database.CreateCommand("SELECT messages::text FROM damir_demo_history WHERE session_id = $1");
        command.Parameters.AddWithValue(sessionId);
        var json = (string?)await command.ExecuteScalarAsync(cancellationToken);
        if (json is null) return [];
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Select(x => ContractJson.Deserialize<Message>(x.GetRawText())).ToArray();
    }

    public async Task AppendMessageAsync(string sessionId, Message message, CancellationToken cancellationToken = default)
    {
        var json = ContractJson.Serialize(message);
        await using var command = database.CreateCommand("""
            INSERT INTO damir_demo_history (session_id, messages) VALUES ($1, jsonb_build_array($2::jsonb))
            ON CONFLICT (session_id) DO UPDATE SET messages = damir_demo_history.messages || jsonb_build_array($2::jsonb)
            """);
        command.Parameters.AddWithValue(sessionId);
        command.Parameters.AddWithValue(json);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ReplaceMessagesAsync(string sessionId, IReadOnlyList<Message> messages, CancellationToken cancellationToken = default)
    {
        var json = "[" + string.Join(',', messages.Select(ContractJson.Serialize)) + "]";
        await using var command = database.CreateCommand("""
            INSERT INTO damir_demo_history (session_id, messages) VALUES ($1, $2::jsonb)
            ON CONFLICT (session_id) DO UPDATE SET messages = EXCLUDED.messages
            """);
        command.Parameters.AddWithValue(sessionId);
        command.Parameters.AddWithValue(json);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
