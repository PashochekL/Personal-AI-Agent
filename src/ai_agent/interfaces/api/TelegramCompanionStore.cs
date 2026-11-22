using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace AiAgent.Api;

public sealed record CompanionState(string SessionId, long? UserId, long UpdateOffset, string? PendingTask,
    string[]? Parts, int NextPart, string? Model, string? Skill, string Channel, long? PendingUpdate, string? PendingMessage,
    bool Connected, string WorkspaceName, string PendingOperation);
public sealed record CompanionLink(string SessionId, long BotId, string Lease, string? Model = null, string? Skill = null);
public sealed record CompanionConnect(string Code, long UserId);
public sealed record CompanionResume(string SessionId, long UserId);
public sealed record CompanionCheckpoint(long UpdateOffset, string? PendingTask, string[]? Parts, int NextPart, long? PendingUpdate = null, string? PendingMessage = null, string PendingOperation = "chat");
public sealed record ConsoleResume(string SessionId, string? Model, string? Skill);

public sealed partial class PostgresTaskStore
{
    public async Task SaveConsoleResumeAsync(ConsoleResume resume, CancellationToken ct)
    {
        if (resume.SessionId is not { Length: 32 } || !resume.SessionId.All(char.IsAsciiHexDigit)) throw new TaskAdmissionException(400, "Invalid session.");
        if (!await HasSessionAsync(resume.SessionId, ct)) throw new TaskAdmissionException(404, "Session unavailable.");
        new TaskSelection(resume.Model, resume.Skill).Validate();
        await using var command = Scoped("INSERT INTO damir_console_resume(owner_id,project_id,session_id,model,skill) VALUES($1,$2,$3,$4,$5) ON CONFLICT(owner_id,project_id) DO UPDATE SET session_id=$3,model=$4,skill=$5");
        command.Parameters.AddWithValue(resume.SessionId);
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)resume.Model ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)resume.Skill ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        await command.ExecuteNonQueryAsync(ct);
    }
    public async Task<ConsoleResume?> ConsoleResumeAsync(CancellationToken ct)
    {
        await using var command = Scoped("DELETE FROM damir_console_resume WHERE owner_id=$1 AND project_id=$2 RETURNING session_id,model,skill");
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)) : null;
    }
    private static string HashLink(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));

    // Session row locks are shared with admission. A transfer cannot race a new task.
    private async Task LockIdleAsync(string sessionId, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct, bool allowRunning = false)
    {
        await using var session = Scoped("SELECT session_id FROM damir_api_sessions WHERE owner_id=$1 AND project_id=$2 AND session_id=$3 FOR UPDATE", connection, transaction);
        session.Parameters.AddWithValue(sessionId);
        if (await session.ExecuteScalarAsync(ct) is null) throw new TaskAdmissionException(404, "Диалог не найден.");
        if (allowRunning) return;
        await using var busy = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM damir_api_tasks WHERE session_id=$1 AND status IN ('PENDING','RUNNING'))", connection, transaction);
        busy.Parameters.AddWithValue(sessionId);
        if ((bool)(await busy.ExecuteScalarAsync(ct))!) throw new TaskAdmissionException(423, "Дождись ответа или останови его перед переключением.");
    }

    public async Task<string> CreateTelegramLinkAsync(CompanionLink link, CancellationToken ct)
    {
        if (link.BotId <= 0 || link.Lease is not { Length: 32 } || !link.Lease.All(char.IsAsciiHexDigit) ||
            link.SessionId is not { Length: 32 } || !link.SessionId.All(char.IsAsciiHexDigit)) throw new TaskAdmissionException(400, "Invalid connection.");
        new TaskSelection(link.Model, link.Skill).Validate();
        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await LockIdleAsync(link.SessionId, connection, transaction, ct, allowRunning: true);
        await using (var existing = Scoped("SELECT EXISTS(SELECT 1 FROM damir_telegram_companion WHERE owner_id=$1 AND project_id=$2 AND bot_id=$3 AND session_id=$4)", connection, transaction))
        {
            existing.Parameters.AddWithValue(link.BotId); existing.Parameters.AddWithValue(link.SessionId);
            if (!(bool)(await existing.ExecuteScalarAsync(ct))!) await LockIdleAsync(link.SessionId, connection, transaction, ct);
        }
        await using var command = Scoped("""
            INSERT INTO damir_telegram_companion(bot_id,owner_id,project_id,session_id,link_hash,link_expires,lease,lease_expires,model,skill)
            VALUES($3,$1,$2,$4,$5,now()+interval '10 minutes',$6,now()+interval '60 seconds',$7,$8)
            ON CONFLICT(bot_id) DO UPDATE SET project_id=EXCLUDED.project_id,session_id=EXCLUDED.session_id,
                link_hash=EXCLUDED.link_hash,link_expires=EXCLUDED.link_expires,lease=EXCLUDED.lease,
                lease_expires=EXCLUDED.lease_expires,
                confirmed_lease=CASE WHEN damir_telegram_companion.lease=$6 AND damir_telegram_companion.project_id=$2
                    AND damir_telegram_companion.session_id=$4 THEN damir_telegram_companion.confirmed_lease ELSE NULL END,
                model=CASE WHEN damir_telegram_companion.pending_message IS NOT NULL THEN damir_telegram_companion.model ELSE EXCLUDED.model END,
                skill=CASE WHEN damir_telegram_companion.pending_message IS NOT NULL THEN damir_telegram_companion.skill ELSE EXCLUDED.skill END
            WHERE damir_telegram_companion.owner_id=$1 AND
                (damir_telegram_companion.lease=$6 OR damir_telegram_companion.lease_expires<now())
                AND ((damir_telegram_companion.project_id=$2 AND damir_telegram_companion.session_id=$4)
                    OR (damir_telegram_companion.pending_task IS NULL AND damir_telegram_companion.parts IS NULL AND damir_telegram_companion.pending_message IS NULL))
            RETURNING bot_id
            """, connection, transaction);
        command.Parameters.AddWithValue(link.BotId); command.Parameters.AddWithValue(link.SessionId);
        command.Parameters.AddWithValue(HashLink(code)); command.Parameters.AddWithValue(link.Lease);
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)link.Model ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)link.Skill ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        if (await command.ExecuteScalarAsync(ct) is null) throw new TaskAdmissionException(423, "Этот бот уже открыт в другом Дамире или ещё доставляет ответ.");
        await transaction.CommitAsync(ct);
        return code;
    }

    public async Task<CompanionState> TelegramStateAsync(long botId, string lease, CancellationToken ct)
    {
        await using (var renew = Scoped("UPDATE damir_telegram_companion SET lease_expires=now()+interval '60 seconds' WHERE owner_id=$1 AND project_id=$2 AND bot_id=$3 AND lease=$4"))
        {
            renew.Parameters.AddWithValue(botId); renew.Parameters.AddWithValue(lease);
            if (await renew.ExecuteNonQueryAsync(ct) != 1) throw new TaskAdmissionException(403, "Connection unavailable.");
        }
        await using var command = Scoped("""
            SELECT c.session_id,c.user_id,c.update_offset,c.pending_task,c.parts,c.next_part,c.model,c.skill,s.input_channel,c.pending_update,c.pending_message,
                COALESCE(c.confirmed_lease=c.lease,false),c.pending_operation
            FROM damir_telegram_companion c JOIN damir_api_sessions s USING(session_id)
            WHERE c.owner_id=$1 AND c.project_id=$2 AND c.bot_id=$3 AND c.lease=$4
            """);
        command.Parameters.AddWithValue(botId); command.Parameters.AddWithValue(lease);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new TaskAdmissionException(403, "Connection unavailable.");
        return new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.GetInt64(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetFieldValue<string[]>(4),
            reader.GetInt32(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetInt64(9), reader.IsDBNull(10) ? null : reader.GetString(10), reader.GetBoolean(11), options.WorkspaceName, reader.GetString(12));
    }

    public async Task ConnectTelegramAsync(long botId, string lease, CompanionConnect connect, CancellationToken ct)
    {
        if (connect.UserId <= 0 || connect.Code is not { Length: 48 } || !connect.Code.All(char.IsAsciiHexDigit)) throw new TaskAdmissionException(403, "Invalid link.");
        var snapshot = await TelegramStateAsync(botId, lease, ct);
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await LockIdleAsync(snapshot.SessionId, connection, transaction, ct);
        await using var bind = Scoped("""
            UPDATE damir_telegram_companion SET user_id=$5,link_hash=NULL,link_expires=NULL,confirmed_lease=lease
            WHERE owner_id=$1 AND project_id=$2 AND bot_id=$3 AND lease=$4 AND session_id=$6
                AND link_hash=$7 AND link_expires>now() AND (user_id IS NULL OR user_id=$5)
            """, connection, transaction);
        bind.Parameters.AddWithValue(botId); bind.Parameters.AddWithValue(lease); bind.Parameters.AddWithValue(connect.UserId);
        bind.Parameters.AddWithValue(snapshot.SessionId); bind.Parameters.AddWithValue(HashLink(connect.Code));
        if (await bind.ExecuteNonQueryAsync(ct) != 1) throw new TaskAdmissionException(403, "Invalid link or account.");
        await using var channel = new NpgsqlCommand("UPDATE damir_api_sessions SET input_channel='telegram' WHERE session_id=$1", connection, transaction);
        channel.Parameters.AddWithValue(snapshot.SessionId); await channel.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task ResumeTelegramAsync(long botId, string lease, CompanionResume resume, CancellationToken ct)
    {
        var state = await TelegramStateAsync(botId, lease, ct);
        if (!state.Connected || resume.UserId <= 0 || state.UserId != resume.UserId || state.SessionId != resume.SessionId)
            throw new TaskAdmissionException(403, "Telegram account or dialogue mismatch.");
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await LockIdleAsync(resume.SessionId, connection, transaction, ct);
        await using var binding = Scoped("""
            SELECT session_id FROM damir_telegram_companion
            WHERE owner_id=$1 AND project_id=$2 AND bot_id=$3 AND lease=$4 AND confirmed_lease=lease AND session_id=$5 AND user_id=$6 FOR UPDATE
            """, connection, transaction);
        binding.Parameters.AddWithValue(botId); binding.Parameters.AddWithValue(lease);
        binding.Parameters.AddWithValue(resume.SessionId); binding.Parameters.AddWithValue(resume.UserId);
        if (await binding.ExecuteScalarAsync(ct) is null) throw new TaskAdmissionException(403, "Telegram binding changed.");
        await using var channel = Scoped("UPDATE damir_api_sessions SET input_channel='telegram' WHERE owner_id=$1 AND project_id=$2 AND session_id=$3", connection, transaction);
        channel.Parameters.AddWithValue(resume.SessionId); await channel.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task ReturnToConsoleAsync(string sessionId, CancellationToken ct)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await LockIdleAsync(sessionId, connection, transaction, ct);
        await using (var pending = Scoped("SELECT EXISTS(SELECT 1 FROM damir_telegram_companion WHERE owner_id=$1 AND project_id=$2 AND session_id=$3 AND pending_message IS NOT NULL)", connection, transaction))
        {
            pending.Parameters.AddWithValue(sessionId);
            if ((bool)(await pending.ExecuteScalarAsync(ct))!) throw new TaskAdmissionException(423, "Дождись отправки или останови сообщение в Telegram.");
        }
        await using var command = Scoped("UPDATE damir_api_sessions SET input_channel='console' WHERE owner_id=$1 AND project_id=$2 AND session_id=$3", connection, transaction);
        command.Parameters.AddWithValue(sessionId); await command.ExecuteNonQueryAsync(ct);
        await using var invalidate = Scoped("UPDATE damir_telegram_companion SET link_hash=NULL,link_expires=NULL WHERE owner_id=$1 AND project_id=$2 AND session_id=$3", connection, transaction);
        invalidate.Parameters.AddWithValue(sessionId); await invalidate.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task CheckpointTelegramAsync(long botId, string lease, CompanionCheckpoint checkpoint, CancellationToken ct)
    {
        new TaskSelection(Operation: checkpoint.PendingOperation).Validate();
        if (checkpoint.UpdateOffset < 0 || checkpoint.NextPart < 0 || checkpoint.NextPart > (checkpoint.Parts?.Length ?? 0) ||
            checkpoint.PendingMessage is not null && (string.IsNullOrWhiteSpace(checkpoint.PendingMessage) || checkpoint.PendingMessage.Length > 32000 ||
                checkpoint.PendingUpdate is null or < 0 || checkpoint.PendingUpdate >= checkpoint.UpdateOffset || checkpoint.PendingTask is not null) ||
            checkpoint.Parts is { Length: > 200 } || checkpoint.Parts?.Any(p => string.IsNullOrEmpty(p) || p.Length > 4000) == true)
            throw new TaskAdmissionException(400, "Invalid checkpoint.");
        var state = await TelegramStateAsync(botId, lease, ct);
        if (checkpoint.PendingMessage is not null && !state.Connected) throw new TaskAdmissionException(403, "Open the current project link first.");
        if (checkpoint.PendingTask is { } task && (await GetAsync(task, ct))?.SessionId != state.SessionId)
            throw new TaskAdmissionException(400, "Task session mismatch.");
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        if (checkpoint.PendingMessage is not null)
        {
            await LockIdleAsync(state.SessionId, connection, transaction, ct, allowRunning: true);
            await using var channel = new NpgsqlCommand("SELECT input_channel FROM damir_api_sessions WHERE session_id=$1", connection, transaction);
            channel.Parameters.AddWithValue(state.SessionId);
            if ((string)(await channel.ExecuteScalarAsync(ct))! != "telegram") throw new TaskAdmissionException(423, "Диалог уже на компьютере.");
        }
        await using var command = Scoped("""
            UPDATE damir_telegram_companion SET update_offset=$5,pending_task=$6,parts=$7,next_part=$8,pending_update=$9,pending_message=$10,pending_operation=$11,
                skill=CASE WHEN $6::text IS NOT NULL AND $11='chat' THEN NULL ELSE skill END
            WHERE owner_id=$1 AND project_id=$2 AND bot_id=$3 AND lease=$4 AND update_offset<=$5
            """, connection, transaction);
        command.Parameters.AddWithValue(botId); command.Parameters.AddWithValue(lease); command.Parameters.AddWithValue(checkpoint.UpdateOffset);
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)checkpoint.PendingTask ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)checkpoint.Parts ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text });
        command.Parameters.AddWithValue(checkpoint.NextPart);
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)checkpoint.PendingUpdate ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)checkpoint.PendingMessage ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        command.Parameters.AddWithValue(checkpoint.PendingOperation);
        await command.ExecuteNonQueryAsync(ct); await transaction.CommitAsync(ct);
    }

    public async Task ReleaseTelegramAsync(long botId, string lease, CancellationToken ct)
    {
        await using var command = Scoped("UPDATE damir_telegram_companion SET lease_expires=now() WHERE owner_id=$1 AND project_id=$2 AND bot_id=$3 AND lease=$4");
        command.Parameters.AddWithValue(botId); command.Parameters.AddWithValue(lease); await command.ExecuteNonQueryAsync(ct);
    }
}
