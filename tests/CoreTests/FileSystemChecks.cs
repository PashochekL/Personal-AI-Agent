using System.Text.Json;
using AiAgent.Core;
using AiAgent.Shared;
using AiAgent.Tools.FileSystem;

static class FileSystemChecks
{
    public static async Task<int> RunAsync()
    {
        int checks = 0;
        void Check(bool condition, string name) { checks++; if (!condition) throw new Exception("Filesystem: " + name); }
        var parent = Path.Combine(Environment.CurrentDirectory, ".uv-cache", "fs-test-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, "workspace");
        var outside = Path.Combine(parent, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "secret");
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            var tools = new ToolRegistry();
            var workspace = new Workspace(root);
            tools.Register(new ReadFileTool(workspace));
            tools.Register(new WriteFileTool(workspace));
            tools.Register(new EditFileTool(workspace));
            tools.Register(new ListDirectoryTool(workspace));
            async Task<ToolResult> Run(string name, object args) => await tools.ExecuteAsync(new ToolCall
            {
                Id = Guid.NewGuid().ToString(), Name = name,
                Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(args))!
            });
            Check((await Run("filesystem.write_file", new { path = "sub/file.txt", content = "hello world" })).Success, "write");
            Check((await Run("filesystem.read_file", new { path = "sub/file.txt" })).Content == "hello world", "read");
            Check((await Run("filesystem.edit_file", new { path = "sub/file.txt", old_text = "world", new_text = "Damir" })).Success, "edit");
            Check((await Run("filesystem.read_file", new { path = "sub/file.txt" })).Content == "hello Damir", "edited content");
            Check(!(await Run("filesystem.edit_file", new { path = "sub/file.txt", old_text = "absent", new_text = "x" })).Success, "edit absent");
            await Run("filesystem.write_file", new { path = "sub/repeated.txt", content = "x x" });
            Check(!(await Run("filesystem.edit_file", new { path = "sub/repeated.txt", old_text = "x", new_text = "y" })).Success, "ambiguous edit");
            Check((await Run("filesystem.read_file", new { path = "sub/repeated.txt" })).Content == "x x", "ambiguous edit unchanged");
            await Run("filesystem.write_file", new { path = "sub/overlap.txt", content = "aaa" });
            Check(!(await Run("filesystem.edit_file", new { path = "sub/overlap.txt", old_text = "aa", new_text = "x" })).Success, "overlapping matches");
            Check(!(await Run("filesystem.write_file", new { path = "large.txt", content = new string('a', Workspace.MaxBytes + 1) })).Success, "size limit");
            var listing = await Run("filesystem.list_directory", new { path = ".", depth = 2 });
            Check(listing.Success && listing.Content.Contains("sub" + Path.DirectorySeparatorChar + "file.txt"), "recursive listing");
            Check(!(await Run("filesystem.read_file", new { path = "../outside/secret.txt" })).Success, "parent traversal");
            Check(!(await Run("filesystem.read_file", new { path = Path.Combine(outside, "secret.txt") })).Success, "absolute path");
            Check(!(await Run("filesystem.read_file", new { path = "missing.txt" })).Success, "missing file");
            Directory.CreateSymbolicLink(Path.Combine(root, "link"), outside);
            Check(!(await Run("filesystem.read_file", new { path = "link/secret.txt" })).Success, "directory symlink");
            Check(!(await Run("filesystem.write_file", new { path = "link/new.txt", content = "bad" })).Success && !File.Exists(Path.Combine(outside, "new.txt")), "write through symlink");
            listing = await Run("filesystem.list_directory", new { path = ".", depth = 2 });
            Check(listing.Success && listing.Content.Contains("link [link]") && !listing.Content.Contains("secret.txt"), "listing does not follow links");
            File.CreateSymbolicLink(Path.Combine(root, "broken"), Path.Combine(outside, "absent"));
            Check(!(await Run("filesystem.write_file", new { path = "broken", content = "bad" })).Success, "dangling symlink");
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            try { await tools.ExecuteAsync(new ToolCall { Id = "cancel", Name = "filesystem.read_file", Arguments = new() }, cts.Token); throw new Exception("Cancellation ignored"); }
            catch (OperationCanceledException) { checks++; }
            return checks;
        }
        finally { Directory.Delete(parent, true); }
    }
}
