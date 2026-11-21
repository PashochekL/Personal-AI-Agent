using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml;
using AiAgent.Core;
using AiAgent.Shared;
using AiAgent.Tools.Web;

internal static class WebSearchChecks
{
    public static async Task<int> RunAsync()
    {
        var checks = 0;
        void Check(bool condition, string message) { checks++; if (!condition) throw new Exception("Web search: " + message); }
        const string rss = """<rss><channel><item><title>Useful &amp; current</title><link>https://example.org/page</link><description><![CDATA[<b>Example</b> snippet]]></description></item><item><title>Bad scheme</title><link>file:///etc/passwd</link></item><item><title>Credentials</title><link>https://user:password@example.org/</link></item></channel></rss>""";
        using var providerHandler = new SearchHandler(rss, HttpStatusCode.OK);
        using var providerHttp = new HttpClient(providerHandler);
        var provider = new SearchProvider(providerHttp);
        var result = await provider.SearchAsync("новости & данные", CancellationToken.None);
        Check(providerHandler.Uri?.Host == "www.bing.com" && providerHandler.Uri.Query.Contains("%26"), "fixed provider and escaped query");
        Check(result.Results.Count == 1 && result.Results[0].Url == "https://example.org/page" && result.Results[0].Snippet == "Example snippet", "results normalized and invalid links removed");
        try { await provider.SearchAsync("query\nheader", CancellationToken.None); throw new Exception("Unsafe query accepted"); } catch (ArgumentException) { checks++; }
        using var malformedHandler = new SearchHandler("<html>captcha</html>", HttpStatusCode.OK);
        using var malformedHttp = new HttpClient(malformedHandler);
        try { await new SearchProvider(malformedHttp).SearchAsync("test", CancellationToken.None); throw new Exception("HTML treated as RSS"); } catch (InvalidDataException) { checks++; }
        using var xxeHandler = new SearchHandler("<!DOCTYPE rss [<!ENTITY test SYSTEM 'file:///etc/passwd'>]><rss><channel>&test;</channel></rss>", HttpStatusCode.OK);
        using var xxeHttp = new HttpClient(xxeHandler);
        try { await new SearchProvider(xxeHttp).SearchAsync("test", CancellationToken.None); throw new Exception("DTD accepted"); } catch (XmlException) { checks++; }
        using var emptyHandler = new SearchHandler("<rss><channel></channel></rss>", HttpStatusCode.OK);
        using var emptyHttp = new HttpClient(emptyHandler);
        Check((await new SearchProvider(emptyHttp).SearchAsync("test", CancellationToken.None)).Results.Count == 0, "empty results kept distinct from errors");

        using var serviceHandler = new SearchHandler("""{"query":"test","results":[{"title":"Example","url":"https://example.org","snippet":"source"}]}""", HttpStatusCode.OK);
        using var serviceHttp = new HttpClient(serviceHandler);
        var tool = new WebSearchTool(serviceHttp, new Uri("http://web-search:8080"));
        var args = new Dictionary<string, JsonElement> { ["query"] = JsonSerializer.SerializeToElement("test") };
        var toolResult = await tool.ExecuteAsync(args);
        Check(toolResult.Success && toolResult.Content.Contains("https://example.org") && toolResult.Content.Contains("untrusted"), "tool returns sourced untrusted data");
        Check(!(await tool.ExecuteAsync(new Dictionary<string, JsonElement>())).Success, "invalid arguments rejected");
        using var failedHandler = new SearchHandler("", HttpStatusCode.BadGateway);
        using var failedHttp = new HttpClient(failedHandler);
        Check(!(await new WebSearchTool(failedHttp, new Uri("http://web-search:8080")).ExecuteAsync(args)).Success, "service errors returned to agent");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { await tool.ExecuteAsync(args, cancelled.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { checks++; }
        var call = new ToolCall { Id = "search-1", Name = "web.search", Arguments = args };
        var model = new Model(new LlmResponse { ToolCalls = [call] }, new LlmResponse { Content = "answer" });
        var store = new Store();
        var registry = new ToolRegistry();
        registry.Register(tool);
        var agentResult = await new Agent(model, store, registry).RunAsync("s", "find information");
        Check(agentResult.Status == AiAgent.Shared.TaskStatus.COMPLETED && store.Messages.Any(x => x.Role == "tool" && x.Content!.Contains("example.org")), "agent loop receives search results");
        return checks;
    }

    private sealed class SearchHandler(string body, HttpStatusCode status) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Uri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8) });
        }
    }
}
