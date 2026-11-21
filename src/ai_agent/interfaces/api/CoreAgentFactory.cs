using AiAgent.Core;
using AiAgent.Gateway;
using AiAgent.Memory;
using AiAgent.Sandbox;
using AiAgent.Shared;
using AiAgent.Tools.FileSystem;
using AiAgent.Tools.Shell;
using AiAgent.Tools.Web;
using Npgsql;

namespace AiAgent.Api;

public sealed class CoreAgentFactory : IBackendAgentFactory, IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(120) };
    private readonly PostgresContextStore _history;
    private readonly BackendOptions _options;
    private readonly Workspace _workspace;
    private readonly LiteLlmClient _llm;
    private readonly MemoryOptions _memoryOptions;
    private readonly NpgsqlDataSource? _memoryDatabase;
    private readonly PostgresLongTermMemory? _memory;
    private readonly string _personality;
    private readonly TimeZoneInfo _timeZone;
    private readonly int _budget;

    public CoreAgentFactory(PostgresContextStore history, BackendOptions options)
    {
        _history = history; _options = options;
        _workspace = new Workspace(Environment.GetEnvironmentVariable("AGENT_WORKSPACE") ?? "/workspace");
        var key = Environment.GetEnvironmentVariable("LITELLM_API_KEY") ?? throw new ArgumentException("LITELLM_API_KEY required");
        var gateway = new Uri(Environment.GetEnvironmentVariable("LITELLM_BASE_URL") ?? "http://gateway:4000");
        _llm = new LiteLlmClient(_http, gateway, key, Environment.GetEnvironmentVariable("DEFAULT_MODEL") ?? "gpt-6.1-sol");
        _budget = int.Parse(Environment.GetEnvironmentVariable("DAMIR_CONTEXT_TOKENS") is { Length: > 0 } budget ? budget : "120000");
        if (_budget < 4096) throw new ArgumentException("Context budget must be at least 4096");
        _personality = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "damir-persona.txt"));
        _timeZone = TimeZoneInfo.FindSystemTimeZoneById(Environment.GetEnvironmentVariable("DAMIR_TIMEZONE") ?? "UTC");
        _memoryOptions = MemoryOptions.FromEnvironment();
        if (_memoryOptions.Enabled)
        {
            var connection = Environment.GetEnvironmentVariable("MEMORY_DATABASE_URL") ?? new NpgsqlConnectionStringBuilder
            {
                Host = "memory-db", Database = "damir_memory", Username = "damir",
                Password = Environment.GetEnvironmentVariable("DAMIR_POSTGRES_PASSWORD")
            }.ConnectionString;
            _memoryDatabase = NpgsqlDataSource.Create(connection);
            _memory = new PostgresLongTermMemory(_memoryDatabase, new LiteLlmEmbeddingClient(_http, gateway, key,
                _memoryOptions.EmbeddingDeployment, _memoryOptions.EmbeddingModel, _memoryOptions.Dimensions));
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_memory is not null) await _memory.InitializeAsync(cancellationToken);
    }

    public Task<IAgent> CreateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tools = new ToolRegistry();
        tools.RegisterRange([new ReadFileTool(_workspace), new WriteFileTool(_workspace), new EditFileTool(_workspace), new ListDirectoryTool(_workspace), new ShellTool(new UnixSandboxExecutor())]);
        tools.Register(new WebSearchTool(_http, new Uri("http://web-search:8080")));
        return Task.FromResult<IAgent>(new Agent(_llm, _history, tools, workspace: _workspace, systemPrompt: _personality,
            systemContext: () => "Current time: " + TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _timeZone).ToString("O") + " (" + _timeZone.Id + ")",
            contextWindow: new ContextWindow(_budget, _llm.CountTokensAsync, new SummaryCache(_history)),
            memory: _memory, memoryScope: _memory is null ? null : _options.Scope));
    }

    public void Dispose() { _http.Dispose(); _memoryDatabase?.Dispose(); }

    private sealed class SummaryCache(PostgresContextStore store) : IContextSummaryCache
    {
        public Task<string?> GetAsync(string sessionId, string key, CancellationToken cancellationToken = default) => store.GetSummaryAsync(sessionId, key, cancellationToken);
        public Task PutAsync(string sessionId, string key, string summary, CancellationToken cancellationToken = default) => store.PutSummaryAsync(sessionId, key, summary, cancellationToken);
        public Task<bool> IsManualAsync(string sessionId, CancellationToken cancellationToken = default) => store.IsManualSummaryAsync(sessionId, cancellationToken);
        public Task SetManualAsync(string sessionId, CancellationToken cancellationToken = default) => store.SetManualSummaryAsync(sessionId, cancellationToken);
    }
}
