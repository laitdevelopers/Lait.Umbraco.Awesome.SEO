using Lait.Umbraco.Awesome.SEO.Rules;
using Lait.Umbraco.Awesome.SEO.Services;

namespace Lait.Umbraco.Awesome.SEO.Search;

/// <summary>Position is Google's average position (1 = top). Ctr is 0-1.</summary>
public sealed record SearchTotals(double Clicks, double Impressions, double Ctr, double Position);

public sealed record QueryPage(string Query, string Page, double Clicks, double Impressions, double Ctr, double Position);

/// <summary>A search where a page is close to more clicks. InTitle: are the words of the search in the page's title (null = title unknown). ExpectedCtr: only for low-CTR rows.</summary>
public sealed record Opportunity(QueryPage Row, string? Title, bool? InTitle, double? ExpectedCtr = null);

public sealed record PageTrend(string Page, double Clicks, double PreviousClicks, double Position, double PreviousPosition);

/// <summary>Several pages of the site get impressions for the same search: they compete with each other.</summary>
public sealed record Cannibal(string Query, double Impressions, IReadOnlyList<QueryPage> Pages);

/// <summary>A page with traffic from Google that has open findings: fix these first, they are the ones people see.</summary>
public sealed record TrafficPage(string Page, double Clicks, double Impressions, IReadOnlyList<string> Findings);

/// <summary>What the latest crawl knows about a page (own or competitor), to hold it up against a search.</summary>
public sealed record CrawledPage(string Url, string? Title, string? H1, int WordCount, bool StructuredData)
{
    /// <summary>Every word of the search is in the title, the H1 or the address: the page is built for the search.</summary>
    public bool Targets(string query) =>
        SearchInsightsBuilder.ContainsWords($"{Title} {H1} {(Uri.TryCreate(Url, UriKind.Absolute, out var u) ? u.AbsolutePath.Replace('-', ' ').Replace('_', ' ') : "")}", query);
}

public sealed record CompetitorPage(string Domain, CrawledPage Page);

/// <summary>
/// A search where the site is below the top 3 and a competitor has a page built for it (and the own page is not, or is much thinner).
/// It does not say who ranks higher, only who has content aimed at the search. Own: the page Google shows for the site (null = not crawled).
/// </summary>
public sealed record ContentGap(string Query, double Impressions, double Position, string OwnUrl, CrawledPage? Own, bool OwnTargets, IReadOnlyList<CompetitorPage> Competitors);

public sealed record SearchInsights(
    DateOnly From, DateOnly To, SearchTotals Current, SearchTotals Previous,
    IReadOnlyList<QueryPage> TopQueries, IReadOnlyList<Opportunity> NearTop, IReadOnlyList<Opportunity> LowCtr,
    IReadOnlyList<PageTrend> Declining, IReadOnlyList<Cannibal> Cannibals, IReadOnlyList<TrafficPage> TrafficWithFindings,
    IReadOnlyList<ContentGap> Gaps);

public static class SearchInsightsBuilder
{
    // ponytail: one generic CTR curve for positions 1-5 (organic, no SERP features); brand searches and rich results differ a lot
    static readonly double[] ExpectedCtr = [0.27, 0.15, 0.10, 0.07, 0.05];
    // ponytail: fixed minimums suit small and mid-size sites; scale them with the site's total impressions if big sites get noisy lists
    const double MinImpressions = 20, MinLowCtrImpressions = 100, MinCannibalImpressions = 50, MinPreviousClicks = 10;

    /// <param name="ownPages">MatchKey (host + path) → the own page from the latest crawl.</param>
    /// <param name="findings">Open findings of the latest crawl of the own site.</param>
    /// <param name="competitors">Pages from the latest crawl of each competitor.</param>
    /// <param name="brand">The site's own name (see <see cref="Brand"/>): searches for it are not content gaps.</param>
    public static SearchInsights Build(SearchData data, IReadOnlyDictionary<string, CrawledPage> ownPages, IReadOnlyList<Finding> findings,
        IReadOnlyList<(string Domain, IReadOnlyList<CrawledPage> Pages)>? competitors = null, string? brand = null)
    {
        var rows = data.QueryPages.Where(r => r.Keys.Count == 2)
            .Select(r => new QueryPage(r.Keys[0], r.Keys[1], r.Clicks, r.Impressions, r.Ctr, r.Position)).ToList();
        string? Title(string page) => ownPages.GetValueOrDefault(Key(page))?.Title;
        Opportunity Op(QueryPage r, double? expected = null) => new(r, Title(r.Page), Title(r.Page) is { } t ? ContainsWords(t, r.Query) : null, expected);

        var topQueries = rows.GroupBy(r => r.Query)
            .Select(g =>
            {
                var impressions = g.Sum(r => r.Impressions);
                var clicks = g.Sum(r => r.Clicks);
                return new QueryPage(g.Key, g.MaxBy(r => r.Clicks + r.Impressions / 1000)!.Page, clicks, impressions,
                    impressions == 0 ? 0 : clicks / impressions, impressions == 0 ? 0 : g.Sum(r => r.Position * r.Impressions) / impressions);
            })
            .OrderByDescending(q => q.Clicks).ThenByDescending(q => q.Impressions).Take(20).ToList();

        // One row per search (its strongest page); several pages for one search are listed under Cannibals.
        var nearTop = rows.Where(r => r.Position >= 3.5 && r.Position <= 20.5 && r.Impressions >= MinImpressions)
            .GroupBy(r => r.Query).Select(g => g.MaxBy(r => r.Impressions)!)
            .OrderByDescending(r => r.Impressions).Take(15).Select(r => Op(r)).ToList();

        var lowCtr = rows.Where(r => r.Position < 5.5 && r.Impressions >= MinLowCtrImpressions)
            .Select(r => (Row: r, Expected: ExpectedCtr[Math.Clamp((int)Math.Round(r.Position) - 1, 0, ExpectedCtr.Length - 1)]))
            .Where(x => x.Row.Ctr < x.Expected / 2)
            .OrderByDescending(x => x.Row.Impressions * (x.Expected - x.Row.Ctr))   // clicks missed
            .Take(10).Select(x => Op(x.Row, x.Expected)).ToList();

        // From the previous period, so a page that lost all its clicks (and is not in this period at all) is included.
        var now = data.Pages.Where(r => r.Keys.Count == 1).ToDictionary(r => r.Keys[0]);
        var declining = data.PreviousPages.Where(r => r.Keys.Count == 1 && r.Clicks >= MinPreviousClicks)
            .Select(b => (Before: b, Now: now.GetValueOrDefault(b.Keys[0])))
            .Where(x => (x.Now?.Clicks ?? 0) <= x.Before.Clicks * 0.7)
            .Select(x => new PageTrend(x.Before.Keys[0], x.Now?.Clicks ?? 0, x.Before.Clicks, x.Now?.Position ?? 0, x.Before.Position))
            .OrderByDescending(t => t.PreviousClicks - t.Clicks).Take(10).ToList();

        var cannibals = rows.GroupBy(r => r.Query)
            .Select(g => (Query: g.Key, Impressions: g.Sum(r => r.Impressions), Pages: g.ToList()))
            .Where(g => g.Impressions >= MinCannibalImpressions)
            .Select(g => new Cannibal(g.Query, g.Impressions, g.Pages.Where(p => p.Impressions >= g.Impressions * 0.1).OrderByDescending(p => p.Impressions).ToList()))
            .Where(c => c.Pages.Count >= 2)
            .OrderByDescending(c => c.Impressions).Take(10).ToList();

        var open = findings.Where(f => f.Status == FindingStatus.Open && HealthScore.Weight(f.RuleId) > 0).ToLookup(f => Key(f.Url));
        var ruleTitles = RuleCatalog.All.ToDictionary(r => r.Id, r => r.Title);
        var traffic = data.Pages.Where(r => r.Keys.Count == 1 && r.Clicks + r.Impressions > 0)
            .OrderByDescending(r => r.Impressions).Take(100)
            .Select(r => (Row: r, Found: open[Key(r.Keys[0])].ToList()))
            .Where(x => x.Found.Count > 0)
            .Select(x => new TrafficPage(x.Row.Keys[0], x.Row.Clicks, x.Row.Impressions,
                x.Found.Select(f => ruleTitles.GetValueOrDefault(f.RuleId, f.RuleId)).Distinct().ToList()))
            .Take(15).ToList();

        return new SearchInsights(data.From, data.To, Totals(data.Totals), Totals(data.PreviousTotals), topQueries, nearTop, lowCtr, declining, cannibals, traffic,
            Gaps(rows, ownPages, competitors ?? [], brand));
    }

    /// <summary>The 50 biggest non-brand searches below the top 3, each held up against every competitor's best page built for it.</summary>
    static List<ContentGap> Gaps(List<QueryPage> rows, IReadOnlyDictionary<string, CrawledPage> ownPages,
        IReadOnlyList<(string Domain, IReadOnlyList<CrawledPage> Pages)> competitors, string? brand)
    {
        if (competitors.Count == 0) return [];
        return rows.GroupBy(r => r.Query).Select(g => g.MaxBy(r => r.Impressions)!)   // the page Google shows most for the search
            .Where(r => r.Position >= 3.5 && r.Impressions >= MinImpressions && (brand is null || !r.Query.Contains(brand, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(r => r.Impressions).Take(50)
            .Select(r =>
            {
                var own = ownPages.GetValueOrDefault(Key(r.Page));
                var ownTargets = own?.Targets(r.Query) ?? false;
                var theirs = competitors
                    .Select(c => c.Pages.Where(p => p.Targets(r.Query)).MaxBy(p => p.WordCount) is { } best ? new CompetitorPage(c.Domain, best) : null)
                    .OfType<CompetitorPage>()
                    .Where(c => !ownTargets || c.Page.WordCount >= 1.5 * own!.WordCount)   // own page built for it too: only much deeper pages are a gap
                    .ToList();
                return new ContentGap(r.Query, r.Impressions, r.Position, r.Page, own, ownTargets, theirs);
            })
            .Where(g => g.Competitors.Count > 0).Take(15).ToList();
    }

    /// <summary>The site's name as people search for it: the first part of the host without www (lait.dk → lait). Null when too short to match safely.</summary>
    // ponytail: host-based brand guess; a brand that differs from the domain (or a 2-letter one) is not filtered out
    public static string? Brand(string siteUrl)
    {
        var host = new Uri(siteUrl).Host;
        var name = (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host).Split('.')[0];
        return name.Length >= 3 ? name : null;
    }

    static SearchTotals Totals(SearchRow? r) => r is null ? new(0, 0, 0, 0) : new(r.Clicks, r.Impressions, r.Ctr, r.Position);

    static string Key(string url) => Uri.TryCreate(url, UriKind.Absolute, out _) ? ContentLookup.MatchKey(url) : url;

    /// <summary>Every word of the search (3+ letters) occurs in the text.</summary>
    // ponytail: plain substring match, no stemming (the search "bureau" finds "bureauet", the search "bureauer" does not find "bureau")
    public static bool ContainsWords(string text, string query)
    {
        var t = text.ToLowerInvariant();
        var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 3).ToList();
        return words.Count > 0 && words.All(t.Contains);
    }
}
