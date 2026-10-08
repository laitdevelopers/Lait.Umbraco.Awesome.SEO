namespace Lait.Umbraco.Awesome.SEO.Configuration;

/// <summary>
/// Bound from the "LaitSeo" section. <see cref="Sites"/> only seeds the database once (first start with no saved sites);
/// after that the Indstillinger tab is the source of truth, see <see cref="SiteSettings"/>.
/// </summary>
public sealed class SeoOptions
{
    public const string SectionName = "LaitSeo";
    public List<SiteOptions> Sites { get; set; } = [];
}

public sealed class SiteOptions
{
    public string Url { get; set; } = "";
    /// <summary>Saved but switched off: not crawled by the scheduler.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Days between runs: daily 1, weekly 7, every 2nd week 14, monthly 30, every 2nd month 60.</summary>
    public int IntervalDays { get; set; } = 7;
    public int MaxPages { get; set; } = 300;
    /// <summary>2-5 competitors; entries beyond 5 are ignored.</summary>
    public List<string> Competitors { get; set; } = [];
    public List<string> DisabledRules { get; set; } = [];

    public string Domain => new Uri(Url).Host;
}
