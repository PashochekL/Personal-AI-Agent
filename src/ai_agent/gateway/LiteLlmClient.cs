using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiAgent.Shared;

namespace AiAgent.Gateway;

/// <summary>OpenAI-compatible LiteLLM proxy adapter. Never calls a provider directly.</summary>
public sealed class LiteLlmClient : ILlmClient
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly Uri _modelsEndpoint;
    private readonly Uri _countEndpoint;
    private readonly Uri _modelInfoEndpoint;
    private readonly string? _apiKey;
    private readonly string _model;

    public LiteLlmClient(HttpClient http, Uri proxyBaseUrl, string? apiKey, string model)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(proxyBaseUrl);
        if (!proxyBaseUrl.IsAbsoluteUri || proxyBaseUrl.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(proxyBaseUrl.UserInfo)) throw new ArgumentException("Invalid proxy URL");
        if (proxyBaseUrl.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("A LiteLLM proxy URL is required");
        if (apiKey is not null) ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        _http = http;
        _endpoint = new Uri(proxyBaseUrl.ToString().TrimEnd('/') + "/v1/responses");
        _modelsEndpoint = new Uri(proxyBaseUrl.ToString().TrimEnd('/') + "/models");
        _countEndpoint = new Uri(proxyBaseUrl.ToString().TrimEnd('/') + "/utils/token_counter");
        _modelInfoEndpoint = new Uri(proxyBaseUrl.ToString().TrimEnd('/') + "/utils/model_info?model=" + Uri.EscapeDataString(model));
        _apiKey = apiKey;
        _model = model;
    }

    private static string WireName(string name) => "tool_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name))).ToLowerInvariant()[..32];

    private static object[] WireMessages(IReadOnlyList<Message> messages) => messages.Select(m =>
    {
        m.Validate();
        var item = new Dictionary<string, object?> { ["role"] = m.Role, ["content"] = m.Content };
        if (m.ToolCallId is not null) item["tool_call_id"] = m.ToolCallId;
        if (m.ToolCalls.Count > 0) item["tool_calls"] = m.ToolCalls.Select(c => new { id = c.Id, type = "function", function = new { name = WireName(c.Name), arguments = JsonSerializer.Serialize(c.Arguments) } }).ToArray();
        return (object)item;
    }).ToArray();

    private static object[] WireTools(IReadOnlyList<ToolDefinition> tools) => tools.Select(t => (object)new { type = "function", function = new { name = WireName(t.Name), description = t.Description, parameters = t.Parameters } }).ToArray();

    private const string ContinuationProvider = "litellm-responses-v1";

    private bool TryContinuation(Message message, out JsonElement items)
    {
        items = default;
        if (message.Role != "assistant" || message.Continuation is not JsonElement state ||
            !state.TryGetProperty("provider", out var provider) || provider.ValueKind != JsonValueKind.String || provider.GetString() != ContinuationProvider ||
            !state.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.String || model.GetString() != _model ||
            !state.TryGetProperty("items", out items) || items.ValueKind != JsonValueKind.Array) return false;
        var calls = new List<(string? Id, string? Name, string? Arguments)>();
        var text = new StringBuilder();
        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) return false;
            switch (type.GetString())
            {
                case "reasoning":
                    // The gateway uses store=false: an item without encrypted
                    // content cannot safely be resumed in a later request.
                    if (!item.TryGetProperty("encrypted_content", out var encrypted) || encrypted.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(encrypted.GetString())) return false;
                    break;
                case "function_call":
                    if (!item.TryGetProperty("call_id", out var id) || id.ValueKind != JsonValueKind.String || !item.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || !item.TryGetProperty("arguments", out var args) || args.ValueKind != JsonValueKind.String) return false;
                    calls.Add((id.GetString(), name.GetString(), args.GetString()));
                    break;
                case "message":
                    if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return false;
                    foreach (var part in content.EnumerateArray())
                    {
                        if (!part.TryGetProperty("type", out var partType) || partType.ValueKind != JsonValueKind.String) return false;
                        var field = partType.GetString() switch { "output_text" => "text", "refusal" => "refusal", _ => null };
                        if (field is null || !part.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String) return false;
                        text.Append(value.GetString());
                    }
                    break;
                default: return false;
            }
        }
        if (calls.Count != message.ToolCalls.Count || (text.Length > 0 ? text.ToString() : null) != message.Content) return false;
        for (var i = 0; i < calls.Count; i++)
        {
            var call = message.ToolCalls[i];
            if (calls[i].Id != call.Id || calls[i].Name != WireName(call.Name)) return false;
            try
            {
                if (!System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(calls[i].Arguments!), JsonSerializer.SerializeToNode(call.Arguments))) return false;
            }
            catch (JsonException) { return false; }
        }
        return true;
    }

    private object[] ResponseInput(IReadOnlyList<Message> messages)
    {
        var result = new List<object>();
        foreach (var message in messages)
        {
            message.Validate();
            if (TryContinuation(message, out var items))
                result.AddRange(items.EnumerateArray().Select(item => (object)item.Clone()));
            else if (message.Role == "tool")
                result.Add(new { type = "function_call_output", call_id = message.ToolCallId, output = message.Content });
            else
            {
                if (message.Content is not null) result.Add(new { role = message.Role, content = message.Content });
                foreach (var call in message.ToolCalls)
                    result.Add(new { type = "function_call", call_id = call.Id, name = WireName(call.Name), arguments = JsonSerializer.Serialize(call.Arguments) });
            }
        }
        return result.ToArray();
    }

    private static object[] ResponseTools(IReadOnlyList<ToolDefinition> tools) =>
        tools.Select(t => (object)new { type = "function", name = WireName(t.Name), description = t.Description, parameters = t.Parameters }).ToArray();

    public async Task<int> CountTokensAsync(IReadOnlyList<Message> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken = default)
    {
        try
        {
            var countingMessages = WireMessages(messages).ToList();
            foreach (var message in messages)
                if (TryContinuation(message, out var items))
                {
                    var reasoning = items.EnumerateArray().Where(item => item.GetProperty("type").GetString() == "reasoning").Select(item => item.GetRawText()).ToArray();
                    if (reasoning.Length > 0) countingMessages.Add(new { role = "user", content = string.Join("\n", reasoning) });
                }
            using var request = new HttpRequestMessage(HttpMethod.Post, _countEndpoint) { Content = JsonContent.Create(new { model = _model, messages = countingMessages, tools = WireTools(tools) }) };
            if (_apiKey is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) throw new AgentError($"Token counter returned HTTP {(int)response.StatusCode}");
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var root = json.RootElement;
            if ((root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.True) || !root.TryGetProperty("total_tokens", out var value) || !value.TryGetInt32(out var count) || count < 0)
                throw new AgentError("Token counter returned invalid data");
            // The encrypted representation can tokenize differently from the
            // underlying reasoning. The counter remains an estimate.
            return count;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (AgentError) { throw; }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException or OperationCanceledException)
        { throw new AgentError("Token counter failed", e); }
    }

    public async Task<int?> GetInputLimitAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _modelInfoEndpoint);
            if (_apiKey is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            return json.RootElement.TryGetProperty("model_info", out var info) && info.TryGetProperty("max_input_tokens", out var value) && value.TryGetInt32(out var limit) && limit > 0 ? limit : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException or OperationCanceledException) { return null; }
    }

    /// <summary>Returns model IDs exposed by this proxy, not the complete provider catalog.</summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _modelsEndpoint);
            if (_apiKey is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) throw new AgentError($"LLM gateway returned HTTP {(int)response.StatusCode}");
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var models = new List<string>();
            foreach (var item in json.RootElement.GetProperty("data").EnumerateArray())
            {
                var id = item.GetProperty("id").GetString();
                if (!string.IsNullOrWhiteSpace(id) && !models.Contains(id, StringComparer.Ordinal)) models.Add(id);
            }
            return models;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (AgentError) { throw; }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or HttpRequestException or OperationCanceledException)
        { throw new AgentError("Could not list gateway models", e); }
    }

    public async Task<LlmResponse> CompleteAsync(IReadOnlyList<Message> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken = default)
    {
        try
        {
            var names = tools.ToDictionary(t => WireName(t.Name), t => t.Name, StringComparer.Ordinal);
            var payload = new Dictionary<string, object?> { ["model"] = _model, ["input"] = ResponseInput(messages), ["store"] = false, ["include"] = new[] { "reasoning.encrypted_content" } };
            if (tools.Count > 0) payload["tools"] = ResponseTools(tools);
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = JsonContent.Create(payload) };
            if (_apiKey is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) throw new AgentError($"LLM gateway returned HTTP {(int)response.StatusCode}");
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var root = json.RootElement;
            var calls = new List<ToolCall>();
            var text = new StringBuilder();
            var output = root.GetProperty("output");
            foreach (var item in output.EnumerateArray())
            {
                var type = item.GetProperty("type").GetString();
                if (type == "function_call")
                {
                    var wireName = item.GetProperty("name").GetString() ?? "";
                    var name = names.TryGetValue(wireName, out var original) ? original : wireName;
                    var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(item.GetProperty("arguments").GetString() ?? "{}") ?? throw new JsonException();
                    calls.Add(new ToolCall { Id = item.GetProperty("call_id").GetString()!, Name = name, Arguments = args });
                }
                else if (type == "reasoning") { }
                else if (type == "message")
                    foreach (var part in item.GetProperty("content").EnumerateArray())
                    {
                        var partType = part.GetProperty("type").GetString();
                        if (partType == "output_text") text.Append(part.GetProperty("text").GetString());
                        else if (partType == "refusal") text.Append(part.GetProperty("refusal").GetString());
                        else throw new AgentError("Unsupported Responses message content");
                    }
                else throw new AgentError("Unsupported Responses output type");
            }
            var status = root.GetProperty("status").GetString();
            var reason = status switch { "completed" => calls.Count > 0 ? "tool_calls" : "stop", "incomplete" => "length", _ => "error" };
            TokenUsage? usage = null;
            if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                int? Count(string name) => u.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n >= 0 ? n : null;
                usage = new TokenUsage { InputTokens = Count("input_tokens"), OutputTokens = Count("output_tokens"), TotalTokens = Count("total_tokens") };
            }
            JsonElement? continuation = status == "completed" && output.GetArrayLength() > 0 ? JsonSerializer.SerializeToElement(new { provider = ContinuationProvider, model = _model, items = output.Clone() }) : null;
            var result = new LlmResponse { Content = text.Length > 0 ? text.ToString() : null, ToolCalls = calls, FinishReason = reason, Usage = usage, Continuation = continuation };
            result.Validate();
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (AgentError) { throw; }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or HttpRequestException or ArgumentException or OperationCanceledException)
        { throw new AgentError("LLM gateway request failed", e); }
    }
}
