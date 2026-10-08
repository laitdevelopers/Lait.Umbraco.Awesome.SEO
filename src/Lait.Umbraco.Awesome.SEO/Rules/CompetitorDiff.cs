using Lait.Umbraco.Awesome.SEO.Crawling;

namespace Lait.Umbraco.Awesome.SEO.Rules;

/// <summary>What we persist per competitor URL; only snapshots can prove "unchanged" (CMP-04).</summary>
public sealed record PageFingerprint(string Url, int Status, string? Title, string? H1, string? MetaDescription, string? Canonical, string JsonLdHash, int WordCount)
{
    public static PageFingerprint From(PageSnapshot p) =>
        new(p.Url, p.Status, p.Html.Title, p.Html.H1s.FirstOrDefault(), p.Html.MetaDescription, p.Html.Canonical, p.Html.JsonLdHash, p.Html.WordCount);
}

public sealed record CompetitorSnapshot(IReadOnlySet<string> SitemapUrls, IReadOnlyDictionary<string, PageFingerprint> Pages);

public sealed record CompetitorChange(string Url, string Kind, string? Field = null, string? Old = null, string? New = null);

public static class CompetitorDiff
{
    /// <summary>CMP-01 (sitemap URL diff) and CMP-02 (field changes). Reports what changed, never why (CMP-05).</summary>
    public static List<CompetitorChange> Compare(CompetitorSnapshot previous, CompetitorSnapshot current)
    {
        var changes = new List<CompetitorChange>();
        // An empty sitemap on either side is almost always a fetch failure, not a site that deleted or added every page.
        if (previous.SitemapUrls.Count > 0 && current.SitemapUrls.Count > 0)
        {
            changes.AddRange(current.SitemapUrls.Except(previous.SitemapUrls).Select(u => new CompetitorChange(u, "new-page")));
            changes.AddRange(previous.SitemapUrls.Except(current.SitemapUrls).Select(u => new CompetitorChange(u, "removed-page")));
        }

        foreach (var (url, now) in current.Pages)
        {
            if (!previous.Pages.TryGetValue(url, out var was)) continue;
            void Diff<T>(string field, T a, T b) { if (!EqualityComparer<T>.Default.Equals(a, b)) changes.Add(new(url, "changed", field, a?.ToString(), b?.ToString())); }
            Diff("status", was.Status, now.Status);
            Diff("title", was.Title, now.Title);
            Diff("h1", was.H1, now.H1);
            Diff("meta", was.MetaDescription, now.MetaDescription);
            Diff("canonical", was.Canonical, now.Canonical);
            Diff("schema", was.JsonLdHash, now.JsonLdHash);
            // A few words more or less is dynamic content (dates, cookie text), not a change: ignore under 10 % / 25 words.
            if (Math.Abs(now.WordCount - was.WordCount) >= Math.Max(25, was.WordCount / 10)) Diff("wordcount", was.WordCount, now.WordCount);
        }
        return changes;
    }
}
