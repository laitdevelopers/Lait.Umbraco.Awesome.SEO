using Lait.Umbraco.Awesome.SEO.Crawling;

namespace Lait.Umbraco.Awesome.SEO.Rules;

public enum Severity { Info, Warning, Critical }

/// <summary>Order = report order (MS): crawl/indexing first.</summary>
public enum Category { Crawl, Technical, OnPage, Content, Links }

public enum FindingStatus { Open, Resolved, Ignored }

public sealed record Finding(string RuleId, string Url, Severity Severity, string Evidence, FindingStatus Status = FindingStatus.Open)
{
    /// <summary>Set for our own site when the URL is a published Umbraco node.</summary>
    public int? NodeId { get; init; }
    public Guid? NodeKey { get; init; }
    public string? DocumentType { get; init; }

    /// <summary>Diff key between runs. URL, not NodeId: one node has one URL per culture, so NodeId alone would merge them.</summary>
    public string DedupeKey => $"{RuleId}|{Url}";
}

public sealed record Hit(string Url, string Evidence, Severity? Severity = null);

public sealed record Rule(string Id, Category Category, Severity DefaultSeverity, string Title, string HowToFix, Func<SiteContext, IEnumerable<Hit>> Check);

/// <summary>Defaults; overridable globally and per document type (see <see cref="SiteContext.ThresholdsFor"/>).</summary>
public sealed class RuleThresholds
{
    public int TitleMin { get; init; } = 30;
    public int TitleMax { get; init; } = 60;
    public int MetaMin { get; init; } = 70;
    public int MetaMax { get; init; } = 160;
    public int ThinWords { get; init; } = 150;
    public int SlowTtfbMs { get; init; } = 1500;
    public int DeepClicks { get; init; } = 4;
    public int RedirectChain { get; init; } = 2;
}

public sealed class SiteContext
{
    public required string StartUrl { get; init; }
    /// <summary>Keyed by the normalized URL that was requested.</summary>
    public required IReadOnlyDictionary<string, PageSnapshot> Pages { get; init; }
    public IReadOnlySet<string> SitemapUrls { get; init; } = new HashSet<string>();
    public bool SitemapFound { get; init; }
    public RobotsTxt Robots { get; init; } = RobotsTxt.AllowAll;
    /// <summary>Internal URLs seen (sitemap/links) that robots.txt forbids us to fetch.</summary>
    public IReadOnlySet<string> BlockedByRobots { get; init; } = new HashSet<string>();
    /// <summary>Crawl stopped at MaxPages; link-graph rules (orphans) would give false positives.</summary>
    public bool Truncated { get; init; }
    public RuleThresholds Thresholds { get; init; } = new();
    public Func<string, RuleThresholds>? ThresholdsFor { get; init; }

    public RuleThresholds For(string url) => ThresholdsFor?.Invoke(url) ?? Thresholds;

    readonly Lazy<Graph> _graph;
    public SiteContext() => _graph = new(BuildGraph);

    /// <summary>Page key for any URL we know a page under (requested or final URL).</summary>
    public string? Resolve(string url) => _graph.Value.Keys.GetValueOrDefault(url);
    public int Inbound(string key) => _graph.Value.Inbound.GetValueOrDefault(key);
    public int? Depth(string key) => _graph.Value.Depth.TryGetValue(key, out var d) ? d : null;
    public bool IsRedirectTarget(string key) => _graph.Value.RedirectTargets.Contains(key);
    public string StartKey => Resolve(StartUrl) ?? StartUrl;

    sealed record Graph(Dictionary<string, string> Keys, Dictionary<string, int> Inbound, Dictionary<string, int> Depth, HashSet<string> RedirectTargets);

    Graph BuildGraph()
    {
        var keys = new Dictionary<string, string>();
        foreach (var k in Pages.Keys) keys[k] = k;
        foreach (var p in Pages.Values) if (p.FinalUrl.Length > 0) keys.TryAdd(p.FinalUrl, p.Url);

        var redirectTargets = Pages.Values.Where(p => p.Redirects > 0 && p.FinalUrl.Length > 0 && p.FinalUrl != p.Url)
            .Select(p => keys[p.FinalUrl]).ToHashSet();

        var inbound = new Dictionary<string, HashSet<string>>();
        var edges = new Dictionary<string, List<string>>();
        foreach (var p in Pages.Values.Where(p => p.IsOk))
            foreach (var l in p.Html.Links.Where(l => l.Internal))
                if (keys.TryGetValue(l.Href, out var target) && target != p.Url)
                {
                    (inbound.TryGetValue(target, out var s) ? s : inbound[target] = []).Add(p.Url);
                    (edges.TryGetValue(p.Url, out var e) ? e : edges[p.Url] = []).Add(target);
                }

        var depth = new Dictionary<string, int>();
        if (keys.TryGetValue(StartUrl, out var start))
        {
            depth[start] = 0;
            var queue = new Queue<string>([start]);
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                foreach (var next in edges.GetValueOrDefault(cur) ?? [])
                    if (depth.TryAdd(next, depth[cur] + 1)) queue.Enqueue(next);
            }
        }
        return new Graph(keys, inbound.ToDictionary(kv => kv.Key, kv => kv.Value.Count), depth, redirectTargets);
    }
}
