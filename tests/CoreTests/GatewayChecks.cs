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
        Check(handler.Authorized && handler.IncludesEncryptedReasoning && first.ToolCalls.Single().Name == tool.Name && first.Continuation is not null, "Responses tool and continuation mapped");
        Check(first.Usage?.InputTokens == 3 && first.Usage.OutputTokens == 2, "usage mapped");
        var savedAssistant = new Message { Role = "assistant", ToolCalls = first.ToolCalls, Continuation = first.Continuation };
        messages.Add(ContractJson.Deserialize<Message>(ContractJson.Serialize(savedAssistant)));
        messages.Add(new Message { Role = "tool", Content = "file contents", ToolCallId = "call-1" });
        Check(await client.CountTokensAsync(messages, [tool]) > 17, "counter includes continuation reserve");
        var final = await client.CompleteAsync(messages, [tool]);
        Check(final.Content == "done" && final.Continuation is not null && handler.HistoryMapped, "persisted reasoning and tool output replayed in order");
        var otherClient = new LiteLlmClient(http, new Uri("http://gateway:4000"), "test-proxy-key", "other-model");
        var otherResult = await otherClient.CompleteAsync(messages, [tool]);
        Check(otherResult.Content == "done" && handler.ModelSwitchMapped, "model switch does not replay incompatible continuation");
        var incompleteState = System.Text.Json.Nodes.JsonNode.Parse(first.Continuation!.Value.GetRawText())!;
        incompleteState["items"]![0]!["encrypted_content"] = null;
        var unresumable = new List<Message> { messages[0], new() { Role = "assistant", ToolCalls = first.ToolCalls, Continuation = JsonSerializer.SerializeToElement(incompleteState) }, messages[2] };
        var fallback = await client.CompleteAsync(unresumable, [tool]);
        Check(fallback.Content == "done" && handler.ModelSwitchMapped, "reasoning without encrypted state is not replayed");
        using (var refusalHttp = new HttpClient(new StaticResponsesHandler("""{"status":"completed","output":[{"type":"message","content":[{"type":"refusal","refusal":"cannot help"}]}]}""")))
        {
            var refusal = await new LiteLlmClient(refusalHttp, new Uri("http://gateway:4000"), "key", "damir").CompleteAsync([], []);
            Check(refusal.Content == "cannot help" && refusal.FinishReason == "stop", "Responses refusal is preserved");
        }
        using (var incompleteHttp = new HttpClient(new StaticResponsesHandler("""{"status":"incomplete","output":[{"type":"message","content":[{"type":"output_text","text":"partial"}]}]}""")))
        {
            var incomplete = await new LiteLlmClient(incompleteHttp, new Uri("http://gateway:4000"), "key", "damir").CompleteAsync([], []);
            Check(incomplete.FinishReason == "length", "incomplete response not reported as final");
        }
        using (var unsupportedHttp = new HttpClient(new StaticResponsesHandler("""{"status":"completed","output":[{"type":"unknown_item"}]}""")))
        {
            try { await new LiteLlmClient(unsupportedHttp, new Uri("http://gateway:4000"), "key", "damir").CompleteAsync([], []); throw new Exception("Unknown output accepted"); }
            catch (AgentError) { count++; }
        }
        var bridgeClient = new LiteLlmClient(http, new Uri("http://gateway-bridge:8080"), null, "damir");
        Check((await bridgeClient.ListModelsAsync()).Count == 2 && !handler.Authorized, "bridge client does not send gateway key");
        try { _ = new LiteLlmClient(http, new Uri("https://api.openai.com/v1"), "secret", "x"); throw new Exception("Direct provider URL accepted"); } catch (ArgumentException) { count++; }
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        try { await client.CompleteAsync(messages, [tool], cts.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { count++; }
        return count;
    }
}
sealed class StaticResponsesHandler(string body) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
}
sealed class GatewayHandler : HttpMessageHandler
{
    private int _calls;
    public bool Authorized { get; private set; }
    public bool HistoryMapped { get; private set; }
    public bool CountMapped { get; private set; }
    public bool IncludesEncryptedReasoning { get; private set; }
    public bool ModelSwitchMapped { get; private set; }
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
            var hasContinuation = root.GetProperty("messages").EnumerateArray().Any(item => item.GetProperty("content").ValueKind == JsonValueKind.String && item.GetProperty("content").GetString()!.Contains("opaque-test-data"));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(hasContinuation ? """{"total_tokens":27,"error":false}""" : """{"total_tokens":17,"error":false}""", Encoding.UTF8, "application/json") };
        }
        if (request.RequestUri.AbsolutePath != "/v1/responses") throw new Exception("Wrong generation endpoint");
        IncludesEncryptedReasoning = root.GetProperty("include")[0].GetString() == "reasoning.encrypted_content" && !root.GetProperty("store").GetBoolean();
        var wireName = root.GetProperty("tools")[0].GetProperty("name").GetString();
        string body;
        if (_calls++ == 0)
            body = JsonSerializer.Serialize(new { status = "completed", output = new object[] { new { type = "reasoning", id = "rs-1", encrypted_content = "opaque-test-data", summary = Array.Empty<object>() }, new { type = "function_call", call_id = "call-1", name = wireName, arguments = "{\"path\":\"file.txt\"}" } }, usage = new { input_tokens = 3, output_tokens = 2, total_tokens = 5 } });
        else
        {
            var items = root.GetProperty("input");
            if (_calls == 2)
                HistoryMapped = items[1].GetProperty("type").GetString() == "reasoning" && items[1].GetProperty("encrypted_content").GetString() == "opaque-test-data" && items[2].GetProperty("type").GetString() == "function_call" && items[2].GetProperty("name").GetString() == wireName && items[3].GetProperty("type").GetString() == "function_call_output" && items[3].GetProperty("call_id").GetString() == "call-1" && items[3].GetProperty("output").GetString() == "file contents";
            else
                ModelSwitchMapped = items.GetArrayLength() == 3 && items[1].GetProperty("type").GetString() == "function_call" && items[2].GetProperty("type").GetString() == "function_call_output";
            body = """{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"done"}]}]}""";
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
