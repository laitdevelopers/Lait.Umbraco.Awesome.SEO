using System.Text.RegularExpressions;

namespace Lait.Umbraco.Awesome.SEO.Crawling;

/// <summary>RFC 9309 robots.txt: grouped user-agents, longest match wins (Allow wins ties), * and $ wildcards.</summary>
public sealed class RobotsTxt
{
    sealed record Rule(bool Allow, Regex Pattern, int Length);

    readonly Dictionary<string, List<Rule>> _groups = new(StringComparer.OrdinalIgnoreCase);
    bool _disallowAll;

    public IReadOnlyList<string> Sitemaps { get; private set; } = [];

    public static RobotsTxt AllowAll => Parse("");
    /// <summary>RFC 9309: robots.txt unreachable (5xx) means assume complete disallow.</summary>
    public static RobotsTxt DisallowAll => new() { _disallowAll = true };

    public static RobotsTxt Parse(string text)
    {
        var robots = new RobotsTxt();
        var sitemaps = new List<string>();
        var agents = new List<string>();
        var lastWasRule = false;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Split('#')[0].Trim();
            var i = line.IndexOf(':');
            if (i < 0) continue;
            var key = line[..i].Trim().ToLowerInvariant();
            var value = line[(i + 1)..].Trim();

            switch (key)
            {
                case "sitemap":
                    sitemaps.Add(value);
                    break;
                case "user-agent":
                    if (lastWasRule) { agents.Clear(); lastWasRule = false; }
                    agents.Add(value);
                    if (!robots._groups.ContainsKey(value)) robots._groups[value] = [];
                    break;
                case "allow" or "disallow":
                    lastWasRule = true;
                    if (value.Length == 0) break; // empty Disallow = allow everything, empty Allow = no-op
                    var rule = new Rule(key == "allow", ToRegex(value), value.Length);
                    foreach (var a in agents) robots._groups[a].Add(rule);
                    break;
            }
        }
        robots.Sitemaps = sitemaps;
        return robots;
    }

    /// <param name="pathAndQuery">e.g. "/shop?x=1"</param>
    /// <param name="userAgentToken">product token, e.g. "LaitSeoReport"</param>
    public bool IsAllowed(string pathAndQuery, string userAgentToken)
    {
        if (_disallowAll) return false;
        // Most specific group: longest agent name our token starts with, else "*".
        var key = _groups.Keys.Where(k => k != "*" && userAgentToken.StartsWith(k, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(k => k.Length).FirstOrDefault() ?? "*";
        if (!_groups.TryGetValue(key, out var rules)) return true;

        Rule? best = null;
        foreach (var r in rules.Where(r => r.Pattern.IsMatch(pathAndQuery)))
            if (best is null || r.Length > best.Length || (r.Length == best.Length && r.Allow)) best = r;
        return best?.Allow ?? true;
    }

    // ponytail: no percent-encoding normalization of patterns/paths; add if real-world files need it
    static Regex ToRegex(string pattern)
    {
        var end = pattern.EndsWith('$');
        var body = end ? pattern[..^1] : pattern;
        return new Regex("^" + Regex.Escape(body).Replace("\\*", ".*") + (end ? "$" : ""), RegexOptions.Singleline | RegexOptions.NonBacktracking);
    }
}
