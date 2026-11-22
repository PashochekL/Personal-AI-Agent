using AiAgent.Shared;
using AiAgent.Observability;
using System.Diagnostics;
using Npgsql;
using StackExchange.Redis;
using TaskStatus = AiAgent.Shared.TaskStatus;

namespace AiAgent.Api;

public interface IBackendAgentFactory
{
    Task<IAgent> CreateAsync(CancellationToken cancellationToken);
    async Task<AgentResult> ExecuteAsync(AgentTask task, TaskSelection selection, CancellationToken ct)
    {
        if (selection != new TaskSelection()) throw new TaskAdmissionException(400, "Console operations are unavailable.");
        var agent = await CreateAsync(ct);
        return await agent.RunAsync(task.SessionId, task.UserMessage, ct);
    }
}

/// <summary>Task orchestration only. All agent reasoning and tool execution stay in Core.</summary>
public sealed class AgentWorker(PostgresTaskStore store, RedisTaskQueue queue, IBackendAgentFactory agents,
    BackendOptions options, IHostApplicationLifetime lifetime, ILogger<AgentWorker> logger) : BackgroundService
{
    private NpgsqlConnection? _workerLease;
    public bool Ready { get; private set; }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _workerLease = await store.TryWorkerLeaseAsync(cancellationToken)
            ?? throw new InvalidOperationException("Another Worker already owns this owner/project. Stop it before starting this backend.");
        try { await store.RecoverInterruptedAsync(cancellationToken); await base.StartAsync(cancellationToken); Ready = true; }
        catch { await ReleaseAsync(_workerLease); _workerLease = null; throw; }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var jobs = Enumerable.Range(0, options.Workers).Select(_ => ConsumeAsync(stop.Token)).ToList();
        jobs.Add(DispatchAsync(stop.Token));
        jobs.Add(WatchLeaseAsync(stop));
        try { await Task.WhenAny(jobs); stop.Cancel(); await Task.WhenAll(jobs); }
        finally { Ready = false; if (_workerLease is not null) await ReleaseAsync(_workerLease); }
    }

    private async Task WatchLeaseAsync(CancellationTokenSource stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stop.Token);
                await using var ping = new NpgsqlCommand("SELECT 1", _workerLease) { CommandTimeout = 3 };
                await ping.ExecuteScalarAsync(stop.Token);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception e) when (e is NpgsqlException or InvalidOperationException)
        {
            Ready = false;
            logger.LogError("Worker database lease lost; stopping execution.");
            stop.Cancel(); lifetime.StopApplication();
        }
    }

    private async Task DispatchAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                foreach (var session in await store.PendingSessionsAsync(ct))
                {
                    await queue.NotifyAsync(session, ct);
                    await store.DispatchedAsync(session, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) when (e is NpgsqlException or RedisException or TimeoutException)
            { logger.LogWarning("Task dispatch unavailable; pending tasks remain in PostgreSQL."); }
            try { await Task.Delay(options.PollInterval, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var session = await queue.PopAsync(ct);
                if (session is null) { await Task.Delay(options.PollInterval, ct); continue; }
                var lease = await store.TrySessionLeaseAsync(session, ct);
                if (lease is null) { await Task.Delay(options.PollInterval, ct); continue; }
                try
                {
                    var task = await store.ClaimAsync(session, ct);
                    if (task is not null) await RunAsync(task, lease, ct);
                }
                finally { await ReleaseAsync(lease); }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) when (e is NpgsqlException or RedisException or TimeoutException or OperationCanceledException)
            {
                // A failed result write leaves RUNNING for explicit recovery on restart, never replay.
                logger.LogWarning("Worker dependency unavailable; execution is not retried.");
                try { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            }
        }
    }

    private async Task RunAsync(AgentTask task, NpgsqlConnection sessionLease, CancellationToken hostToken)
    {
        using var activity = AgentTelemetry.StartTask(task.TaskId, task.SessionId, options.Scope, await store.TraceParentAsync(task.TaskId, hostToken));
        var started = Stopwatch.GetTimestamp();
        using var logScope = logger.BeginScope(new Dictionary<string, object?> { ["TraceId"] = activity?.TraceId.ToString(), ["SpanId"] = activity?.SpanId.ToString(), ["TaskId"] = task.TaskId });
        logger.LogInformation("Task {TaskId} running in session {SessionId}", task.TaskId, task.SessionId);
        using var timeout = new CancellationTokenSource(options.TaskTimeout);
        using var run = CancellationTokenSource.CreateLinkedTokenSource(hostToken, timeout.Token);
        using var monitorStop = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
        var monitor = MonitorAsync(task.TaskId, sessionLease, run, monitorStop.Token);
        AgentResult result;
        try
        {
            var selection = await store.SelectionAsync(task.TaskId, run.Token);
            result = await agents.ExecuteAsync(task, selection, run.Token);
            result.Validate();
            if (result.SessionId != task.SessionId) throw new InvalidOperationException("Mismatched session");
            if (run.IsCancellationRequested) result = new AgentResult { SessionId = task.SessionId, Status = TaskStatus.CANCELLED, Response = result.Response };
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested)
        { result = new AgentResult { SessionId = task.SessionId, Status = TaskStatus.CANCELLED }; }
        catch (Exception e) when (e is not OutOfMemoryException)
        { result = new AgentResult { SessionId = task.SessionId, Status = TaskStatus.FAILED, Error = "Agent execution failed. External effects may be partial." }; }
        finally { monitorStop.Cancel(); await monitor; }
        if (timeout.IsCancellationRequested && !hostToken.IsCancellationRequested)
            result = new AgentResult { SessionId = task.SessionId, Status = TaskStatus.FAILED, Error = "Task timed out. External effects may be partial; execution will not be retried." };
        using var save = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await store.FinishAsync(task, result, save.Token); }
        catch (Exception e) when (e is NpgsqlException or OperationCanceledException)
        { AgentTelemetry.TaskFinished(activity, "FAILED", Stopwatch.GetElapsedTime(started).TotalSeconds); Ready = false; lifetime.StopApplication(); throw; }
        AgentTelemetry.TaskFinished(activity, result.Status.ToString(), Stopwatch.GetElapsedTime(started).TotalSeconds);
        logger.LogInformation("Task {TaskId} finished", task.TaskId);
    }

    private async Task MonitorAsync(string id, NpgsqlConnection lease, CancellationTokenSource run, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(options.PollInterval, ct);
                await using var ping = new NpgsqlCommand("SELECT 1", lease) { CommandTimeout = 3 };
                await ping.ExecuteScalarAsync(ct);
                if (await store.CancellationRequestedAsync(id, ct)) { run.Cancel(); return; }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e) when (e is NpgsqlException or InvalidOperationException) { run.Cancel(); }
    }

    private static async Task ReleaseAsync(NpgsqlConnection lease)
    {
        try
        {
            await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock_all()", lease) { CommandTimeout = 2 };
            await unlock.ExecuteNonQueryAsync();
        }
        catch (Exception e) when (e is NpgsqlException or InvalidOperationException) { }
        finally { await lease.DisposeAsync(); }
    }
}
