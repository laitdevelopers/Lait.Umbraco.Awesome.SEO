using Lait.Umbraco.Awesome.SEO.Configuration;

namespace Lait.Umbraco.Awesome.SEO.Tests;

public class SettingsTests
{
    static SiteOptions Site(string url = "https://www.example.dk/", Action<SiteOptions>? tweak = null)
    {
        var s = new SiteOptions { Url = url, Enabled = true, IntervalDays = 7, MaxPages = 300 };
        tweak?.Invoke(s);
        return s;
    }

    [Fact]
    public void Valid_input_is_normalized()
    {
        var (sites, errors) = SiteValidator.Validate([Site("https://www.example.dk/shop?x=1#top", s =>
        {
            s.Competitors = [" https://a.dk/some/page ", "https://a.dk/", "", "https://b.dk"];
            s.DisabledRules = ["TEC-05", "ONP-02", "TEC-05"];
        })]);

        Assert.Empty(errors);
        var site = Assert.Single(sites);
        Assert.Equal("https://www.example.dk/shop", site.Url);
        Assert.Equal(["https://a.dk/", "https://b.dk/"], site.Competitors);
        Assert.Equal(["ONP-02", "TEC-05"], site.DisabledRules);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://example.dk/")]
    [InlineData("")]
    public void Invalid_site_urls_are_rejected(string url)
    {
        var (sites, errors) = SiteValidator.Validate([Site(url)]);
        Assert.Empty(sites);
        Assert.Single(errors);
    }

    [Fact]
    public void Limits_duplicates_and_unknowns_are_reported_together()
    {
        var (_, errors) = SiteValidator.Validate(
        [
            Site("https://one.dk/", s => { s.IntervalDays = 0; s.MaxPages = 99999; s.DisabledRules = ["NOPE-99"]; }),
            Site("https://ONE.dk/other"),                                                      // same host
            Site("https://two.dk/", s => s.Competitors = ["https://two.dk/page", "x://bad"]),    // itself + invalid
            Site("https://three.dk/", s => s.Competitors = Enumerable.Range(0, 6).Select(i => $"https://c{i}.dk/").ToList()),
        ]);

        Assert.Contains(errors, e => e.Contains("interval"));
        Assert.Contains(errors, e => e.Contains("maks. sider"));
        Assert.Contains(errors, e => e.Contains("NOPE-99"));
        Assert.Contains(errors, e => e.Contains("to gange"));
        Assert.Contains(errors, e => e.Contains("kan ikke være sitet selv"));
        Assert.Contains(errors, e => e.Contains("x://bad"));
        Assert.Contains(errors, e => e.Contains("højst 5 konkurrenter"));
    }

    [Fact]
    public void Too_many_sites_is_an_error()
    {
        var many = Enumerable.Range(0, SiteValidator.MaxSites + 1).Select(i => Site($"https://s{i}.dk/")).ToList();
        Assert.Contains(SiteValidator.Validate(many).Errors, e => e.Contains("Højst"));
    }
}

public class ContentLookupTests
{
    [Theory]
    [InlineData("https://Example.dk/Om-Os/", "http://example.dk/om-os")]
    [InlineData("http://example.dk/om-os?x=1", "https://example.dk/om-os/")]
    [InlineData("https://example.dk/", "http://example.dk")]
    public void Match_key_ignores_scheme_case_trailing_slash_and_query(string a, string b) =>
        Assert.Equal(Services.ContentLookup.MatchKey(a), Services.ContentLookup.MatchKey(b));

    [Fact]
    public void Match_key_keeps_host_and_path_apart()
    {
        Assert.NotEqual(Services.ContentLookup.MatchKey("https://a.dk/x"), Services.ContentLookup.MatchKey("https://b.dk/x"));
        Assert.NotEqual(Services.ContentLookup.MatchKey("https://a.dk/x"), Services.ContentLookup.MatchKey("https://a.dk/y"));
    }
}
