using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AiAgent.Shared;

namespace AiAgent.Gateway;

/// <summary>Embeddings always go through LiteLLM, including locally hosted Ollama models.</summary>
public sealed class LiteLlmEmbeddingClient : IEmbeddingClient
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string? _apiKey;
    private readonly string _deployment;
    public string Model { get; }
    public int Dimensions { get; }

    public LiteLlmEmbeddingClient(HttpClient http, Uri proxyBaseUrl, string? apiKey, string deployment,
        string model, int dimensions = 768)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(proxyBaseUrl);
        if (!proxyBaseUrl.IsAbsoluteUri || proxyBaseUrl.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(proxyBaseUrl.UserInfo)
            || proxyBaseUrl.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("A LiteLLM proxy URL is required");
        ArgumentException.ThrowIfNullOrWhiteSpace(deployment);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (model.Length > 256 || dimensions is < 1 or > 4096) throw new ArgumentException("Invalid embedding configuration");
        if (apiKey is not null) ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        _http = http;
        _endpoint = new Uri(proxyBaseUrl.ToString().TrimEnd('/') + "/v1/embeddings");
        _apiKey = apiKey;
        _deployment = deployment;
        Model = model;
        Dimensions = dimensions;
    }

    public async Task<Embedding> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (text.Length > 4000) throw new ArgumentException("Embedding input is too long", nameof(text));
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = JsonContent.Create(new { model = _deployment, input = text })
            };
            if (_apiKey is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) throw new MemoryError($"Embedding gateway returned HTTP {(int)response.StatusCode}");
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var data = json.RootElement.GetProperty("data");
            if (data.GetArrayLength() != 1 || data[0].GetProperty("index").GetInt32() != 0) throw new MemoryError("Invalid embedding response");
            var values = data[0].GetProperty("embedding").EnumerateArray().Select(x => x.GetSingle()).ToList();
            var result = new Embedding { Model = Model, Values = values };
            result.Validate();
            if (values.Count != Dimensions) throw new MemoryError("Embedding dimension mismatch");
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (MemoryError) { throw; }
        catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or FormatException or OverflowException or OperationCanceledException)
        { throw new MemoryError("Could not generate an embedding"); }
    }
}
