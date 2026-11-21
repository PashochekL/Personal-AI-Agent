using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

internal sealed record SearchItem(string Title, string Url, string Snippet);
internal sealed record SearchResponse(string Query, IReadOnlyList<SearchItem> Results);

/// <summary>Searches Bing's RSS results without accepting arbitrary outgoing URLs.</summary>
internal sealed class SearchProvider(HttpClient http)
{
    public async Task<SearchResponse> SearchAsync(string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 500 || query.Any(char.IsControl))
            throw new ArgumentException("Invalid search query", nameof(query));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var url = new Uri("https://www.bing.com/search?format=rss&q=" + Uri.EscapeDataString(query));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("DamirAssistant/1.0 (+https://github.com/PashochekL/Personal-AI-Agent)");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 1_048_576) throw new InvalidDataException("Search response too large");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, timeout.Token)) > 0)
        {
            if (memory.Length + count > 1_048_576) throw new InvalidDataException("Search response too large");
            await memory.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
        }
        memory.Position = 0;
        using var reader = XmlReader.Create(memory, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1_048_576 });
        var xml = XDocument.Load(reader);
        if (xml.Root?.Name.LocalName != "rss" || xml.Root.Element("channel") is not { } channel)
            throw new InvalidDataException("Invalid search response");
        var results = new List<SearchItem>();
        foreach (var item in channel.Elements("item"))
        {
            if (results.Count >= 8) break;
            var address = item.Element("link")?.Value.Trim();
            if (!Uri.TryCreate(address, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(parsed.UserInfo)) continue;
            var title = Clean(item.Element("title")?.Value, 300);
            if (title.Length == 0) continue;
            results.Add(new SearchItem(title, parsed.AbsoluteUri, Clean(item.Element("description")?.Value, 1000)));
        }
        return new SearchResponse(query, results);
    }

    private static string Clean(string? value, int max)
    {
        var text = WebUtility.HtmlDecode(value ?? "");
        text = Regex.Replace(text, "<[^>]{0,512}>", " ", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        text = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var clean = new string(text.Where(c => !char.IsControl(c)).ToArray());
        return clean.Length <= max ? clean : clean[..max];
    }
}
