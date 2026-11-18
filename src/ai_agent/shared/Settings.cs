using System.Collections;
using System.Globalization;
using System.Text.Json.Serialization;
namespace AiAgent.Shared;

public sealed class Settings
{
    [JsonIgnore] public string? DatabaseUrl { get; private init; }
    [JsonIgnore] public string? RedisUrl { get; private init; }
    public string? LitellmBaseUrl { get; private init; }
    [JsonIgnore] public string? LitellmApiKey { get; private init; }
    public string? DefaultModel { get; private init; }
    [JsonIgnore] public string? TelegramBotToken { get; private init; }
    public int AgentMaxIterations { get; private init; } = 20;
    public string AgentWorkspace { get; private init; } = "./workspace";

    public static Settings FromEnvironment(IReadOnlyDictionary<string, string>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .ToDictionary(x => (string)x.Key, x => (string)x.Value!);
        string? Get(string name) => environment.TryGetValue(name, out var value) && value != "" ? value : null;
        var iterations = 20;
        if (environment.TryGetValue("AGENT_MAX_ITERATIONS", out var raw) &&
            (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out iterations) || iterations < 1))
            throw new ArgumentException("Invalid AGENT_MAX_ITERATIONS");
        return new Settings {
            DatabaseUrl = Get("DATABASE_URL"), RedisUrl = Get("REDIS_URL"),
            LitellmBaseUrl = Get("LITELLM_BASE_URL"), LitellmApiKey = Get("LITELLM_API_KEY"),
            DefaultModel = Get("DEFAULT_MODEL"), TelegramBotToken = Get("TELEGRAM_BOT_TOKEN"),
            AgentMaxIterations = iterations,
            AgentWorkspace = environment.TryGetValue("AGENT_WORKSPACE", out var workspace) ? workspace : "./workspace"
        };
    }
}
