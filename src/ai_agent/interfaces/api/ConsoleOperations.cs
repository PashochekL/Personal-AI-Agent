using System.Net.Sockets;
using System.Text;
using AiAgent.Core;
using AiAgent.Gateway;
using AiAgent.Sandbox;
using AiAgent.Shared;
using AiAgent.Tools.FileSystem;
using AiAgent.Tools.Shell;
using AiAgent.Tools.Web;
using Npgsql;
using TaskStatus = AiAgent.Shared.TaskStatus;

namespace AiAgent.Api;

public sealed partial class CoreAgentFactory
{
    public async Task<object> CatalogAsync(CancellationToken ct) => new
    {
        default_model = _defaultModel,
        models = (await _llm.ListModelsAsync(ct)).Where(m => m != _memoryOptions.EmbeddingDeployment).ToArray(),
        skills = (await new SkillCatalog(_workspace).ListAsync(ct)).Select(s => new { name = s.Name, description = s.Description }).ToArray(),
        github = _github is not null
    };

    public async Task ValidateAsync(TaskSelection selection, CancellationToken ct)
    {
        selection.Validate();
        if (selection.Model is not null && !(await _llm.ListModelsAsync(ct)).Contains(selection.Model, StringComparer.Ordinal))
            throw new TaskAdmissionException(400, "Model is not configured in the gateway.");
        if (selection.Model == _memoryOptions.EmbeddingDeployment) throw new TaskAdmissionException(400, "Embedding model cannot be used for chat.");
        if (selection.Skill is not null)
        {
            try { _ = await new SkillCatalog(_workspace).LoadAsync(selection.Skill, ct); }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException or YamlDotNet.Core.YamlException)
            { throw new TaskAdmissionException(400, "Project skill could not be loaded."); }
        }
    }

    public Task<UsageSnapshot> UsageAsync(string session, CancellationToken ct) => _usage.GetAsync(session, ct);

    // Worker holds the session lease for every operation, including context inspection and compression.
    public async Task<AgentResult> ExecuteAsync(AgentTask task, TaskSelection selection, CancellationToken ct)
    {
        try { return await ExecuteCoreAsync(task, selection, ct); }
        catch (ContextLimitException e) { return new AgentResult { SessionId = task.SessionId, Status = TaskStatus.FAILED, Error = e.Message }; }
    }

    private async Task<AgentResult> ExecuteCoreAsync(AgentTask task, TaskSelection selection, CancellationToken ct)
    {
        selection.Validate();
        var (agent, display) = await BuildAgentAsync(task.SessionId, selection, ct);
        if (selection.Operation == "chat")
            return selection.Skill is null ? await agent.RunAsync(task.SessionId, task.UserMessage, ct)
                : await agent.RunWithSkillAsync(task.SessionId, task.UserMessage, selection.Skill, ct);
        string response;
        if (selection.Operation == "compress") response = await agent.CompactAsync(task.SessionId, ct) ? "Готово." : "История пока пуста.";
        else
        {
            var estimate = await agent.EstimateContextAsync(task.SessionId, ct);
            response = estimate.SummaryUnavailable ? "Не удалось оценить сжатый контекст. Выполни /compress."
                : FormattableString.Invariant($"~{estimate.Tokens} / {display} ({100.0 * estimate.Tokens / display:F1}%)");
        }
        return new AgentResult { SessionId = task.SessionId, Status = TaskStatus.COMPLETED, Response = response };
    }

    private async Task<(Agent Agent, int Display)> BuildAgentAsync(string? sessionId, TaskSelection selection, CancellationToken ct)
    {
        var client = new LiteLlmClient(_http, _gateway, _key, selection.Model ?? _defaultModel);
        var tools = new ToolRegistry();
        tools.RegisterRange([new ReadFileTool(_workspace), new WriteFileTool(_workspace), new EditFileTool(_workspace), new ListDirectoryTool(_workspace), new ShellTool(new UnixSandboxExecutor())]);
        tools.Register(new WebSearchTool(_http, new Uri("http://web-search:8080")));
        _github?.Register(tools);
        var totals = new UsageTotals();
        var tracked = new UsageTrackingClient(client, totals, sessionId is null ? null : (usage, token) => _usage.AddAsync(sessionId, usage, token));
        var limit = await client.GetInputLimitAsync(ct);
        var (display, budget) = ModelContextPolicy.For(selection.Model ?? _defaultModel, limit, _budget);
        var agent = new Agent(tracked, _history, tools, workspace: _workspace, systemPrompt: _personality,
            systemContext: () => "Current time: " + TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _timeZone).ToString("O") + " (" + _timeZone.Id + ")",
            contextWindow: new ContextWindow(budget, client.CountTokensAsync, new SummaryCache(_history)),
            memory: _memory, memoryScope: _memory is null ? null : _options.Scope);
        return (agent, display);
    }

    private static async Task<McpTools> ConnectGithubAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        Socket? socket = null;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try { await socket.ConnectAsync(new UnixDomainSocketEndPoint("/github-ipc/mcp.sock"), timeout.Token); break; }
                catch (SocketException) when (attempt < 40) { socket.Dispose(); await Task.Delay(250, timeout.Token); }
            }
            var stream = new NetworkStream(socket, ownsSocket: true);
            await stream.WriteAsync(Encoding.UTF8.GetBytes("{\"command\":\"github\",\"arguments\":[]}\n"), timeout.Token);
            var bytes = new List<byte>(); var one = new byte[1];
            while (await stream.ReadAsync(one, timeout.Token) > 0 && one[0] != '\n' && bytes.Count < 256) bytes.Add(one[0]);
            if (Encoding.UTF8.GetString(bytes.ToArray()) != "{\"ok\":true}") throw new IOException("GitHub MCP unavailable");
            return await McpTools.ConnectAsync(stream, stream, "github", timeout.Token);
        }
        catch { socket?.Dispose(); throw; }
    }
}

internal sealed class ConsoleUsageStore(NpgsqlDataSource database)
{
    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var command = database.CreateCommand("""
            CREATE TABLE IF NOT EXISTS damir_demo_usage(session_id text PRIMARY KEY,
                input_tokens bigint NOT NULL DEFAULT 0, output_tokens bigint NOT NULL DEFAULT 0,
                total_tokens bigint NOT NULL DEFAULT 0, missing_responses integer NOT NULL DEFAULT 0)
            """);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<UsageSnapshot> GetAsync(string session, CancellationToken ct)
    {
        await using var command = database.CreateCommand("SELECT input_tokens,output_tokens,total_tokens,missing_responses FROM damir_demo_usage WHERE session_id=$1");
        command.Parameters.AddWithValue(session);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt32(3), null) : new(0, 0, 0, 0, null);
    }

    public async Task AddAsync(string session, TokenUsage? usage, CancellationToken ct)
    {
        var total = usage?.TotalTokens is int reported ? (long)reported : usage?.InputTokens is int input && usage.OutputTokens is int output ? (long)input + output : 0;
        var missing = usage?.TotalTokens is null && (usage?.InputTokens is null || usage.OutputTokens is null) ? 1 : 0;
        await using var command = database.CreateCommand("""
            INSERT INTO damir_demo_usage(session_id,input_tokens,output_tokens,total_tokens,missing_responses) VALUES($1,$2,$3,$4,$5)
            ON CONFLICT(session_id) DO UPDATE SET input_tokens=damir_demo_usage.input_tokens+EXCLUDED.input_tokens,
                output_tokens=damir_demo_usage.output_tokens+EXCLUDED.output_tokens,
                total_tokens=damir_demo_usage.total_tokens+EXCLUDED.total_tokens,
                missing_responses=damir_demo_usage.missing_responses+EXCLUDED.missing_responses
            """);
        command.Parameters.AddWithValue(session); command.Parameters.AddWithValue((long)(usage?.InputTokens ?? 0));
        command.Parameters.AddWithValue((long)(usage?.OutputTokens ?? 0)); command.Parameters.AddWithValue(total); command.Parameters.AddWithValue(missing);
        await command.ExecuteNonQueryAsync(ct);
    }
}
