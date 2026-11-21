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
        });
        static IResult Model<T>(T model, int code = 200) where T : ExchangeModel => Results.Content(ContractJson.Serialize(model), "application/json", statusCode: code);
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
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
        app.MapGet("/sessions/{id}", async (string id, PostgresTaskStore store, CancellationToken token) =>
            await store.HasSessionAsync(id, token) ? Model(new Session { SessionId = id }) : Results.NotFound());
        app.MapGet("/sessions/{id}/messages", async (string id, PostgresTaskStore store, PostgresContextStore history, CancellationToken token) =>
        {
            if (!await store.HasSessionAsync(id, token)) return Results.NotFound();
            var messages = await history.GetMessagesAsync(id, token);
            return Results.Content("[" + string.Join(",", messages.Select(ContractJson.Serialize)) + "]", "application/json");
        });
        app.MapPost("/tasks", async (HttpContext context, PostgresTaskStore store, CancellationToken token) =>
        {
            if (!context.Request.HasJsonContentType()) return Results.StatusCode(415);
            var request = await JsonSerializer.DeserializeAsync<TaskRequest>(context.Request.Body, Json, token)
                ?? throw new TaskAdmissionException(400, "Request body required.");
            var task = await store.SubmitAsync(request, context.Request.Headers.TryGetValue("Idempotency-Key", out var key) ? key.ToString() : null, token);
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
