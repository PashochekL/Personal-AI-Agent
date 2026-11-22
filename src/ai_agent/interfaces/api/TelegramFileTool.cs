using AiAgent.Shared;
using Npgsql;
using System.Text.Json;

namespace AiAgent.Api;

/// <summary>Snapshots a requested workspace file into a durable Telegram outbox.</summary>
public sealed class TelegramFileTool(string root, string session, PostgresTaskStore store) : ITool
{
    public string Name => "filesystem.send_to_telegram";
    public string Description => "Send a file from the current workspace to the user's linked Telegram chat. Use when the user asks to receive a file here. Relative path only, maximum 20 MiB. Queues delivery; do not claim delivery is confirmed.";
    public IReadOnlyDictionary<string, JsonElement> Parameters => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
        """{"type":"object","properties":{"path":{"type":"string","minLength":1}},"required":["path"],"additionalProperties":false}""")!;

    public async Task<ToolResult> ExecuteAsync(IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            var path = arguments["path"].GetString() ?? "";
            var full = ResolvePath(root, path);
            await using var file = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
            if (file.Length > 20 * 1024 * 1024) throw new IOException("File exceeds 20 MiB.");
            var content = new byte[(int)file.Length];
            await file.ReadExactlyAsync(content, cancellationToken);
            await store.QueueTelegramFileAsync(session, Path.GetFileName(full), content, cancellationToken);
            return new ToolResult { Success = true, Content = "File queued for delivery to the linked Telegram chat." };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or KeyNotFoundException or TaskAdmissionException)
        { return new ToolResult { Success = false, Content = "", Error = "Не удалось подготовить файл. Нужен существующий файл до 20 МиБ внутри рабочей папки и активный диалог в Telegram." }; }
    }

    public static string ResolvePath(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains(':') || path.Contains('\\')) throw new ArgumentException("Relative path required.");
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => p is ".." or ".") || parts.Length == 0) throw new ArgumentException("Invalid path.");
        var current = Path.GetFullPath(root);
        foreach (var part in new[] { "" }.Concat(parts))
        {
            current = Path.Combine(current, part);
            if (new FileInfo(current).LinkTarget is not null || (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Links are not allowed.");
        }
        return current;
    }
}

public sealed record TelegramFile(string FileId, string Filename, byte[] Content);

public sealed partial class PostgresTaskStore
{
    public async Task QueueTelegramFileAsync(string session, string filename, byte[] content, CancellationToken ct)
    {
        if (content.Length > 20 * 1024 * 1024) throw new TaskAdmissionException(400, "File too large.");
        await using var command = Scoped("""
            INSERT INTO damir_telegram_files(file_id,session_id,filename,content)
            SELECT $4,s.session_id,$5,$6 FROM damir_api_sessions s
            WHERE s.owner_id=$1 AND s.project_id=$2 AND s.session_id=$3 AND s.input_channel='telegram'
                AND EXISTS(SELECT 1 FROM damir_telegram_companion c WHERE c.session_id=s.session_id AND c.user_id IS NOT NULL)
                AND (SELECT count(*) FROM damir_telegram_files f WHERE f.session_id=s.session_id)<3
            """);
        command.Parameters.AddWithValue(session); command.Parameters.AddWithValue(Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue(filename); command.Parameters.AddWithValue(content);
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw new TaskAdmissionException(423, "Telegram unavailable or file queue full.");
    }

    public async Task<TelegramFile[]> TelegramFilesAsync(string session, CancellationToken ct)
    {
        await using var command = Scoped("""
            SELECT f.file_id,f.filename,f.content FROM damir_telegram_files f JOIN damir_api_sessions s USING(session_id)
            WHERE s.owner_id=$1 AND s.project_id=$2 AND s.session_id=$3 ORDER BY f.created_at,f.file_id LIMIT 1
            """);
        command.Parameters.AddWithValue(session);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var files = new List<TelegramFile>();
        while (await reader.ReadAsync(ct)) files.Add(new(reader.GetString(0), reader.GetString(1), reader.GetFieldValue<byte[]>(2)));
        return files.ToArray();
    }

    public async Task AcknowledgeTelegramFileAsync(string session, string file, CancellationToken ct)
    {
        await using var command = Scoped("""
            DELETE FROM damir_telegram_files f USING damir_api_sessions s
            WHERE f.session_id=s.session_id AND s.owner_id=$1 AND s.project_id=$2 AND s.session_id=$3 AND f.file_id=$4
            """);
        command.Parameters.AddWithValue(session); command.Parameters.AddWithValue(file); await command.ExecuteNonQueryAsync(ct);
    }
}
