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

public static class HealthScore
{
    /// <summary>Per page max(0, 100 − (10·critical + 3·warning + 1·info)); domain = mean over crawled pages. Ignored findings do not count.</summary>
    public static double Domain(SiteContext ctx, IEnumerable<Finding> findings)
    {
        if (ctx.Pages.Count == 0) return 0;
        var byPage = findings.Where(f => f.Status == FindingStatus.Open).ToLookup(f => f.Url);
        return ctx.Pages.Keys.Average(url => Page(byPage[url]));
    }

    public static int Page(IEnumerable<Finding> pageFindings) =>
        Math.Max(0, 100 - pageFindings.Sum(f => f.Severity switch { Severity.Critical => 10, Severity.Warning => 3, _ => 1 }));
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
