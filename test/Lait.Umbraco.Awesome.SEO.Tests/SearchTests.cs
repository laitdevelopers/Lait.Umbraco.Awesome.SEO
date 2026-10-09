using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Lait.Umbraco.Awesome.SEO.Rules;
using Lait.Umbraco.Awesome.SEO.Search;

namespace Lait.Umbraco.Awesome.SEO.Tests;

public class SearchTests
{
    static ServiceAccount Account(RSA rsa) => new("seo@proj.iam.gserviceaccount.com", rsa.ExportPkcs8PrivateKeyPem());

    [Fact]
    public void Service_account_parses_only_real_key_files()
    {
        using var rsa = RSA.Create(2048);
        var json = new JsonObject { ["type"] = "service_account", ["client_email"] = "a@b.iam.gserviceaccount.com", ["private_key"] = rsa.ExportPkcs8PrivateKeyPem() }.ToJsonString();
        Assert.Equal("a@b.iam.gserviceaccount.com", ServiceAccount.Parse(json)?.ClientEmail);
        Assert.Null(ServiceAccount.Parse("{\"client_email\":\"a@b\"}"));
        Assert.Null(ServiceAccount.Parse("not json"));
        Assert.Null(ServiceAccount.Parse(null));
    }

    [Fact]
    public void Assertion_is_an_RS256_jwt_for_googles_token_endpoint()
    {
        using var rsa = RSA.Create(2048);
        var jwt = SearchConsoleClient.Assertion(Account(rsa), DateTimeOffset.FromUnixTimeSeconds(1_800_000_000)).Split('.');
        static byte[] B64(string s) => Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/').PadRight(s.Length + (4 - s.Length % 4) % 4, '='));

        var claims = JsonNode.Parse(B64(jwt[1]))!;
        Assert.Equal("RS256", JsonNode.Parse(B64(jwt[0]))!["alg"]!.GetValue<string>());
        Assert.Equal("seo@proj.iam.gserviceaccount.com", claims["iss"]!.GetValue<string>());
        Assert.Equal("https://oauth2.googleapis.com/token", claims["aud"]!.GetValue<string>());
        Assert.Equal("https://www.googleapis.com/auth/webmasters.readonly", claims["scope"]!.GetValue<string>());
        Assert.Equal(3600, claims["exp"]!.GetValue<long>() - claims["iat"]!.GetValue<long>());
        Assert.True(rsa.VerifyData(Encoding.ASCII.GetBytes($"{jwt[0]}.{jwt[1]}"), B64(jwt[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public void Property_match_prefers_url_prefix_then_domain_property()
    {
        string[] all = ["sc-domain:lait.dk", "https://www.lait.dk/", "https://www.lait.dk/en/", "http://lait.dk/", "https://other.dk/"];
        Assert.Equal("https://www.lait.dk/", SearchConsoleClient.MatchProperty("https://www.lait.dk/", all));
        Assert.Equal("https://www.lait.dk/en/", SearchConsoleClient.MatchProperty("https://www.lait.dk/en/", all));
        Assert.Equal("https://www.lait.dk/", SearchConsoleClient.MatchProperty("https://lait.dk/", all.Where(p => p != "http://lait.dk/")));   // www variant
        Assert.Equal("sc-domain:lait.dk", SearchConsoleClient.MatchProperty("https://shop.lait.dk/", all));
        Assert.Null(SearchConsoleClient.MatchProperty("https://notlait.dk/", all));
    }

    sealed class Google(Func<HttpRequestMessage, string, (HttpStatusCode, string)> respond) : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var body = r.Content is null ? "" : await r.Content.ReadAsStringAsync(ct);
            lock (Calls) Calls.Add($"{r.Method} {r.RequestUri}");
            var (status, json) = respond(r, body);
            return new HttpResponseMessage(status) { Content = new StringContent(json) };
        }
    }

    sealed class Http(HttpMessageHandler h) : IHttpClientFactory { public HttpClient CreateClient(string name) => new(h, false); }

    [Fact]
    public async Task Client_gets_a_token_once_and_reads_five_periods_and_maps_errors()
    {
        using var rsa = RSA.Create(2048);
        var google = new Google((r, body) => r.RequestUri!.Host switch
        {
            "oauth2.googleapis.com" => (HttpStatusCode.OK, "{\"access_token\":\"tok\",\"expires_in\":3600}"),
            _ when r.Headers.Authorization?.Parameter != "tok" => (HttpStatusCode.Unauthorized, "{}"),
            _ when r.RequestUri.AbsolutePath.EndsWith("/sites") => (HttpStatusCode.OK, "{\"siteEntry\":[{\"siteUrl\":\"sc-domain:lait.dk\"}]}"),
            _ when body.Contains("\"query\"") => (HttpStatusCode.OK, "{\"rows\":[{\"keys\":[\"umbraco bureau\",\"https://lait.dk/\"],\"clicks\":3,\"impressions\":90,\"ctr\":0.033,\"position\":7.2}]}"),
            _ => (HttpStatusCode.OK, "{\"rows\":[{\"keys\":[],\"clicks\":10,\"impressions\":200,\"ctr\":0.05,\"position\":9}]}"),
        });
        var client = new SearchConsoleClient(new Http(google));

        Assert.Equal(["sc-domain:lait.dk"], await client.PropertiesAsync(Account(rsa), default));
        var data = await client.FetchAsync(Account(rsa), "sc-domain:lait.dk", new DateOnly(2026, 10, 9), default);
        Assert.Equal((new DateOnly(2026, 9, 9), new DateOnly(2026, 10, 6)), (data.From, data.To));
        Assert.Equal(10, data.Totals!.Clicks);
        Assert.Equal("umbraco bureau", data.QueryPages.Single().Keys[0]);
        Assert.Single(google.Calls, c => c.Contains("oauth2"));
        Assert.Contains(google.Calls, c => c.Contains("sites/sc-domain%3Alait.dk/searchAnalytics/query"));

        var denied = new SearchConsoleClient(new Http(new Google((r, _) => r.RequestUri!.Host == "oauth2.googleapis.com"
            ? (HttpStatusCode.OK, "{\"access_token\":\"tok\"}")
            : (HttpStatusCode.Forbidden, "{\"error\":{\"message\":\"User does not have sufficient permission for site\"}}"))));
        var e = await Assert.ThrowsAsync<SearchConsoleException>(() => denied.FetchAsync(Account(rsa), "sc-domain:lait.dk", new DateOnly(2026, 10, 9), default));
        Assert.Contains("seo@proj.iam.gserviceaccount.com", e.Message);
    }

    static SearchRow R(double clicks, double impressions, double position, params string[] keys) => new(keys, clicks, impressions, impressions == 0 ? 0 : clicks / impressions, position);

    [Fact]
    public void Insights_find_near_top_low_ctr_declines_cannibals_and_pages_with_traffic_and_findings()
    {
        const string home = "https://lait.dk/", seo = "https://lait.dk/seo/", web = "https://lait.dk/web/", gone = "https://lait.dk/gammel/";
        var data = new SearchData(new DateOnly(2026, 9, 9), new DateOnly(2026, 10, 6), R(50, 3000, 8), R(80, 2800, 7),
            Pages: [R(20, 1500, 5, home), R(5, 900, 9, seo), R(25, 600, 2, web)],
            PreviousPages: [R(22, 1400, 5, home), R(20, 800, 6, seo), R(15, 300, 4, gone)],
            QueryPages:
            [
                R(2, 400, 7.4, "umbraco bureau", home),     // near top, words not in the title
                R(1, 300, 6.0, "seo bureau", seo),          // near top, words in the title
                R(0, 150, 2.1, "umbraco hosting", web),     // position 2 with 0 % CTR: low CTR
                R(1, 120, 9, "seo pris", seo), R(1, 80, 12, "seo pris", home),   // two pages for one search
                R(9, 10, 3.2, "lait", home),                // too few impressions to matter
            ]);
        var titles = new Dictionary<string, CrawledPage>
        {
            ["lait.dk"] = new(home, "LAIT – digitalt bureau", null, 500, false), ["lait.dk/seo"] = new(seo, "SEO bureau i Aarhus", null, 400, false),
        };
        Finding[] findings = [new("ONP-05", seo, Severity.Warning, "Ingen meta description"), new("TEC-05", home, Severity.Info, "x")];

        var s = SearchInsightsBuilder.Build(data, titles, findings);

        Assert.Equal(["umbraco bureau", "seo bureau", "seo pris"], s.NearTop.Select(o => o.Row.Query));
        Assert.Equal([false, true, false], s.NearTop.Select(o => o.InTitle));   // "pris" is not in "SEO bureau i Aarhus"
        Assert.Equal("umbraco hosting", s.LowCtr.Single().Row.Query);
        Assert.Null(s.LowCtr.Single().InTitle);                                     // /web/ was not crawled: title unknown
        Assert.Equal([seo, gone], s.Declining.Select(d => d.Page));                 // 20 → 5, and 15 → gone; home 22 → 20 is noise
        Assert.Equal([seo, home], s.Cannibals.Single().Pages.Select(p => p.Page));
        Assert.Equal(seo, s.TrafficWithFindings.Single().Page);                     // TEC-05 has weight 0: not a finding that matters
        Assert.Equal(["Meta description mangler"], s.TrafficWithFindings.Single().Findings);
        Assert.Equal((50, 80), (s.Current.Clicks, s.Previous.Clicks));
        Assert.Equal("lait", s.TopQueries[0].Query);
        Assert.Empty(s.Gaps);   // no competitors given
    }

    [Fact]
    public void Content_gaps_hold_own_searches_up_against_competitor_pages_built_for_them()
    {
        const string home = "https://www.lait.dk/", hosting = "https://www.lait.dk/hosting/";
        var data = new SearchData(new DateOnly(2026, 9, 9), new DateOnly(2026, 10, 6), null, null, [], [],
        [
            R(1, 400, 8.2, "umbraco hosting", home),        // own page is the front page: not built for the search
            R(1, 300, 6.0, "umbraco support", hosting),     // own page is built for it, but much thinner than theirs
            R(1, 250, 9.0, "seo rådgivning", home),         // no competitor page for it
            R(1, 500, 7.0, "lait umbraco", home),           // brand search
            R(9, 900, 1.4, "umbraco bureau", home),         // already top 3
        ]);
        var own = new CrawledPage[] { new(home, "LAIT – digitalt bureau", "Vi bygger websites", 600, false), new(hosting, "Hosting og support af Umbraco", null, 300, false) }
            .ToDictionary(p => Lait.Umbraco.Awesome.SEO.Services.ContentLookup.MatchKey(p.Url));
        IReadOnlyList<(string, IReadOnlyList<CrawledPage>)> competitors =
        [
            ("ecreo.dk", [new("https://ecreo.dk/umbraco-hosting/", "Hosting", null, 940, true), new("https://ecreo.dk/umbraco/", "Umbraco hosting light", null, 300, false),
                          new("https://ecreo.dk/support/", "Umbraco support", null, 800, true), new("https://ecreo.dk/umbraco-bureau/", "Umbraco bureau", null, 900, true)]),
            ("rival.dk", [new("https://rival.dk/om/", "Om os", null, 400, false)]),
        ];

        var gaps = SearchInsightsBuilder.Build(data, own, [], competitors, SearchInsightsBuilder.Brand(home)).Gaps;

        Assert.Equal(["umbraco hosting", "umbraco support"], gaps.Select(g => g.Query));
        Assert.Equal("https://ecreo.dk/umbraco-hosting/", gaps[0].Competitors.Single().Page.Url);   // the words are in its address; deepest page wins
        Assert.False(gaps[0].OwnTargets);
        Assert.True(gaps[1].OwnTargets);                                                            // own page targets it, theirs is 800 vs 300 words
        Assert.Equal("lait", SearchInsightsBuilder.Brand("https://www.lait.dk/"));
        Assert.Null(SearchInsightsBuilder.Brand("https://ab.dk/"));
    }
}
