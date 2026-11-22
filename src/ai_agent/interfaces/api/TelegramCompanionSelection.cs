using Npgsql;

namespace AiAgent.Api;

public sealed record CompanionSelection(string SessionId, long UserId, string? Model, string? Skill);

public sealed partial class PostgresTaskStore
{
    public async Task SelectTelegramAsync(long botId, string lease, CompanionSelection selection, CancellationToken ct)
    {
        new TaskSelection(selection.Model, selection.Skill).Validate();
        var state = await TelegramStateAsync(botId, lease, ct);
        if (!state.Connected || state.UserId != selection.UserId || state.SessionId != selection.SessionId)
            throw new TaskAdmissionException(403, "Telegram binding mismatch.");
        if (state.Channel != "telegram") throw new TaskAdmissionException(423, "Сначала верни диалог в Telegram.");
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await LockIdleAsync(state.SessionId, connection, transaction, ct);
        await using var command = Scoped("""
            UPDATE damir_telegram_companion SET model=$7,skill=$8
            WHERE owner_id=$1 AND project_id=$2 AND bot_id=$3 AND lease=$4 AND confirmed_lease=lease
                AND session_id=$5 AND user_id=$6 AND pending_message IS NULL AND pending_task IS NULL AND parts IS NULL
                AND EXISTS(SELECT 1 FROM damir_api_sessions WHERE session_id=$5 AND input_channel='telegram')
            """, connection, transaction);
        command.Parameters.AddWithValue(botId); command.Parameters.AddWithValue(lease);
        command.Parameters.AddWithValue(selection.SessionId); command.Parameters.AddWithValue(selection.UserId);
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)selection.Model ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)selection.Skill ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw new TaskAdmissionException(423, "Дождись завершения ответа перед выбором.");
        await transaction.CommitAsync(ct);
    }
}
