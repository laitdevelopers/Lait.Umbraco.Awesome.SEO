namespace Lait.Umbraco.Awesome.SEO.Crawling;

public sealed record Link(string Href, string Text, bool Internal);

/// <summary>Everything extracted from one HTML document.</summary>
public sealed record ParsedHtml
{
    public string? Title { get; init; }
    public string? MetaDescription { get; init; }
    public string? Canonical { get; init; }
    public string? RobotsMeta { get; init; }
    public string? Lang { get; init; }
    public bool HasViewport { get; init; }
    public IReadOnlyList<int> HeadingLevels { get; init; } = [];
    public IReadOnlyList<string> H1s { get; init; } = [];
    public int ImagesMissingAlt { get; init; }
    public IReadOnlyList<Link> Links { get; init; } = [];
    public int MixedContentCount { get; init; }
    public bool EmptyAppRoot { get; init; }
    public int WordCount { get; init; }
    public string ContentHash { get; init; } = "";
    public string JsonLdHash { get; init; } = "";
    /// <summary>schema.org @type values found in JSON-LD (incl. @graph and nested objects), distinct.</summary>
    public IReadOnlyList<string> SchemaTypes { get; init; } = [];
    /// <summary>At least one JSON-LD block could not be parsed as JSON.</summary>
    public bool JsonLdInvalid { get; init; }
    public string? OgTitle { get; init; }
    public string? OgImage { get; init; }
    public bool HasTwitterCard { get; init; }
}

public sealed class PageSnapshot
{
    public required string Url { get; init; }
    public string FinalUrl { get; init; } = "";
    public int Status { get; init; }
    public bool IsHtml { get; init; }
    public int Redirects { get; init; }
    public bool RedirectLoop { get; init; }
    public long TtfbMs { get; init; }
    /// <summary>Lower-cased header names, joined values.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    public ParsedHtml Html { get; init; } = new();

    public bool IsOk => Status is >= 200 and < 300 && IsHtml;

    public bool Noindex =>
        (Html.RobotsMeta?.Contains("noindex", StringComparison.OrdinalIgnoreCase) ?? false)
        || (Headers.GetValueOrDefault("x-robots-tag")?.Contains("noindex", StringComparison.OrdinalIgnoreCase) ?? false);

    public bool Indexable => IsOk && !Noindex;

    /// <summary>Canonical missing or pointing at itself.</summary>
    public bool CanonicalIsSelf => Html.Canonical is null || UrlNormalizer.Same(Html.Canonical, Url) || UrlNormalizer.Same(Html.Canonical, FinalUrl);

    /// <summary>Gate for duplicate checks: indexable and not canonicalized elsewhere.</summary>
    public bool IsCanonicalPage => Indexable && CanonicalIsSelf;
}
