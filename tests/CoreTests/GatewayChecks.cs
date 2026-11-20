using System.Net;
using System.Text;
using System.Text.Json;
using AiAgent.Gateway;
using AiAgent.Shared;

static class GatewayChecks
{
    public static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool ok, string name) { count++; if (!ok) throw new Exception("Gateway: " + name); }
        using var handler = new GatewayHandler();
        using var http = new HttpClient(handler);
        var client = new LiteLlmClient(http, new Uri("http://gateway:4000"), "test-proxy-key", "damir");
        var models = await client.ListModelsAsync();
        Check(models.SequenceEqual(["first", "second"]) && handler.Authorized, "model list and authentication");
        var tool = new ToolDefinition { Name = "filesystem.read_file", Description = "read", Parameters = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"type":"object"}""")! };
        var messages = new List<Message> { new() { Role = "user", Content = "read" } };
        Check(await client.CountTokensAsync(messages, [tool]) == 17 && handler.CountMapped, "token estimate uses selected model and tool schema");
        Check(await client.GetInputLimitAsync() == 500000, "model input limit from gateway");
        var first = await client.CompleteAsync(messages, [tool]);
        Check(handler.Authorized && first.ToolCalls.Single().Name == tool.Name, "tool name mapped");
        Check(first.Usage?.InputTokens == 3 && first.Usage.OutputTokens == 2, "usage mapped");
        messages.Add(new Message { Role = "assistant", ToolCalls = first.ToolCalls });
        messages.Add(new Message { Role = "tool", Content = "file contents", ToolCallId = "call-1" });
        var final = await client.CompleteAsync(messages, [tool]);
        Check(final.Content == "done" && handler.HistoryMapped, "tool history and final response");
        try { _ = new LiteLlmClient(http, new Uri("https://api.openai.com/v1"), "secret", "x"); throw new Exception("Direct provider URL accepted"); } catch (ArgumentException) { count++; }
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        try { await client.CompleteAsync(messages, [tool], cts.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { count++; }
        return count;
    }
}
sealed class GatewayHandler : HttpMessageHandler
{
    private int _calls;
    public bool Authorized { get; private set; }
    public bool HistoryMapped { get; private set; }
    public bool CountMapped { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Authorized = request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == "test-proxy-key";
        if (request.RequestUri!.AbsolutePath == "/utils/model_info")
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"model_info":{"max_input_tokens":500000}}""", Encoding.UTF8, "application/json") };
        if (request.Method == HttpMethod.Get)
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"data":[{"id":"first"},{"id":"second"},{"id":"first"}]}""", Encoding.UTF8, "application/json") };
        using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        var root = json.RootElement;
        if (request.RequestUri.AbsolutePath == "/utils/token_counter")
        {
            CountMapped = root.GetProperty("model").GetString() == "damir" && root.GetProperty("tools").GetArrayLength() == 1;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"total_tokens":17,"error":false}""", Encoding.UTF8, "application/json") };
        }
        var wireName = root.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString();
        string body;
        if (_calls++ == 0)
            body = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = (string?)null, tool_calls = new[] { new { id = "call-1", function = new { name = wireName, arguments = "{\"path\":\"file.txt\"}" } } } }, finish_reason = "tool_calls" } }, usage = new { prompt_tokens = 3, completion_tokens = 2, total_tokens = 5 } });
        else
        {
            var messages = root.GetProperty("messages");
            HistoryMapped = messages[1].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("name").GetString() == wireName && messages[2].GetProperty("tool_call_id").GetString() == "call-1";
            body = """{"choices":[{"message":{"content":"done"},"finish_reason":"stop"}]}""";
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
