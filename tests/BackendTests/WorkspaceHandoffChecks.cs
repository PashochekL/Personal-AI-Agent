using System.Net;
using AiAgent.Api;
using AiAgent.Http;
using AiAgent.Memory;
using AiAgent.Shared;
using AiAgent.Telegram;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TaskStatus = AiAgent.Shared.TaskStatus;

internal static class WorkspaceHandoffChecks
{
    public static async Task<int> RunAsync(BackendOptions original)
    {
        var count = 0;
        void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); count++; Console.WriteLine("PASS: " + label); }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90)); var ct = deadline.Token;
        await using var database = NpgsqlDataSource.Create(original.Database);
        var prefix = "folder-handoff-" + Guid.NewGuid().ToString("N");
        var projectA = prefix + "-a"; var projectB = prefix + "-b";
        var bot = Random.Shared.NextInt64(100000000000, 999999999999);
        var apps = new List<WebApplication>(); var clients = new List<HttpClient>();
        BackendOptions Options(string project, string label) => new()
        {
            Database = original.Database, Redis = original.Redis, ApiKey = original.ApiKey,
            Scope = new() { OwnerId = original.Scope.OwnerId, ProjectId = project }, WorkspaceName = label,
            Url = "http://127.0.0.1:0", PollInterval = TimeSpan.FromMilliseconds(40)
        };
        async Task<(WebApplication App, BackendClient Client)> Open(string project, string label)
        {
            var app = await BackendApplication.BuildAsync(Options(project, label), new ProjectAgent(new PostgresContextStore(database), label), ct);
            apps.Add(app); await app.StartAsync(ct);
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var http = new HttpClient(); clients.Add(http);
            return (app, new BackendClient(http, new Uri(address), original.ApiKey));
        }
        async Task Close(WebApplication app) { await app.StopAsync(ct); await app.DisposeAsync(); apps.Remove(app); }
        async Task<string> Lease()
        {
            await using var command = database.CreateCommand("SELECT lease FROM damir_telegram_companion WHERE bot_id=$1");
            command.Parameters.AddWithValue(bot); return (string)(await command.ExecuteScalarAsync(ct))!;
        }
        async Task Send(CompanionChecks.TelegramTransport stub, BackendClient client, string lease, long id, string text, long user = 900001)
        {
            stub.Incoming.Enqueue(new(id, new(id, new(user, false, null), new(user, "private"), text)));
            while ((await client.TelegramStateAsync(bot, lease, ct)).UpdateOffset <= id) await Task.Delay(20, ct);
        }
        async Task Finish(BackendClient client, string lease)
        {
            while ((await client.TelegramStateAsync(bot, lease, ct)) is { } state && (state.PendingMessage is not null || state.PendingTask is not null || state.Parts is not null))
                await Task.Delay(20, ct);
        }
        try
        {
            var a = await Open(projectA, "Workspace A");
            var sessionA = (await a.Client.ResolveSessionAsync("folder-default", ct)).SessionId;
            var seedA = await a.Client.SubmitAsync(new(sessionA, "only-folder-a", RequestKey: "seed-a"), ct); await a.Client.WaitAsync(seedA.TaskId, ct);
            using var stubA = new CompanionChecks.TelegramTransport(bot); using var httpA = new HttpClient(stubA);
            string oldLink;
            await using (var companionA = new TelegramCompanion(new TelegramApi(httpA, bot + ":synthetic_workspace_token"), a.Client, TextWriter.Null))
            {
                oldLink = await companionA.LinkAsync(sessionA, null, null, ct); var lease = await Lease();
                await Send(stubA, a.Client, lease, 1, "/start " + oldLink.Split("?start=")[1]);
                await Send(stubA, a.Client, lease, 2, "telegram-in-folder-a"); await Finish(a.Client, lease);
                Check((await a.Client.MessagesAsync(sessionA, ct)).Any(m => m.Content == "telegram-in-folder-a"), "Telegram initially writes to the console's project A session");
                var contender = new PostgresTaskStore(database, Options(projectB, "Workspace B"));
                var contenderSession = await contender.ResolveSessionAsync("folder-default", ct);
                try { await contender.CreateTelegramLinkAsync(new(contenderSession.SessionId, bot, Guid.NewGuid().ToString("N")), ct); throw new Exception("Expected single receiver"); }
                catch (TaskAdmissionException e) { Check(e.StatusCode == 423, "another folder cannot take over the bot while project A is still running"); }
            }
            await Close(a.App);
            var b = await Open(projectB, "Workspace B");
            var sessionB = (await b.Client.ResolveSessionAsync("folder-default", ct)).SessionId;
            Check(sessionA != sessionB, "the same default dialogue name resolves separately in another folder");
            var seedB = await b.Client.SubmitAsync(new(sessionB, "only-folder-b", RequestKey: "seed-b"), ct); await b.Client.WaitAsync(seedB.TaskId, ct);
            // Even a project previously left in Telegram mode must require confirmation for this run.
            await using (var leaveTelegram = database.CreateCommand("UPDATE damir_api_sessions SET input_channel='telegram' WHERE session_id=$1"))
            { leaveTelegram.Parameters.AddWithValue(sessionB); await leaveTelegram.ExecuteNonQueryAsync(ct); }
            using var stubB = new CompanionChecks.TelegramTransport(bot); using var httpB = new HttpClient(stubB);
            await using (var companionB = new TelegramCompanion(new TelegramApi(httpB, bot + ":synthetic_workspace_token"), b.Client, TextWriter.Null))
            {
                var linkB = await companionB.LinkAsync(sessionB, null, null, ct); var lease = await Lease();
                var state = await b.Client.TelegramStateAsync(bot, lease, ct);
                Check(!state.Connected && state.WorkspaceName == "Workspace B", "switching folders clears Telegram confirmation and exposes the current workspace label");
                await Send(stubB, b.Client, lease, 3, "old-chat-must-not-run-in-b");
                await Send(stubB, b.Client, lease, 4, "Продолжить здесь");
                await Send(stubB, b.Client, lease, 5, "Сжать контекст");
                Check(!(await b.Client.MessagesAsync(sessionB, ct)).Any(m => m.Content == "old-chat-must-not-run-in-b") &&
                    (await b.Client.LatestAsync(sessionB, ct))?.TaskId == seedB.TaskId, "old-chat messages and buttons cannot execute in the newly opened folder");
                Check(stubB.Messages.Any(m => m.Text.Contains("Workspace B") && m.Text.Contains("свежую ссылку")), "old chat tells the owner which folder needs a fresh link");
                try { await b.Client.TelegramResumeAsync(bot, lease, sessionB, 900001, ct); throw new Exception("Expected fresh confirmation"); }
                catch (BackendException e) { Check(e.Status == HttpStatusCode.Forbidden, "backend also rejects button resume before fresh project confirmation"); }
                await Send(stubB, b.Client, lease, 6, "/start " + oldLink.Split("?start=")[1]);
                Check(!(await b.Client.TelegramStateAsync(bot, lease, ct)).Connected, "an old project A link cannot activate project B");
                await Send(stubB, b.Client, lease, 7, "/start " + linkB.Split("?start=")[1], 900002);
                Check(!(await b.Client.TelegramStateAsync(bot, lease, ct)).Connected, "another Telegram account cannot claim the new folder link");
                await Send(stubB, b.Client, lease, 8, "/start " + linkB.Split("?start=")[1]);
                await Send(stubB, b.Client, lease, 9, "telegram-in-folder-b"); await Finish(b.Client, lease);
                var historyB = await b.Client.MessagesAsync(sessionB, ct);
                Check(historyB.Any(m => m.Content == "telegram-in-folder-b") && !historyB.Any(m => m.Content == "only-folder-a"), "fresh link activates only project B and its own persistent history");
            }
            await Close(b.App);
            var reopened = await Open(projectA, "Workspace A");
            var reopenedSession = (await reopened.Client.ResolveSessionAsync("folder-default", ct)).SessionId;
            Check(reopenedSession == sessionA && (await reopened.Client.MessagesAsync(sessionA, ct)).Any(m => m.Content == "telegram-in-folder-a"), "returning to the original folder resolves the original dialogue and full saved history");
            using var stubReturn = new CompanionChecks.TelegramTransport(bot); using var httpReturn = new HttpClient(stubReturn);
            await using (var companionReturn = new TelegramCompanion(new TelegramApi(httpReturn, bot + ":synthetic_workspace_token"), reopened.Client, TextWriter.Null))
            {
                var link = await companionReturn.LinkAsync(sessionA, null, null, ct); var lease = await Lease();
                await Send(stubReturn, reopened.Client, lease, 10, "old-b-chat-must-not-run-in-a");
                Check(!(await reopened.Client.MessagesAsync(sessionA, ct)).Any(m => m.Content == "old-b-chat-must-not-run-in-a"), "returning to A also blocks stale Telegram input until its new link is opened");
                await Send(stubReturn, reopened.Client, lease, 11, "/start " + link.Split("?start=")[1]);
                Check(stubReturn.Messages.Any(m => m.Text.Contains("telegram-in-folder-a")) && !stubReturn.Messages.Any(m => m.Text.Contains("only-folder-b")), "reconnection previews the restored A dialogue without B history");
            }
            return count;
        }
        finally
        {
            foreach (var app in apps) { await app.StopAsync(); await app.DisposeAsync(); }
            foreach (var client in clients) client.Dispose();
            foreach (var table in new[] { "damir_telegram_files", "damir_telegram_companion", "damir_console_resume", "damir_demo_summaries", "damir_demo_context", "damir_demo_history", "damir_api_tasks", "damir_api_sessions" })
            {
                await using var cleanup = database.CreateCommand($"DELETE FROM {table} WHERE session_id IN (SELECT session_id FROM damir_api_sessions WHERE owner_id=$1 AND project_id=ANY($2))");
                cleanup.Parameters.AddWithValue(original.Scope.OwnerId); cleanup.Parameters.AddWithValue(new[] { projectA, projectB }); await cleanup.ExecuteNonQueryAsync();
            }
        }
    }

    private sealed class ProjectAgent(PostgresContextStore history, string workspace) : IBackendAgentFactory, IAgent
    {
        public Task<IAgent> CreateAsync(CancellationToken cancellationToken) => Task.FromResult<IAgent>(this);
        public async Task<AgentResult> RunAsync(string sessionId, string userMessage, CancellationToken cancellationToken = default)
        {
            await history.AppendMessageAsync(sessionId, new Message { Role = "user", Content = userMessage }, cancellationToken);
            var answer = workspace + ": " + userMessage;
            await history.AppendMessageAsync(sessionId, new Message { Role = "assistant", Content = answer }, cancellationToken);
            return new() { SessionId = sessionId, Status = TaskStatus.COMPLETED, Response = answer };
        }
    }
}
