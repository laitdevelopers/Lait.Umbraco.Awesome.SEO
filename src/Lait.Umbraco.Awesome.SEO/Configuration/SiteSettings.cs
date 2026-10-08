using System.Text.Json;
using Lait.Umbraco.Awesome.SEO.Crawling;
using Lait.Umbraco.Awesome.SEO.Persistence;
using Lait.Umbraco.Awesome.SEO.Rules;
using Lait.Umbraco.Awesome.SEO.Services;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Services;

namespace Lait.Umbraco.Awesome.SEO.Configuration;

/// <summary>The sites the plugin crawls. Database is the source of truth; appsettings seeds it once.</summary>
public sealed class SiteSettings(SeoStore store, IOptionsMonitor<SeoOptions> seed, IKeyValueService keyValues, IDomainService domains)
{
    const string SeededKey = "Lait.Umbraco.Awesome.SEO.SitesSeeded";

    /// <summary>All saved sites, valid URLs only.</summary>
    public IReadOnlyList<SiteOptions> All()
    {
        SeedOnce();
        return store.SiteRows().Select(ToOptions).Where(s => Uri.TryCreate(s.Url, UriKind.Absolute, out _)).ToList();
    }

    /// <summary>Sites the scheduler and dashboard work with.</summary>
    public IReadOnlyList<SiteOptions> Active() => All().Where(s => s.Enabled).ToList();

    public void Save(IEnumerable<SiteOptions> sites) => store.ReplaceSites(sites.Select(ToRow));

    /// <summary>Domains configured in Umbraco, as crawlable URLs (host-less/culture-only entries are skipped).</summary>
    public IReadOnlyList<string> UmbracoDomainUrls() => domains.GetAll(false)
        .Select(d => d.DomainName.Trim())
        .Where(n => n.Length > 0 && !n.StartsWith('/'))
        .Select(n => n.Contains("://") ? n : "https://" + n)
        .Select(n => UrlNormalizer.Normalize(n))
        .OfType<string>()
        .Select(n => new Uri(n).GetLeftPart(UriPartial.Authority) + "/")
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    void SeedOnce()
    {
        if (keyValues.GetValue(SeededKey) is not null) return;
        // Mark first: a deleted-everything list must not be re-seeded from appsettings on the next read.
        keyValues.SetValue(SeededKey, "1");
        if (store.SiteRows().Count == 0 && seed.CurrentValue.Sites.Count > 0) Save(seed.CurrentValue.Sites);
    }

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    static SiteOptions ToOptions(SiteRow r) => new()
    {
        Url = r.Url, Enabled = r.Enabled, IntervalDays = r.IntervalDays, MaxPages = r.MaxPages,
        Competitors = JsonSerializer.Deserialize<List<string>>(r.Competitors, Json) ?? [],
        DisabledRules = JsonSerializer.Deserialize<List<string>>(r.DisabledRules, Json) ?? [],
    };

    static SiteRow ToRow(SiteOptions s) => new()
    {
        Url = s.Url, Enabled = s.Enabled, IntervalDays = s.IntervalDays, MaxPages = s.MaxPages,
        Competitors = JsonSerializer.Serialize(s.Competitors, Json), DisabledRules = JsonSerializer.Serialize(s.DisabledRules, Json),
    };
}

/// <summary>Validates and normalizes what the settings tab submits (admin input is still a trust boundary: it makes the server fetch URLs).</summary>
public static class SiteValidator
{
    public const int MaxSites = 20;
    public const int MaxPagesCap = 2000;

    /// <summary>Returns normalized sites, or the list of problems (Danish, shown in the UI).</summary>
    public static (List<SiteOptions> Sites, List<string> Errors) Validate(IReadOnlyList<SiteOptions> input)
    {
        var errors = new List<string>();
        var result = new List<SiteOptions>();
        var ruleIds = RuleCatalog.All.Select(r => r.Id).ToHashSet();

        if (input.Count > MaxSites) errors.Add($"Højst {MaxSites} sites.");

        foreach (var s in input.Take(MaxSites))
        {
            var url = UrlNormalizer.Normalize(s.Url ?? "");
            if (url is null) { errors.Add($"\"{s.Url}\" er ikke en gyldig http(s)-adresse."); continue; }
            var host = new Uri(url).Host;
            var label = host;

            if (result.Any(r => r.Domain.Equals(host, StringComparison.OrdinalIgnoreCase))) { errors.Add($"{label}: domænet er tilføjet to gange."); continue; }
            if (s.IntervalDays is < 1 or > 365) errors.Add($"{label}: interval skal være 1-365 dage.");
            if (s.MaxPages is < 1 or > MaxPagesCap) errors.Add($"{label}: maks. sider skal være 1-{MaxPagesCap}.");

            var competitors = new List<string>();
            foreach (var c in (s.Competitors ?? []).Select(c => c.Trim()).Where(c => c.Length > 0))
            {
                var cu = UrlNormalizer.Normalize(c);
                if (cu is null) { errors.Add($"{label}: konkurrent \"{c}\" er ikke en gyldig adresse."); continue; }
                var origin = new Uri(cu).GetLeftPart(UriPartial.Authority) + "/";
                if (new Uri(cu).Host.Equals(host, StringComparison.OrdinalIgnoreCase)) errors.Add($"{label}: en konkurrent kan ikke være sitet selv.");
                else if (!competitors.Contains(origin, StringComparer.OrdinalIgnoreCase)) competitors.Add(origin);
            }
            if (competitors.Count > SeoRunner.MaxCompetitors) errors.Add($"{label}: højst {SeoRunner.MaxCompetitors} konkurrenter.");

            var unknown = (s.DisabledRules ?? []).Where(r => !ruleIds.Contains(r)).ToList();
            if (unknown.Count > 0) errors.Add($"{label}: ukendte regler: {string.Join(", ", unknown)}.");

            result.Add(new SiteOptions
            {
                Url = new Uri(url).GetLeftPart(UriPartial.Authority) + new Uri(url).AbsolutePath,
                Enabled = s.Enabled, IntervalDays = s.IntervalDays, MaxPages = s.MaxPages,
                Competitors = competitors.Take(SeoRunner.MaxCompetitors).ToList(),
                DisabledRules = (s.DisabledRules ?? []).Where(ruleIds.Contains).Distinct().OrderBy(r => r).ToList(),
            });
        }
        return (result, errors);
    }
}
