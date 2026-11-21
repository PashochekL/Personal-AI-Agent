using System.Text.Json;
using AiAgent.Core;
using AiAgent.Shared;
using AiAgent.Tools.FileSystem;
using Status = AiAgent.Shared.TaskStatus;

static class InstructionChecks
{
    public static async Task<int> RunAsync()
    {
        var checks = 0;
        void Check(bool condition, string name) { checks++; if (!condition) throw new Exception("Instructions: " + name); }
        var root = Path.Combine(Environment.CurrentDirectory, ".uv-cache", "instructions-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src", "nested"));
        Directory.CreateDirectory(Path.Combine(root, "skills", "helpful-skill"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "AGENTS.md"), "root rule");
            await File.WriteAllTextAsync(Path.Combine(root, "src", "AGENTS.md"), "nested rule");
            await File.WriteAllTextAsync(Path.Combine(root, "src", "nested", "AGENTS.md"), "deep rule");
            await File.WriteAllTextAsync(Path.Combine(root, "src", "nested", "file.txt"), "content");
            await File.WriteAllTextAsync(Path.Combine(root, "skills", "helpful-skill", "SKILL.md"), "---\nname: helpful-skill\ndescription: |\n  Help with a task.\n---\nSECRET SKILL BODY\n");
            var workspace = new Workspace(root);
            var loader = new InstructionLoader(workspace);
            var instructions = await loader.LoadForPathAsync("src/nested/file.txt");
            Check(instructions.Count == 3 && instructions[0].Content == "root rule" && instructions[1].Content == "nested rule" && instructions[2].Content == "deep rule", "hierarchy and order");
            Check((await loader.LoadForPathAsync("other.txt")).Count == 1, "sibling scope");
            var catalog = new SkillCatalog(workspace);
            var skills = await catalog.ListAsync();
            Check(skills.Count == 1 && skills[0].Name == "helpful-skill" && skills[0].Description.Contains("Help") && !skills[0].Description.Contains("SECRET"), "skill metadata only");
            Check((await catalog.LoadAsync("helpful-skill")).Contains("SECRET SKILL BODY"), "on demand load");
            var skillTool = new SkillTool(catalog, false);
            var loaded = await skillTool.ExecuteAsync(new Dictionary<string, JsonElement> { ["name"] = JsonSerializer.SerializeToElement("helpful-skill") });
            Check(loaded.Success && loaded.Content.Contains("SECRET SKILL BODY"), "skill tool loads body");
            var selectedModel = new Model(new LlmResponse { Content = "selected skill answer" });
            var selectedStore = new Store();
            var selectedAgent = new Agent(selectedModel, selectedStore, new ToolRegistry(), workspace: workspace);
            var selectedResult = await selectedAgent.RunWithSkillAsync("selected", "perform task", "helpful-skill");
            Check(selectedResult.Status == Status.COMPLETED && selectedModel.Requests[0].Any(m => m.Content?.Contains("SECRET SKILL BODY") == true) && selectedModel.Requests[0][^1].Content == "perform task", "selected skill loaded before request without tool call");
            Check(selectedStore.Messages.All(m => m.Content?.Contains("SECRET SKILL BODY") != true), "selected skill body is not recorded as a user message");
            var invalidModel = new Model();
            var invalidAgent = new Agent(invalidModel, new Store(), new ToolRegistry(), workspace: workspace);
            Check((await invalidAgent.RunWithSkillAsync("s", "task", "../escape")).Status == Status.FAILED && invalidModel.Requests.Count == 0, "selected skill traversal rejected before model call");
            var automaticCall = new ToolCall { Id = "skill-1", Name = "skills.load", Arguments = new Dictionary<string, JsonElement> { ["name"] = JsonSerializer.SerializeToElement("helpful-skill") } };
            var autoModel = new Model(new LlmResponse { ToolCalls = [automaticCall] }, new LlmResponse { Content = "loaded" });
            var autoStore = new Store();
            var autoResult = await new Agent(autoModel, autoStore, new ToolRegistry(), workspace: workspace).RunAsync("auto", "help with a task");
            Check(autoResult.Status == Status.COMPLETED && autoStore.Messages.Any(m => m.Role == "tool" && m.Content?.Contains("SECRET SKILL BODY") == true), "model can load a skill using tool calling");
            try { await catalog.LoadAsync("../escape"); throw new Exception("Traversal accepted"); } catch (ArgumentException) { checks++; }
            Directory.CreateDirectory(Path.Combine(root, "skills", "bad-skill"));
            await File.WriteAllTextAsync(Path.Combine(root, "skills", "bad-skill", "SKILL.md"), "---\nname: other\ndescription: x\n---\nbody");
            try { await catalog.LoadAsync("bad-skill"); throw new Exception("Mismatched name accepted"); } catch (ArgumentException) { checks++; }
            foreach (var invalid in new[] { "compatibility: [linux]", "metadata: [invalid]", "metadata:\n  author: [invalid]", "allowed-tools: [shell]" })
            {
                await File.WriteAllTextAsync(Path.Combine(root, "skills", "bad-skill", "SKILL.md"), "---\nname: bad-skill\ndescription: test\n" + invalid + "\n---\nbody");
                try { await catalog.LoadAsync("bad-skill"); throw new Exception("Invalid optional field accepted"); } catch (ArgumentException) { checks++; }
            }
            await File.WriteAllTextAsync(Path.Combine(root, "skills", "bad-skill", "SKILL.md"), "---\nname: bad-skill\ndescription: test\ncompatibility: Linux\nlicense: MIT\nmetadata:\n  author: test\nallowed-tools: shell\n---\nbody");
            Check((await catalog.LoadAsync("bad-skill")).Contains("body"), "optional skill metadata accepted");
            Directory.Delete(Path.Combine(root, "skills", "bad-skill"), true);
            var store = new Store();
            var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"path":"src/nested/file.txt"}""")!;
            var first = new ToolCall { Id = "1", Name = "filesystem.read_file", Arguments = args };
            var retry = new ToolCall { Id = "2", Name = "filesystem.read_file", Arguments = args };
            var model = new Model(new LlmResponse { ToolCalls = [first] }, new LlmResponse { ToolCalls = [retry] }, new LlmResponse { Content = "done" });
            var registry = new ToolRegistry();
            registry.Register(new ReadFileTool(workspace));
            var result = await new Agent(model, store, registry, workspace: workspace).RunAsync("s", "read");
            Check(model.Requests[0].Any(m => m.Content is not null && m.Content.Contains("root rule") && m.Content.Contains("helpful-skill") && !m.Content.Contains("SECRET SKILL BODY")), "root instructions and skill metadata in context");
            Check(result.Status == Status.COMPLETED && store.Messages[2].Content!.Contains("nested rule") && !store.Messages[2].Content!.Contains("content"), "instructions precede file operation");
            Check(store.Messages[2].Content!.Contains("More deeply nested") && store.Messages[2].Content!.Contains("deep rule"), "nearest instructions take precedence");
            Check(store.Messages[4].Content == "content", "file read after instructions");
            var batchStore = new Store();
            var batchModel = new Model(new LlmResponse { ToolCalls = [first, retry] }, new LlmResponse { Content = "done" });
            var batchRegistry = new ToolRegistry();
            batchRegistry.Register(new ReadFileTool(workspace));
            _ = await new Agent(batchModel, batchStore, batchRegistry, workspace: workspace).RunAsync("s", "read");
            Check(batchStore.Messages[2].Content!.Contains("nested rule") && batchStore.Messages[3].Content!.Contains("nested rule"), "parallel calls do not bypass instruction delivery");
            return checks;
        }
        finally { Directory.Delete(root, true); }
    }
}
