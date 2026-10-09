using System.Text.RegularExpressions;
using Lait.Umbraco.Awesome.SEO.Crawling;

namespace Lait.Umbraco.Awesome.SEO.Rules;

/// <summary>v1 rule set, see "Umbraco SEO Report - Regelkatalog". Not yet: ONP-15/16 (need focus keywords from the UI), all v2 rules.</summary>
public static partial class RuleCatalog
{
    static readonly string[] GenericTitles = ["home", "untitled", "forside", "hjem", "ny side", "new page", "welcome", "velkommen", "index"];
    static readonly string[] GenericAnchors = ["klik her", "læs mere", "læs mere her", "her", "se mere", "mere", "read more", "click here", "more", "learn more"];

    public static IReadOnlyList<Rule> All { get; } =
    [
        // A. Crawl og indeksering
        Pages("CRA-01", Category.Crawl, Severity.Critical, "Serverfejl (5xx)", "Find og ret fejlen på serveren; siden svarer med en 5xx-status.",
            p => p.Status >= 500, p => $"HTTP {p.Status}"),
        Pages("CRA-02", Category.Crawl, Severity.Warning, "Brudt side (4xx)", "Gendan siden, eller redirect (301) til en relevant side.",
            p => p.Status is >= 400 and < 500, p => $"HTTP {p.Status}"),
        Page("CRA-03", Category.Crawl, Severity.Critical, "Brudt internt link", "Ret eller fjern linkene så de peger på en side der svarer 200. Ligger linket i menu, header eller footer, rettes det ét sted i skabelonen.",
            (p, c) =>
            {
                // One line per broken target: "<url> → HTTP <status> · linktekst "<text>" · i <region>" (the dashboard parses this format).
                var broken = p.Html.Links.Where(l => l.Internal && c.Resolve(l.Href) is { } k && c.Pages[k].Status >= 400)
                    .GroupBy(l => l.Href)
                    .Select(g =>
                    {
                        var status = c.Pages[c.Resolve(g.Key)!].Status;
                        var text = g.Select(l => l.Text).FirstOrDefault(t => t.Length > 0);
                        return $"{g.Key} → HTTP {status}{(text is null ? "" : $" · linktekst \"{Short(text)}\"")} · i {string.Join("/", g.Select(l => l.Region).Distinct())}";
                    }).ToList();
                return broken.Count == 0 ? null : Lines(broken);
            }),
        Pages("CRA-04", Category.Crawl, Severity.Warning, "Redirect-kæde", "Peg linket direkte på den endelige URL så der kun er ét redirect.",
            p => p.Redirects > 0, p => $"{p.Redirects} redirects: {p.Url} → {p.FinalUrl}", (p, c) => p.Redirects >= c.For(p.Url).RedirectChain && !p.RedirectLoop),
        Pages("CRA-05", Category.Crawl, Severity.Warning, "Redirect-loop", "Ret redirect-reglerne så de ender på en side der svarer 200.",
            p => p.RedirectLoop, p => $"Loop fra {p.Url}"),
        new Rule("CRA-06", Category.Crawl, Severity.Info, "Noindex", "Fjern noindex hvis siden skal findes i søgemaskiner. Kritisk hvis siden står i sitemap.",
            c => c.Pages.Values.Where(p => p.IsOk && p.Noindex)
                .Select(p => new Hit(p.Url, "noindex", c.SitemapUrls.Contains(p.Url) ? Severity.Critical : null))),
        Page("CRA-07", Category.Crawl, Severity.Warning, "Forældreløs side (orphan)", "Link til siden fra relevante sider, eller fjern den hvis den ikke skal bruges.",
            (p, c) => !c.Truncated && p.Url != c.StartKey && !c.IsRedirectTarget(p.Url) && c.Inbound(p.Url) == 0 ? "Ingen interne links peger hertil" : null),
        Page("CRA-08", Category.Crawl, Severity.Info, "Dyb side", "Flyt siden tættere på forsiden eller link til den fra højere niveauer.",
            (p, c) => c.Depth(p.Url) is { } d && d >= c.For(p.Url).DeepClicks ? $"{d} klik fra forsiden" : null),
        new Rule("CRA-09", Category.Crawl, Severity.Critical, "robots.txt blokerer vigtig side", "Fjern Disallow-reglen, eller fjern siden fra sitemap.",
            c => c.SitemapUrls.Where(c.BlockedByRobots.Contains).Select(u => new Hit(u, "Står i sitemap men er blokeret i robots.txt"))),
        new Rule("CRA-10", Category.Crawl, Severity.Warning, "Sitemap-problemer", "Sitemap skal findes, stå i robots.txt og kun indeholde indekserbare 200-URL'er med sig selv som canonical.", Sitemap),
        Page("CRA-11", Category.Crawl, Severity.Info, "Side mangler i sitemap", "Tilføj siden til sitemap, så søgemaskiner finder den hurtigere (eller sæt noindex, hvis den ikke skal findes).",
            (p, c) => c.SitemapFound && p.IsCanonicalPage && !c.SitemapUrls.Contains(p.Url) && !c.SitemapUrls.Contains(p.FinalUrl) ? "Siden er indekserbar, men står ikke i sitemap" : null),

        // B. Teknisk
        Page("TEC-01", Category.Technical, Severity.Warning, "Canonical mangler", "Tilføj <link rel=\"canonical\"> med sidens egen URL.",
            (p, _) => p.Html.Canonical is null ? "Ingen canonical" : null),
        Page("TEC-02", Category.Technical, Severity.Info, "Canonical peger et andet sted hen", "Kontrollér at det er tilsigtet at siden er canonicalized til en anden URL.",
            (p, _) => p.Html.Canonical is not null && !p.CanonicalIsSelf ? $"Canonical: {p.Html.Canonical}" : null),
        Page("TEC-03", Category.Technical, Severity.Warning, "Canonical i HTML ≠ Link-header", "Sørg for at HTML og HTTP-header angiver samme canonical.",
            (p, _) => HeaderCanonical(p) is { } h && p.Html.Canonical is { } c && !UrlNormalizer.Same(h, c) ? $"HTML: {c} · header: {h}" : null),
        Page("TEC-04", Category.Technical, Severity.Critical, "Ikke HTTPS / mixed content", "Server siden over HTTPS og ret ressourcer til https://.",
            (p, _) => p.Url.StartsWith("http:") ? "Siden leveres over HTTP"
                : p.Html.MixedContentCount > 0 ? $"{p.Html.MixedContentCount} ressourcer hentes over http://" : null),
        new Rule("TEC-05", Category.Technical, Severity.Info, "Sikkerhedsheadere mangler", "Tilføj HSTS, CSP, X-Frame-Options, X-Content-Type-Options og Referrer-Policy.", SecurityHeaders),
        Page("TEC-06", Category.Technical, Severity.Warning, "Viewport-meta mangler", "Tilføj <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">.",
            (p, _) => p.Html.HasViewport ? null : "Ingen viewport-meta"),
        Page("TEC-07", Category.Technical, Severity.Warning, "<html lang> mangler", "Sæt lang-attributten på <html>, fx lang=\"da\".",
            (p, _) => string.IsNullOrWhiteSpace(p.Html.Lang) ? "Ingen lang-attribut" : null),
        Page("TEC-08", Category.Technical, Severity.Info, "Langsom svartid (TTFB)", "Optimér server, cache og databasekald for siden.",
            (p, c) => p.TtfbMs > c.For(p.Url).SlowTtfbMs ? $"{p.TtfbMs} ms" : null),
        Page("TEC-11", Category.Technical, Severity.Warning, "Mistænkt JS-shell", "Sørg for at hovedindholdet er i det serverrenderede HTML.",
            (p, _) => p.Html.WordCount < 20 && p.Html.HeadingLevels.Count == 0 && (p.Html.EmptyAppRoot || p.Html.Links.Count == 0)
                ? $"{p.Html.WordCount} ord, ingen overskrifter" : null),

        Page("TEC-12", Category.Technical, Severity.Warning, "Ugyldig struktureret data (JSON-LD)", "Ret JSON-LD-blokken, så den er gyldig JSON (test med Googles Rich Results Test).",
            (p, _) => p.Html.JsonLdInvalid ? "Mindst én JSON-LD-blok kan ikke læses" : null),

        // C. On-page
        Page("ONP-01", Category.OnPage, Severity.Critical, "Title mangler", "Tilføj en unik <title>.",
            (p, _) => Blank(p.Html.Title) ? "Ingen title" : null),
        Page("ONP-02", Category.OnPage, Severity.Info, "Title for kort eller lang", "Hold titlen inden for tærsklen.",
            (p, c) => !Blank(p.Html.Title) && Outside(p.Html.Title!.Length, c.For(p.Url).TitleMin, c.For(p.Url).TitleMax)
                ? $"{p.Html.Title!.Length} tegn (mål {c.For(p.Url).TitleMin}-{c.For(p.Url).TitleMax})" : null),
        Page("ONP-03", Category.OnPage, Severity.Warning, "Generisk title", "Skriv en beskrivende title for siden.",
            (p, _) => !Blank(p.Html.Title) && GenericTitles.Contains(p.Html.Title!.Trim().ToLowerInvariant()) ? $"\"{p.Html.Title}\"" : null),
        Dupes("ONP-04", Category.OnPage, Severity.Warning, "Dublet-title", "Giv hver side en unik title.", p => p.Html.Title),
        Page("ONP-05", Category.OnPage, Severity.Warning, "Meta description mangler", "Skriv en meta description.",
            (p, _) => Blank(p.Html.MetaDescription) ? "Ingen meta description" : null),
        Page("ONP-06", Category.OnPage, Severity.Info, "Meta description for kort eller lang", "Hold beskrivelsen inden for tærsklen.",
            (p, c) => !Blank(p.Html.MetaDescription) && Outside(p.Html.MetaDescription!.Length, c.For(p.Url).MetaMin, c.For(p.Url).MetaMax)
                ? $"{p.Html.MetaDescription!.Length} tegn (mål {c.For(p.Url).MetaMin}-{c.For(p.Url).MetaMax})" : null),
        Dupes("ONP-07", Category.OnPage, Severity.Warning, "Dublet-meta description", "Skriv en unik meta description pr. side.", p => p.Html.MetaDescription),
        Page("ONP-08", Category.OnPage, Severity.Warning, "H1 mangler", "Tilføj én H1.",
            (p, _) => p.Html.H1s.Count == 0 ? "Ingen H1" : null),
        Page("ONP-09", Category.OnPage, Severity.Warning, "Flere H1", "Brug kun én H1 pr. side.",
            (p, _) => p.Html.H1s.Count > 1 ? $"{p.Html.H1s.Count} H1" : null),
        Page("ONP-10", Category.OnPage, Severity.Info, "Sprunget overskriftsniveau", "Brug overskrifter i rækkefølge (H2 → H3, ikke H2 → H4).",
            (p, _) => p.Html.HeadingLevels.Zip(p.Html.HeadingLevels.Skip(1)).FirstOrDefault(x => x.Second > x.First + 1) is var (a, b) && b > 0 ? $"H{a} → H{b}" : null),
        Page("ONP-11", Category.OnPage, Severity.Warning, "Billeder uden alt-attribut", "Tilføj alt-tekst (alt=\"\" er OK for dekorative billeder). Billeder i menu, header eller footer rettes ét sted i skabelonen; billeder i indholdet på mediet eller i blokken.",
            (p, _) => p.Html.ImagesMissingAlt == 0 ? null
                : Lines(p.Html.ImagesWithoutAlt.Select(i => $"{i.Src} · i {i.Region}").ToList())),
        Page("ONP-12", Category.OnPage, Severity.Info, "Generiske linkankre", "Brug beskrivende linktekst i stedet for \"læs mere\".",
            (p, _) =>
            {
                var n = p.Html.Links.Count(l => GenericAnchors.Contains(TrimAnchor(l.Text)));
                return n > 0 ? $"{n} generiske links" : null;
            }),

        Page("ONP-13", Category.OnPage, Severity.Info, "Open Graph mangler", "Tilføj og:title og og:image, så siden ser pæn ud, når den deles på LinkedIn, Facebook m.fl.",
            (p, _) =>
            {
                if (!p.Indexable) return null;
                var missing = new[] { Blank(p.Html.OgTitle) ? "og:title" : null, Blank(p.Html.OgImage) ? "og:image" : null }.OfType<string>().ToList();
                return missing.Count == 0 ? null : "Mangler: " + string.Join(", ", missing);
            }),
        Dupes("ONP-14", Category.OnPage, Severity.Info, "Dublet-H1", "Giv hver side en H1, der beskriver netop den side.", p => p.Html.H1s.FirstOrDefault()),

        // D. Indhold
        Page("CON-01", Category.Content, Severity.Warning, "Tyndt indhold", "Uddyb indholdet, eller slå siden sammen/noindex hvis den ikke skal ranke.",
            (p, c) => p.Html.WordCount < c.For(p.Url).ThinWords ? $"{p.Html.WordCount} ord (grænse {c.For(p.Url).ThinWords})" : null),
        Dupes("CON-02", Category.Content, Severity.Warning, "Dublet-indhold", "Gør indholdet unikt, eller peg canonical på originalen.", p => p.Html.WordCount > 0 ? p.Html.ContentHash : null),
        Page("CON-03", Category.Content, Severity.Warning, "Ingen udgående links", "Link til relevante sider.",
            (p, _) => p.Indexable && p.Html.Links.Count == 0 ? "Siden har ingen links" : null),
    ];

    // ---- helpers ----------------------------------------------------------

    static bool Blank(string? s) => string.IsNullOrWhiteSpace(s);
    static bool Outside(int n, int min, int max) => n < min || n > max;
    static string Join(IReadOnlyList<string> items) => string.Join(", ", items.Take(5)) + (items.Count > 5 ? $" (+{items.Count - 5})" : "");
    /// <summary>One item per line, at most 20, with a count of the rest. <paramref name="total"/> when the list itself was already capped.</summary>
    static string Lines(IReadOnlyList<string> items, int? total = null)
    {
        var all = total ?? items.Count;
        return string.Join("\n", items.Take(20)) + (all > Math.Min(20, items.Count) ? $"\n(+{all - Math.Min(20, items.Count)} flere)" : "");
    }

    static string Short(string s) => s.Length <= 60 ? s : s[..57] + "…";

    static string TrimAnchor(string t) => t.Trim().Trim('→', '»', '>', '.', '…', ' ').ToLowerInvariant();

    static string? HeaderCanonical(PageSnapshot p) =>
        p.Headers.GetValueOrDefault("link") is { } h && CanonicalHeader().Match(h) is { Success: true } m ? UrlNormalizer.Normalize(m.Groups[1].Value, p.Url) : null;

    [GeneratedRegex("<([^>]+)>\\s*;[^,]*rel=\"?canonical\"?", RegexOptions.IgnoreCase)]
    private static partial Regex CanonicalHeader();

    /// <summary>Per-page rule on <c>IsOk</c> pages; <paramref name="test"/> returns evidence when violated.</summary>
    static Rule Page(string id, Category cat, Severity sev, string title, string fix, Func<PageSnapshot, SiteContext, string?> test) =>
        new(id, cat, sev, title, fix, c => c.Pages.Values.Where(p => p.IsOk)
            .Select(p => (p, ev: test(p, c))).Where(x => x.ev is not null).Select(x => new Hit(x.p.Url, x.ev!)));

    /// <summary>Per-page rule on any crawled page (status checks), custom gate.</summary>
    static Rule Pages(string id, Category cat, Severity sev, string title, string fix, Func<PageSnapshot, bool> match,
        Func<PageSnapshot, string> evidence, Func<PageSnapshot, SiteContext, bool>? gate = null) =>
        new(id, cat, sev, title, fix, c => c.Pages.Values.Where(p => match(p) && (gate?.Invoke(p, c) ?? true)).Select(p => new Hit(p.Url, evidence(p))));

    /// <summary>Same non-empty value on ≥2 canonical, indexable pages.</summary>
    static Rule Dupes(string id, Category cat, Severity sev, string title, string fix, Func<PageSnapshot, string?> key) =>
        new(id, cat, sev, title, fix, c => c.Pages.Values.Where(p => p.IsCanonicalPage && !Blank(key(p)))
            .GroupBy(p => key(p)!.Trim()).Where(g => g.Count() > 1)
            .SelectMany(g => g.Select(p => new Hit(p.Url, $"Deles med: {Join(g.Where(o => o != p).Select(o => o.Url).ToList())}"))));

    static IEnumerable<Hit> Sitemap(SiteContext c)
    {
        var origin = new Uri(c.StartUrl).GetLeftPart(UriPartial.Authority);
        if (!c.SitemapFound) { yield return new Hit(origin + "/sitemap.xml", "Ingen sitemap fundet"); yield break; }
        if (c.Robots.Sitemaps.Count == 0) yield return new Hit(origin + "/robots.txt", "Sitemap er ikke angivet i robots.txt");
        foreach (var u in c.SitemapUrls)
            if (c.Pages.GetValueOrDefault(u) is { } p && (p.Status != 200 || p.Noindex || !p.CanonicalIsSelf))
                yield return new Hit(u, p.Status != 200 ? $"HTTP {p.Status}" : p.Noindex ? "noindex" : $"Canonical: {p.Html.Canonical}");
    }

    static IEnumerable<Hit> SecurityHeaders(SiteContext c)
    {
        // ponytail: homepage only, headers are normally set site-wide; per-page if they turn out to differ
        if (c.Pages.GetValueOrDefault(c.StartKey) is not { IsOk: true } home) yield break;
        var h = home.Headers;
        var csp = h.GetValueOrDefault("content-security-policy") ?? "";
        var missing = new List<string>();
        if (!h.ContainsKey("strict-transport-security")) missing.Add("HSTS");
        if (csp.Length == 0) missing.Add("CSP");
        if (!h.ContainsKey("x-frame-options") && !csp.Contains("frame-ancestors")) missing.Add("X-Frame-Options");
        if (!h.ContainsKey("x-content-type-options")) missing.Add("X-Content-Type-Options");
        if (!h.ContainsKey("referrer-policy")) missing.Add("Referrer-Policy");
        if (missing.Count > 0) yield return new Hit(home.Url, "Mangler: " + string.Join(", ", missing));
    }
}
