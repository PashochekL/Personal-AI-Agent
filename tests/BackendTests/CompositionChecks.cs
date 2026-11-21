using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiAgent.Api;
using AiAgent.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using TaskStatus = AiAgent.Shared.TaskStatus;

/// <summary>Real HTTP API, Worker, Core, gateway adapter and pgvector. Only model HTTP responses are scripted.</summary>
static class CompositionChecks
{
    public static async Task<int> RunAsync(BackendOptions options)
    {
        var workspace = Path.Combine(Directory.GetCurrentDirectory(), ".uv-cache", "backend-composition-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        var agentsDirectory = Path.Combine(workspace, ".agents");
        var skillsDirectory = Path.Combine(agentsDirectory, "skills");
        var skillDirectory = Path.Combine(skillsDirectory, "merged-skill");
        Directory.CreateDirectory(skillDirectory);
        File.WriteAllText(Path.Combine(agentsDirectory, "AGENTS.md"), "merged-project-instructions");
        File.WriteAllText(Path.Combine(skillDirectory, "SKILL.md"), "---\nname: merged-skill\ndescription: Verify merged project context\n---\nSynthetic skill instructions.\n");
        var changes = new Dictionary<string, string?>
        {
            ["AGENT_WORKSPACE"] = workspace, ["LITELLM_API_KEY"] = "synthetic-gateway-key", ["DEFAULT_MODEL"] = "synthetic-chat",
            ["MEMORY_DATABASE_URL"] = options.Database, ["DAMIR_MEMORY_ENABLED"] = "1", ["DAMIR_MEMORY_OWNER"] = options.Scope.OwnerId,
            ["DAMIR_MEMORY_PROJECT"] = options.Scope.ProjectId, ["DAMIR_EMBEDDING_DIMENSIONS"] = "3", ["DAMIR_EMBEDDING_MODEL"] = "synthetic-vector",
            ["DAMIR_EMBEDDING_DEPLOYMENT"] = "synthetic-vector", ["LITELLM_BASE_URL"] = null, ["DAMIR_CONTEXT_TOKENS"] = "120000", ["DAMIR_TIMEZONE"] = "UTC"
        };
        var previous = changes.ToDictionary(x => x.Key, x => Environment.GetEnvironmentVariable(x.Key));
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        await using var gateway = builder.Build();
        gateway.MapPost("/utils/token_counter", () => Results.Json(new { total_tokens = 100 }));
        gateway.MapPost("/v1/embeddings", () => Results.Json(new { data = new[] { new { index = 0, embedding = new[] { 1f, 0f, 0f } } } }));
        var modelCalls = 0;
        var webToolSeen = 0;
        var projectContextSeen = 0;
        var webWireName = "tool_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("web.search"))).ToLowerInvariant()[..32];
        gateway.MapPost("/v1/responses", async (HttpContext context) =>
        {
            Interlocked.Increment(ref modelCalls);
            using var json = await JsonDocument.ParseAsync(context.Request.Body);
            var input = json.RootElement.GetProperty("input").GetRawText();
            if (json.RootElement.GetProperty("tools").GetRawText().Contains(webWireName)) Interlocked.Exchange(ref webToolSeen, 1);
            if (input.Contains("merged-project-instructions") && input.Contains("merged-skill")) Interlocked.Exchange(ref projectContextSeen, 1);
            if (input.Contains("save-backend-fact") && !input.Contains("function_call_output"))
            {
                var name = "tool_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("memory.save"))).ToLowerInvariant()[..32];
                return Results.Json(new { status = "completed", output = new[] { new { type = "function_call", call_id = "backend-save-1", name,
                    arguments = "{\"key\":\"backend_fact\",\"content\":\"likes synthetic violet\"}" } } });
            }
            var answer = input.Contains("recall-backend-fact")
                ? input.Contains("likes synthetic violet") ? "recalled synthetic violet" : "missing memory"
                : "saved through Core";
            return Results.Json(new { status = "completed", output = new[] { new { type = "message", role = "assistant",
                content = new[] { new { type = "output_text", text = answer } } } } });
        });
        await gateway.StartAsync();
        changes["LITELLM_BASE_URL"] = Address(gateway);
        try
        {
            foreach (var setting in changes) Environment.SetEnvironmentVariable(setting.Key, setting.Value);
            await using var app = await BackendApplication.BuildAsync(options); await app.StartAsync();
            using var http = new HttpClient { BaseAddress = new Uri(Address(app)) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
            var saved = await Run(http, "save-backend-fact");
            if (saved.Result?.Response != "saved through Core") throw new Exception("Real Core composition did not execute memory.save: " + saved.Result?.Error);
            var recall = await Run(http, "recall-backend-fact");
            if (recall.Result?.Response != "recalled synthetic violet" || recall.SessionId == saved.SessionId) throw new Exception("Memory was not retrieved in a new API session");
            var history = await http.GetStringAsync("/sessions/" + saved.SessionId + "/messages");
            if (!history.Contains("memory.save") || !history.Contains("backend-save-1") || modelCalls != 3) throw new Exception("Tool calls did not round-trip through shared history");
            if (webToolSeen != 1 || projectContextSeen != 1) throw new Exception("Upstream web search or .agents context was lost in backend composition");
            await app.StopAsync();
            Console.WriteLine("PASS: production composition executes Core memory tool through gateway HTTP");
            Console.WriteLine("PASS: API session retrieves long-term memory saved in a different session");
            Console.WriteLine("PASS: production composition persists tool call IDs and results");
            Console.WriteLine("PASS: production composition advertises upstream web.search tool");
            Console.WriteLine("PASS: .agents instructions and skills coexist with long-term memory");
            return 5;
        }
        finally
        {
            foreach (var setting in previous) Environment.SetEnvironmentVariable(setting.Key, setting.Value);
            await gateway.StopAsync();
            await using var db = NpgsqlDataSource.Create(options.Database);
            await using var cleanup = db.CreateCommand("DELETE FROM damir_memories WHERE owner_id=$1 AND project_id=$2 AND memory_key='backend_fact'");
            cleanup.Parameters.AddWithValue(options.Scope.OwnerId); cleanup.Parameters.AddWithValue(options.Scope.ProjectId);
            await cleanup.ExecuteNonQueryAsync();
            File.Delete(Path.Combine(skillDirectory, "SKILL.md"));
            File.Delete(Path.Combine(agentsDirectory, "AGENTS.md"));
            Directory.Delete(skillDirectory);
            Directory.Delete(skillsDirectory);
            Directory.Delete(agentsDirectory);
            Directory.Delete(workspace); // Only fixture paths, no recursive deletion.
        }
    }

    private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    private static async Task<AgentTask> Run(HttpClient http, string message)
    {
        using var sessionResponse = await http.PostAsync("/sessions", null); sessionResponse.EnsureSuccessStatusCode();
        var session = ContractJson.Deserialize<Session>(await sessionResponse.Content.ReadAsStringAsync());
        using var response = await http.PostAsJsonAsync("/tasks", new { session_id = session.SessionId, user_message = message }); response.EnsureSuccessStatusCode();
        var task = ContractJson.Deserialize<AgentTask>(await response.Content.ReadAsStringAsync());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (task.Status is TaskStatus.PENDING or TaskStatus.RUNNING)
        {
            await Task.Delay(30, deadline.Token);
            task = ContractJson.Deserialize<AgentTask>(await http.GetStringAsync("/tasks/" + task.TaskId, deadline.Token));
        }
        return task;
    }
}
