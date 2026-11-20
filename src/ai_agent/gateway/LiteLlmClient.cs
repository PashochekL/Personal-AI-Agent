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
        _endpoint = new Uri(proxyBaseUrl.ToString().TrimEnd('/') + "/chat/completions");
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

    public async Task<int> CountTokensAsync(IReadOnlyList<Message> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _countEndpoint) { Content = JsonContent.Create(new { model = _model, messages = WireMessages(messages), tools = WireTools(tools) }) };
            if (_apiKey is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) throw new AgentError($"Token counter returned HTTP {(int)response.StatusCode}");
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var root = json.RootElement;
            if ((root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.True) || !root.TryGetProperty("total_tokens", out var value) || !value.TryGetInt32(out var count) || count < 0)
                throw new AgentError("Token counter returned invalid data");
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
            var wireMessages = WireMessages(messages);
            var payload = new Dictionary<string, object?> { ["model"] = _model, ["messages"] = wireMessages };
            if (tools.Count > 0) payload["tools"] = WireTools(tools);
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = JsonContent.Create(payload) };
            if (_apiKey is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) throw new AgentError($"LLM gateway returned HTTP {(int)response.StatusCode}");
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var root = json.RootElement;
            var choice = root.GetProperty("choices")[0];
            var message = choice.GetProperty("message");
            var calls = new List<ToolCall>();
            if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                foreach (var call in toolCalls.EnumerateArray())
                {
                    var fn = call.GetProperty("function");
                    var wireName = fn.GetProperty("name").GetString() ?? "";
                    var name = names.TryGetValue(wireName, out var original) ? original : wireName;
                    var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(fn.GetProperty("arguments").GetString() ?? "{}") ?? throw new JsonException();
                    calls.Add(new ToolCall { Id = call.GetProperty("id").GetString()!, Name = name, Arguments = args });
                }
            var reason = choice.TryGetProperty("finish_reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            reason = reason is null or "stop" or "tool_calls" or "length" or "error" ? reason : "unknown";
            TokenUsage? usage = null;
            if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                int? Count(string name) => u.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n >= 0 ? n : null;
                usage = new TokenUsage { InputTokens = Count("prompt_tokens"), OutputTokens = Count("completion_tokens"), TotalTokens = Count("total_tokens") };
            }
            var result = new LlmResponse { Content = message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null, ToolCalls = calls, FinishReason = reason, Usage = usage };
            result.Validate();
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (AgentError) { throw; }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or HttpRequestException or ArgumentException or OperationCanceledException)
        { throw new AgentError("LLM gateway request failed", e); }
    }
}
