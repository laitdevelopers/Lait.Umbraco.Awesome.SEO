using Lait.Umbraco.Awesome.SEO.Crawling;
using Lait.Umbraco.Awesome.SEO.Rules;

namespace Lait.Umbraco.Awesome.SEO.Tests;

public class BenchmarkTests
{
    const string Site = "https://own.dk";

    static PageSnapshot Page(string path, string html, string site = Site) => new()
    {
        Url = site + path, FinalUrl = site + path, Status = 200, IsHtml = true,
        Html = PageParser.Parse(html, site + path),
    };

    /// <summary>A page whose quality is controlled by flags, so percentages are easy to predict.</summary>
    static string Html(bool meta = true, bool h1 = true, bool schema = false, int words = 200, string title = "En passende title til denne side her") =>
        $"<html lang=da><head><title>{title}</title><meta name=viewport content=x>" +
        (meta ? $"<meta name=description content='{new string('m', 100)}'>" : "") +
        (schema ? "<script type='application/ld+json'>{\"@type\":\"Organization\"}</script>" : "") +
        $"<link rel=canonical href='SELF'></head><body>{(h1 ? "<h1>H</h1>" : "")}<p>{string.Join(' ', Enumerable.Repeat("ord", words))}</p></body></html>";

    static SiteContext Ctx(string site, params (string Path, string Html)[] pages) => new()
    {
        StartUrl = site + "/",
        Pages = pages.Select(p => Page(p.Path, p.Html.Replace("SELF", site + p.Path), site)).ToDictionary(p => p.Url),
    };

    static SiteMetrics Metrics(SiteContext c) => BenchmarkMetrics.Compute(c, RuleEngine.Run(c));

    [Fact]
    public void Metrics_are_percentages_of_indexable_pages()
    {
        var c = Ctx(Site, ("/", Html()), ("/a", Html(meta: false)), ("/b", Html(meta: false, h1: false)), ("/c", Html(schema: true, words: 50)));
        var m = Metrics(c);

        Assert.Equal(4, m.Pages);
        Assert.Equal(50, m.MetaDescriptionPct);        // "/" and "/c" have a meta description: 2 of 4
        Assert.Equal(75, m.SingleH1Pct);               // /b has no H1
        Assert.Equal(25, m.StructuredDataPct);         // only /c
        Assert.Equal(25, m.ThinContentPct);            // /c has 50 words (< 150)
        Assert.Equal(100, m.CanonicalPct);
        Assert.Equal(100, m.LangPct);
    }

    [Fact]
    public void Sections_group_by_first_path_segment_skip_language_prefix_and_ignore_top_level_pages()
    {
        var pages = new[]
        {
            Page("/da/services/a", Html()), Page("/da/services/b", Html(words: 400)), Page("/services/c", Html()),
            Page("/blog/x", Html()), Page("/om", Html()), Page("/", Html()),
        };
        var s = BenchmarkMetrics.Sections(pages);

        Assert.Equal(["services", "blog"], s.Select(x => x.Name));
        Assert.Equal(3, s[0].Pages);
        Assert.Null(BenchmarkMetrics.SectionOf(Site + "/om"));
        Assert.Equal("services", BenchmarkMetrics.SectionOf(Site + "/da/services/a"));
    }

    static (string, SiteMetrics) Comp(string domain, SiteMetrics m) => (domain, m);

    [Fact]
    public void Gaps_appear_only_where_a_competitor_is_clearly_better_and_list_the_own_pages_to_fix()
    {
        var own = new SiteMetrics { Pages = 58, Score = 70, MetaDescriptionPct = 62, TitleOkPct = 90, ThinContentPct = 10, AvgWords = 400 };
        var theirs = new SiteMetrics { Pages = 139, Score = 85, MetaDescriptionPct = 91, TitleOkPct = 92, ThinContentPct = 30, AvgWords = 410 };
        var findings = new List<Finding>
        {
            new("ONP-05", "https://own.dk/a", Severity.Warning, "Ingen meta description") { NodeId = 11, NodeKey = Guid.NewGuid(), DocumentType = "page" },
            new("ONP-05", "https://own.dk/b", Severity.Warning, "Ingen meta description"),
            new("ONP-05", "https://own.dk/c", Severity.Warning, "x", FindingStatus.Resolved),   // resolved: not a page to fix
            new("TEC-01", "https://own.dk/d", Severity.Warning, "Ingen canonical"),
        };

        var b = BenchmarkBuilder.Build("own.dk", own, findings, [Comp("rival.dk", theirs)]);

        var keys = b.Gaps.Select(g => g.Key).ToList();
        Assert.Contains("score", keys);                      // 85 vs 70
        Assert.Contains("meta", keys);                       // 91 vs 62
        Assert.DoesNotContain("title", keys);                // 92 vs 90: within tolerance
        Assert.DoesNotContain("thin", keys);                 // lower is better: we have 10 % thin vs their 30 %
        Assert.DoesNotContain("words", keys);                // 410 vs 400: within tolerance

        var meta = b.Gaps.Single(g => g.Key == "meta");
        Assert.Equal("rival.dk", meta.BestDomain);
        Assert.Contains("62 %", meta.Summary);
        Assert.Contains("91 %", meta.Summary);
        Assert.Equal(2, meta.AffectedTotal);
        Assert.Equal(["https://own.dk/a", "https://own.dk/b"], meta.Pages.Select(p => p.Url));
        Assert.Equal(11, meta.Pages[0].NodeId);

        Assert.Equal(["own.dk", "rival.dk"], b.Domains);
        Assert.All(b.Rows, r => Assert.Single(r.Competitors));
    }

    [Fact]
    public void Biggest_gap_relative_to_its_tolerance_comes_first_and_lower_is_better_metrics_are_handled()
    {
        var own = new SiteMetrics { ThinContentPct = 60, MetaDescriptionPct = 80 };
        var theirs = new SiteMetrics { ThinContentPct = 10, MetaDescriptionPct = 90 };
        var b = BenchmarkBuilder.Build("own.dk", own, [], [Comp("rival.dk", theirs)]);

        Assert.Equal("thin", b.Gaps[0].Key);       // 50 points behind (tolerance 5) beats 10 points behind
        Assert.Contains("meta", b.Gaps.Select(g => g.Key));
    }

    [Fact]
    public void Sections_only_competitors_have_are_listed_when_they_have_at_least_three_pages()
    {
        var own = new SiteMetrics { Sections = [new SectionStat("services", 10, 300)] };
        var a = new SiteMetrics { Sections = [new SectionStat("services", 5, 300), new SectionStat("cases", 12, 650), new SectionStat("faq", 2, 200)] };
        var b2 = new SiteMetrics { Sections = [new SectionStat("cases", 4, 500), new SectionStat("priser", 3, 420)] };

        var r = BenchmarkBuilder.Build("own.dk", own, [], [Comp("a.dk", a), Comp("b.dk", b2)]);

        Assert.Equal(["cases", "cases", "priser"], r.SectionGaps.Select(s => s.Name));   // "faq" has only 2 pages; "services" we have
        Assert.Equal(["a.dk", "b.dk", "b.dk"], r.SectionGaps.Select(s => s.Domain));
    }

    [Fact]
    public void Without_competitors_there_are_no_gaps()
    {
        var b = BenchmarkBuilder.Build("own.dk", new SiteMetrics(), [], []);
        Assert.Empty(b.Gaps);
        Assert.Empty(b.SectionGaps);
    }

    [Fact]
    public void Competitor_diff_ignores_small_word_count_shifts_but_reports_real_ones()
    {
        PageFingerprint P(int words) => new("https://c.dk/p", 200, "t", "h", "m", null, "", words);
        CompetitorSnapshot Snap(int words) => new(new HashSet<string>(), new Dictionary<string, PageFingerprint> { ["https://c.dk/p"] = P(words) });

        Assert.Empty(CompetitorDiff.Compare(Snap(710), Snap(707)));            // -3 words: noise
        Assert.Empty(CompetitorDiff.Compare(Snap(100), Snap(120)));            // +20 but under 25 words
        Assert.Single(CompetitorDiff.Compare(Snap(100), Snap(130)));           // +30 words (30 %): a change
        Assert.Single(CompetitorDiff.Compare(Snap(7356), Snap(8200)));         // +844 words (> 10 %): a change
        Assert.Empty(CompetitorDiff.Compare(Snap(7356), Snap(7353)));
    }
}
