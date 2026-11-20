using System.Text;
using System.Text.Json;
using AiAgent.Shared;

namespace AiAgent.Tools.FileSystem;

/// <summary>Path checks for a trusted, isolated workspace; not an OS sandbox.</summary>
public sealed class Workspace
{
    public const int MaxBytes = 1_048_576;
    private readonly string _root;
    internal string Root => _root;
    public Workspace(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
        if (!Directory.Exists(_root)) throw new DirectoryNotFoundException("Workspace does not exist");
        RejectLink(_root);
    }

    internal string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)) throw new ArgumentException("Path must be relative to workspace");
        var parts = path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => p == "..")) throw new ArgumentException("Parent paths are not allowed");
        var full = Path.GetFullPath(path, _root);
        var relative = Path.GetRelativePath(_root, full);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new ArgumentException("Path is outside workspace");
        var current = _root;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            current = Path.Combine(current, part);
            RejectLink(current);
        }
        return full;
    }

    private static void RejectLink(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is not null) throw new UnauthorizedAccessException("Symbolic links are not allowed");
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Links are not allowed");
    }
}

public abstract class FileSystemTool(Workspace workspace) : ITool
{
    protected Workspace Workspace { get; } = workspace ?? throw new ArgumentNullException(nameof(workspace));
    public abstract string Name { get; }
    public abstract string Description { get; }
    protected abstract string Schema { get; }
    public IReadOnlyDictionary<string, JsonElement> Parameters => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Schema)!;
    public async Task<ToolResult> ExecuteAsync(IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { return new ToolResult { Success = true, Content = await ExecuteCoreAsync(arguments, cancellationToken) }; }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or DecoderFallbackException)
        { return new ToolResult { Success = false, Content = "", Error = "File operation failed: " + e.Message }; }
    }
    protected abstract Task<string> ExecuteCoreAsync(IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken cancellationToken);
    protected static string Required(IReadOnlyDictionary<string, JsonElement> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value.ValueKind != JsonValueKind.String) throw new ArgumentException($"{key} must be a string");
        return value.GetString()!;
    }
    protected static async Task<string> ReadAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (stream.Length > Workspace.MaxBytes) throw new IOException("File exceeds size limit");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
        var buffer = new char[4096];
        var text = new StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
        {
            text.Append(buffer, 0, count);
            if (text.Length > Workspace.MaxBytes) throw new IOException("File exceeds size limit");
        }
        return text.ToString();
    }
    protected static async Task WriteAsync(string path, string content, CancellationToken token)
    {
        if (Encoding.UTF8.GetByteCount(content) > Workspace.MaxBytes) throw new ArgumentException("Content exceeds size limit");
        var dir = Path.GetDirectoryName(path)!;
        var temp = Path.Combine(dir, ".agent-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllTextAsync(temp, content, new UTF8Encoding(false, true), token);
            token.ThrowIfCancellationRequested();
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

public sealed class ReadFileTool(Workspace workspace) : FileSystemTool(workspace)
{
    public override string Name => "filesystem.read_file";
    public override string Description => "Read a UTF-8 file relative to workspace.";
    protected override string Schema => """{"type":"object","properties":{"path":{"type":"string","minLength":1}},"required":["path"],"additionalProperties":false}""";
    protected override Task<string> ExecuteCoreAsync(IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken cancellationToken) => ReadAsync(Workspace.Resolve(Required(arguments, "path")), cancellationToken);
}
public sealed class WriteFileTool(Workspace workspace) : FileSystemTool(workspace)
{
    public override string Name => "filesystem.write_file";
    public override string Description => "Create or replace a UTF-8 file in workspace; parent directory must exist.";
    protected override string Schema => """{"type":"object","properties":{"path":{"type":"string","minLength":1},"content":{"type":"string"}},"required":["path","content"],"additionalProperties":false}""";
    protected override async Task<string> ExecuteCoreAsync(IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken cancellationToken)
    {
        await WriteAsync(Workspace.Resolve(Required(arguments, "path")), Required(arguments, "content"), cancellationToken);
        return "File written";
    }
}
public sealed class EditFileTool(Workspace workspace) : FileSystemTool(workspace)
{
    public override string Name => "filesystem.edit_file";
    public override string Description => "Replace exactly one occurrence of old_text in a UTF-8 file.";
    protected override string Schema => """{"type":"object","properties":{"path":{"type":"string","minLength":1},"old_text":{"type":"string","minLength":1},"new_text":{"type":"string"}},"required":["path","old_text","new_text"],"additionalProperties":false}""";
    protected override async Task<string> ExecuteCoreAsync(IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken cancellationToken)
    {
        var path = Workspace.Resolve(Required(arguments, "path"));
        var oldText = Required(arguments, "old_text");
        var replacement = Required(arguments, "new_text");
        if (oldText.Length == 0) throw new ArgumentException("old_text must not be empty");
        var text = await ReadAsync(path, cancellationToken);
        var index = text.IndexOf(oldText, StringComparison.Ordinal);
        if (index < 0 || text.IndexOf(oldText, index + 1, StringComparison.Ordinal) >= 0) throw new ArgumentException("old_text must occur exactly once");
        await WriteAsync(path, string.Concat(text.AsSpan(0, index), replacement, text.AsSpan(index + oldText.Length)), cancellationToken);
        return "File edited";
    }
}
public sealed class ListDirectoryTool(Workspace workspace) : FileSystemTool(workspace)
{
    public override string Name => "filesystem.list_directory";
    public override string Description => "List a workspace directory recursively up to a bounded depth. Links are listed but never followed.";
    protected override string Schema => """{"type":"object","properties":{"path":{"type":"string","minLength":1},"depth":{"type":"integer","minimum":1,"maximum":5}},"required":["path"],"additionalProperties":false}""";
    protected override Task<string> ExecuteCoreAsync(IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken cancellationToken)
    {
        var path = Workspace.Resolve(Required(arguments, "path"));
        var depth = arguments.TryGetValue("depth", out var d) && d.TryGetInt32(out var n) ? n : 1;
        if (depth is < 1 or > 5) throw new ArgumentException("depth must be between 1 and 5");
        var output = new List<string>();
        void Visit(string dir, string prefix, int level)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir).Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (output.Count >= 1000) throw new IOException("Directory listing exceeds 1000 entries");
                var name = Path.GetFileName(entry);
                var relative = Path.Combine(prefix, name);
                var attrs = File.GetAttributes(entry);
                var link = (attrs & FileAttributes.ReparsePoint) != 0 || new FileInfo(entry).LinkTarget is not null;
                var directory = (attrs & FileAttributes.Directory) != 0;
                output.Add(relative + (link ? " [link]" : directory ? "/" : ""));
                if (directory && !link && level < depth) Visit(entry, relative, level + 1);
            }
        }
        Visit(path, "", 1);
        return Task.FromResult(string.Join("\n", output));
    }
}
