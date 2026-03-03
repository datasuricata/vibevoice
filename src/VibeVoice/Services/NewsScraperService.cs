using HtmlAgilityPack;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VibeVoice.Services;

public record NewsItem(string Title, string Category, string? Url = null);

public class NewsScraperService(HttpClient httpClient, ILogger<NewsScraperService> logger)
{
    private const string ForbesUrl = "https://forbes.com.br/ultimas-noticias/";

    public async Task<IReadOnlyList<NewsItem>> GetLatestNewsAsync(
        int maxItems = 15,
        CancellationToken ct = default)
    {
        try
        {
            var html = await httpClient.GetStringAsync(ForbesUrl, ct);
            var items = ParseForbesHtml(html, maxItems);

            if (items.Count == 0)
                logger.LogWarning("No articles found on Forbes. HTML structure may have changed.");
            else
                logger.LogInformation("Found {Count} articles on Forbes.", items.Count);

            return items;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error scraping Forbes news.");
            return [];
        }
    }

    private static List<NewsItem> ParseForbesHtml(string html, int maxItems)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var results = new List<NewsItem>();

        results.AddRange(ParseJsonLd(doc));

        if (results.Count < 5)
            results.AddRange(ParseHeadings(doc));

        if (results.Count < 5)
            results.AddRange(ParseArticleLinks(doc));

        return results
            .DistinctBy(n => n.Title)
            .Where(n => n.Title.Length > 20)
            .Take(maxItems)
            .ToList();
    }

    private static IEnumerable<NewsItem> ParseJsonLd(HtmlDocument doc)
    {
        var scripts = doc.DocumentNode.SelectNodes("//script[@type='application/ld+json']");
        if (scripts is null) yield break;

        foreach (var script in scripts)
        {
            var json = script.InnerText.Trim();
            if (string.IsNullOrEmpty(json)) continue;

            JsonElement root;
            try { root = JsonSerializer.Deserialize<JsonElement>(json); }
            catch { continue; }

            if (root.TryGetProperty("@graph", out var graph) && graph.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in graph.EnumerateArray())
                    if (TryExtractLdItem(item, out var ni)) yield return ni!;
                continue;
            }

            if (TryExtractLdItem(root, out var single)) yield return single!;
        }
    }

    private static bool TryExtractLdItem(JsonElement item, out NewsItem? result)
    {
        result = null;
        var type = item.TryGetProperty("@type", out var t) ? t.GetString() ?? "" : "";
        if (!type.Contains("Article") && !type.Contains("NewsArticle")) return false;

        var headline = item.TryGetProperty("headline", out var h) ? h.GetString() : null;
        if (string.IsNullOrWhiteSpace(headline)) return false;

        var section = item.TryGetProperty("articleSection", out var s) ? s.GetString() ?? "Forbes" : "Forbes";
        var url = item.TryGetProperty("url", out var u) ? u.GetString() : null;

        result = new NewsItem(headline, section, url);
        return true;
    }

    private static IEnumerable<NewsItem> ParseHeadings(HtmlDocument doc)
    {
        string[] xpathSelectors =
        [
            "//h2[contains(@class,'card')]/a",
            "//h2[contains(@class,'title')]/a",
            "//h3[contains(@class,'card')]/a",
            "//h3[contains(@class,'title')]/a",
            "//article//h2/a",
            "//article//h3/a",
            "//h2[@class]/a",
            "//h2/a",
        ];

        foreach (var xpath in xpathSelectors)
        {
            var nodes = doc.DocumentNode.SelectNodes(xpath);
            if (nodes is null) continue;

            foreach (var node in nodes)
            {
                var title = CleanText(node.InnerText);
                var href = node.GetAttributeValue("href", "");
                if (title.Length > 20)
                    yield return new NewsItem(title, ExtractCategory(href), href);
            }
        }
    }

    private static IEnumerable<NewsItem> ParseArticleLinks(HtmlDocument doc)
    {
        var links = doc.DocumentNode.SelectNodes("//a[@href]");
        if (links is null) yield break;

        foreach (var link in links)
        {
            var text = CleanText(link.InnerText);
            var href = link.GetAttributeValue("href", "");

            if (text.Length < 40 || text.Length > 200) continue;
            if (!href.Contains("forbes.com.br")) continue;
            if (href.Contains("#") || href.Contains("categoria") || href.Contains("author")) continue;

            if (Regex.IsMatch(text, @"^[A-ZÀ-Ú]"))
                yield return new NewsItem(text, ExtractCategory(href), href);
        }
    }

    private static string CleanText(string raw) =>
        Regex.Replace(HtmlEntity.DeEntitize(raw ?? ""), @"\s+", " ").Trim();

    private static string ExtractCategory(string href)
    {
        var match = Regex.Match(href ?? "", @"forbes\.com\.br/([^/]+)/");
        return match.Success
            ? match.Groups[1].Value.ToUpperInvariant() switch
            {
                "TECH" => "Forbes Tech",
                "MONEY" => "Forbes Money",
                "CARREIRA" => "Forbes Career",
                "AGRO" => "Forbes Agro",
                "MULHER" => "Forbes Women",
                "LIFE" => "Forbes Life",
                "ESG" => "Forbes ESG",
                _ => "Forbes"
            }
            : "Forbes";
    }
}
