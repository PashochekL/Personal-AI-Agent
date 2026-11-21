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

public sealed partial class CoreAgentFactory : IBackendAgentFactory, IAsyncDisposable
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
    private readonly int? _budget;
    private readonly string _defaultModel;
    private readonly Uri _gateway;
    private readonly string _key;
    private readonly ConsoleUsageStore _usage;
    private McpTools? _github;

    public CoreAgentFactory(PostgresContextStore history, BackendOptions options, NpgsqlDataSource database)
    {
        _history = history; _options = options;
        _workspace = new Workspace(Environment.GetEnvironmentVariable("AGENT_WORKSPACE") ?? "/workspace");
        var key = Environment.GetEnvironmentVariable("LITELLM_API_KEY") ?? throw new ArgumentException("LITELLM_API_KEY required");
        var gateway = new Uri(Environment.GetEnvironmentVariable("LITELLM_BASE_URL") ?? "http://gateway:4000");
        _gateway = gateway; _key = key;
        _defaultModel = Environment.GetEnvironmentVariable("DEFAULT_MODEL") ?? "gpt-6.1-sol";
        _llm = new LiteLlmClient(_http, gateway, key, _defaultModel);
        _usage = new ConsoleUsageStore(database);
        _budget = Environment.GetEnvironmentVariable("DAMIR_CONTEXT_TOKENS") is { Length: > 0 } budget ? int.Parse(budget) : null;
        if (_budget is < 4096) throw new ArgumentException("Context budget must be at least 4096");
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
        await _usage.InitializeAsync(cancellationToken);
        if (Environment.GetEnvironmentVariable("DAMIR_GITHUB_MCP") == "1") _github = await ConnectGithubAsync(cancellationToken);
    }

    public async Task<IAgent> CreateAsync(CancellationToken cancellationToken) =>
        (await BuildAgentAsync(null, new TaskSelection(), cancellationToken)).Agent;

    public async ValueTask DisposeAsync()
    {
        if (_github is not null) await _github.DisposeAsync();
        _http.Dispose();
        if (_memoryDatabase is not null) await _memoryDatabase.DisposeAsync();
    }

    private sealed class SummaryCache(PostgresContextStore store) : IContextSummaryCache
    {
        public Task<string?> GetAsync(string sessionId, string key, CancellationToken cancellationToken = default) => store.GetSummaryAsync(sessionId, key, cancellationToken);
        public Task PutAsync(string sessionId, string key, string summary, CancellationToken cancellationToken = default) => store.PutSummaryAsync(sessionId, key, summary, cancellationToken);
        public Task<bool> IsManualAsync(string sessionId, CancellationToken cancellationToken = default) => store.IsManualSummaryAsync(sessionId, cancellationToken);
        public Task SetManualAsync(string sessionId, CancellationToken cancellationToken = default) => store.SetManualSummaryAsync(sessionId, cancellationToken);
    }
}
