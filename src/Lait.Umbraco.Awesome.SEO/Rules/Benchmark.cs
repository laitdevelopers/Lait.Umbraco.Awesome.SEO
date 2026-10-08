using System.Text.RegularExpressions;
using Lait.Umbraco.Awesome.SEO.Crawling;

namespace Lait.Umbraco.Awesome.SEO.Rules;

public sealed record SectionStat(string Name, int Pages, double AvgWords);

/// <summary>What can be measured about a crawled site from its HTML, so own site and competitors can be compared like for like.</summary>
public sealed record SiteMetrics
{
    /// <summary>Which measurements exist: runs stored before a metric was added deserialize as 0 and must not be compared on it.</summary>
    public const int CurrentVersion = 2;
    public int Version { get; init; }
    public int Pages { get; init; }
    public double Score { get; init; }
    public double MetaDescriptionPct { get; init; }
    public double TitleOkPct { get; init; }
    public double SingleH1Pct { get; init; }
    public double CanonicalPct { get; init; }
    public double LangPct { get; init; }
    public double ViewportPct { get; init; }
    public double StructuredDataPct { get; init; }
    public double AltOkPct { get; init; }
    public double ThinContentPct { get; init; }
    public double BrokenLinkPagesPct { get; init; }
    public double AvgWords { get; init; }
    public double AvgInternalLinks { get; init; }
    public double AvgTtfbMs { get; init; }
    public int SecurityHeaders { get; init; }
    public IReadOnlyList<SectionStat> Sections { get; init; } = [];
    // Version 2
    public double OpenGraphPct { get; init; }
    public double DuplicateTitlePct { get; init; }
    public double DuplicateMetaPct { get; init; }
    public double DeepPagesPct { get; init; }
    public double SitemapCoveragePct { get; init; }
    /// <summary>schema.org type → number of indexable pages using it.</summary>
    public IReadOnlyDictionary<string, int> SchemaTypes { get; init; } = new Dictionary<string, int>();
}

/// <summary>One comparable measurement: how to read it, which rules explain a gap, and what to do about it.</summary>
public sealed record MetricDef(string Key, string Label, string Unit, bool HigherIsBetter, Func<SiteMetrics, double> Get, string[] RuleIds, string Fix, double MinGap, int Since = 0);

public static class BenchmarkMetrics
{
    static readonly Regex Lang = new("^[a-z]{2}(-[a-z]{2})?$", RegexOptions.IgnoreCase);

    public static SiteMetrics Compute(SiteContext ctx, IReadOnlyList<Finding> findings)
    {
        var t = ctx.Thresholds;
        var ok = ctx.Pages.Values.Where(p => p.IsOk).ToList();
        var idx = ok.Where(p => p.Indexable).ToList();

        double Pct(Func<PageSnapshot, bool> f, IReadOnlyCollection<PageSnapshot> set) => set.Count == 0 ? 0 : 100.0 * set.Count(f) / set.Count;
        double Avg(Func<PageSnapshot, double> f, IReadOnlyCollection<PageSnapshot> set) => set.Count == 0 ? 0 : set.Average(f);

        var brokenPages = findings.Where(f => f.RuleId == "CRA-03").Select(f => f.Url).Distinct().Count();
        double FindingPct(string rule) => idx.Count == 0 ? 0 : 100.0 * findings.Where(f => f.RuleId == rule).Select(f => f.Url).Distinct().Count() / idx.Count;
        var withDepth = idx.Where(p => ctx.Depth(p.Url) is not null).ToList();
        var home = ctx.Pages.GetValueOrDefault(ctx.StartKey);

        return new SiteMetrics
        {
            Version = SiteMetrics.CurrentVersion,
            Pages = ctx.Pages.Count,
            Score = HealthScore.Domain(ctx, findings),
            MetaDescriptionPct = Pct(p => !string.IsNullOrWhiteSpace(p.Html.MetaDescription), idx),
            TitleOkPct = Pct(p => !string.IsNullOrWhiteSpace(p.Html.Title) && p.Html.Title!.Length >= t.TitleMin && p.Html.Title.Length <= t.TitleMax, idx),
            SingleH1Pct = Pct(p => p.Html.H1s.Count == 1, idx),
            CanonicalPct = Pct(p => p.Html.Canonical is not null, idx),
            LangPct = Pct(p => !string.IsNullOrWhiteSpace(p.Html.Lang), idx),
            ViewportPct = Pct(p => p.Html.HasViewport, idx),
            StructuredDataPct = Pct(p => p.Html.JsonLdHash.Length > 0, idx),
            AltOkPct = Pct(p => p.Html.ImagesMissingAlt == 0, idx),
            ThinContentPct = Pct(p => p.Html.WordCount < t.ThinWords, idx),
            BrokenLinkPagesPct = ok.Count == 0 ? 0 : 100.0 * brokenPages / ok.Count,
            AvgWords = Avg(p => p.Html.WordCount, idx),
            AvgInternalLinks = Avg(p => p.Html.Links.Count(l => l.Internal), idx),
            AvgTtfbMs = Avg(p => p.TtfbMs, ok),
            SecurityHeaders = home is null ? 0 : CountSecurityHeaders(home),
            Sections = Sections(idx),
            OpenGraphPct = Pct(p => !string.IsNullOrWhiteSpace(p.Html.OgTitle) && !string.IsNullOrWhiteSpace(p.Html.OgImage), idx),
            DuplicateTitlePct = FindingPct("ONP-04"),
            DuplicateMetaPct = FindingPct("ONP-07"),
            DeepPagesPct = Pct(p => ctx.Depth(p.Url) >= t.DeepClicks, withDepth),
            SitemapCoveragePct = ctx.SitemapFound ? Pct(p => ctx.SitemapUrls.Contains(p.Url) || ctx.SitemapUrls.Contains(p.FinalUrl), idx) : 0,
            SchemaTypes = idx.SelectMany(p => p.Html.SchemaTypes.Distinct(StringComparer.OrdinalIgnoreCase))
                .GroupBy(x => x, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count()),
        };
    }

    static int CountSecurityHeaders(PageSnapshot home)
    {
        var h = home.Headers;
        var csp = h.GetValueOrDefault("content-security-policy") ?? "";
        return new[]
        {
            h.ContainsKey("strict-transport-security"), csp.Length > 0, h.ContainsKey("x-frame-options") || csp.Contains("frame-ancestors"),
            h.ContainsKey("x-content-type-options"), h.ContainsKey("referrer-policy"),
        }.Count(x => x);
    }

    /// <summary>Pages grouped by first path segment (after a language prefix like /da/); only pages two levels deep or more count.</summary>
    public static IReadOnlyList<SectionStat> Sections(IEnumerable<PageSnapshot> pages) => pages
        .Select(p => (Section: SectionOf(p.Url), p.Html.WordCount))
        .Where(x => x.Section is not null)
        .GroupBy(x => x.Section!, StringComparer.OrdinalIgnoreCase)
        .Select(g => new SectionStat(g.Key, g.Count(), Math.Round(g.Average(x => x.WordCount))))
        .OrderByDescending(s => s.Pages).Take(25).ToList();

    public static string? SectionOf(string url)
    {
        var segs = new Uri(url).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (segs.Count > 0 && Lang.IsMatch(segs[0])) segs.RemoveAt(0);
        return segs.Count >= 2 ? segs[0].ToLowerInvariant() : null;
    }

    public static readonly IReadOnlyList<MetricDef> Definitions =
    [
        new("score", "Sundhedsscore", "point", true, m => m.Score, [], "Ret de kritiske fund først (se fanen Fund).", 5),
        new("meta", "Sider med meta description", "%", true, m => m.MetaDescriptionPct, ["ONP-05"], "Skriv en unik meta description på siderne.", 5),
        new("title", "Sider med title i rigtig længde", "%", true, m => m.TitleOkPct, ["ONP-01", "ONP-02", "ONP-03"], "Ret titlerne, så de er beskrivende og holder sig inden for længdegrænsen.", 5),
        new("h1", "Sider med præcis én H1", "%", true, m => m.SingleH1Pct, ["ONP-08", "ONP-09"], "Giv hver side én H1.", 5),
        new("canonical", "Sider med canonical", "%", true, m => m.CanonicalPct, ["TEC-01"], "Tilføj canonical-tag med sidens egen URL.", 5),
        new("lang", "Sider med sprogangivelse", "%", true, m => m.LangPct, ["TEC-07"], "Sæt lang på <html>.", 5),
        new("viewport", "Sider med viewport-meta", "%", true, m => m.ViewportPct, ["TEC-06"], "Tilføj viewport-meta.", 5),
        new("schema", "Sider med struktureret data (JSON-LD)", "%", true, m => m.StructuredDataPct, [], "Tilføj JSON-LD (fx Organization, Article, BreadcrumbList) på relevante sider.", 10),
        new("alt", "Sider hvor alle billeder har alt-tekst", "%", true, m => m.AltOkPct, ["ONP-11"], "Tilføj alt-tekst til billederne.", 5),
        new("thin", "Sider med tyndt indhold", "%", false, m => m.ThinContentPct, ["CON-01"], "Uddyb de tynde sider, eller slå dem sammen.", 5),
        new("broken", "Sider med brudte interne links", "%", false, m => m.BrokenLinkPagesPct, ["CRA-03"], "Ret eller fjern de brudte links.", 3),
        new("og", "Sider med Open Graph (deling på sociale medier)", "%", true, m => m.OpenGraphPct, ["ONP-13"], "Tilføj og:title og og:image i layoutet, så delte links får titel og billede.", 10, 2),
        new("duptitle", "Sider med en title, som andre sider også har", "%", false, m => m.DuplicateTitlePct, ["ONP-04"], "Giv hver side en unik title.", 5, 2),
        new("dupmeta", "Sider med en meta description, som andre sider også har", "%", false, m => m.DuplicateMetaPct, ["ONP-07"], "Skriv en unik meta description pr. side.", 5, 2),
        new("deep", "Sider, der ligger 4+ klik fra forsiden", "%", false, m => m.DeepPagesPct, ["CRA-08"], "Link til de vigtige sider fra forsiden, menuen eller oversigtssider.", 5, 2),
        new("sitemap", "Indekserbare sider, der står i sitemap", "%", true, m => m.SitemapCoveragePct, ["CRA-11", "CRA-10"], "Sørg for, at sitemap genereres automatisk og indeholder alle sider, der skal findes.", 10, 2),
        new("words", "Gennemsnitligt ordtal pr. side", "ord", true, m => m.AvgWords, [], "Byg dybere indhold på de vigtigste sider.", 25),
        new("links", "Interne links pr. side", "stk.", true, m => m.AvgInternalLinks, [], "Link mere på tværs af relateret indhold.", 3),
        new("ttfb", "Gennemsnitlig svartid (TTFB)", "ms", false, m => m.AvgTtfbMs, ["TEC-08"], "Optimér server og cache.", 150),
        new("security", "Sikkerhedsheadere (af 5)", "stk.", true, m => m.SecurityHeaders, ["TEC-05"], "Tilføj HSTS, CSP, X-Frame-Options, X-Content-Type-Options og Referrer-Policy.", 1),
    ];
}

public sealed record AffectedPage(string Url, string RuleId, int? NodeId, Guid? NodeKey, string? DocumentType);

/// <summary>Competitors holds null where that competitor's run is older than the metric. Verdict: ahead | level | behind (own vs best competitor, with tolerance).</summary>
public sealed record MetricRow(string Key, string Label, string Unit, bool HigherIsBetter, double Own, IReadOnlyList<double?> Competitors, string Verdict);

public sealed record Strength(string Key, string Label, string Summary);

public sealed record SchemaGap(string Type, string Domain, int Pages);

public sealed record Gap(string Key, string Label, string Unit, string Summary, string Fix, double Own, double Best, string BestDomain, IReadOnlyList<AffectedPage> Pages, int AffectedTotal);

public sealed record SectionGap(string Name, string Domain, int Pages, double AvgWords);

public sealed record Benchmark(
    IReadOnlyList<string> Domains, IReadOnlyList<MetricRow> Rows, IReadOnlyList<Gap> Gaps, IReadOnlyList<SectionGap> SectionGaps)
{
    /// <summary>Measurements where the own site is clearly better than every competitor.</summary>
    public IReadOnlyList<Strength> Strengths { get; init; } = [];
    /// <summary>schema.org types competitors use on 3+ pages that the own site does not use at all.</summary>
    public IReadOnlyList<SchemaGap> SchemaGaps { get; init; } = [];
}

public static class BenchmarkBuilder
{
    public const int MaxAffectedPages = 25;

    /// <summary>Own site vs competitors: every measurement side by side, the gaps where a competitor is clearly better, and sections only they have.</summary>
    public static Benchmark Build(string ownDomain, SiteMetrics own, IReadOnlyList<Finding> ownFindings, IReadOnlyList<(string Domain, SiteMetrics Metrics)> competitors)
    {
        var rows = new List<MetricRow>();
        var gaps = new List<(double Weight, Gap Gap)>();
        var strengths = new List<Strength>();

        foreach (var d in BenchmarkMetrics.Definitions)
        {
            if (own.Version < d.Since) continue;   // own run predates the metric: nothing to compare
            var comparable = competitors.Where(c => c.Metrics.Version >= d.Since).ToList();
            var ours = d.Get(own);
            var verdict = "level";

            if (comparable.Count > 0)
            {
                var best = d.HigherIsBetter ? comparable.MaxBy(c => d.Get(c.Metrics)) : comparable.MinBy(c => d.Get(c.Metrics));
                var theirs = d.Get(best.Metrics);
                var behind = d.HigherIsBetter ? theirs - ours : ours - theirs;
                if (behind >= d.MinGap)
                {
                    verdict = "behind";
                    var affected = ownFindings.Where(f => f.Status == FindingStatus.Open && d.RuleIds.Contains(f.RuleId)).ToList();
                    var pages = affected.Take(MaxAffectedPages).Select(f => new AffectedPage(f.Url, f.RuleId, f.NodeId, f.NodeKey, f.DocumentType)).ToList();
                    gaps.Add((behind / d.MinGap, new Gap(d.Key, d.Label, d.Unit, Summary(d, ours, theirs, best.Domain), d.Fix, ours, theirs, best.Domain, pages, affected.Count)));
                }
                else if (-behind >= d.MinGap)
                {
                    verdict = "ahead";
                    strengths.Add(new Strength(d.Key, d.Label, Summary(d, ours, theirs, best.Domain).Replace(" hos " + best.Domain, " hos den bedste konkurrent (" + best.Domain + ")")));
                }
            }
            rows.Add(new MetricRow(d.Key, d.Label, d.Unit, d.HigherIsBetter, ours,
                competitors.Select(c => c.Metrics.Version >= d.Since ? d.Get(c.Metrics) : (double?)null).ToList(), verdict));
        }

        var ourSections = own.Sections.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sectionGaps = competitors
            .SelectMany(c => c.Metrics.Sections.Where(s => s.Pages >= 3 && !ourSections.Contains(s.Name)).Select(s => new SectionGap(s.Name, c.Domain, s.Pages, s.AvgWords)))
            .OrderByDescending(s => s.Pages).Take(15).ToList();

        var schemaGaps = own.Version < 2 ? [] : competitors
            .Where(c => c.Metrics.Version >= 2)
            .SelectMany(c => c.Metrics.SchemaTypes.Where(t => t.Value >= 3 && !own.SchemaTypes.ContainsKey(t.Key)).Select(t => new SchemaGap(t.Key, c.Domain, t.Value)))
            .OrderByDescending(g => g.Pages).Take(12).ToList();

        return new Benchmark([ownDomain, .. competitors.Select(c => c.Domain)], rows, gaps.OrderByDescending(g => g.Weight).Select(g => g.Gap).ToList(), sectionGaps)
        {
            Strengths = strengths,
            SchemaGaps = schemaGaps,
        };
    }

    static string Summary(MetricDef d, double ours, double theirs, string domain) => d.Unit == "%"
        ? $"{d.Label}: {Fmt(ours)} % hos jer mod {Fmt(theirs)} % hos {domain}"
        : $"{d.Label}: {Fmt(ours)} {d.Unit} hos jer mod {Fmt(theirs)} {d.Unit} hos {domain}";

    static string Fmt(double v) => Math.Abs(v - Math.Round(v)) < 0.05 || v >= 100 ? Math.Round(v).ToString("0") : v.ToString("0.0");
}
