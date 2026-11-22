using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiAgent.Api;
using AiAgent.Http;
using AiAgent.Shared;
using AiAgent.Telegram;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using TaskStatus = AiAgent.Shared.TaskStatus;

// Real API/Worker/Core/history/memory/filesystem for two owners; only the model and Telegram HTTP are synthetic.
internal static class IsolatedAccountsChecks
{
    public static object? ModelReply(JsonElement root)
    {
        var input = root.GetProperty("input").EnumerateArray().ToArray();
        var user = input.LastOrDefault(e => e.TryGetProperty("role", out var r) && r.GetString() == "user");
        if (user.ValueKind == JsonValueKind.Undefined) return null;
        var content = user.GetProperty("content");
        var prompt = content.ValueKind == JsonValueKind.String ? content.GetString()! : content[0].GetProperty("text").GetString()!;
        if (!prompt.StartsWith("iso:")) return null;
        object Answer(string answer) => new { status = "completed", output = new[] { new { type = "message", role = "assistant",
            content = new[] { new { type = "output_text", text = answer } } } } };
        if (input.Last().TryGetProperty("type", out var type) && type.GetString() == "function_call_output")
            return Answer(input.Last().GetProperty("output").GetString()!);
        var parts = prompt.Split(':');
        var (tool, args) = parts[1] switch
        {
            "save" => ("memory.save", (object)new { key = "same_key", content = "memory-" + parts[2] }),
            "list" => ("memory.list", (object)new { }),
            "write" => ("filesystem.write_file", (object)new { path = "private.txt", content = "file-" + parts[2] }),
            "read" => ("filesystem.read_file", (object)new { path = "private.txt" }),
            "escape" => ("filesystem.read_file", (object)new { path = "../B/private.txt" }),
            _ => ("", (object)new { })
        };
        if (tool.Length == 0) return Answer(prompt);
        var name = "tool_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tool))).ToLowerInvariant()[..32];
        return new { status = "completed", output = new[] { new { type = "function_call", call_id = Guid.NewGuid().ToString("N"), name,
            arguments = JsonSerializer.Serialize(args) } } };
    }

    public static async Task<int> RunAsync(BackendOptions original)
    {
        var count = 0;
        void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); count++; Console.WriteLine("PASS: " + label); }
        var fixture = Path.Combine(Directory.GetCurrentDirectory(), ".uv-cache", "isolated-composition-" + Guid.NewGuid().ToString("N"));
        var root = new TelegramOptions { Token = "123456:synthetic_token_not_a_real_secret", AllowedUserIds = [900001, 900002],
            Owner = original.Scope.OwnerId, Project = fixture, StateDirectory = Path.Combine(fixture, "state") };
        var names = new[] { "AGENT_WORKSPACE", "DAMIR_MEMORY_OWNER", "DAMIR_MEMORY_PROJECT", "DAMIR_GITHUB_MCP" };
        var previous = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        var apps = new List<WebApplication>(); var clients = new List<HttpClient>();
        try
        {
            var profiles = new List<TelegramBackendProfile>();
            foreach (var id in root.EffectiveUserIds)
            {
                var dir = Path.Combine(fixture, id == 900001 ? "A" : "B"); Directory.CreateDirectory(dir);
                Environment.SetEnvironmentVariable("AGENT_WORKSPACE", dir);
                Environment.SetEnvironmentVariable("DAMIR_MEMORY_OWNER", root.AccountOwner(id));
                Environment.SetEnvironmentVariable("DAMIR_MEMORY_PROJECT", root.AccountProject);
                Environment.SetEnvironmentVariable("DAMIR_GITHUB_MCP", "0");
                var key = Guid.NewGuid().ToString("N");
                var app = await BackendApplication.BuildAsync(new BackendOptions { Database = original.Database, Redis = original.Redis, ApiKey = key,
                    Scope = new MemoryScope { OwnerId = root.AccountOwner(id), ProjectId = root.AccountProject }, Url = "http://127.0.0.1:0" });
                apps.Add(app); await app.StartAsync();
                var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                profiles.Add(new(id, url + "/", key, root.AccountOwner(id), root.AccountProject));
                var http = new HttpClient { BaseAddress = new Uri(url) }; clients.Add(http);
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            }
            var backends = clients.Select((http, i) => new BackendClient(http, new Uri(profiles[i].Url), profiles[i].ApiKey)).ToArray();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var ct = deadline.Token;
            async Task<AgentTask> Run(int account, string prompt)
            {
                var session = await backends[account].CreateSessionAsync(ct);
                var accepted = await backends[account].SubmitAsync(new(session.SessionId, prompt, RequestKey: Guid.NewGuid().ToString("N")), ct);
                var done = await backends[account].WaitAsync(accepted.TaskId, ct);
                Check(done.Status == TaskStatus.COMPLETED, "isolated Core executes " + prompt);
                return done;
            }
            using (var wrong = new HttpClient())
            {
                wrong.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", profiles[0].ApiKey);
                using var response = await wrong.GetAsync(profiles[1].Url + "identity", ct);
                Check(response.StatusCode == HttpStatusCode.Unauthorized, "another account API key is rejected by the real server");
            }
            await Run(0, "iso:save:A"); await Run(1, "iso:save:B");
            var memoryA = (await Run(0, "iso:list")).Result!.Response!;
            var memoryB = (await Run(1, "iso:list")).Result!.Response!;
            Check(memoryA.Contains("memory-A") && !memoryA.Contains("memory-B") && memoryB.Contains("memory-B") && !memoryB.Contains("memory-A"),
                "same memory key stays private across owners and new sessions in real pgvector storage");
            await Run(0, "iso:write:A"); await Run(1, "iso:write:B");
            Check((await Run(0, "iso:read")).Result!.Response == "file-A" && (await Run(1, "iso:read")).Result!.Response == "file-B",
                "real Core filesystem tools use separate account workspaces");
            var escaped = (await Run(0, "iso:escape")).Result!.Response!;
            Check(escaped.StartsWith("Error:") && !escaped.Contains("file-B"), "filesystem traversal toward another account is denied");
            using var telegramHandler = new TelegramCompositionChecks.TelegramHandler(); using var telegramHttp = new HttpClient(telegramHandler);
            using var router = new TelegramRouter(new TelegramApi(telegramHttp, root.Token), root, profiles.ToArray(), TextWriter.Null);
            await router.InitializeAsync(ct);
            var sessionA = router.AccountState(900001).SessionId; var sessionB = router.AccountState(900002).SessionId;
            Check(sessionA != sessionB, "one Telegram session name resolves to distinct real server sessions");
            TelegramUpdate Update(long update, long id, string text) => new(update, new(update, new(id, false, null), new(id, "private"), text));
            await router.HandleAsync(Update(1, 900001, "iso:telegram-A"), ct);
            await router.HandleAsync(Update(2, 900002, "iso:telegram-B"), ct);
            for (var i = 0; i < 2; i++)
            {
                var id = root.EffectiveUserIds[i]; await router.DeliverAccountAsync(id, ct);
                await backends[i].WaitAsync(router.AccountState(id).Deliveries.Single().TaskId!, ct);
                await router.DeliverAccountAsync(id, ct); await router.DeliverAccountAsync(id, ct);
            }
            Check(telegramHandler.SentTo.Contains((900001, "iso:telegram-A")) && telegramHandler.SentTo.Contains((900002, "iso:telegram-B")),
                "isolated Telegram routing executes through real Redis workers and delivers to the correct sender");
            var taskA = await backends[0].LatestAsync(sessionA, ct);
            foreach (var path in new[] { "/sessions/" + sessionA, $"/sessions/{sessionA}/messages", $"/sessions/{sessionA}/usage",
                $"/sessions/{sessionA}/tasks/latest", "/tasks/" + taskA!.TaskId })
            {
                using var response = await clients[1].GetAsync(path, ct);
                Check(response.StatusCode == HttpStatusCode.NotFound, "foreign resource is hidden: " + path.Split('/').Last());
            }
            using (var cancel = await clients[1].PostAsync($"/tasks/{taskA!.TaskId}/cancel", null, ct))
                Check(cancel.StatusCode == HttpStatusCode.NotFound, "another owner cannot cancel a known real task ID");
            using (var submit = await clients[1].PostAsJsonAsync("/tasks", new { session_id = sessionA, user_message = "foreign-injection" }, ct))
                Check(submit.StatusCode == HttpStatusCode.NotFound, "another owner cannot submit to a known real session ID");
            var historyA = await clients[0].GetStringAsync($"/sessions/{sessionA}/messages", ct);
            var historyB = await clients[1].GetStringAsync($"/sessions/{sessionB}/messages", ct);
            Check(!historyA.Contains("telegram-B") && !historyB.Contains("telegram-A") && !historyA.Contains("foreign-injection"), "persistent histories contain no messages from the other account");
            return count;
        }
        finally
        {
            foreach (var http in clients) http.Dispose();
            foreach (var app in apps) { await app.StopAsync(); await app.DisposeAsync(); }
            foreach (var setting in previous) Environment.SetEnvironmentVariable(setting.Key, setting.Value);
            foreach (var dir in new[] { "state", "A", "B" })
            {
                var path = Path.Combine(fixture, dir);
                if (!Directory.Exists(path)) continue;
                foreach (var file in Directory.GetFiles(path)) File.Delete(file);
                Directory.Delete(path);
            }
            if (Directory.Exists(fixture)) Directory.Delete(fixture);
        }
    }
}
