using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace Lait.Umbraco.Awesome.SEO.Crawling;

public static partial class PageParser
{
    public static ParsedHtml Parse(string html, string pageUrl)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var root = doc.DocumentNode;
        var https = pageUrl.StartsWith("https:", StringComparison.OrdinalIgnoreCase);
        var host = new Uri(pageUrl).Host;

        string? Meta(string name) => root.SelectSingleNode($"//meta[translate(@name,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz')='{name}']")
            ?.GetAttributeValue("content", null)?.Trim();

        var headings = root.SelectNodes("//h1|//h2|//h3|//h4|//h5|//h6")?.ToList() ?? [];

        var links = new List<Link>();
        foreach (var a in root.SelectNodes("//a[@href]")?.ToList() ?? [])
        {
            var href = UrlNormalizer.Normalize(HtmlEntity.DeEntitize(a.GetAttributeValue("href", "")), pageUrl);
            if (href is null || IsCdnInternal(href)) continue;
            var anchor = Clean(a);
            if (string.IsNullOrEmpty(anchor)) anchor = a.SelectSingleNode(".//img[@alt]")?.GetAttributeValue("alt", null)?.Trim() ?? a.GetAttributeValue("aria-label", "").Trim();
            links.Add(new Link(href, anchor, new Uri(href).Host.Equals(host, StringComparison.OrdinalIgnoreCase), RegionOf(a)));
        }

        var noAlt = root.SelectNodes("//img[not(@alt)]")?.ToList() ?? [];

        var mixed = https
            ? (root.SelectNodes("//img[@src]|//script[@src]|//iframe[@src]|//source[@src]|//audio[@src]|//video[@src]|//link[@rel='stylesheet'][@href]")
                ?.Count(n => (n.GetAttributeValue("src", null) ?? n.GetAttributeValue("href", ""))
                    .StartsWith("http://", StringComparison.OrdinalIgnoreCase)) ?? 0)
            : 0;

        // Body text without chrome/scripts, for word count and duplicate-content hash.
        var body = root.SelectSingleNode("//body") ?? root;
        var clone = HtmlNode.CreateNode("<div>" + body.InnerHtml + "</div>");   // one root, even for body-less fragments
        foreach (var n in clone.SelectNodes(".//script|.//style|.//noscript|.//nav|.//header|.//footer")?.ToList() ?? [])
            n.Remove();
        // Join text nodes with a space: InnerText glues adjacent elements together ("</h2><p>" would merge two words into one).
        var text = Ws().Replace(HtmlEntity.DeEntitize(string.Join(" ", clone.DescendantsAndSelf().Where(n => n.NodeType == HtmlNodeType.Text).Select(n => n.InnerText))), " ").Trim().ToLowerInvariant();

        var ldBlocks = root.SelectNodes("//script[@type='application/ld+json']")?.Select(n => n.InnerText).ToList() ?? [];
        var jsonLd = string.Concat(ldBlocks.Select(b => Ws().Replace(b, "")));
        var (types, invalid) = SchemaTypesOf(ldBlocks);
        string? Property(string name) => root.SelectSingleNode($"//meta[@property='{name}']")?.GetAttributeValue("content", null)?.Trim();

        var canonicalHref = root.SelectSingleNode("//link[translate(@rel,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz')='canonical']")
            ?.GetAttributeValue("href", null);

        return new ParsedHtml
        {
            Title = Clean(root.SelectSingleNode("//title")),
            MetaDescription = Meta("description"),
            Canonical = string.IsNullOrWhiteSpace(canonicalHref) ? null : UrlNormalizer.Normalize(HtmlEntity.DeEntitize(canonicalHref), pageUrl),
            RobotsMeta = Meta("robots"),
            Lang = root.SelectSingleNode("//html")?.GetAttributeValue("lang", null)?.Trim(),
            HasViewport = Meta("viewport") is not null,
            HeadingLevels = headings.Select(h => h.Name[1] - '0').ToList(),
            H1s = headings.Where(h => h.Name == "h1").Select(h => Clean(h) ?? "").ToList(),
            ImagesMissingAlt = noAlt.Count,
            ImagesWithoutAlt = noAlt
                .Select(i => new ImageRef(ImageSrc(i, pageUrl), RegionOf(i)))
                .DistinctBy(i => i.Src).Take(100).ToList(),
            Links = links,
            MixedContentCount = mixed,
            EmptyAppRoot = root.SelectNodes("//*[@id='root' or @id='app' or @id='__next' or @id='__nuxt']")
                ?.Any(n => string.IsNullOrWhiteSpace(n.InnerText) && !n.Descendants("a").Any()) ?? false,
            WordCount = text.Length == 0 ? 0 : text.Split(' ').Length,
            ContentHash = Hash(text),
            JsonLdHash = jsonLd.Length == 0 ? "" : Hash(jsonLd),
            SchemaTypes = types,
            JsonLdInvalid = invalid,
            OgTitle = Property("og:title"),
            OgImage = Property("og:image"),
            HasTwitterCard = Meta("twitter:card") is not null,
        };
    }

    static (List<string> Types, bool Invalid) SchemaTypesOf(List<string> blocks)
    {
        var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var invalid = false;
        foreach (var b in blocks)
        {
            try
            {
                using var doc = JsonDocument.Parse(HtmlEntity.DeEntitize(b), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 32 });
                Collect(doc.RootElement, 0);
            }
            catch (JsonException) { invalid = true; }
        }
        return (types.ToList(), invalid);

        void Collect(JsonElement e, int depth)
        {
            if (depth > 6) return;   // nested enough for @graph/mainEntity/author; deeper is rarely a page-level type
            if (e.ValueKind == JsonValueKind.Array) { foreach (var x in e.EnumerateArray()) Collect(x, depth); return; }
            if (e.ValueKind != JsonValueKind.Object) return;
            foreach (var p in e.EnumerateObject())
            {
                if (p.Name == "@type")
                {
                    if (p.Value.ValueKind == JsonValueKind.String) types.Add(p.Value.GetString()!);
                    else if (p.Value.ValueKind == JsonValueKind.Array) foreach (var t in p.Value.EnumerateArray()) if (t.ValueKind == JsonValueKind.String) types.Add(t.GetString()!);
                }
                else if (p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) Collect(p.Value, depth + 1);
            }
        }
    }

    /// <summary>Cloudflare rewrites mailto links to /cdn-cgi/l/email-protection (decoded by script in the browser); these are not real pages.</summary>
    static bool IsCdnInternal(string href) => new Uri(href).AbsolutePath.StartsWith("/cdn-cgi/", StringComparison.OrdinalIgnoreCase);

    /// <summary>head when inside &lt;head&gt; (e.g. a tracking pixel in noscript); menu / header / footer when the element sits inside such a landmark (usually shared template), otherwise indhold.</summary>
    static string RegionOf(HtmlNode n)
    {
        for (var p = n.ParentNode; p is not null && p.NodeType == HtmlNodeType.Element; p = p.ParentNode)
        {
            if (p.Name == "head") return "head";
            var role = p.GetAttributeValue("role", "");
            if (p.Name == "nav" || role == "navigation") return "menu";
            if (p.Name == "header" || role == "banner") return "header";
            if (p.Name == "footer" || role == "contentinfo") return "footer";
        }
        return "indhold";
    }

    static string ImageSrc(HtmlNode img, string pageUrl)
    {
        var raw = img.GetAttributeValue("src", null) ?? img.GetAttributeValue("data-src", null)
                  ?? img.GetAttributeValue("srcset", "").Split(',', ' ').FirstOrDefault(s => s.Length > 0) ?? "";
        raw = HtmlEntity.DeEntitize(raw.Trim());
        if (raw.Length == 0) return "(uden src)";
        if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return "(indlejret billede)";
        return Uri.TryCreate(new Uri(pageUrl), raw, out var u) ? u.GetLeftPart(UriPartial.Path) : raw;
    }

    static string? Clean(HtmlNode? n) => n is null ? null : Ws().Replace(HtmlEntity.DeEntitize(n.InnerText), " ").Trim();
    static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    [GeneratedRegex(@"\s+")] private static partial Regex Ws();
}
