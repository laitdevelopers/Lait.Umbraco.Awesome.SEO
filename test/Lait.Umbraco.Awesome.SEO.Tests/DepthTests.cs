using Lait.Umbraco.Awesome.SEO.Ai;
using Lait.Umbraco.Awesome.SEO.Crawling;
using Lait.Umbraco.Awesome.SEO.Rules;

namespace Lait.Umbraco.Awesome.SEO.Tests;

/// <summary>Open Graph, schema types, sitemap coverage, duplicates/depth metrics, strengths and the fuller plan.</summary>
public class DepthTests
{
    const string Site = "https://own.dk";

    static PageSnapshot Page(string path, string html) => new()
    {
        Url = Site + path, FinalUrl = Site + path, Status = 200, IsHtml = true, Html = PageParser.Parse(html, Site + path),
    };

    [Fact]
    public void Parser_reads_open_graph_and_schema_types_from_graph_arrays_and_nested_objects()
    {
        var html = """
            <html><head>
            <meta property="og:title" content="T"><meta property="og:image" content="https://own.dk/i.png"><meta name="twitter:card" content="summary">
            <script type="application/ld+json">{"@context":"https://schema.org","@graph":[{"@type":"Organization"},{"@type":["WebPage","FAQPage"],"author":{"@type":"Person"}}]}</script>
            <script type="application/ld+json">{ broken json </script>
            </head><body></body></html>
            """;
        var p = PageParser.Parse(html, Site + "/");

        Assert.Equal("T", p.OgTitle);
        Assert.NotNull(p.OgImage);
        Assert.True(p.HasTwitterCard);
        Assert.Equal(["FAQPage", "Organization", "Person", "WebPage"], p.SchemaTypes.Order());
        Assert.True(p.JsonLdInvalid);
    }

    static SiteContext Ctx(bool sitemap, params PageSnapshot[] pages) => new()
    {
        StartUrl = Site + "/",
        Pages = pages.ToDictionary(p => p.Url),
        SitemapFound = sitemap,
        SitemapUrls = sitemap ? new HashSet<string> { Site + "/" } : new HashSet<string>(),
    };

    const string Full = "<html lang=da><head><title>En passende title til denne side her</title><meta property=og:title content=x><meta property=og:image content=y></head><body><h1>H</h1><a href='/a'>a</a></body></html>";
    const string Bare = "<html lang=da><head><title>En anden passende title til siden her</title></head><body><h1>A</h1><a href='/'>forside</a></body></html>";

    [Fact]
    public void New_rules_flag_missing_open_graph_pages_missing_from_sitemap_and_invalid_json_ld()
    {
        var c = Ctx(true, Page("/", Full), Page("/a", Bare.Replace("</head>", "<script type='application/ld+json'>{nope</script></head>")));
        var f = RuleEngine.Run(c);

        Assert.Contains(f, x => x.RuleId == "ONP-13" && x.Url == Site + "/a");
        Assert.DoesNotContain(f, x => x.RuleId == "ONP-13" && x.Url == Site + "/");
        Assert.Contains(f, x => x.RuleId == "CRA-11" && x.Url == Site + "/a");
        Assert.DoesNotContain(f, x => x.RuleId == "CRA-11" && x.Url == Site + "/");
        Assert.Contains(f, x => x.RuleId == "TEC-12" && x.Url == Site + "/a");
    }

    [Fact]
    public void Without_a_sitemap_no_page_is_reported_as_missing_from_it()
    {
        var f = RuleEngine.Run(Ctx(false, Page("/", Full), Page("/a", Bare)));
        Assert.DoesNotContain(f, x => x.RuleId == "CRA-11");
    }

    [Fact]
    public void Metrics_v2_cover_open_graph_sitemap_and_schema_types()
    {
        var c = Ctx(true, Page("/", Full.Replace("</head>", "<script type='application/ld+json'>{\"@type\":\"Organization\"}</script></head>")), Page("/a", Bare));
        var m = BenchmarkMetrics.Compute(c, RuleEngine.Run(c));

        Assert.Equal(SiteMetrics.CurrentVersion, m.Version);
        Assert.Equal(50, m.OpenGraphPct);
        Assert.Equal(50, m.SitemapCoveragePct);
        Assert.Equal(1, m.SchemaTypes["Organization"]);
    }

    [Fact]
    public void Metrics_a_competitor_run_predates_are_not_compared_so_old_runs_never_create_false_gaps()
    {
        var own = new SiteMetrics { Version = 2, OpenGraphPct = 10, MetaDescriptionPct = 50 };
        var oldRival = new SiteMetrics { Version = 0, OpenGraphPct = 0, MetaDescriptionPct = 90 };   // stored before OG existed

        var b = BenchmarkBuilder.Build("own.dk", own, [], [("rival.dk", oldRival)]);

        Assert.Contains(b.Gaps, g => g.Key == "meta");
        Assert.DoesNotContain(b.Strengths, s => s.Key == "og");      // 10 vs "0" would look like a win, but rival has no OG data
        Assert.Null(b.Rows.Single(r => r.Key == "og").Competitors[0]);
        Assert.Equal("behind", b.Rows.Single(r => r.Key == "meta").Verdict);
    }

    [Fact]
    public void Strengths_and_schema_gaps_are_reported()
    {
        var own = new SiteMetrics { Version = 2, MetaDescriptionPct = 95, SchemaTypes = new Dictionary<string, int> { ["Organization"] = 10 } };
        var rival = new SiteMetrics { Version = 2, MetaDescriptionPct = 60, SchemaTypes = new Dictionary<string, int> { ["Organization"] = 5, ["FAQPage"] = 8, ["Product"] = 2 } };

        var b = BenchmarkBuilder.Build("own.dk", own, [], [("rival.dk", rival)]);

        Assert.Contains(b.Strengths, s => s.Key == "meta");
        Assert.Equal("ahead", b.Rows.Single(r => r.Key == "meta").Verdict);
        var gap = Assert.Single(b.SchemaGaps);       // Product is on only 2 pages
        Assert.Equal(("FAQPage", "rival.dk", 8), (gap.Type, gap.Domain, gap.Pages));
    }

    [Fact]
    public void Plan_lists_every_open_rule_once_warnings_as_high_and_info_as_nice()
    {
        var bm = new Benchmark(["own.dk", "rival.dk"], [], [], []) { SchemaGaps = [new("FAQPage", "rival.dk", 8)] };
        var findings = new List<Finding>
        {
            new("ONP-08", Site + "/a", Severity.Warning, "Ingen H1"),
            new("ONP-08", Site + "/b", Severity.Warning, "Ingen H1"),
            new("ONP-12", Site + "/a", Severity.Info, "2 generiske links"),
        };

        var items = PlanBuilder.Build(bm, findings);

        Assert.Equal(["schema:FAQPage", "rule:ONP-08", "rule:ONP-12"], items.Select(i => i.Key));
        Assert.Equal(["high", "high", "nice"], items.Select(i => i.Bucket));
        Assert.Equal(2, items[1].PagesAffected);
        Assert.Contains("(2 sider)", items[1].Title);
        Assert.Contains("(1 side)", items[2].Title);
    }

    [Fact]
    public void Writer_output_budget_grows_with_items_and_is_capped()
    {
        Assert.True(PlanWriter.OutputTokensFor(30) > PlanWriter.OutputTokensFor(5));
        Assert.True(PlanWriter.OutputTokensFor(1000) <= 8192);
    }

    [Fact]
    public void Broken_links_and_images_without_alt_say_which_one_with_link_text_and_where_on_the_page()
    {
        var html = "<html lang=da><head><title>En passende title til denne side her</title></head><body>" +
                   "<nav><a href='/borte'>Karla</a></nav><main><p>Se <a href='/borte'>vores ydelse</a></p>" +
                   "<img src='/media/hero.jpg'><img src='/media/hero.jpg'><img data-src='/media/lazy.png'><img alt='' src='/ok.png'></main>" +
                   "<footer><img src='/logo.svg'></footer></body></html>";
        var gone = new PageSnapshot { Url = Site + "/borte", FinalUrl = Site + "/borte", Status = 404, IsHtml = true };
        var f = RuleEngine.Run(Ctx(false, Page("/", html), gone));

        var broken = f.Single(x => x.RuleId == "CRA-03").Evidence;
        Assert.Equal(Site + "/borte → HTTP 404 · linktekst \"Karla\" · i menu/indhold", broken);

        var alt = f.Single(x => x.RuleId == "ONP-11").Evidence.Split('\n');
        Assert.Equal([Site + "/media/hero.jpg · i indhold", Site + "/media/lazy.png · i indhold", Site + "/logo.svg · i footer"], alt);
    }

    [Fact]
    public void Cloudflare_email_protection_links_are_ignored_and_a_tracking_pixel_in_head_is_marked_as_head()
    {
        var p = PageParser.Parse("<html><head><noscript><img src='https://www.facebook.com/tr?id=1'></noscript></head><body>" +
                                 "<footer><a href='/cdn-cgi/l/email-protection#abc'>mail</a></footer></body></html>", Site + "/");

        Assert.Empty(p.Links);
        Assert.Equal("head", Assert.Single(p.ImagesWithoutAlt).Region);
    }
}
