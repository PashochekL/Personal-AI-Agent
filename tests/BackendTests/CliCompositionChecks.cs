using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiAgent.Api;
using AiAgent.Cli;
using AiAgent.Shared;
using TaskStatus = AiAgent.Shared.TaskStatus;

internal static class CliCompositionChecks
{
    public static async Task<int> RunAsync(HttpClient http, string key)
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); count++; Console.WriteLine("PASS: " + name); }
        using var transport = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var client = new BackendClient(transport, http.BaseAddress!, key);
        var catalog = await client.CatalogAsync(default);
        Check(catalog.Models.SequenceEqual(new[] { "synthetic-chat", "synthetic-other" }) && catalog.Skills.Single().Name == "merged-skill", "CLI receives chat models and project skills from production backend");
        var named = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => client.ResolveSessionAsync("cli-synthetic", default)));
        Check(named.Select(s => s.SessionId).Distinct().Count() == 1, "concurrent reconnects resolve one server-generated session");
        var session = named[0].SessionId;
        var input = new Submission(session, "cli-selected-skill", "synthetic-other", "merged-skill", RequestKey: "cli-request");
        var first = await client.SubmitAsync(input, default);
        var repeated = await client.SubmitAsync(input, default);
        Check(first.TaskId == repeated.TaskId, "HTTP client submission replay returns same task");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var result = await client.WaitAsync(first.TaskId, deadline.Token);
        Check(result.Status == TaskStatus.COMPLETED, "CLI task completes through API, Redis, Worker and Core");
        Check((await client.LatestAsync(session, default))?.TaskId == first.TaskId, "latest durable receipt recovered after reconnect");
        try { await client.SubmitAsync(input with { Model = "synthetic-chat" }, default); throw new Exception("Expected conflict"); }
        catch (BackendException e) { Check(e.Status == HttpStatusCode.Conflict, "model change with same request key conflicts"); }
        try { await client.SubmitAsync(input with { Skill = null }, default); throw new Exception("Expected conflict"); }
        catch (BackendException e) { Check(e.Status == HttpStatusCode.Conflict, "skill change with same request key conflicts"); }
        var before = await http.GetStringAsync($"/sessions/{session}/messages");
        var context = await client.SubmitAsync(new(session, "/context", "synthetic-other", Operation: "context", RequestKey: "context"), default);
        Check((await client.WaitAsync(context.TaskId, deadline.Token)).Result?.Response?.Contains("~100") == true, "context command executes under Worker session lease");
        var compress = await client.SubmitAsync(new(session, "/compress", "synthetic-other", Operation: "compress", RequestKey: "compress"), default);
        Check((await client.WaitAsync(compress.TaskId, deadline.Token)).Result?.Response == "Готово.", "manual compression executes through Core");
        Check(await http.GetStringAsync($"/sessions/{session}/messages") == before, "console operations preserve original history");
        var usage = await client.UsageAsync(session, default);
        Check(usage.Total >= 120, "backend persists reported token usage including compaction");
        using var invalid = await http.PostAsJsonAsync("/tasks", new { session_id = session, user_message = "no", operation = "execute-anything" });
        Check(invalid.StatusCode == HttpStatusCode.BadRequest, "unknown operation rejected before queue admission");
        using var badModel = await http.PostAsJsonAsync("/tasks", new { session_id = session, user_message = "no", model = "synthetic-vector" });
        Check(badModel.StatusCode == HttpStatusCode.BadRequest, "embedding model cannot be selected for chat");
        using var badSkill = await http.PostAsJsonAsync("/tasks", new { session_id = session, user_message = "no", skill = "missing-skill" });
        Check(badSkill.StatusCode == HttpStatusCode.BadRequest, "missing skill rejected without queueing work");
        var transcript = await RunCliAsync(http.BaseAddress!, key, "/skill merged-skill\ncli-from-process\n/tokens\n/context\n/compress\n/session\n/exit\n");
        Check(transcript.ExitCode == 0 && transcript.Output.Contains("saved through Core") && transcript.Output.Contains("Готово.") && !transcript.Output.Contains(key), "real CLI process uses HTTP commands and exits without leaking credentials");
        var reconnect = await RunCliAsync(http.BaseAddress!, key, "/session\n/exit\n");
        Check(reconnect.ExitCode == 0 && reconnect.Output.Contains("Последняя задача:"), "real CLI restart recovers session and last receipt");
        return count;
    }

    public static async Task RunOfflineCatalogAsync(HttpClient http, string key)
    {
        var result = await RunCliAsync(http.BaseAddress!, key, "/session\n/exit\n");
        if (result.ExitCode != 0 || !result.Output.Contains("Последняя задача:") || !result.Output.Contains("Каталог моделей недоступен"))
            throw new Exception("CLI could not recover task receipts while model catalog was unavailable");
        Console.WriteLine("PASS: CLI starts and recovers durable task receipts with unavailable model catalog");
    }

    private static async Task<(int ExitCode, string Output)> RunCliAsync(Uri address, string key, string input)
    {
        var dll = typeof(BackendClient).Assembly.Location;
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(dll);
        start.Environment["DAMIR_BACKEND_URL"] = address.ToString(); start.Environment["DAMIR_BACKEND_API_KEY"] = key;
        start.Environment["DAMIR_SESSION_ID"] = "cli-process-test";
        using var process = Process.Start(start) ?? throw new Exception("CLI process did not start");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(input); process.StandardInput.Close();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, await output + await error);
    }
}
