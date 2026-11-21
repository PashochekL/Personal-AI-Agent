using AiAgent.Shared;
using Npgsql;
using System.Text.Json;
using TaskStatus = AiAgent.Shared.TaskStatus;

namespace AiAgent.Api;

/// <summary>Durable admission, ordered execution and terminal results. Redis carries only notifications.</summary>
public sealed class PostgresTaskStore(NpgsqlDataSource database, BackendOptions options)
{
    private NpgsqlCommand Scoped(string sql, NpgsqlConnection? connection = null, NpgsqlTransaction? transaction = null)
    {
        var command = connection is null ? database.CreateCommand(sql) : new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(options.Scope.OwnerId);
        command.Parameters.AddWithValue(options.Scope.ProjectId);
        return command;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var command = database.CreateCommand("""
            CREATE TABLE IF NOT EXISTS damir_api_sessions (
                session_id text PRIMARY KEY, owner_id text NOT NULL, project_id text NOT NULL,
                created_at timestamptz NOT NULL DEFAULT now(), dispatched_at timestamptz NOT NULL DEFAULT '-infinity');
            CREATE TABLE IF NOT EXISTS damir_api_tasks (
                task_id text PRIMARY KEY, session_id text NOT NULL REFERENCES damir_api_sessions(session_id),
                sequence bigint GENERATED ALWAYS AS IDENTITY UNIQUE, user_message text NOT NULL,
                status text NOT NULL CHECK(status IN ('PENDING','RUNNING','COMPLETED','FAILED','CANCELLED')),
                result jsonb, cancel_requested boolean NOT NULL DEFAULT false, request_key text,
                created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(),
                UNIQUE(session_id, request_key),
                CHECK ((status IN ('PENDING','RUNNING') AND result IS NULL) OR
                    (status IN ('COMPLETED','FAILED') AND result IS NOT NULL) OR status = 'CANCELLED'));
            CREATE INDEX IF NOT EXISTS damir_api_pending ON damir_api_tasks(session_id, sequence)
                WHERE status IN ('PENDING','RUNNING');
            CREATE INDEX IF NOT EXISTS damir_api_scope ON damir_api_sessions(owner_id, project_id);
            ALTER TABLE damir_api_sessions ADD COLUMN IF NOT EXISTS client_name text;
            CREATE UNIQUE INDEX IF NOT EXISTS damir_api_named_session ON damir_api_sessions(owner_id,project_id,client_name);
            ALTER TABLE damir_api_tasks ADD COLUMN IF NOT EXISTS selection text NOT NULL DEFAULT '{}';
            """);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<Session> CreateSessionAsync(CancellationToken ct = default)
    {
        var session = new Session { SessionId = Guid.NewGuid().ToString("N") };
        await using var command = Scoped("INSERT INTO damir_api_sessions(session_id,owner_id,project_id) VALUES($3,$1,$2)");
        command.Parameters.AddWithValue(session.SessionId);
        await command.ExecuteNonQueryAsync(ct);
        return session;
    }

    public async Task<bool> HasSessionAsync(string id, CancellationToken ct = default)
    {
        await using var command = Scoped("SELECT EXISTS(SELECT 1 FROM damir_api_sessions WHERE owner_id=$1 AND project_id=$2 AND session_id=$3)");
        command.Parameters.AddWithValue(id);
        return (bool)(await command.ExecuteScalarAsync(ct))!;
    }

    public async Task<Session> ResolveSessionAsync(string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(char.IsControl))
            throw new TaskAdmissionException(400, "Invalid session name.");
        await using var command = Scoped("""
            INSERT INTO damir_api_sessions(session_id,owner_id,project_id,client_name) VALUES($3,$1,$2,$4)
            ON CONFLICT(owner_id,project_id,client_name) DO UPDATE SET client_name=EXCLUDED.client_name RETURNING session_id
            """);
        command.Parameters.AddWithValue(Guid.NewGuid().ToString("N")); command.Parameters.AddWithValue(name);
        return new Session { SessionId = (string)(await command.ExecuteScalarAsync(ct))! };
    }

    public async Task<TaskSelection> SelectionAsync(string taskId, CancellationToken ct)
    {
        await using var command = Scoped("SELECT t.selection FROM damir_api_tasks t JOIN damir_api_sessions s USING(session_id) WHERE s.owner_id=$1 AND s.project_id=$2 AND task_id=$3");
        command.Parameters.AddWithValue(taskId);
        var json = (string?)await command.ExecuteScalarAsync(ct) ?? throw new InvalidOperationException("Task not found");
        return JsonSerializer.Deserialize<TaskSelection>(json) ?? new();
    }

    public async Task<AgentTask?> ReplayAsync(TaskRequest request, string? key, CancellationToken ct)
    {
        if (key is null) return null;
        await using var command = Scoped($"SELECT {Columns},t.selection FROM damir_api_tasks t JOIN damir_api_sessions s USING(session_id) WHERE s.owner_id=$1 AND s.project_id=$2 AND s.session_id=$3 AND t.request_key=$4");
        command.Parameters.AddWithValue(request.SessionId); command.Parameters.AddWithValue(key);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var previous = Read(reader);
        if (previous.UserMessage != request.UserMessage || JsonSerializer.Deserialize<TaskSelection>(reader.GetString(5)) != request.Selection)
            throw new TaskAdmissionException(409, "Idempotency-Key has a different request.");
        return previous;
    }

    private const string Columns = "t.task_id,t.session_id,t.user_message,t.status,t.result::text";
    private static AgentTask Read(NpgsqlDataReader reader)
    {
        var task = new AgentTask { TaskId = reader.GetString(0), SessionId = reader.GetString(1), UserMessage = reader.GetString(2),
            Status = Enum.Parse<TaskStatus>(reader.GetString(3)), Result = reader.IsDBNull(4) ? null : ContractJson.Deserialize<AgentResult>(reader.GetString(4)) };
        task.Validate();
        return task;
    }

    public async Task<AgentTask?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var command = Scoped($"SELECT {Columns} FROM damir_api_tasks t JOIN damir_api_sessions s USING(session_id) WHERE s.owner_id=$1 AND s.project_id=$2 AND t.task_id=$3");
        command.Parameters.AddWithValue(id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task<AgentTask?> LatestAsync(string session, CancellationToken ct)
    {
        await using var command = Scoped($"SELECT {Columns} FROM damir_api_tasks t JOIN damir_api_sessions s USING(session_id) WHERE s.owner_id=$1 AND s.project_id=$2 AND s.session_id=$3 ORDER BY t.sequence DESC LIMIT 1");
        command.Parameters.AddWithValue(session);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task<AgentTask> SubmitAsync(TaskRequest request, string? requestKey, CancellationToken ct = default)
    {
        request.Selection.Validate();
        if (string.IsNullOrWhiteSpace(request.SessionId) || request.SessionId.Length > 128 || string.IsNullOrWhiteSpace(request.UserMessage) || request.UserMessage.Length > 32000)
            throw new TaskAdmissionException(400, "Invalid session_id or user_message (maximum 32000 characters).");
        if (requestKey is not null && (requestKey.Length is < 1 or > 128 || requestKey.Any(c => c is < '!' or > '~')))
            throw new TaskAdmissionException(400, "Invalid Idempotency-Key.");
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var session = Scoped("SELECT session_id FROM damir_api_sessions WHERE owner_id=$1 AND project_id=$2 AND session_id=$3 FOR UPDATE", connection, transaction))
        {
            session.Parameters.AddWithValue(request.SessionId);
            if (await session.ExecuteScalarAsync(ct) is null) throw new TaskAdmissionException(404, "Session not found.");
        }
        if (requestKey is not null)
        {
            await using var existing = new NpgsqlCommand($"SELECT {Columns},t.selection FROM damir_api_tasks t WHERE session_id=$1 AND request_key=$2", connection, transaction);
            existing.Parameters.AddWithValue(request.SessionId); existing.Parameters.AddWithValue(requestKey);
            await using var reader = await existing.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var previous = Read(reader);
                if (previous.UserMessage != request.UserMessage || JsonSerializer.Deserialize<TaskSelection>(reader.GetString(5)) != request.Selection)
                    throw new TaskAdmissionException(409, "Idempotency-Key has a different request.");
                return previous;
            }
        }
        await using (var count = new NpgsqlCommand("SELECT count(*) FROM damir_api_tasks WHERE session_id=$1 AND status IN ('PENDING','RUNNING')", connection, transaction))
        {
            count.Parameters.AddWithValue(request.SessionId);
            if ((long)(await count.ExecuteScalarAsync(ct))! >= 100) throw new TaskAdmissionException(429, "Session queue is full.");
        }
        var task = new AgentTask { TaskId = Guid.NewGuid().ToString("N"), SessionId = request.SessionId, UserMessage = request.UserMessage };
        await using (var insert = new NpgsqlCommand("INSERT INTO damir_api_tasks(task_id,session_id,user_message,status,request_key,selection) VALUES($1,$2,$3,'PENDING',$4,$5)", connection, transaction))
        {
            insert.Parameters.AddWithValue(task.TaskId); insert.Parameters.AddWithValue(task.SessionId); insert.Parameters.AddWithValue(task.UserMessage);
            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text, Value = (object?)requestKey ?? DBNull.Value });
            insert.Parameters.AddWithValue(JsonSerializer.Serialize(request.Selection));
            await insert.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return task;
    }

    public async Task<AgentTask?> CancelAsync(string id, CancellationToken ct = default)
    {
        await using var command = Scoped("""
            UPDATE damir_api_tasks t SET cancel_requested=true,
                status=CASE WHEN t.status='PENDING' THEN 'CANCELLED' ELSE t.status END, updated_at=now()
            FROM damir_api_sessions s WHERE t.session_id=s.session_id AND s.owner_id=$1 AND s.project_id=$2
                AND t.task_id=$3 AND t.status IN ('PENDING','RUNNING')
            """);
        command.Parameters.AddWithValue(id);
        await command.ExecuteNonQueryAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<IReadOnlyList<string>> PendingSessionsAsync(CancellationToken ct)
    {
        await using var command = Scoped("""
            SELECT s.session_id FROM damir_api_sessions s WHERE owner_id=$1 AND project_id=$2
              AND EXISTS(SELECT 1 FROM damir_api_tasks t WHERE t.session_id=s.session_id AND t.status='PENDING')
            ORDER BY dispatched_at,created_at LIMIT 100
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var sessions = new List<string>();
        while (await reader.ReadAsync(ct)) sessions.Add(reader.GetString(0));
        return sessions;
    }

    public async Task DispatchedAsync(string sessionId, CancellationToken ct)
    {
        await using var command = Scoped("UPDATE damir_api_sessions SET dispatched_at=now() WHERE owner_id=$1 AND project_id=$2 AND session_id=$3");
        command.Parameters.AddWithValue(sessionId); await command.ExecuteNonQueryAsync(ct);
    }

    // Called only while holding the deployment Worker lease and a session lease.
    public async Task<AgentTask?> ClaimAsync(string sessionId, CancellationToken ct)
    {
        await using var command = Scoped($"""
            UPDATE damir_api_tasks t SET status='RUNNING',updated_at=now()
            WHERE t.task_id=(SELECT p.task_id FROM damir_api_tasks p JOIN damir_api_sessions s USING(session_id)
                WHERE s.owner_id=$1 AND s.project_id=$2 AND p.session_id=$3 AND p.status='PENDING'
                AND NOT EXISTS(SELECT 1 FROM damir_api_tasks r WHERE r.session_id=p.session_id AND r.status='RUNNING')
                ORDER BY p.sequence LIMIT 1) AND t.status='PENDING' RETURNING {Columns}
            """);
        command.Parameters.AddWithValue(sessionId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task<bool> CancellationRequestedAsync(string id, CancellationToken ct)
    {
        await using var command = Scoped("SELECT t.cancel_requested FROM damir_api_tasks t JOIN damir_api_sessions s USING(session_id) WHERE s.owner_id=$1 AND s.project_id=$2 AND t.task_id=$3 AND t.status='RUNNING'");
        command.Parameters.AddWithValue(id);
        return await command.ExecuteScalarAsync(ct) is not false;
    }

    public async Task FinishAsync(AgentTask task, AgentResult result, CancellationToken ct)
    {
        result.Validate();
        if (result.SessionId != task.SessionId) throw new ArgumentException("Result session mismatch");
        var cancelled = ContractJson.Serialize(new AgentResult { SessionId = task.SessionId, Status = TaskStatus.CANCELLED, Response = result.Response });
        await using var command = Scoped("""
            UPDATE damir_api_tasks t SET status=CASE WHEN cancel_requested THEN 'CANCELLED' ELSE $4 END,
                result=CASE WHEN cancel_requested THEN $5::jsonb ELSE $6::jsonb END,updated_at=now()
            FROM damir_api_sessions s WHERE s.session_id=t.session_id AND s.owner_id=$1 AND s.project_id=$2
                AND t.task_id=$3 AND t.status='RUNNING'
            """);
        command.Parameters.AddWithValue(task.TaskId); command.Parameters.AddWithValue(result.Status.ToString());
        command.Parameters.AddWithValue(cancelled); command.Parameters.AddWithValue(ContractJson.Serialize(result));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task RecoverInterruptedAsync(CancellationToken ct)
    {
        // Never retry an execution whose external effects may have happened before a crash.
        await using var command = Scoped("""
            UPDATE damir_api_tasks t SET status='FAILED',updated_at=now(),result=jsonb_build_object(
                'session_id',t.session_id,'status','FAILED','response',NULL,
                'error','Worker interrupted. Outcome may be partial; this task will not be replayed.')
            FROM damir_api_sessions s WHERE s.session_id=t.session_id AND s.owner_id=$1 AND s.project_id=$2 AND t.status='RUNNING'
            """);
        await command.ExecuteNonQueryAsync(ct);
    }

    public Task<NpgsqlConnection?> TryWorkerLeaseAsync(CancellationToken ct) => TryLeaseAsync("damir-worker:" + options.Scope.OwnerId.Length + ":" + options.Scope.OwnerId + options.Scope.ProjectId, ct);
    public Task<NpgsqlConnection?> TrySessionLeaseAsync(string sessionId, CancellationToken ct) => TryLeaseAsync(sessionId, ct);
    private async Task<NpgsqlConnection?> TryLeaseAsync(string key, CancellationToken ct)
    {
        var connection = await database.OpenConnectionAsync(ct);
        try
        {
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtextextended($1,0))", connection);
            command.Parameters.AddWithValue(key);
            if ((bool)(await command.ExecuteScalarAsync(ct))!) return connection;
            await connection.DisposeAsync(); return null;
        }
        catch { await connection.DisposeAsync(); throw; }
    }
}
