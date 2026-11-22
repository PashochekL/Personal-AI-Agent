using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiAgent.Memory;
using AiAgent.Shared;
using Microsoft.AspNetCore.Http.Features;
using Npgsql;
using StackExchange.Redis;

namespace AiAgent.Api;

public static class BackendApplication
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    public static async Task<WebApplication> BuildAsync(BackendOptions options, IBackendAgentFactory? agentFactory = null, CancellationToken ct = default)
    {
        options.Validate();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.WebHost.UseUrls(options.Url);
        builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = 131072);
        // Do not log URLs, bodies, connection strings, authentication headers or provider exceptions.
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(options.Database));
        builder.Services.AddSingleton<PostgresTaskStore>();
        builder.Services.AddSingleton<PostgresContextStore>();
        builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var settings = ConfigurationOptions.Parse(options.Redis);
            settings.AbortOnConnectFail = false; settings.ConnectTimeout = 3000; settings.AsyncTimeout = 3000;
            return ConnectionMultiplexer.Connect(settings);
        });
        builder.Services.AddSingleton<RedisTaskQueue>();
        if (agentFactory is null) builder.Services.AddSingleton<IBackendAgentFactory, CoreAgentFactory>();
        else builder.Services.AddSingleton(agentFactory);
        builder.Services.AddSingleton<AgentWorker>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<AgentWorker>());
        var app = builder.Build();
        try
        {
            await app.Services.GetRequiredService<PostgresTaskStore>().InitializeAsync(ct);
            await app.Services.GetRequiredService<PostgresContextStore>().InitializeAsync(ct);
            if (app.Services.GetRequiredService<IBackendAgentFactory>() is CoreAgentFactory core) await core.InitializeAsync(ct);
        }
        catch { await app.DisposeAsync(); throw; }

        var keyHash = SHA256.HashData(Encoding.UTF8.GetBytes(options.ApiKey));
        app.Use(async (context, next) =>
        {
            if (context.Request.Path != "/health/live")
            {
                var header = context.Request.Headers.Authorization.ToString();
                if (!header.StartsWith("Bearer ", StringComparison.Ordinal) || header.Length > 520 ||
                    !CryptographicOperations.FixedTimeEquals(keyHash, SHA256.HashData(Encoding.UTF8.GetBytes(header[7..]))))
                { context.Response.StatusCode = 401; return; }
            }
            try { await next(context); }
            catch (TaskAdmissionException e) { context.Response.StatusCode = e.StatusCode; await context.Response.WriteAsJsonAsync(new { error = e.Message }, context.RequestAborted); }
            catch (Exception e) when (e is JsonException or BadHttpRequestException)
            { context.Response.StatusCode = e is BadHttpRequestException bad ? bad.StatusCode : 400; await context.Response.WriteAsJsonAsync(new { error = "Invalid request body." }, context.RequestAborted); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            catch (Exception e) when (e is NpgsqlException or RedisException or TimeoutException)
            { context.Response.StatusCode = 503; await context.Response.WriteAsJsonAsync(new { error = "Backend dependency unavailable. Check task status before retrying a submission." }, context.RequestAborted); }
            catch (Exception e) when (e is AgentError or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or YamlDotNet.Core.YamlException)
            { context.Response.StatusCode = 503; await context.Response.WriteAsJsonAsync(new { error = "Console service unavailable. Check gateway and project configuration." }, context.RequestAborted); }
        });
        static IResult Model<T>(T model, int code = 200) where T : ExchangeModel => Results.Content(ContractJson.Serialize(model), "application/json", statusCode: code);
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
        app.MapGet("/identity", (BackendOptions configured) => Results.Json(new { owner_id = configured.Scope.OwnerId, project_id = configured.Scope.ProjectId }));
        app.MapGet("/health/ready", async (PostgresTaskStore store, RedisTaskQueue queue, AgentWorker worker, CancellationToken token) =>
        {
            await store.HasSessionAsync("health", token);
            await queue.PingAsync().WaitAsync(token);
            return worker.Ready ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503);
        });
        app.MapPost("/sessions", async (HttpContext context, PostgresTaskStore store, CancellationToken token) =>
        {
            var session = await store.CreateSessionAsync(token);
            context.Response.Headers.Location = "/sessions/" + session.SessionId;
            return Model(session, 201);
        });
        app.MapPost("/sessions/named/{name}", async (string name, PostgresTaskStore store, CancellationToken token) =>
            Model(await store.ResolveSessionAsync(name, token)));
        app.MapGet("/console", async (IBackendAgentFactory factory, CancellationToken token) =>
            factory is CoreAgentFactory core ? Results.Json(await core.CatalogAsync(token)) : Results.StatusCode(501));
        app.MapGet("/sessions/{id}/usage", async (string id, PostgresTaskStore store, IBackendAgentFactory factory, CancellationToken token) =>
        {
            if (!await store.HasSessionAsync(id, token)) return Results.NotFound();
            if (factory is not CoreAgentFactory core) return Results.StatusCode(501);
            var usage = await core.UsageAsync(id, token);
            return Results.Json(new { input = usage.Input, output = usage.Output, total = usage.Total, missing_usage_responses = usage.MissingUsageResponses });
        });
        app.MapGet("/sessions/{id}", async (string id, PostgresTaskStore store, CancellationToken token) =>
            await store.HasSessionAsync(id, token) ? Model(new Session { SessionId = id }) : Results.NotFound());
        app.MapGet("/sessions/{id}/messages", async (string id, PostgresTaskStore store, PostgresContextStore history, CancellationToken token) =>
        {
            if (!await store.HasSessionAsync(id, token)) return Results.NotFound();
            var messages = await history.GetMessagesAsync(id, token);
            return Results.Content("[" + string.Join(",", messages.Select(ContractJson.Serialize)) + "]", "application/json");
        });
        app.MapGet("/sessions/{id}/tasks/latest", async (string id, PostgresTaskStore store, CancellationToken token) =>
        {
            if (!await store.HasSessionAsync(id, token)) return Results.NotFound();
            return await store.LatestAsync(id, token) is { } task ? Model(task) : Results.NoContent();
        });
        app.MapPost("/tasks", async (HttpContext context, PostgresTaskStore store, IBackendAgentFactory factory, CancellationToken token) =>
        {
            if (!context.Request.HasJsonContentType()) return Results.StatusCode(415);
            var request = await JsonSerializer.DeserializeAsync<TaskRequest>(context.Request.Body, Json, token)
                ?? throw new TaskAdmissionException(400, "Request body required.");
            request.Selection.Validate();
            var requestKey = context.Request.Headers.TryGetValue("Idempotency-Key", out var key) ? key.ToString() : null;
            if (string.IsNullOrWhiteSpace(request.SessionId)) throw new TaskAdmissionException(400, "Session required.");
            // A lost receipt can be recovered even if Gateway or the selected skill is now unavailable.
            if (await store.ReplayAsync(request, requestKey, token) is { } replay) return Model(replay, 202);
            if (factory is CoreAgentFactory core) await core.ValidateAsync(request.Selection, token);
            else if (request.Selection != new TaskSelection()) throw new TaskAdmissionException(400, "Console operations unavailable.");
            var task = await store.SubmitAsync(request, requestKey, token);
            context.Response.Headers.Location = "/tasks/" + task.TaskId;
            return Model(task, 202);
        });
        app.MapGet("/tasks/{id}", async (string id, PostgresTaskStore store, CancellationToken token) =>
            await store.GetAsync(id, token) is { } task ? Model(task) : Results.NotFound());
        app.MapPost("/tasks/{id}/cancel", async (string id, PostgresTaskStore store, CancellationToken token) =>
            await store.CancelAsync(id, token) is { } task ? Model(task, 202) : Results.NotFound());
        return app;
    }
}
