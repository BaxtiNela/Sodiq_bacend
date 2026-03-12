using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WA.Agent.Services;

/// <summary>
/// Internet access: DuckDuckGo qidiruv + URL o'qish.
/// API key kerak emas — HTML scraping orqali ishlaydi.
/// </summary>
public class WebSearchService
{
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
        DefaultRequestHeaders =
        {
            { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0.0.0 Safari/537.36" },
            { "Accept", "text/html,application/xhtml+xml" },
            { "Accept-Language", "en-US,en;q=0.9" }
        }
    };

    // ── Web Search (DuckDuckGo Lite) ──────────────────────────────────────────

    public async Task<string> SearchAsync(string query, int numResults = 5)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Err("Qidiruv so'zi bo'sh");

        try
        {
            var encoded = Uri.EscapeDataString(query);
            var url = $"https://lite.duckduckgo.com/lite/?q={encoded}";
            var html = await _http.GetStringAsync(url);

            var results = ParseDuckDuckGoResults(html, numResults);

            if (results.Count == 0)
            {
                // Fallback: Bing search
                results = await BingFallbackAsync(query, numResults);
            }

            if (results.Count == 0)
                return Ok($"'{query}' uchun natija topilmadi. Boshqacha kalit so'z bilan urinib ko'ring.");

            var sb = new StringBuilder();
            sb.AppendLine($"Qidiruv: \"{query}\" — {results.Count} ta natija:\n");
            for (int i = 0; i < results.Count; i++)
            {
                var r = results[i];
                sb.AppendLine($"{i + 1}. {r.Title}");
                sb.AppendLine($"   URL: {r.Url}");
                if (!string.IsNullOrWhiteSpace(r.Snippet))
                    sb.AppendLine($"   {r.Snippet}");
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return Err($"Qidiruv xato: {ex.Message}");
        }
    }

    private static List<SearchResult> ParseDuckDuckGoResults(string html, int max)
    {
        var results = new List<SearchResult>();

        // DuckDuckGo Lite: <a class="result-link" href="URL">Title</a>
        // snippet: next <td class="result-snippet">...</td>
        var linkPattern = new Regex(
            @"<a[^>]+class=""result-link""[^>]+href=""([^""]+)""[^>]*>(.*?)</a>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        var snippetPattern = new Regex(
            @"<td[^>]+class=""result-snippet""[^>]*>(.*?)</td>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        var linkMatches  = linkPattern.GetMatches(html);
        var snippetMatches = snippetPattern.GetMatches(html);

        for (int i = 0; i < Math.Min(linkMatches.Count, max); i++)
        {
            var url     = HtmlDecode(linkMatches[i].Groups[1].Value.Trim());
            var title   = StripTags(linkMatches[i].Groups[2].Value).Trim();
            var snippet = i < snippetMatches.Count
                ? StripTags(snippetMatches[i].Groups[1].Value).Trim()
                : "";

            if (string.IsNullOrWhiteSpace(url) || url.StartsWith("//duckduckgo")) continue;
            if (!url.StartsWith("http")) url = "https:" + url;

            results.Add(new SearchResult(title, url, snippet));
        }

        return results;
    }

    private async Task<List<SearchResult>> BingFallbackAsync(string query, int max)
    {
        try
        {
            var encoded = Uri.EscapeDataString(query);
            var html = await _http.GetStringAsync($"https://www.bing.com/search?q={encoded}&count={max}");

            var results = new List<SearchResult>();
            // Bing: <h2><a href="URL">Title</a></h2> ... <p class="b_lineclamp3 b_algoSlug">Snippet</p>
            var linkPattern = new Regex(
                @"<h2><a href=""(https?://[^""]+)""[^>]*>(.*?)</a></h2>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            foreach (Match m in linkPattern.Matches(html))
            {
                if (results.Count >= max) break;
                var url   = m.Groups[1].Value;
                var title = StripTags(m.Groups[2].Value).Trim();
                results.Add(new SearchResult(title, url, ""));
            }

            return results;
        }
        catch
        {
            return [];
        }
    }

    // ── URL Reader ────────────────────────────────────────────────────────────

    public async Task<string> ReadUrlAsync(string url, int maxChars = 5000)
    {
        if (string.IsNullOrWhiteSpace(url))
            return Err("URL bo'sh");

        if (!url.StartsWith("http://") && !url.StartsWith("https://"))
            url = "https://" + url;

        try
        {
            var html    = await _http.GetStringAsync(url);
            var text    = ExtractReadableText(html);
            var trimmed = text.Length > maxChars ? text[..maxChars] + "\n...[qisqartirildi]" : text;

            return $"URL: {url}\n\n{trimmed}";
        }
        catch (Exception ex)
        {
            return Err($"URL o'qish xato: {ex.Message}");
        }
    }

    // ── HTML processing ───────────────────────────────────────────────────────

    private static string ExtractReadableText(string html)
    {
        // Remove scripts, styles, nav, header, footer
        html = Regex.Replace(html, @"<(script|style|nav|header|footer|aside|noscript)[^>]*>.*?</\1>",
            "", RegexOptions.IgnoreCase | RegexOptions.Singleline);

        // Strip all remaining tags
        var text = StripTags(html);

        // Normalize whitespace
        text = Regex.Replace(text, @"[ \t]+", " ");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");

        // Decode HTML entities
        text = HtmlDecode(text);

        return text.Trim();
    }

    private static string StripTags(string html) =>
        Regex.Replace(html, @"<[^>]+>", " ");

    private static string HtmlDecode(string text) =>
        System.Net.WebUtility.HtmlDecode(text);

    private static string Ok(string msg) => msg;
    private static string Err(string msg) => $"[Xato] {msg}";

    private record SearchResult(string Title, string Url, string Snippet);
}

// Extension to avoid ambiguity with Match
internal static class RegexExtensions
{
    public static List<Match> GetMatches(this Regex regex, string input)
    {
        var list  = new List<Match>();
        var match = regex.Match(input);
        while (match.Success)
        {
            list.Add(match);
            match = match.NextMatch();
        }
        return list;
    }
}
