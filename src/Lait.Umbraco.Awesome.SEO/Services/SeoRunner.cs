using System.Collections.Concurrent;
using System.Text.Json;
using Lait.Umbraco.Awesome.SEO.Ai;
using Lait.Umbraco.Awesome.SEO.Configuration;
using Lait.Umbraco.Awesome.SEO.Crawling;
using Lait.Umbraco.Awesome.SEO.Persistence;
using Lait.Umbraco.Awesome.SEO.Rules;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lait.Umbraco.Awesome.SEO.Services;

public sealed record CompetitorResult(string Domain, int RunId, bool IsBaseline, IReadOnlyList<CompetitorChange> Changes);

/// <summary>Where a running crawl is, for the dashboard. Done/Total are pages in the crawl phases (Total = pages known so far, an upper bound).</summary>
public sealed record RunProgress(string Domain, string Phase, string? Detail, int Step, int Steps, int Done, int? Total, int ElapsedSeconds);

public sealed record RunFailure(string Domain, string Message, DateTime AtUtc);

public sealed record SeoRunResult(int RunId, double Score, bool IsBaseline, DiffResult Diff, IReadOnlyList<CompetitorResult> Competitors);

/// <summary>One full run for a site: crawl, rules, diff, store; then the competitors; then (unless baseline/dry run) the AI report.</summary>
public sealed class SeoRunner(IHttpClientFactory httpFactory, SeoStore store, ContentLookup contentLookup, AiPipeline aiPipeline, IOptionsMonitor<AiOptions> aiOptions, Microsoft.Extensions.Configuration.IConfiguration config, ILogger<SeoRunner> logger)
{
    public const string HttpClientName = "LaitSeo";
    public const int MaxCompetitors = 5;

    // One run at a time per process; across servers only the scheduling-role server runs (see SeoScheduler).
    readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsRunning => _gate.CurrentCount == 0;

    // ---- progress (read by the dashboard while a run is going) ----
    readonly object _progressLock = new();
    string _domain = "", _phase = "";
    string? _detail;
    int _step, _steps, _done;
    int? _total;
    DateTime _started;
    RunFailure? _lastFailure;

    public RunProgress? Progress
    {
        get
        {
            lock (_progressLock)
                return IsRunning && _domain.Length > 0
                    ? new RunProgress(_domain, _phase, _detail, _step, _steps, _done, _total, (int)(DateTime.UtcNow - _started).TotalSeconds)
                    : null;
        }
    }

    /// <summary>The last run that failed, until the next run starts.</summary>
    public RunFailure? LastFailure { get { lock (_progressLock) return _lastFailure; } }

    // ponytail: in memory, so a failure is forgotten on restart until the next scheduled run hits it again; persist it if that is too quiet
    readonly ConcurrentDictionary<string, RunFailure> _competitorFailures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Why the last crawl of this competitor (host as configured) failed, until a crawl of it succeeds. Nothing is stored for a failed crawl.</summary>
    public RunFailure? CompetitorFailure(string host) => _competitorFailures.GetValueOrDefault(host);

    void Set(string phase, int step, string? detail = null, int done = 0, int? total = null, int? steps = null)
    {
        lock (_progressLock) { _phase = phase; _step = step; _detail = detail; _done = done; _total = total; if (steps is { } n) _steps = n; }
    }

    /// <param name="useAi">false = dry run: only the free rule-based checks, no provider is called.</param>
    public async Task<SeoRunResult> RunAsync(SiteOptions site, CancellationToken ct = default, bool useAi = true)
    {
        await _gate.WaitAsync(ct);
        try { return await RunLockedAsync(site, ct, useAi); }
        finally { _gate.Release(); }
    }

    /// <summary>Starts a run in the background unless one is already going. Atomic, so a double click cannot queue a second run.</summary>
    public bool TryStart(SiteOptions site, bool useAi, Action<Exception> onError)
    {
        if (!_gate.Wait(0)) return false;
        _ = Task.Run(async () =>
        {
            try { await RunLockedAsync(site, CancellationToken.None, useAi); }
            catch (Exception e) { onError(e); }
            finally { _gate.Release(); }
        });
        return true;
    }

    async Task<SeoRunResult> RunLockedAsync(SiteOptions site, CancellationToken ct, bool useAi)
    {
        try
        {
            var competitorUrls = site.Competitors.Take(MaxCompetitors).ToList();
            var aiPossible = useAi && aiOptions.CurrentValue.Enabled;
            lock (_progressLock) { _domain = site.Domain; _started = DateTime.UtcNow; _lastFailure = null; }
            Set("crawl-own", 1, site.Domain, steps: 2 + competitorUrls.Count + (aiPossible ? 2 : 0));

            var ctx = await CrawlAsync(site.Url, site.MaxPages, ct, (done, queued) => Set("crawl-own", 1, site.Domain, done, Math.Min(site.MaxPages, done + queued)), OwnSiteHeaders());
            if (!ctx.Pages.Values.Any(p => p.Status > 0))
                throw new InvalidOperationException($"Ingen sider på {site.Domain} kunne hentes: {WhyNothing(ctx)} Intet er gemt.");
            if (Blocked(ctx) is { } why)
                throw new InvalidOperationException($"{site.Domain} afviser analysen: {why} Intet er gemt.");
            Set("analyze", 2);
            var disabled = site.DisabledRules.ToHashSet();
            var findings = await WithNodesAsync(RuleEngine.Run(ctx, disabled), site.Url);
            // A rule that was switched off is not "resolved": leave its old findings out of the diff.
            var previous = store.PreviousFindings(site.Domain)?.Where(f => !disabled.Contains(f.RuleId)).ToList();
            var baseline = previous is null;
            var diff = FindingDiff.Compare(previous ?? [], findings);
            var newKeys = baseline ? [] : diff.New.Select(f => f.DedupeKey).ToHashSet();
            var score = HealthScore.Compute(findings, ctx.Pages.Count, disabled).Score;

            var rows = findings.Select(f => Row(f, newKeys.Contains(f.DedupeKey)))
                .Concat(diff.Resolved.Select(f => Row(f, false)));   // resolved ones are kept so the dashboard can show them
            var runId = store.Save(
                new RunRow { Domain = site.Domain, StartedUtc = DateTime.UtcNow, PageCount = ctx.Pages.Count, Score = score, IsBaseline = baseline, MetricsJson = JsonSerializer.Serialize(BenchmarkMetrics.Compute(ctx, findings, disabled), Json) },
                rows, PageRows(ctx), []);   // own pages too: Search Console insights need their titles

            var competitors = new List<CompetitorResult>();
            for (var i = 0; i < competitorUrls.Count; i++)
            {
                var url = competitorUrls[i];
                var host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;
                var step = 3 + i;
                Set("crawl-competitor", step, host);
                try
                {
                    competitors.Add(await RunCompetitorAsync(url, site.MaxPages, disabled, ct, (done, queued) => Set("crawl-competitor", step, host, done, Math.Min(site.MaxPages, done + queued))));
                    _competitorFailures.TryRemove(host, out _);
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning(e, "SEO crawl of competitor {Url} failed", url);
                    _competitorFailures[host] = new RunFailure(host, e.Message.Length > 300 ? e.Message[..300] : e.Message, DateTime.UtcNow);
                }
            }

            // The first run is a baseline and never calls AI: drop the AI steps from the count.
            if (baseline) lock (_progressLock) _steps = 2 + competitorUrls.Count;
            if (aiPossible && !baseline)
                await RunAiAsync(runId, site.Domain, score, findings, diff, competitors, ct, phase => Set(phase, phase == "ai-triage" ? 3 + competitorUrls.Count : 4 + competitorUrls.Count));

            logger.LogInformation("SEO run {RunId} for {Domain}: score {Score:F0}, {New} new, {Resolved} resolved{Baseline}",
                runId, site.Domain, score, baseline ? 0 : diff.New.Count, diff.Resolved.Count, baseline ? " (baseline)" : "");
            return new SeoRunResult(runId, score, baseline, diff, competitors);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            lock (_progressLock) _lastFailure = new RunFailure(_domain, e.Message.Length > 300 ? e.Message[..300] : e.Message, DateTime.UtcNow);
            throw;
        }
    }

    /// <summary>
    /// Why the start page could not be read, when it is a block rather than a broken site: password protection (typical on
    /// Umbraco Cloud development/staging environments) or a bot challenge (Cloudflare). Saving such a run would wipe the report.
    /// </summary>
    internal static string? Blocked(SiteContext ctx)
    {
        if (ctx.Pages.GetValueOrDefault(ctx.StartKey) is not { } home) return null;
        var challenged = home.Headers.ContainsKey("cf-mitigated");
        return home.Status switch
        {
            401 => "forsiden kræver login (HTTP 401). Er miljøet beskyttet med adgangskode, fx et Umbraco Cloud dev- eller staging-miljø, så analysér det offentlige live-site i stedet.",
            403 or 429 or 503 when challenged => $"forsiden svarer med en bot-kontrol fra Cloudflare (HTTP {home.Status}). Tillad brugeragenten \"LaitSeoReport\" i Cloudflare (WAF / Bot Fight Mode) for sitet.",
            403 => "forsiden svarer HTTP 403 (adgang nægtet). Tjek IP-begrænsninger, adgangskode eller bot-beskyttelse for sitet.",
            _ => null,
        };
    }

    /// <summary>Why a crawl got no readable page, in words an editor can act on (Danish, for the dashboard).</summary>
    internal static string WhyNothing(SiteContext ctx)
    {
        var start = new Uri(ctx.StartUrl);
        var origin = start.GetLeftPart(UriPartial.Authority);
        var other = start.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? start.Host[4..] : "www." + start.Host;
        if (ReferenceEquals(ctx.Robots, RobotsTxt.DisallowAll))
            return $"robots.txt på {origin} kunne ikke hentes (serverfejl eller intet svar), og så må crawlere ikke hente nogen sider.";
        if (ctx.BlockedByRobots.Contains(ctx.StartUrl))
            return $"robots.txt på {origin} forbyder crawlere at hente forsiden. Ligger sitet på {other}, så ret adressen til {start.Scheme}://{other}/.";
        var status = ctx.Pages.GetValueOrDefault(ctx.StartKey)?.Status ?? 0;
        return status switch
        {
            0 => $"{start.Host} svarer ikke.",
            401 or 403 or 429 => $"Forsiden afviser crawleren (HTTP {status}), typisk på grund af login eller bot-beskyttelse.",
            _ => $"Forsiden svarer HTTP {status}, og ingen sider kunne læses.",
        };
    }

    /// <summary>Own site only: attach node id/key/document type to findings whose URL is a published node. Never fails the run.</summary>
    async Task<List<Finding>> WithNodesAsync(List<Finding> findings, string siteUrl)
    {
        try
        {
            var map = await contentLookup.BuildAsync(siteUrl);
            if (map.Count == 0) return findings;
            return findings.Select(f => map.TryGetValue(ContentLookup.MatchKey(f.Url), out var n)
                ? f with { NodeId = n.NodeId, NodeKey = n.Key, DocumentType = n.DocumentType } : f).ToList();
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "SEO content lookup failed for {Site}; findings are stored without node ids", siteUrl);
            return findings;
        }
    }

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    async Task RunAiAsync(int runId, string domain, double score, List<Finding> findings, DiffResult diff, List<CompetitorResult> competitors, CancellationToken ct, Action<string> onPhase)
    {
        var titles = RuleCatalog.All.ToDictionary(r => r.Id, r => r.Title);
        var input = new AiInput(domain, score,
            findings.Count(f => f.Status == FindingStatus.Open), diff.Resolved.Count,
            diff.New.Select(f => new OwnChange(f.RuleId, titles.GetValueOrDefault(f.RuleId, f.RuleId), f.Severity.ToString(), f.Url, f.Evidence)).ToList(),
            competitors.Where(c => c.Changes.Count > 0).Select(c => new CompetitorInput(c.Domain, c.Changes)).ToList());
        if (input.NewFindings.Count == 0 && input.Competitors.Count == 0) return; // nothing changed, nothing to pay for

        var ai = aiOptions.CurrentValue;
        var report = new ReportRow { RunId = runId, CreatedUtc = DateTime.UtcNow, ReportModel = ai.CanWrite ? ai.Claude.ReportModel : "", TriageProvider = ai.UseJev ? "Jev" : "Claude" };

        // Hard cap: the estimate is an upper bound, so a run that could take the month over budget is not started.
        // The gate makes check → spend → save atomic against a plan being written at the same time.
        await AiBudget.Gate.WaitAsync(ct);
        try { await SpendAsync(); }
        finally { AiBudget.Gate.Release(); }

        async Task SpendAsync()
        {
        var used = store.TokensThisMonth(DateTime.UtcNow);
        var spent = store.SpendThisMonthUsd(DateTime.UtcNow);
        var est = TokenEstimator.Estimate(ai, input.Competitors.Count, used, spent);
        if (AiBudget.Check(ai, est, used, spent) is { } reason)
        {
            report.Status = "skipped-limit";
            report.Error = reason;
            store.SaveReport(report);
            logger.LogWarning("SEO AI report for {Domain} skipped: {Reason}", domain, reason);
            return;
        }

        try
        {
            var o = await aiPipeline.RunAsync(input, ct, onPhase);
            report.Status = o.Report is not null ? "ok" : o.Unreadable ? "failed" : "nothing-important";
            if (o.Unreadable) report.Error = "AI-svaret kunne ikke læses. Prøv igen ved næste kørsel.";
            report.Json = o.Report is null ? null : JsonSerializer.Serialize(o.Report, Json);
            report.TriageProvider = o.TriageProvider.Length > 0 ? o.TriageProvider : report.TriageProvider;
            (report.ChatInputTokens, report.ChatOutputTokens, report.JevInputTokens) = (o.ChatInputTokens, o.ChatOutputTokens, o.JevInputTokens);
            report.CostUsd = o.CostUsd;
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            logger.LogError(e, "SEO AI report for {Domain} failed", domain);
            report.Status = "failed";
            report.Error = e.Message.Length > 500 ? e.Message[..500] : e.Message;
            if (e is AiException { Spent: { } s })   // what was paid before it failed still counts towards the budget
                (report.ChatInputTokens, report.ChatOutputTokens, report.JevInputTokens, report.CostUsd) = (s.ChatIn, s.ChatOut, s.JevIn, s.CostUsd);
        }
        store.SaveReport(report);
        }
    }

    async Task<CompetitorResult> RunCompetitorAsync(string url, int maxPages, IReadOnlySet<string> disabled, CancellationToken ct, Action<int, int> onProgress)
    {
        var ctx = await CrawlAsync(url, maxPages, ct, onProgress);
        // Without a single readable page every measurement would be 0 and compare as if it were real; keep the last good run instead.
        if (!ctx.Pages.Values.Any(p => p.IsOk)) throw new InvalidOperationException(WhyNothing(ctx));
        var theirFindings = RuleEngine.Run(ctx, disabled);   // not stored: only used for their score and metrics in the comparison
        var domain = new Uri(ctx.StartUrl).Host;
        var fingerprints = ctx.Pages.Values.Select(PageFingerprint.From).ToDictionary(p => p.Url);
        var snapshot = new CompetitorSnapshot(ctx.SitemapUrls, fingerprints);

        var previous = store.PreviousSnapshot(domain);
        var changes = previous is null ? [] : CompetitorDiff.Compare(previous, snapshot);

        var pages = PageRows(ctx);

        var runId = store.Save(
            new RunRow
            {
                Domain = domain, IsCompetitor = true, StartedUtc = DateTime.UtcNow, PageCount = ctx.Pages.Count, IsBaseline = previous is null,
                Score = HealthScore.Compute(theirFindings, ctx.Pages.Count, disabled).Score, MetricsJson = JsonSerializer.Serialize(BenchmarkMetrics.Compute(ctx, theirFindings, disabled), Json),
            },
            [], pages, changes.Select(c => new ChangeRow { Url = c.Url, Kind = c.Kind, Field = c.Field, OldValue = c.Old, NewValue = c.New }));
        return new CompetitorResult(domain, runId, previous is null, changes);
    }

    static IEnumerable<PageRow> PageRows(SiteContext ctx) => ctx.Pages.Values.Select(p => new PageRow
    {
        Url = p.Url, Status = p.Status, Title = p.Html.Title, H1 = p.Html.H1s.FirstOrDefault(), MetaDescription = p.Html.MetaDescription,
        Canonical = p.Html.Canonical, JsonLdHash = p.Html.JsonLdHash, WordCount = p.Html.WordCount, InSitemap = ctx.SitemapUrls.Contains(p.Url),
    }).Concat(ctx.SitemapUrls.Where(u => !ctx.Pages.ContainsKey(u)).Select(u => new PageRow { Url = u, InSitemap = true })); // sitemap-only URLs, so CMP-01 still sees them next time

    Task<SiteContext> CrawlAsync(string url, int maxPages, CancellationToken ct, Action<int, int>? onProgress = null, IReadOnlyDictionary<string, string>? headers = null) =>
        new SiteCrawler(httpFactory.CreateClient(HttpClientName), new CrawlOptions { MaxPages = maxPages, Headers = headers ?? new Dictionary<string, string>() }).CrawlAsync(url, ct, onProgress);

    /// <summary>
    /// Umbraco basic auth (Umbraco Cloud protects development/staging this way) lets the site call itself with a shared secret header.
    /// Sent only when crawling the own site, never to competitors.
    /// </summary>
    internal IReadOnlyDictionary<string, string> OwnSiteHeaders() => SharedSecretHeader(config);

    internal static IReadOnlyDictionary<string, string> SharedSecretHeader(Microsoft.Extensions.Configuration.IConfiguration config)
    {
        var secret = config["Umbraco:CMS:BasicAuth:SharedSecret:Value"];
        if (string.IsNullOrWhiteSpace(secret)) return new Dictionary<string, string>();
        var name = config["Umbraco:CMS:BasicAuth:SharedSecret:HeaderName"];
        return new Dictionary<string, string> { [string.IsNullOrWhiteSpace(name) ? "X-Authentication-Shared-Secret" : name] = secret };
    }

    static FindingRow Row(Finding f, bool isNew) =>
        new()
        {
            RuleId = f.RuleId, Url = f.Url, Severity = (int)f.Severity, Evidence = f.Evidence, Status = (int)f.Status, IsNew = isNew,
            NodeId = f.NodeId, NodeKey = f.NodeKey, DocumentType = f.DocumentType,
        };
}
