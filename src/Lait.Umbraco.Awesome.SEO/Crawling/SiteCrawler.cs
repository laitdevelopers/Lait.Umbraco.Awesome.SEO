using System.Diagnostics;
using System.Xml.Linq;
using Lait.Umbraco.Awesome.SEO.Rules;

namespace Lait.Umbraco.Awesome.SEO.Crawling;

public sealed class CrawlOptions
{
    public int MaxPages { get; init; } = 300;
    public int DelayMs { get; init; } = 250;
    public int MaxRedirects { get; init; } = 10;
    /// <summary>Product token matched against robots.txt groups.</summary>
    public string UserAgentToken { get; init; } = "LaitSeoReport";
    public string UserAgent { get; init; } = "LaitSeoReport/1.0 (+https://github.com/laitdevelopers/Lait.Umbraco.Awesome.SEO)";
    /// <summary>Whole request incl. reading the body (HttpClient.Timeout stops at the headers with ResponseHeadersRead).</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Bodies are cut here: enough for any real page, protects memory against huge or endless responses.</summary>
    public int MaxBodyBytes { get; init; } = 5 * 1024 * 1024;
    /// <summary>Sent with every request (the crawler never leaves the site). Only for the own site: e.g. Umbraco's basic-auth shared secret.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Sitemap first, then links; same host only; respects robots.txt. The HttpClient must have AllowAutoRedirect = false
/// (redirects are followed here so chains and loops can be reported).
/// </summary>
public sealed class SiteCrawler(HttpClient http, CrawlOptions? options = null)
{
    readonly CrawlOptions _o = options ?? new();
    bool _first = true;

    /// <param name="onProgress">Called after every fetched page with (pages fetched so far, pages still queued).</param>
    public async Task<SiteContext> CrawlAsync(string startUrl, CancellationToken ct = default, Action<int, int>? onProgress = null)
    {
        var start = UrlNormalizer.Normalize(startUrl) ?? throw new ArgumentException("Invalid start URL", nameof(startUrl));
        var host = new Uri(start).Host;
        var origin = new Uri(start).GetLeftPart(UriPartial.Authority);

        var robots = await LoadRobotsAsync(origin, ct);
        // Sitemaps listed in robots.txt on another host are ignored: we never fetch outside the site being crawled.
        var listed = robots.Sitemaps.Where(s => SameSite(s, host)).ToList();
        var (sitemapFound, sitemapUrls) = await LoadSitemapsAsync(listed.Count > 0 ? listed : [origin + "/sitemap.xml"], host, ct);

        var pages = new Dictionary<string, PageSnapshot>();
        var seen = new HashSet<string> { start };
        var blocked = new HashSet<string>();
        var queue = new Queue<string>([start]);
        foreach (var u in sitemapUrls) if (seen.Add(u)) queue.Enqueue(u);
        var truncated = false;

        while (queue.Count > 0)
        {
            var url = queue.Dequeue();
            if (!robots.IsAllowed(new Uri(url).PathAndQuery, _o.UserAgentToken)) { blocked.Add(url); continue; }
            if (pages.Count >= _o.MaxPages) { truncated = true; break; }

            PageSnapshot page;
            try { page = await FetchAsync(url, host, ct); }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // One page that times out, resets the connection or cannot be parsed must not end the whole crawl.
                page = new PageSnapshot { Url = url, FinalUrl = url, Status = 0 };
            }
            pages[url] = page;
            if (page.FinalUrl.Length > 0) seen.Add(page.FinalUrl);

            if (page.IsOk)
                foreach (var l in page.Html.Links.Where(l => l.Internal))
                    if (seen.Add(l.Href)) queue.Enqueue(l.Href);
            onProgress?.Invoke(pages.Count, queue.Count);
        }

        return new SiteContext
        {
            StartUrl = start,
            Pages = pages,
            SitemapUrls = sitemapUrls,
            SitemapFound = sitemapFound,
            Robots = robots,
            BlockedByRobots = blocked,
            Truncated = truncated,
        };
    }

    /// <summary>Same host, ignoring a leading "www." (lait.dk and www.lait.dk are one site).</summary>
    static bool SameSite(string url, string host) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && Bare(u.Host).Equals(Bare(host), StringComparison.OrdinalIgnoreCase);

    static string Bare(string host) => host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;

    async Task<PageSnapshot> FetchAsync(string url, string host, CancellationToken ct)
    {
        var chain = new HashSet<string> { url };
        var current = url;
        var redirects = 0;
        while (true)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, current);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_o.RequestTimeout);
            var sw = Stopwatch.StartNew();
            using var resp = await SendAsync(req, timeout.Token);
            var ttfb = sw.ElapsedMilliseconds;
            var headers = resp.Headers.Concat(resp.Content.Headers)
                .ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(", ", h.Value));
            var status = (int)resp.StatusCode;

            if (status is >= 300 and < 400 && resp.Headers.Location is { } loc)
            {
                var next = UrlNormalizer.Normalize(loc.ToString(), current);
                // A redirect off the site is reported, not followed: we only crawl the site we were asked to.
                if (next is null || redirects >= _o.MaxRedirects || !SameSite(next, host))
                    return new PageSnapshot { Url = url, FinalUrl = current, Status = status, Redirects = redirects, Headers = headers, TtfbMs = ttfb };
                redirects++;
                if (!chain.Add(next))
                    return new PageSnapshot { Url = url, FinalUrl = next, Status = status, Redirects = redirects, RedirectLoop = true, Headers = headers, TtfbMs = ttfb };
                current = next;
                continue;
            }

            var isHtml = resp.Content.Headers.ContentType?.MediaType is "text/html" or "application/xhtml+xml";
            var html = isHtml ? await ReadCappedAsync(resp.Content, timeout.Token) : "";
            return new PageSnapshot
            {
                Url = url,
                FinalUrl = current,
                Status = status,
                IsHtml = isHtml,
                Redirects = redirects,
                TtfbMs = ttfb,
                Headers = headers,
                Html = isHtml ? PageParser.Parse(html, current) : new ParsedHtml(),
            };
        }
    }

    async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        if (!_first) await Task.Delay(_o.DelayMs, ct);
        _first = false;
        req.Headers.UserAgent.ParseAdd(_o.UserAgent);
        foreach (var (name, value) in _o.Headers) req.Headers.TryAddWithoutValidation(name, value);
        return await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    /// <summary>Reads at most MaxBodyBytes, decoding with the response charset (UTF-8 if none or unknown).</summary>
    async Task<string> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        var buffer = new byte[_o.MaxBodyBytes];
        var read = 0;
        int n;
        while (read < buffer.Length && (n = await stream.ReadAsync(buffer.AsMemory(read), ct)) > 0) read += n;
        var encoding = System.Text.Encoding.UTF8;
        try { if (content.Headers.ContentType?.CharSet is { Length: > 0 } cs) encoding = System.Text.Encoding.GetEncoding(cs.Trim('"')); }
        catch (ArgumentException) { /* unknown charset: UTF-8 */ }
        return encoding.GetString(buffer, 0, read);
    }

    async Task<string?> GetTextAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_o.RequestTimeout);
        using var resp = await SendAsync(req, timeout.Token);
        return resp.IsSuccessStatusCode ? await ReadCappedAsync(resp.Content, timeout.Token) : null;
    }

    async Task<RobotsTxt> LoadRobotsAsync(string origin, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, origin + "/robots.txt");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_o.RequestTimeout);
            using var resp = await SendAsync(req, timeout.Token);
            return (int)resp.StatusCode switch
            {
                >= 500 => RobotsTxt.DisallowAll,   // RFC 9309: server error = assume complete disallow
                >= 400 => RobotsTxt.AllowAll,
                _ => RobotsTxt.Parse(await ReadCappedAsync(resp.Content, timeout.Token)),
            };
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return RobotsTxt.DisallowAll;   // unreachable robots.txt is treated like a 5xx (RFC 9309 §2.3.1.4)
        }
    }

    /// <summary>One level of sitemap index. ponytail: no .gz sitemaps, max 20 child sitemaps.</summary>
    async Task<(bool Found, HashSet<string> Urls)> LoadSitemapsAsync(IEnumerable<string> locations, string host, CancellationToken ct)
    {
        var urls = new HashSet<string>();
        var found = false;
        var todo = new Queue<(string Url, int Depth)>(locations.Select(l => (l, 0)));
        var fetched = 0;
        while (todo.Count > 0 && fetched++ < 20)
        {
            var (loc, depth) = todo.Dequeue();
            XDocument doc;
            if (!SameSite(loc, host)) continue;
            try
            {
                if (await GetTextAsync(loc, ct) is not { } xml) continue;
                doc = XDocument.Parse(xml);
            }
            catch (Exception e) when (e is HttpRequestException or IOException or System.Xml.XmlException or TaskCanceledException && !ct.IsCancellationRequested) { continue; }

            found = true;
            var isIndex = doc.Root?.Name.LocalName == "sitemapindex";
            foreach (var l in doc.Descendants().Where(e => e.Name.LocalName == "loc").Select(e => e.Value.Trim()))
            {
                if (isIndex) { if (depth == 0) todo.Enqueue((l, 1)); }
                else if (UrlNormalizer.Normalize(l) is { } u && SameSite(u, host)) urls.Add(u);
            }
        }
        return (found, urls);
    }
}
