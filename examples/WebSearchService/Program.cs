using System.Text.Json;
using System.Xml;

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:8080");
builder.Logging.ClearProviders();
using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
var provider = new SearchProvider(client);
var app = builder.Build();
app.MapGet("/health", () => Results.Ok());
app.MapGet("/search", async (string? q, HttpContext context) =>
{
    try
    {
        if (q is null) return Results.BadRequest(new { error = "Invalid search query" });
        return Results.Json(await provider.SearchAsync(q, context.RequestAborted), new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { return Results.StatusCode(499); }
    catch (ArgumentException) { return Results.BadRequest(new { error = "Invalid search query" }); }
    catch (Exception e) when (e is HttpRequestException or IOException or XmlException or InvalidOperationException or OperationCanceledException)
    { return Results.Json(new { error = "Search provider unavailable" }, statusCode: 502); }
});
await app.RunAsync();
