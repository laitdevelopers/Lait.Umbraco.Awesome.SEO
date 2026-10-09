using System.Net;
using Lait.Umbraco.Awesome.SEO.Crawling;
using Lait.Umbraco.Awesome.SEO.Rules;

namespace Lait.Umbraco.Awesome.SEO.Tests;

public class EngineTests
{
    const string Site = "https://example.dk";

    static PageSnapshot Page(string path, string html, int status = 200, IDictionary<string, string>? headers = null, int redirects = 0, string? final = null) => new()
    {
        Url = Site + path,
        FinalUrl = final ?? Site + path,
        Status = status,
        IsHtml = html.Length > 0,
        Redirects = redirects,
        Headers = (headers ?? new Dictionary<string, string>()).ToDictionary(k => k.Key, k => k.Value),
        Html = html.Length > 0 ? PageParser.Parse(html.Replace("SELF", Site + path), Site + path) : new ParsedHtml(),
    };

    static SiteContext Ctx(params PageSnapshot[] pages) => new() { StartUrl = Site + "/", Pages = pages.ToDictionary(p => p.Url) };

    static string Good(string title = "En fin og tilpas lang titel til siden", string links = "") =>
        $"<html lang=da><head><title>{title}</title><meta name=viewport content=x><meta name=description content='{new string('m', 100)}'><link rel=canonical href='SELF'>" +
        "<script type='application/ld+json'>{\"@type\":\"WebSite\",\"publisher\":{\"@type\":\"Organization\"}}</script></head>" +
        $"<body><h1>H</h1><p>{string.Join(' ', Enumerable.Repeat("ord", 200))}</p><a href='/a'>Produkter</a>{links}</body></html>";

    static HashSet<string> Ids(IEnumerable<Finding> f) => f.Select(x => x.RuleId).ToHashSet();

    [Fact]
    public void Robots_longest_match_wildcards_and_groups()
    {
        var r = RobotsTxt.Parse("User-agent: *\nDisallow: /private\nAllow: /private/open\nDisallow: /*.pdf$\n\nUser-agent: LaitSeoReport\nDisallow: /secret\nSitemap: https://x/s.xml");
        Assert.False(r.IsAllowed("/secret/a", "LaitSeoReport/1.0"));
        Assert.True(r.IsAllowed("/private", "LaitSeoReport"), "specific group replaces *");
        Assert.False(r.IsAllowed("/private/x", "Other"));
        Assert.True(r.IsAllowed("/private/open/x", "Other"), "longer Allow wins");
        Assert.False(r.IsAllowed("/a.pdf", "Other"));
        Assert.True(r.IsAllowed("/a.pdf?x=1", "Other"), "$ anchors the end");
        Assert.Equal(["https://x/s.xml"], r.Sitemaps);
        Assert.False(RobotsTxt.DisallowAll.IsAllowed("/", "x"));
    }

    [Fact]
    public void Clean_page_has_no_findings_but_empty_page_trips_core_rules()
    {
        var clean = Page("/", Good());
        var findings = RuleEngine.Run(Ctx(clean));
        Assert.Empty(findings.Where(f => f.Severity != Severity.Info && f.RuleId is not ("CRA-10" or "CRA-07")).Select(f => $"{f.RuleId} {f.Url} {f.Evidence}"));

        var bare = RuleEngine.Run(Ctx(Page("/", "<html><body><p>hej</p></body></html>")));
        Assert.Superset(new HashSet<string> { "ONP-01", "ONP-05", "ONP-08", "TEC-01", "TEC-06", "TEC-07", "CON-01", "TEC-11" }, Ids(bare));
    }

    [Fact]
    public void Duplicates_broken_links_orphans_and_depth()
    {
        var home = Page("/", Good(links: "<a href='/b'>x</a><a href='/gone'>x</a>"));
        var a = Page("/a", Good(links: "<a href='/c'>x</a>"));
        var b = Page("/b", Good());          // same title as home → dupe
        var c = Page("/c", Good("Helt anden titel på den tredje side nu"));
        var gone = Page("/gone", "<html></html>", status: 404);
        var orphan = Page("/orphan", Good("Forældreløs side med sin egen titel her"));
        var ctx = new SiteContext { StartUrl = Site + "/", Pages = new[] { home, a, b, c, gone, orphan }.ToDictionary(p => p.Url) };
        
        var f = RuleEngine.Run(ctx);

        Assert.Contains(f, x => x.RuleId == "CRA-03" && x.Url == home.Url && x.Evidence.Contains("/gone"));
        Assert.Contains(f, x => x.RuleId == "CRA-02" && x.Url == gone.Url);
        Assert.Contains(f, x => x.RuleId == "ONP-04" && x.Url == b.Url);
        Assert.Contains(f, x => x.RuleId == "CRA-07" && x.Url == orphan.Url);
        Assert.DoesNotContain(f, x => x.RuleId == "CRA-07" && x.Url == home.Url);
        Assert.Equal(1, ctx.Depth(b.Url));
        Assert.Equal(2, ctx.Depth(c.Url));
        Assert.Null(ctx.Depth(orphan.Url));
    }

    [Fact]
    public void Noindex_in_sitemap_is_critical_and_ignore_and_disable_work()
    {
        var p = Page("/", Good().Replace("<head>", "<head><meta name=robots content=noindex>"));
        var ctx = new SiteContext { StartUrl = Site + "/", Pages = new Dictionary<string, PageSnapshot> { [p.Url] = p }, SitemapUrls = new HashSet<string> { p.Url } };

        var f = RuleEngine.Run(ctx);
        Assert.Equal(Severity.Critical, f.Single(x => x.RuleId == "CRA-06").Severity);

        var ignored = RuleEngine.Run(ctx, ignoredKeys: new HashSet<string> { $"CRA-06|{p.Url}" });
        Assert.Equal(FindingStatus.Ignored, ignored.Single(x => x.RuleId == "CRA-06").Status);
        Assert.DoesNotContain(RuleEngine.Run(ctx, disabledRules: new HashSet<string> { "CRA-06" }), x => x.RuleId == "CRA-06");
    }

    [Fact]
    public void Score_weighs_rules_by_impact_and_share_of_pages()
    {
        Finding F(string rule, string path, FindingStatus s = FindingStatus.Open) => new(rule, Site + path, Severity.Warning, "", s);
        var disabled = new HashSet<string> { "ONP-04" };
        var total = RuleCatalog.All.Where(r => !disabled.Contains(r.Id)).Sum(r => HealthScore.Weight(r.Id));

        var s = HealthScore.Compute(
        [
            F("ONP-01", "/a"), F("ONP-01", "/b"), F("ONP-01", "/b"),   // title missing on 2 of 4 pages: half its weight
            F("TEC-13", "/"),                                          // site-wide: one finding = full weight
            F("TEC-05", "/"),                                          // weight 0: shown, not counted
            F("CRA-01", "/c", FindingStatus.Ignored), F("ONP-04", "/c"),   // ignored and disabled: not counted
        ], pageCount: 4, disabled);

        var lost = 0.5 * HealthScore.Weight("ONP-01") + HealthScore.Weight("TEC-13");
        Assert.Equal(100 * (1 - lost / total), s.Score, 6);
        Assert.Equal(["TEC-13", "ONP-01"], s.Losses.Select(l => l.RuleId));
        Assert.Equal(["Crawl", "Technical", "OnPage", "Content"], s.Categories.Select(c => c.Category));
        Assert.Equal(100, s.Categories[0].Score);
        Assert.True(s.Categories[1].Score < s.Score);
        Assert.Equal(100, HealthScore.Compute([], 4).Score);
        Assert.Equal(0, HealthScore.Compute([], 0).Score);
    }

    [Fact]
    public void Every_rule_has_an_explicit_weight()
    {
        Assert.Empty(RuleCatalog.All.Select(r => r.Id).Except(HealthScore.Weights.Keys));
        Assert.Empty(HealthScore.Weights.Keys.Except(RuleCatalog.All.Select(r => r.Id)));
    }

    [Fact]
    public void Structured_data_rules()
    {
        var bare = Good().Replace("<script type='application/ld+json'>", "<script type='x'>");
        var none = RuleEngine.Run(Ctx(Page("/", bare), Page("/a", bare)));
        Assert.Equal("Mangler: Organization, WebSite", none.Single(f => f.RuleId == "TEC-13").Evidence);
        Assert.Equal(2, none.Count(f => f.RuleId == "TEC-14"));

        var local = bare.Replace("</head>", "<script type='application/ld+json'>{\"@type\":\"Dentist\"}</script></head>");
        var dentist = RuleEngine.Run(Ctx(Page("/", local)));
        Assert.Equal("Mangler: WebSite", dentist.Single(f => f.RuleId == "TEC-13").Evidence);
        Assert.DoesNotContain(dentist, f => f.RuleId == "TEC-14");

        var ok = RuleEngine.Run(Ctx(Page("/", Good()), Page("/a", bare)));
        Assert.DoesNotContain(ok, f => f.RuleId == "TEC-13");   // only the start page counts
        Assert.Equal([Site + "/a"], ok.Where(f => f.RuleId == "TEC-14").Select(f => f.Url));
    }

    [Fact]
    public void Diff_new_persisting_resolved_and_ignoring_is_not_resolving()
    {
        Finding F(string r, FindingStatus s = FindingStatus.Open) => new(r, Site, Severity.Warning, "", s);
        var d = FindingDiff.Compare([F("A"), F("B"), F("C")], [F("B"), F("C", FindingStatus.Ignored), F("D")]);
        Assert.Equal(["D"], d.New.Select(x => x.RuleId));
        Assert.Equal(["B"], d.Persisting.Select(x => x.RuleId));
        Assert.Equal(["A"], d.Resolved.Select(x => x.RuleId));
    }

    [Fact]
    public void Competitor_diff_reports_sitemap_and_field_changes()
    {
        PageFingerprint P(string title) => new(Site + "/p", 200, title, "h", "m", null, "", 100);
        var was = new CompetitorSnapshot(new HashSet<string> { Site + "/old", Site + "/p" }, new Dictionary<string, PageFingerprint> { [Site + "/p"] = P("Gammel") });
        var now = new CompetitorSnapshot(new HashSet<string> { Site + "/new", Site + "/p" }, new Dictionary<string, PageFingerprint> { [Site + "/p"] = P("Ny") });
        var c = CompetitorDiff.Compare(was, now);
        Assert.Contains(c, x => x is { Kind: "new-page" } && x.Url.EndsWith("/new"));
        Assert.Contains(c, x => x is { Kind: "removed-page" } && x.Url.EndsWith("/old"));
        Assert.Contains(c, x => x is { Kind: "changed", Field: "title", Old: "Gammel", New: "Ny" });
        Assert.Equal(3, c.Count);
    }

    [Fact]
    public async Task Crawler_follows_sitemap_and_links_respects_robots_and_reports_redirect_chain()
    {
        var routes = new Dictionary<string, (HttpStatusCode, string, string?)>
        {
            ["/robots.txt"] = (HttpStatusCode.OK, "User-agent: *\nDisallow: /hidden\nSitemap: https://example.dk/sm.xml", "text/plain"),
            ["/sm.xml"] = (HttpStatusCode.OK, "<urlset xmlns='http://www.sitemaps.org/schemas/sitemap/0.9'><url><loc>https://example.dk/in-sitemap</loc></url><url><loc>https://example.dk/hidden</loc></url></urlset>", "application/xml"),
            ["/"] = (HttpStatusCode.OK, Good(links: "<a href='/old'>x</a><a href='/hidden'>x</a>").Replace("SELF", Site + "/"), "text/html"),
            ["/in-sitemap"] = (HttpStatusCode.OK, Good("Side kun i sitemap med egen titel her").Replace("SELF", Site + "/in-sitemap"), "text/html"),
            ["/old"] = (HttpStatusCode.MovedPermanently, "", "/mid"),
            ["/mid"] = (HttpStatusCode.Found, "", "/new"),
            ["/new"] = (HttpStatusCode.OK, Good("Ny side efter redirect med egen titel").Replace("SELF", Site + "/new"), "text/html"),
        };
        var http = new HttpClient(new FakeHandler(routes));
        var progress = new List<(int Done, int Queued)>();
        var ctx = await new SiteCrawler(http, new CrawlOptions { DelayMs = 0 }).CrawlAsync(Site + "/", default, (done, queued) => progress.Add((done, queued)));

        Assert.Equal(ctx.Pages.Count, progress.Count);                                   // one callback per fetched page
        Assert.Equal(Enumerable.Range(1, progress.Count), progress.Select(x => x.Done)); // count only goes up
        Assert.Equal(0, progress[^1].Queued);                                            // queue is empty when the crawl ends

        Assert.True(ctx.SitemapFound);
        Assert.Contains(Site + "/in-sitemap", ctx.Pages.Keys);
        Assert.DoesNotContain(Site + "/hidden", ctx.Pages.Keys);
        Assert.Contains(Site + "/hidden", ctx.BlockedByRobots);
        var old = ctx.Pages[Site + "/old"];
        Assert.Equal((2, 200, Site + "/new"), (old.Redirects, old.Status, old.FinalUrl));

        var f = RuleEngine.Run(ctx);
        Assert.Contains(f, x => x.RuleId == "CRA-04" && x.Url == old.Url);
        Assert.Contains(f, x => x.RuleId == "CRA-09" && x.Url == Site + "/hidden");
    }

    [Fact]
    public async Task Crawler_survives_a_failing_page_never_follows_redirects_off_the_site_and_ignores_foreign_sitemaps()
    {
        var routes = new Dictionary<string, (HttpStatusCode, string, string?)>
        {
            ["/robots.txt"] = (HttpStatusCode.OK, "User-agent: *\nSitemap: http://169.254.169.254/latest/meta-data", "text/plain"),
            ["/"] = (HttpStatusCode.OK, Good(links: "<a href='/broken'>x</a><a href='/go/partner'>x</a><a href='/fragment'>x</a>").Replace("SELF", Site + "/"), "text/html"),
            ["/broken"] = (HttpStatusCode.OK, "THROW", "text/html"),
            ["/go/partner"] = (HttpStatusCode.Found, "", "https://partner.com/"),
            ["/fragment"] = (HttpStatusCode.OK, "<p>a</p><p>b</p>", "text/html"),   // no <body>: used to crash the parser
        };
        var ctx = await new SiteCrawler(new HttpClient(new FakeHandler(routes)), new CrawlOptions { DelayMs = 0 }).CrawlAsync(Site + "/");

        Assert.Equal(0, ctx.Pages[Site + "/broken"].Status);
        var partner = ctx.Pages[Site + "/go/partner"];
        Assert.Equal((302, Site + "/go/partner"), (partner.Status, partner.FinalUrl));   // stopped at the redirect, partner.com never requested
        Assert.Equal(2, ctx.Pages[Site + "/fragment"].Html.WordCount);   // "<p>a</p><p>b</p>" is two words, not "ab"
        Assert.False(ctx.SitemapFound);   // the foreign sitemap was never fetched (the handler would have thrown)
    }

    [Fact]
    public void Competitor_sitemap_that_failed_once_is_not_reported_as_every_page_removed()
    {
        var before = new CompetitorSnapshot(new HashSet<string> { "https://c.dk/a", "https://c.dk/b" }, new Dictionary<string, PageFingerprint>());
        var failed = new CompetitorSnapshot(new HashSet<string>(), new Dictionary<string, PageFingerprint>());
        Assert.Empty(CompetitorDiff.Compare(before, failed));
        Assert.Empty(CompetitorDiff.Compare(failed, before));
    }

    [Fact]
    public void Scheduler_is_due_when_never_run_or_interval_has_passed()
    {
        var now = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(Services.SeoScheduler.IsDue(null, 7, now));
        Assert.False(Services.SeoScheduler.IsDue(now.AddDays(-6), 7, now));
        Assert.True(Services.SeoScheduler.IsDue(now.AddDays(-7), 7, now));
        Assert.False(Services.SeoScheduler.IsDue(now.AddHours(-1), 0, now), "interval is at least 1 day");
    }

    sealed class FakeHandler(Dictionary<string, (HttpStatusCode Status, string Body, string? Extra)> routes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Host != "example.dk") throw new InvalidOperationException("Crawler left the site: " + request.RequestUri);
            if (!routes.TryGetValue(request.RequestUri.AbsolutePath, out var r)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (r.Body == "THROW") throw new HttpRequestException("connection reset");
            var resp = new HttpResponseMessage(r.Status);
            if ((int)r.Status is >= 300 and < 400) resp.Headers.Location = new Uri(r.Extra!, r.Extra!.StartsWith("http") ? UriKind.Absolute : UriKind.Relative);
            else resp.Content = new StringContent(r.Body, System.Text.Encoding.UTF8, r.Extra ?? "text/html");
            return Task.FromResult(resp);
        }
    }
}
