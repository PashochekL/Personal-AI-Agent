namespace AiAgent.Shared;

/// <summary>Trusted application scope; never taken from model-supplied tool arguments.</summary>
public sealed class MemoryScope : ExchangeModel
{
    public required string OwnerId { get; init; }
    public required string ProjectId { get; init; }
    public override void Validate() => Require(NonBlank(OwnerId) && OwnerId.Length <= 256 && NonBlank(ProjectId) && ProjectId.Length <= 256, "Invalid memory scope");
}

public sealed class MemoryRecord : ExchangeModel
{
    public required MemoryScope Scope { get; init; }
    public required string Key { get; init; }
    public required string Content { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public override void Validate()
    {
        Require(Scope is not null, "Memory scope is required");
        Scope!.Validate();
        ValidateText(Key, Content);
        Require(CreatedAt != default && UpdatedAt >= CreatedAt, "Invalid memory timestamps");
    }
    public static void ValidateText(string key, string content)
    {
        Require(NonBlank(key) && key.Length <= 128, "Memory key must contain 1-128 characters");
        Require(NonBlank(content) && content.Length <= 2000, "Memory content must contain 1-2000 characters");
    }
}

public sealed class MemoryMatch : ExchangeModel
{
    public required MemoryRecord Record { get; init; }
    public required double Similarity { get; init; }
    public override void Validate()
    {
        Require(Record is not null, "Memory record is required");
        Record!.Validate();
        Require(double.IsFinite(Similarity) && Similarity is >= -1 and <= 1, "Invalid memory similarity");
    }
}

public sealed class Embedding : ExchangeModel
{
    public required string Model { get; init; }
    public required List<float> Values { get; init; }
    public override void Validate()
    {
        Require(NonBlank(Model) && Model.Length <= 256, "Invalid embedding model");
        Require(Values is { Count: > 0 and <= 4096 } && Values.All(float.IsFinite) && Values.Any(x => x != 0), "Invalid embedding vector");
    }
}

public interface IEmbeddingClient
{
    string Model { get; }
    int Dimensions { get; }
    Task<Embedding> EmbedAsync(string text, CancellationToken cancellationToken = default);
}

public interface ILongTermMemory
{
    Task<MemoryRecord> SaveAsync(MemoryScope scope, string key, string content, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MemoryMatch>> SearchAsync(MemoryScope scope, string query, int limit = 5, double minimumSimilarity = 0.3, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MemoryRecord>> ListAsync(MemoryScope scope, int limit = 20, string? afterKey = null, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(MemoryScope scope, string key, CancellationToken cancellationToken = default);
}

public sealed class MemoryError : AgentError
{
    public MemoryError(string message) : base(message) { }
}
