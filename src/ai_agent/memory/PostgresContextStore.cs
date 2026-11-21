using System.Text.Json;
using AiAgent.Shared;
using Npgsql;

namespace AiAgent.Memory;

/// <summary>Shared history storage; preserves the existing tables and exchange JSON.</summary>
public sealed class PostgresContextStore(NpgsqlDataSource database) : IContextStore
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var command = database.CreateCommand("""
            CREATE TABLE IF NOT EXISTS damir_demo_history (
                session_id text PRIMARY KEY, messages jsonb NOT NULL DEFAULT '[]'::jsonb
            );
            CREATE TABLE IF NOT EXISTS damir_demo_summaries (
                session_id text NOT NULL, cache_key text NOT NULL, summary text NOT NULL,
                PRIMARY KEY (session_id, cache_key)
            );
            CREATE TABLE IF NOT EXISTS damir_demo_context (
                session_id text PRIMARY KEY, prefer_summary boolean NOT NULL DEFAULT false
            );
            """);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Message>> GetMessagesAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        await using var command = database.CreateCommand("SELECT messages::text FROM damir_demo_history WHERE session_id = $1");
        command.Parameters.AddWithValue(sessionId);
        var json = (string?)await command.ExecuteScalarAsync(cancellationToken);
        if (json is null) return [];
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Select(x => ContractJson.Deserialize<Message>(x.GetRawText())).ToArray();
    }

    public async Task AppendMessageAsync(string sessionId, Message message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
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
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var json = "[" + string.Join(',', messages.Select(ContractJson.Serialize)) + "]";
        await using var command = database.CreateCommand("""
            INSERT INTO damir_demo_history (session_id, messages) VALUES ($1, $2::jsonb)
            ON CONFLICT (session_id) DO UPDATE SET messages = EXCLUDED.messages
            """);
        command.Parameters.AddWithValue(sessionId);
        command.Parameters.AddWithValue(json);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<string?> GetSummaryAsync(string sessionId, string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await using var command = database.CreateCommand("SELECT summary FROM damir_demo_summaries WHERE session_id = $1 AND cache_key = $2");
        command.Parameters.AddWithValue(sessionId);
        command.Parameters.AddWithValue(key);
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    public async Task PutSummaryAsync(string sessionId, string key, string summary, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        await using var command = database.CreateCommand("""
            INSERT INTO damir_demo_summaries (session_id, cache_key, summary) VALUES ($1, $2, $3)
            ON CONFLICT (session_id, cache_key) DO NOTHING
            """);
        command.Parameters.AddWithValue(sessionId);
        command.Parameters.AddWithValue(key);
        command.Parameters.AddWithValue(summary);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> IsManualSummaryAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        await using var command = database.CreateCommand("SELECT prefer_summary FROM damir_demo_context WHERE session_id = $1");
        command.Parameters.AddWithValue(sessionId);
        return (bool?)await command.ExecuteScalarAsync(cancellationToken) ?? false;
    }

    public async Task SetManualSummaryAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        await using var command = database.CreateCommand("""
            INSERT INTO damir_demo_context (session_id, prefer_summary) VALUES ($1, true)
            ON CONFLICT (session_id) DO UPDATE SET prefer_summary = true
            """);
        command.Parameters.AddWithValue(sessionId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
