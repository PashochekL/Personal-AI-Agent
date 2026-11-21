using System.Globalization;
using AiAgent.Shared;
using Npgsql;

namespace AiAgent.Memory;

/// <summary>Persistent scoped facts. Exact cosine search is intentional for small personal memories.</summary>
public sealed class PostgresLongTermMemory : ILongTermMemory
{
    private readonly NpgsqlDataSource _database;
    private readonly IEmbeddingClient _embeddings;
    public PostgresLongTermMemory(NpgsqlDataSource database, IEmbeddingClient embeddings)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(embeddings);
        ArgumentException.ThrowIfNullOrWhiteSpace(embeddings.Model);
        if (embeddings.Dimensions is < 1 or > 4096) throw new ArgumentException("Invalid embedding dimensions");
        _database = database;
        _embeddings = embeddings;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var command = _database.CreateCommand("""
                CREATE EXTENSION IF NOT EXISTS vector;
                CREATE TABLE IF NOT EXISTS damir_memories (
                    owner_id text NOT NULL CHECK (length(owner_id) BETWEEN 1 AND 256),
                    project_id text NOT NULL CHECK (length(project_id) BETWEEN 1 AND 256),
                    memory_key text NOT NULL CHECK (length(memory_key) BETWEEN 1 AND 128),
                    content text NOT NULL CHECK (length(content) BETWEEN 1 AND 2000),
                    embedding_model text NOT NULL,
                    embedding vector NOT NULL,
                    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
                    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
                    PRIMARY KEY (owner_id, project_id, memory_key)
                );
                """);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (NpgsqlException) { throw new MemoryError("Could not initialize long-term memory; PostgreSQL must provide pgvector"); }
    }

    public async Task<MemoryRecord> SaveAsync(MemoryScope scope, string key, string content, CancellationToken cancellationToken = default)
    {
        scope.Validate();
        MemoryRecord.ValidateText(key, content);
        var embedding = await EmbedAsync(content, cancellationToken);
        try
        {
            await using var command = _database.CreateCommand("""
                INSERT INTO damir_memories (owner_id, project_id, memory_key, content, embedding_model, embedding)
                VALUES ($1, $2, $3, $4, $5, $6::text::vector)
                ON CONFLICT (owner_id, project_id, memory_key) DO UPDATE SET
                    content = EXCLUDED.content, embedding_model = EXCLUDED.embedding_model,
                    embedding = EXCLUDED.embedding, updated_at = clock_timestamp()
                RETURNING memory_key, content, created_at, updated_at
                """);
            command.Parameters.AddWithValue(scope.OwnerId);
            command.Parameters.AddWithValue(scope.ProjectId);
            command.Parameters.AddWithValue(key);
            command.Parameters.AddWithValue(content);
            command.Parameters.AddWithValue(embedding.Model);
            command.Parameters.AddWithValue(VectorText(embedding));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new MemoryError("Memory was not saved");
            return ReadRecord(reader, scope);
        }
        catch (NpgsqlException) { throw new MemoryError("Could not save long-term memory; its outcome may be unknown"); }
    }

    public async Task<IReadOnlyList<MemoryMatch>> SearchAsync(MemoryScope scope, string query, int limit = 5,
        double minimumSimilarity = 0.3, CancellationToken cancellationToken = default)
    {
        scope.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (query.Length > 4000 || limit is < 1 or > 20 || !double.IsFinite(minimumSimilarity) || minimumSimilarity is < -1 or > 1)
            throw new ArgumentException("Invalid memory search options");
        var embedding = await EmbedAsync(query, cancellationToken);
        try
        {
            // CASE prevents evaluating cosine distance for an incompatible vector dimension.
            // Incompatible rows are reported rather than silently becoming forgotten memories.
            await using var command = _database.CreateCommand("""
                SELECT memory_key, content, created_at, updated_at,
                    CASE WHEN embedding_model = $3 AND vector_dims(embedding) = $4
                        THEN 1 - (embedding <=> $5::text::vector) ELSE NULL END AS similarity
                FROM damir_memories
                WHERE owner_id = $1 AND project_id = $2
                ORDER BY similarity DESC NULLS FIRST, memory_key
                LIMIT $6
                """);
            command.Parameters.AddWithValue(scope.OwnerId);
            command.Parameters.AddWithValue(scope.ProjectId);
            command.Parameters.AddWithValue(embedding.Model);
            command.Parameters.AddWithValue(embedding.Values.Count);
            command.Parameters.AddWithValue(VectorText(embedding));
            command.Parameters.AddWithValue(limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var result = new List<MemoryMatch>();
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.IsDBNull(4)) throw new MemoryError("Memory embedding model changed; list and re-save existing records to reindex them");
                var distanceScore = reader.GetDouble(4);
                if (!double.IsFinite(distanceScore)) throw new MemoryError("Memory search returned an invalid similarity");
                var similarity = Math.Clamp(distanceScore, -1, 1);
                if (similarity < minimumSimilarity) continue;
                var match = new MemoryMatch { Record = ReadRecord(reader, scope), Similarity = similarity };
                match.Validate();
                result.Add(match);
            }
            return result;
        }
        catch (NpgsqlException) { throw new MemoryError("Could not search long-term memory"); }
    }

    public async Task<IReadOnlyList<MemoryRecord>> ListAsync(MemoryScope scope, int limit = 20, string? afterKey = null, CancellationToken cancellationToken = default)
    {
        scope.Validate();
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        if (afterKey is not null) MemoryRecord.ValidateText(afterKey, "validation");
        try
        {
            await using var command = _database.CreateCommand("""
                SELECT memory_key, content, created_at, updated_at FROM damir_memories
                WHERE owner_id = $1 AND project_id = $2 AND ($3::text IS NULL OR memory_key > $3)
                ORDER BY memory_key LIMIT $4
                """);
            command.Parameters.AddWithValue(scope.OwnerId);
            command.Parameters.AddWithValue(scope.ProjectId);
            command.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Text, (object?)afterKey ?? DBNull.Value);
            command.Parameters.AddWithValue(limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var result = new List<MemoryRecord>();
            while (await reader.ReadAsync(cancellationToken)) result.Add(ReadRecord(reader, scope));
            return result;
        }
        catch (NpgsqlException) { throw new MemoryError("Could not list long-term memory"); }
    }

    public async Task<bool> DeleteAsync(MemoryScope scope, string key, CancellationToken cancellationToken = default)
    {
        scope.Validate();
        MemoryRecord.ValidateText(key, "validation");
        try
        {
            await using var command = _database.CreateCommand("DELETE FROM damir_memories WHERE owner_id = $1 AND project_id = $2 AND memory_key = $3");
            command.Parameters.AddWithValue(scope.OwnerId);
            command.Parameters.AddWithValue(scope.ProjectId);
            command.Parameters.AddWithValue(key);
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        }
        catch (NpgsqlException) { throw new MemoryError("Could not delete long-term memory; its outcome may be unknown"); }
    }

    private async Task<Embedding> EmbedAsync(string text, CancellationToken cancellationToken)
    {
        var result = await _embeddings.EmbedAsync(text, cancellationToken);
        try { result.Validate(); }
        catch (ArgumentException) { throw new MemoryError("Embedding client returned an invalid vector"); }
        if (result.Model != _embeddings.Model || result.Values.Count != _embeddings.Dimensions)
            throw new MemoryError("Embedding model or dimension mismatch");
        return result;
    }
    private static string VectorText(Embedding embedding) => "[" + string.Join(',', embedding.Values.Select(x => x.ToString("R", CultureInfo.InvariantCulture))) + "]";
    private static MemoryRecord ReadRecord(NpgsqlDataReader reader, MemoryScope scope)
    {
        var result = new MemoryRecord { Scope = scope, Key = reader.GetString(0), Content = reader.GetString(1),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(2), UpdatedAt = reader.GetFieldValue<DateTimeOffset>(3) };
        result.Validate();
        return result;
    }
}
