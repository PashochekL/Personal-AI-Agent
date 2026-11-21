using AiAgent.Shared;

namespace AiAgent.Memory;

public sealed class MemoryOptions
{
    public bool Enabled { get; init; } = true;
    public string EmbeddingDeployment { get; init; } = "damir-memory-embedding";
    public string EmbeddingModel { get; init; } = "ollama/embeddinggemma:300m";
    public int Dimensions { get; init; } = 768;
    public MemoryScope? Scope { get; init; }

    public static MemoryOptions FromEnvironment(Func<string, string?>? get = null)
    {
        get ??= Environment.GetEnvironmentVariable;
        var enabled = get("DAMIR_MEMORY_ENABLED") ?? "1";
        if (enabled is not ("0" or "1")) throw new ArgumentException("DAMIR_MEMORY_ENABLED must be 0 or 1");
        if (enabled == "0") return new MemoryOptions { Enabled = false };
        var scope = new MemoryScope { OwnerId = get("DAMIR_MEMORY_OWNER") ?? "local", ProjectId = get("DAMIR_MEMORY_PROJECT") ?? "" };
        scope.Validate();
        var dimensionsText = get("DAMIR_EMBEDDING_DIMENSIONS") ?? "768";
        if (!int.TryParse(dimensionsText, out var dimensions) || dimensions is < 1 or > 4096) throw new ArgumentException("Invalid embedding dimensions");
        var deployment = get("DAMIR_EMBEDDING_DEPLOYMENT") ?? "damir-memory-embedding";
        var model = get("DAMIR_EMBEDDING_MODEL") ?? "ollama/embeddinggemma:300m";
        ArgumentException.ThrowIfNullOrWhiteSpace(deployment);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (deployment.Length > 256 || model.Length > 256) throw new ArgumentException("Embedding model identifier is too long");
        return new MemoryOptions { Scope = scope, Dimensions = dimensions, EmbeddingDeployment = deployment, EmbeddingModel = model };
    }
}
