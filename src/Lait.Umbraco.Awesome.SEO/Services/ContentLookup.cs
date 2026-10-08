using Lait.Umbraco.Awesome.SEO.Crawling;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Core.PublishedCache;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services.Navigation;

namespace Lait.Umbraco.Awesome.SEO.Services;

/// <summary>What a crawled URL is in Umbraco: node id (shown), key (backoffice link) and document type (thresholds, filtering).</summary>
public sealed record ContentRef(int NodeId, Guid Key, string DocumentType);

/// <summary>
/// Maps crawled URLs of our own site to published content, by asking Umbraco for the URL of every published node
/// (as seen from the site's own address, so domain bindings resolve) and matching on host + path.
/// </summary>
public sealed class ContentLookup(
    IDocumentNavigationQueryService navigation, IPublishedContentCache cache, IPublishedUrlProvider urls, IUmbracoContextFactory umbracoContextFactory,
    ILogger<ContentLookup> logger)
{
    /// <summary>Lower-case host + path without trailing slash or query: scheme and trailing-slash differences must not break a match.</summary>
    public static string MatchKey(string url)
    {
        var u = new Uri(url);
        return (u.Host + u.AbsolutePath.TrimEnd('/')).ToLowerInvariant();
    }

    public async Task<IReadOnlyDictionary<string, ContentRef>> BuildAsync(string siteUrl)
    {
        var map = new Dictionary<string, ContentRef>();
        var current = new Uri(siteUrl);
        // The URL provider needs an UmbracoContext, which a background job does not have: create one for this scope.
        using var umbracoContext = umbracoContextFactory.EnsureUmbracoContext();
        if (!navigation.TryGetRootKeys(out var roots)) return map;

        var keys = new List<Guid>();
        foreach (var root in roots)
        {
            keys.Add(root);
            if (navigation.TryGetDescendantsKeys(root, out var descendants)) keys.AddRange(descendants);
        }

        foreach (var key in keys)
        {
            var content = await cache.GetByIdAsync(key);   // published only
            if (content is null) continue;
            var node = new ContentRef(content.Id, content.Key, content.ContentType.Alias);

            // Invariant content has no cultures; culture-variant content has one URL per culture.
            foreach (var culture in content.Cultures.Count == 0 ? [null] : content.Cultures.Keys.Select(c => (string?)c))
            {
                var url = urls.GetUrl(content, UrlMode.Absolute, culture, current);
                if (!Uri.TryCreate(url, UriKind.Absolute, out var abs)) continue;   // "#" = not routable
                map.TryAdd(MatchKey(abs.AbsoluteUri), node);
            }
        }
        logger.LogDebug("SEO content lookup: {Count} routable URLs for {Site}", map.Count, siteUrl);
        return map;
    }
}
