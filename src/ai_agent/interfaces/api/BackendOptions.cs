using AiAgent.Shared;
using Npgsql;

namespace AiAgent.Api;

/// <summary>One authenticated local owner and workspace per backend deployment.</summary>
public sealed class BackendOptions
{
    public required string Database { get; init; }
    public required string Redis { get; init; }
    public required string ApiKey { get; init; }
    public required MemoryScope Scope { get; init; }
    public string Url { get; init; } = "http://127.0.0.1:8080";
    public int Workers { get; init; } = 2;
    public TimeSpan TaskTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(300);

    public void Validate()
    {
        Scope.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(Database);
        ArgumentException.ThrowIfNullOrWhiteSpace(Redis);
        if (ApiKey.Length is < 24 or > 512 || ApiKey.Any(char.IsWhiteSpace)) throw new ArgumentException("Invalid backend API key");
        if (Workers is < 1 or > 8 || TaskTimeout < TimeSpan.FromSeconds(1) || TaskTimeout > TimeSpan.FromHours(1)
            || PollInterval < TimeSpan.FromMilliseconds(20) || PollInterval > TimeSpan.FromSeconds(5)) throw new ArgumentException("Invalid worker limits");
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.AbsolutePath != "/" || uri.UserInfo.Length > 0)
            throw new ArgumentException("Invalid backend URL");
    }

    public static BackendOptions FromEnvironment()
    {
        string? Get(string key) => Environment.GetEnvironmentVariable(key);
        var database = Get("DATABASE_URL");
        if (string.IsNullOrWhiteSpace(database)) database = new NpgsqlConnectionStringBuilder
        {
            Host = "postgres", Database = "damir", Username = "damir",
            Password = Get("DAMIR_POSTGRES_PASSWORD") ?? throw new ArgumentException("Database password required")
        }.ConnectionString;
        return new BackendOptions
        {
            Database = database, Redis = Get("REDIS_URL") ?? "redis:6379", ApiKey = Get("DAMIR_BACKEND_API_KEY") ?? "",
            Scope = new MemoryScope { OwnerId = Get("DAMIR_MEMORY_OWNER") ?? "local", ProjectId = Get("DAMIR_MEMORY_PROJECT") ?? "" },
            Url = Get("DAMIR_BACKEND_URL") ?? "http://127.0.0.1:8080",
            Workers = int.Parse(Get("DAMIR_BACKEND_WORKERS") ?? "2"),
            TaskTimeout = TimeSpan.FromSeconds(int.Parse(Get("DAMIR_TASK_TIMEOUT_SECONDS") ?? "300"))
        };
    }
}

public sealed class TaskRequest
{
    public required string SessionId { get; init; }
    public required string UserMessage { get; init; }
    public string? Model { get; init; }
    public string? Skill { get; init; }
    public string Operation { get; init; } = "chat";

    [System.Text.Json.Serialization.JsonIgnore]
    public TaskSelection Selection => new(Model, Skill, Operation);
}

// HTTP and persistence details belong to Interfaces; shared exchange models stay unchanged.
public sealed record TaskSelection(string? Model = null, string? Skill = null, string Operation = "chat")
{
    public void Validate()
    {
        if (Operation is not ("chat" or "compress" or "context") ||
            Model is not null && (string.IsNullOrWhiteSpace(Model) || Model.Length > 256 || Model.Any(char.IsControl)) ||
            Skill is not null && (Skill.Length is < 1 or > 128 || Skill.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))) ||
            Operation != "chat" && Skill is not null)
            throw new TaskAdmissionException(400, "Invalid model, skill or operation.");
    }
}

public sealed class TaskAdmissionException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
