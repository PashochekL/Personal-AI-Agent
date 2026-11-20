using System.Text.Json;
using System.Text.RegularExpressions;
using AiAgent.Shared;
using AiAgent.Tools.FileSystem;
using YamlDotNet.RepresentationModel;

namespace AiAgent.Core;

public sealed record InstructionFile(string Path, string Content);
public sealed record SkillMetadata(string Name, string Description, string Path);

/// <summary>Loads project instructions within the configured workspace, from root to target.</summary>
public sealed class InstructionLoader(Workspace workspace)
{
    public async Task<IReadOnlyList<InstructionFile>> LoadForPathAsync(string path, CancellationToken cancellationToken = default)
    {
        var full = workspace.Resolve(path);
        var relative = Path.GetRelativePath(workspace.Root, full);
        var isDirectory = Directory.Exists(full);
        var parent = isDirectory ? relative : Path.GetDirectoryName(relative) ?? ".";
        var segments = parent.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Where(p => p != ".");
        var dirs = new List<string> { "." };
        foreach (var segment in segments) dirs.Add(Path.Combine(dirs[^1], segment));
        var result = new List<InstructionFile>();
        foreach (var dir in dirs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = Path.Combine(dir, "AGENTS.md");
            var absolute = workspace.Resolve(file);
            if (!File.Exists(absolute)) continue;
            var content = await WorkspaceText.ReadAsync(workspace, file, cancellationToken);
            result.Add(new InstructionFile(file, content));
        }
        return result;
    }
}

/// <summary>Discovers one-level skills under skills/ and loads an individual skill on demand.</summary>
public sealed class SkillCatalog(Workspace workspace)
{
    public async Task<IReadOnlyList<SkillMetadata>> ListAsync(CancellationToken cancellationToken = default)
    {
        var root = workspace.Resolve("skills");
        if (!Directory.Exists(root)) return [];
        var skills = new List<SkillMetadata>();
        foreach (var directory in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (skills.Count >= 100) throw new InvalidOperationException("Too many skills");
            var name = Path.GetFileName(directory);
            var path = Path.Combine("skills", name, "SKILL.md");
            var absolute = workspace.Resolve(path);
            if (!File.Exists(absolute)) continue;
            var text = await WorkspaceText.ReadAsync(workspace, path, cancellationToken);
            skills.Add(Parse(name, path, text));
        }
        return skills;
    }

    public async Task<string> LoadAsync(string name, CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        var path = Path.Combine("skills", name, "SKILL.md");
        var text = await WorkspaceText.ReadAsync(workspace, path, cancellationToken);
        _ = Parse(name, path, text);
        return text;
    }

    private static SkillMetadata Parse(string directory, string path, string text)
    {
        ValidateName(directory);
        using var reader = new StringReader(text);
        if (reader.ReadLine()?.TrimStart('\uFEFF') != "---") throw new ArgumentException("SKILL.md requires YAML frontmatter");
        var yaml = new System.Text.StringBuilder();
        string? line;
        while ((line = reader.ReadLine()) is not null && line != "---") yaml.AppendLine(line);
        if (line is null) throw new ArgumentException("Unterminated SKILL.md frontmatter");
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml.ToString()));
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode map) throw new ArgumentException("Invalid SKILL.md frontmatter");
        string? Field(string key) => map.Children.TryGetValue(new YamlScalarNode(key), out var node) && node is YamlScalarNode scalar ? scalar.Value : null;
        var name = Field("name") ?? throw new ArgumentException("Skill name is required");
        var description = Field("description");
        ValidateName(name);
        if (name != directory) throw new ArgumentException("Skill name must match directory");
        if (string.IsNullOrWhiteSpace(description) || description.Length > 1024) throw new ArgumentException("Invalid skill description");
        if (map.Children.TryGetValue(new YamlScalarNode("compatibility"), out var compatibilityNode))
        {
            if (compatibilityNode is not YamlScalarNode compatibility || string.IsNullOrWhiteSpace(compatibility.Value) || compatibility.Value.Length > 500)
                throw new ArgumentException("Invalid skill compatibility");
        }
        foreach (var key in new[] { "license", "allowed-tools" })
            if (map.Children.TryGetValue(new YamlScalarNode(key), out var value) && (value is not YamlScalarNode scalar || scalar.Value is null))
                throw new ArgumentException($"Invalid skill {key}");
        if (map.Children.TryGetValue(new YamlScalarNode("metadata"), out var metadata) &&
            (metadata is not YamlMappingNode mapping || mapping.Children.Any(pair => pair.Key is not YamlScalarNode { Value: not null } || pair.Value is not YamlScalarNode { Value: not null })))
            throw new ArgumentException("Invalid skill metadata");
        return new SkillMetadata(name, description, path);
    }
    private static void ValidateName(string name)
    {
        if (name.Length is < 1 or > 64 || !Regex.IsMatch(name, "^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Invalid skill name");
    }
}

internal static class WorkspaceText
{
    public static async Task<string> ReadAsync(Workspace workspace, string path, CancellationToken cancellationToken)
    {
        var result = await new ReadFileTool(workspace).ExecuteAsync(new Dictionary<string, JsonElement> { ["path"] = JsonSerializer.SerializeToElement(path) }, cancellationToken);
        if (!result.Success) throw new IOException(result.Error);
        return result.Content;
    }
}

public sealed class SkillTool(SkillCatalog catalog, bool list) : ITool
{
    public string Name => list ? "skills.list" : "skills.load";
    public string Description => list ? "List skill names and descriptions; skill instructions are loaded on demand." : "Load SKILL.md by skill name. Related resources are relative to the returned skill path.";
    public IReadOnlyDictionary<string, JsonElement> Parameters => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(list
        ? """{"type":"object","properties":{},"additionalProperties":false}"""
        : """{"type":"object","properties":{"name":{"type":"string","minLength":1}},"required":["name"],"additionalProperties":false}""")!;
    public async Task<ToolResult> ExecuteAsync(IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            string content;
            if (list) content = JsonSerializer.Serialize(await catalog.ListAsync(cancellationToken));
            else
            {
                if (!arguments.TryGetValue("name", out var value) || value.ValueKind != JsonValueKind.String) throw new ArgumentException("Skill name is required");
                var name = value.GetString()!;
                content = $"Skill path: skills/{name}/SKILL.md\n" + await catalog.LoadAsync(name, cancellationToken);
            }
            return new ToolResult { Success = true, Content = content };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or YamlDotNet.Core.YamlException)
        { return new ToolResult { Success = false, Content = "", Error = "Skill operation failed: " + e.Message }; }
    }
}
