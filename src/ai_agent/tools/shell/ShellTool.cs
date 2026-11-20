using System.Text.Json;
using AiAgent.Shared;

namespace AiAgent.Tools.Shell;

/// <summary>Runs an explicitly approved command through the sandbox contract.</summary>
public sealed class ShellTool(ISandboxExecutor sandbox, Func<string, int, CancellationToken, Task<bool>> approve) : ITool
{
    public string Name => "shell.execute";
    public string Description => "Run a shell command in an isolated Linux container after owner approval. The working directory is /workspace and the container has no network. /bin/sh is the default; use pwsh -NoLogo -NoProfile -Command for PowerShell 7 on Linux.";
    public IReadOnlyDictionary<string, JsonElement> Parameters => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"type":"object","properties":{"command":{"type":"string","minLength":1,"maxLength":8192},"timeout":{"type":"integer","minimum":1,"maximum":120}},"required":["command"],"additionalProperties":false}""")!;
    public async Task<ToolResult> ExecuteAsync(IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!arguments.TryGetValue("command", out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()) || value.GetString()!.Length > 8192)
            return new ToolResult { Success = false, Content = "", Error = "Invalid command" };
        var command = value.GetString()!;
        var timeout = 30;
        if (arguments.TryGetValue("timeout", out var duration) && (duration.ValueKind != JsonValueKind.Number || !duration.TryGetInt32(out timeout) || timeout is < 1 or > 120))
            return new ToolResult { Success = false, Content = "", Error = "Invalid timeout" };
        if (!await approve(command, timeout, cancellationToken))
            return new ToolResult { Success = false, Content = "", Error = "Command was not approved by the owner" };
        try
        {
            var result = await sandbox.ExecuteAsync(command, "/workspace", timeout, cancellationToken);
            var content = $"Exit code: {result.ExitCode?.ToString() ?? "unknown"}\nTimed out: {result.TimedOut}\nstdout:\n{result.Stdout}\nstderr:\n{result.Stderr}";
            return result.TimedOut || result.ExitCode != 0
                ? new ToolResult { Success = false, Content = content, Error = result.TimedOut ? "Command timed out" : "Command failed" }
                : new ToolResult { Success = true, Content = content };
        }
        catch (AgentError) { return new ToolResult { Success = false, Content = "", Error = "Sandbox unavailable" }; }
    }
}
