using System.Text.Json;
using AiAgent.Core;
using AiAgent.Shared;

internal static class ProgressChecks
{
    public static async Task<int> RunAsync()
    {
        using var output = new StringWriter();
        await using (var progress = new ConsoleProgress(output, true))
        {
            progress.Model();
            progress.Tool("filesystem.read_file\u001b[31m");
        }
        var text = output.ToString();
        if (!text.Contains("Обращается к модели") || !text.Contains("Вызывает: filesystem.read_file31m") || !text.EndsWith("\r\u001b[2K", StringComparison.Ordinal))
            throw new Exception("Progress events or cleanup failed");
        using var quietOutput = new StringWriter();
        await using (var quiet = new ConsoleProgress(quietOutput, false)) { quiet.Model(); quiet.Tool("test"); }
        if (quietOutput.ToString().Length != 0) throw new Exception("Redirected output must stay clean");
        var names = new List<string>();
        var registry = new ToolRegistry(names.Add);
        registry.Register(new Echo());
        var call = new ToolCall { Id = "1", Name = "echo", Arguments = new Dictionary<string, JsonElement> { ["text"] = JsonSerializer.SerializeToElement("hi") } };
        var result = await registry.ExecuteAsync(call);
        if (!result.Success || !names.SequenceEqual(["echo"])) throw new Exception("Tool execution progress missing");
        await registry.ExecuteAsync(new ToolCall { Id = "2", Name = "echo", Arguments = new Dictionary<string, JsonElement>() });
        if (names.Count != 1) throw new Exception("Invalid calls must not be reported as executed");
        var faulty = new ToolRegistry(_ => throw new InvalidOperationException());
        faulty.Register(new Echo());
        if (!(await faulty.ExecuteAsync(call)).Success) throw new Exception("Progress reporting disrupted tool");
        return 5;
    }
}
