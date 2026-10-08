namespace Lait.Umbraco.Awesome.SEO.Crawling;

public static class UrlNormalizer
{
    /// <summary>Absolute http(s) URL without fragment, or null. Trailing slash is left alone (it is a distinct URL).</summary>
    public static string? Normalize(string href, string? baseUrl = null)
    {
        Uri? uri;
        if (baseUrl is not null && Uri.TryCreate(baseUrl, UriKind.Absolute, out var b)) Uri.TryCreate(b, href.Trim(), out uri);
        else Uri.TryCreate(href.Trim(), UriKind.Absolute, out uri);
        if (uri is null || uri.Scheme is not ("http" or "https")) return null;
        return new UriBuilder(uri) { Fragment = "" }.Uri.AbsoluteUri;
    }

    public static bool Same(string? a, string? b) =>
        a is not null && b is not null && Normalize(a) is { } x && x == Normalize(b);
}
