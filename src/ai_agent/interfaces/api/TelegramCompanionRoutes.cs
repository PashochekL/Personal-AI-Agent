namespace AiAgent.Api;

public static class TelegramCompanionRoutes
{
    public static void MapTelegramCompanion(this WebApplication app)
    {
        app.MapPost("/console/resume", async (ConsoleResume resume, PostgresTaskStore store, CancellationToken ct) =>
        { await store.SaveConsoleResumeAsync(resume, ct); return Results.NoContent(); });
        app.MapGet("/console/resume", async (PostgresTaskStore store, CancellationToken ct) => Results.Json(await store.ConsoleResumeAsync(ct)));
        app.MapPost("/telegram/link", async (CompanionLink link, PostgresTaskStore store, CancellationToken ct) =>
            Results.Json(new { code = await store.CreateTelegramLinkAsync(link, ct) }));
        app.MapGet("/telegram/{botId:long}/{lease}", async (long botId, string lease, PostgresTaskStore store, CancellationToken ct) =>
            Results.Json(await store.TelegramStateAsync(botId, lease, ct)));
        app.MapPost("/telegram/{botId:long}/{lease}/connect", async (long botId, string lease, CompanionConnect connect, PostgresTaskStore store, CancellationToken ct) =>
        { await store.ConnectTelegramAsync(botId, lease, connect, ct); return Results.NoContent(); });
        app.MapPost("/telegram/{botId:long}/{lease}/resume", async (long botId, string lease, CompanionResume resume, PostgresTaskStore store, CancellationToken ct) =>
        { await store.ResumeTelegramAsync(botId, lease, resume, ct); return Results.NoContent(); });
        app.MapPost("/telegram/{botId:long}/{lease}/selection", async (long botId, string lease, CompanionSelection selection, PostgresTaskStore store, IBackendAgentFactory factory, CancellationToken ct) =>
        {
            if (factory is CoreAgentFactory core) await core.ValidateAsync(new TaskSelection(selection.Model, selection.Skill), ct);
            else if (selection.Model is not null || selection.Skill is not null) throw new TaskAdmissionException(400, "Catalog unavailable.");
            await store.SelectTelegramAsync(botId, lease, selection, ct); return Results.NoContent();
        });
        app.MapPost("/telegram/{botId:long}/{lease}/checkpoint", async (long botId, string lease, CompanionCheckpoint checkpoint, PostgresTaskStore store, CancellationToken ct) =>
        { await store.CheckpointTelegramAsync(botId, lease, checkpoint, ct); return Results.NoContent(); });
        app.MapPost("/telegram/{botId:long}/{lease}/release", async (long botId, string lease, PostgresTaskStore store, CancellationToken ct) =>
        { await store.ReleaseTelegramAsync(botId, lease, ct); return Results.NoContent(); });
        app.MapPost("/sessions/{id}/console", async (string id, PostgresTaskStore store, CancellationToken ct) =>
        { await store.ReturnToConsoleAsync(id, ct); return Results.NoContent(); });
        app.MapGet("/sessions/{id}/telegram-files", async (string id, PostgresTaskStore store, CancellationToken ct) =>
            Results.Json(await store.TelegramFilesAsync(id, ct)));
        app.MapPost("/sessions/{id}/telegram-files/{file}/ack", async (string id, string file, PostgresTaskStore store, CancellationToken ct) =>
        { await store.AcknowledgeTelegramFileAsync(id, file, ct); return Results.NoContent(); });
    }
}
