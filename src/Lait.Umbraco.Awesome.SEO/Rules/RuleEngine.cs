namespace Lait.Umbraco.Awesome.SEO.Rules;

public static class RuleEngine
{
    /// <param name="disabledRules">Rule ids switched off for this domain.</param>
    /// <param name="ignoredKeys"><see cref="Finding.DedupeKey"/>s on the ignore list; they get status Ignored.</param>
    public static List<Finding> Run(SiteContext ctx, IReadOnlySet<string>? disabledRules = null, IReadOnlySet<string>? ignoredKeys = null) =>
    [
        .. RuleCatalog.All.Where(r => disabledRules?.Contains(r.Id) != true)
            .SelectMany(r => r.Check(ctx).Select(h =>
            {
                var f = new Finding(r.Id, h.Url, h.Severity ?? r.DefaultSeverity, h.Evidence);
                return ignoredKeys?.Contains(f.DedupeKey) == true ? f with { Status = FindingStatus.Ignored } : f;
            })),
    ];
}

/// <summary>Points a rule costs the score (0-100 scale), for "what pulls the score down".</summary>
public sealed record ScoreLoss(string RuleId, string Title, double Points);

public sealed record CategoryScore(string Category, double Score);

/// <summary>Categories in report order. Losses: largest first.</summary>
public sealed record ScoreBreakdown(double Score, IReadOnlyList<CategoryScore> Categories, IReadOnlyList<ScoreLoss> Losses);

public static class HealthScore
{
    /// <summary>
    /// How much a rule matters for rankings in Google, 0-5. 0 = shown as a finding but not counted (no known ranking effect).
    /// Indexing and relevance (title, duplicates, thin content) weigh most; things that mostly change how a result looks or
    /// how the site is understood (meta description, Open Graph, structured data) less; security headers and heading order not at all.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, int> Weights = new Dictionary<string, int>
    {
        // Crawl og indeksering: a page that cannot be fetched or indexed cannot rank.
        ["CRA-01"] = 5, ["CRA-02"] = 3, ["CRA-03"] = 3, ["CRA-04"] = 1, ["CRA-05"] = 3, ["CRA-06"] = 2,
        ["CRA-07"] = 2, ["CRA-08"] = 1, ["CRA-09"] = 4, ["CRA-10"] = 2, ["CRA-11"] = 1,
        // Teknisk
        ["TEC-01"] = 2, ["TEC-02"] = 0, ["TEC-03"] = 2, ["TEC-04"] = 3, ["TEC-05"] = 0, ["TEC-06"] = 3, ["TEC-07"] = 1,
        ["TEC-08"] = 2, ["TEC-11"] = 4, ["TEC-12"] = 1, ["TEC-13"] = 3, ["TEC-14"] = 2,
        // On-page: the title is the strongest on-page signal.
        ["ONP-01"] = 5, ["ONP-02"] = 2, ["ONP-03"] = 3, ["ONP-04"] = 3, ["ONP-05"] = 2, ["ONP-06"] = 1, ["ONP-07"] = 1,
        ["ONP-08"] = 2, ["ONP-09"] = 1, ["ONP-10"] = 0, ["ONP-11"] = 1, ["ONP-12"] = 1, ["ONP-13"] = 1, ["ONP-14"] = 1,
        // Indhold
        ["CON-01"] = 3, ["CON-02"] = 4, ["CON-03"] = 1,
    };

    /// <summary>Checks of the site as a whole (sitemap, the start page's headers and structured data): one finding fails the whole check.</summary>
    static readonly HashSet<string> SiteWide = ["CRA-10", "TEC-05", "TEC-13"];

    public static int Weight(string ruleId) => Weights.GetValueOrDefault(ruleId, 1);

    /// <summary>
    /// 100 × (1 − Σ weight·fail / Σ weight) over enabled rules with weight &gt; 0. fail = share of crawled pages with an open finding
    /// (site-wide rules: 0 or 1). Ignored findings do not count. A gap on every page (no structured data anywhere) thus costs the rule's
    /// full weight, where the old per-page average let it cost 1 point.
    /// </summary>
    public static ScoreBreakdown Compute(IEnumerable<Finding> findings, int pageCount, IReadOnlySet<string>? disabledRules = null)
    {
        if (pageCount == 0) return new(0, [], []);
        var hitUrls = findings.Where(f => f.Status == FindingStatus.Open).GroupBy(f => f.RuleId)
            .ToDictionary(g => g.Key, g => g.Select(f => f.Url).Distinct().Count());
        var rules = RuleCatalog.All.Where(r => disabledRules?.Contains(r.Id) != true && Weight(r.Id) > 0).Select(r =>
        {
            var hits = hitUrls.GetValueOrDefault(r.Id);
            return (r.Id, r.Title, Category: r.Category.ToString(), W: (double)Weight(r.Id), Fail: SiteWide.Contains(r.Id) ? Math.Min(1, hits) : Math.Min(1.0, (double)hits / pageCount));
        }).ToList();

        static double Score(IReadOnlyCollection<(string Id, string Title, string Category, double W, double Fail)> set) =>
            set.Count == 0 ? 100 : 100 * (1 - set.Sum(x => x.W * x.Fail) / set.Sum(x => x.W));
        var total = rules.Sum(x => x.W);
        return new(
            Score(rules),
            rules.GroupBy(x => x.Category).Select(g => new CategoryScore(g.Key, Score(g.ToList()))).ToList(),
            rules.Where(x => x.Fail > 0).Select(x => new ScoreLoss(x.Id, x.Title, 100 * x.W * x.Fail / total)).OrderByDescending(l => l.Points).ToList());
    }
}

public sealed record DiffResult(IReadOnlyList<Finding> New, IReadOnlyList<Finding> Persisting, IReadOnlyList<Finding> Resolved);

public static class FindingDiff
{
    /// <summary>Key = rule id + URL. First run has no previous, so everything is "new" (callers treat that as baseline).</summary>
    public static DiffResult Compare(IEnumerable<Finding> previous, IEnumerable<Finding> current)
    {
        var prevOpen = previous.Where(f => f.Status == FindingStatus.Open).ToDictionary(f => f.DedupeKey);
        var cur = current.ToList();
        var curOpen = cur.Where(f => f.Status == FindingStatus.Open).ToList();
        var curKeys = cur.Select(f => f.DedupeKey).ToHashSet(); // includes Ignored: ignoring is not "resolved"

        return new DiffResult(
            curOpen.Where(f => !prevOpen.ContainsKey(f.DedupeKey)).ToList(),
            curOpen.Where(f => prevOpen.ContainsKey(f.DedupeKey)).ToList(),
            prevOpen.Values.Where(f => !curKeys.Contains(f.DedupeKey)).Select(f => f with { Status = FindingStatus.Resolved }).ToList());
    }
}
