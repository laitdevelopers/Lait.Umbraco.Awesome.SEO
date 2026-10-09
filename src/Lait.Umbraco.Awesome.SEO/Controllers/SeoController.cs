using System.Text.Json;
using Lait.Umbraco.Awesome.SEO.Ai;
using Lait.Umbraco.Awesome.SEO.Composing;
using Lait.Umbraco.Awesome.SEO.Configuration;
using Lait.Umbraco.Awesome.SEO.Models;
using Lait.Umbraco.Awesome.SEO.Persistence;
using Lait.Umbraco.Awesome.SEO.Rules;
using Lait.Umbraco.Awesome.SEO.Search;
using Lait.Umbraco.Awesome.SEO.Services;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Api.Common.Attributes;
using Umbraco.Cms.Web.Common.Authorization;
using Umbraco.Cms.Web.Common.Routing;

namespace Lait.Umbraco.Awesome.SEO.Controllers;

/// <summary>Custom backoffice API under /umbraco/lait-seo/api/v1/ (Umbraco's documented pattern for package APIs).</summary>
[ApiController]
[BackOfficeRoute("lait-seo/api/v{version:apiVersion}")]
[MapToApi(SeoApi.Name)]
[ApiVersion("1.0")]
[ApiExplorerSettings(GroupName = "Lait SEO")]
[Authorize(Policy = AuthorizationPolicies.BackOfficeAccess)]
public class SeoController(SeoStore store, SeoRunner runner, SiteSettings sites, AiSettings aiSettings, ConnectionTester tester, PlanService plans, global::Umbraco.Cms.Core.Services.IMediaService mediaService, IOptionsMonitor<AiOptions> aiOptions,
    SearchConsoleSettings searchSettings, SearchConsoleClient searchConsole, IMemoryCache cache, ContentLookup contentLookup, ILogger<SeoController> logger) : ControllerBase
{
    static readonly Dictionary<string, Rule> Rules = RuleCatalog.All.ToDictionary(r => r.Id);

    SiteOptions? Site(string domain) =>
        sites.Active().FirstOrDefault(s => s.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase));

    [HttpGet("domains")]
    [ProducesResponseType(typeof(IEnumerable<DomainSummary>), StatusCodes.Status200OK)]
    public IActionResult Domains() => Ok(sites.Active()
        .Select(s =>
        {
            var run = store.LatestRun(s.Domain, false);
            var progress = runner.Progress is { } p && p.Domain.Equals(s.Domain, StringComparison.OrdinalIgnoreCase) ? p : null;
            var failure = runner.LastFailure is { } f && f.Domain.Equals(s.Domain, StringComparison.OrdinalIgnoreCase) ? f.Message : null;
            return new DomainSummary(s.Domain, s.Url, run?.StartedUtc, run?.Score, run?.PageCount, run?.IsBaseline, runner.IsRunning, progress, failure);
        }));

    [HttpGet("report")]
    [ProducesResponseType(typeof(ReportDto), StatusCodes.Status200OK)]
    public IActionResult Report(string domain)
    {
        var site = Site(domain);
        if (site is null) return NotFound();

        var run = store.LatestRun(site.Domain, false);
        var rows = run is null ? [] : store.FindingRows(run.Id);
        var findings = rows.Select(Dto).ToList();
        var open = findings.Where(f => f.Status == nameof(FindingStatus.Open)).ToList();
        // Recomputed from the stored findings, so a run from before the weighted score still shows it (and the breakdown matches the number).
        var score = run is null ? null : HealthScore.Compute(rows.Select(ToFinding), run.PageCount, site.DisabledRules.ToHashSet());

        var competitors = site.Competitors.Take(SeoRunner.MaxCompetitors)
            .Where(u => Uri.TryCreate(u, UriKind.Absolute, out _))
            .Select(u => store.LatestRun(new Uri(u).Host, true))
            .OfType<RunRow>()
            .Select(r => new CompetitorDto(r.Domain, r.StartedUtc, r.IsBaseline, r.PageCount,
                store.ChangeRows(r.Id).Select(c => new ChangeDto(c.Url, c.Kind, c.Field, c.OldValue, c.NewValue)).ToList()))
            .ToList();

        return Ok(new ReportDto(
            run is null ? null : new RunDto(run.Id, run.StartedUtc, score!.Score, run.PageCount, run.IsBaseline),
            new Counts(open.Count(f => f.IsNew), open.Count, findings.Count(f => f.Status == nameof(FindingStatus.Resolved)),
                open.Count(f => f.Severity == nameof(Severity.Critical)), open.Count(f => f.Severity == nameof(Severity.Warning)), open.Count(f => f.Severity == nameof(Severity.Info))),
            findings, competitors, run is null ? null : Ai(run.Id))
        {
            Score = score,
            CompetitorFailures = site.Competitors.Take(SeoRunner.MaxCompetitors)
                .Where(u => Uri.TryCreate(u, UriKind.Absolute, out _))
                .Select(u => runner.CompetitorFailure(new Uri(u).Host)).OfType<RunFailure>().ToList(),
            Passed = run is null ? [] : RuleCatalog.All
                .Where(r => !site.DisabledRules.Contains(r.Id) && !open.Any(f => f.RuleId == r.Id))
                .Select(r => new PassedCheck(r.Id, r.Title, r.Category.ToString())).ToList(),
        });
    }

    static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    static SiteMetrics? Metrics(RunRow? run)
    {
        if (run?.MetricsJson is not { } json) return null;
        try { return JsonSerializer.Deserialize<SiteMetrics>(json, WebJson); }
        catch (JsonException) { return null; }
    }

    /// <summary>Own site vs competitors, from the latest crawl of each: side-by-side numbers, gaps, and sections only they have.</summary>
    [HttpGet("benchmark")]
    [ProducesResponseType(typeof(BenchmarkDto), StatusCodes.Status200OK)]
    public IActionResult BenchmarkFor(string domain)
    {
        var site = Site(domain);
        if (site is null) return NotFound();

        var (ownRun, bm, _, message) = Compare(site);
        return Ok(new BenchmarkDto(bm is not null, message, ownRun?.StartedUtc, bm));
    }

    /// <summary>Latest own crawl vs latest competitor crawls. Benchmark is null (with a Danish message) when something is missing.</summary>
    (RunRow? Run, Benchmark? Benchmark, List<Finding> Findings, string? Message) Compare(SiteOptions site)
    {
        var ownRun = store.LatestRun(site.Domain, false);
        var own = Metrics(ownRun);
        var findings = ownRun is null ? [] : store.FindingRows(ownRun.Id).Select(ToFinding).ToList();
        if (ownRun is null || own is null)
            return (ownRun, null, findings, "Kør et nyt crawl for at få sammenligningen: det seneste crawl er fra før sammenligningen fandtes.");

        var competitors = site.Competitors.Take(SeoRunner.MaxCompetitors)
            .Where(u => Uri.TryCreate(u, UriKind.Absolute, out _))
            .Select(u => store.LatestRun(new Uri(u).Host, true))
            .Select(r => (Run: r, Metrics: Metrics(r)))
            .Where(x => x.Run is not null && x.Metrics is not null)
            .Select(x => (x.Run!.Domain, x.Metrics!))
            .ToList();
        if (competitors.Count == 0)
            return (ownRun, null, findings, site.Competitors.Count == 0 ? "Tilføj konkurrenter under Indstillinger for at sammenligne." : "Kør et nyt crawl for at få konkurrenternes tal.");
        return (ownRun, BenchmarkBuilder.Build(site.Domain, own, findings, competitors), findings, null);
    }

    PlanDto BuildPlan(SiteOptions site, out RunRow? run)
    {
        var (ownRun, bm, findings, message) = Compare(site);
        run = ownRun;
        var items = PlanBuilder.Build(bm, findings);
        StoredPlan? stored = null;
        var row = ownRun is null ? null : store.PlanFor(ownRun.Id);
        if (row?.Json is not null)
            try { stored = JsonSerializer.Deserialize<StoredPlan>(row.Json, WebJson); }
            catch (JsonException) { /* older shape: show the rule-based plan */ }
        var source = row is null ? "rules" : row.TriageProvider.ToLowerInvariant();
        return new PlanDto(source, stored?.Summary, row?.CreatedUtc, row?.CostUsd, PlanBuilder.Apply(items, stored), message);
    }

    /// <summary>
    /// Search Console for the site: the last 28 days with final data vs the 28 days before, crossed with the latest crawl (titles, findings).
    /// Google is asked at most every 6 hours per site; the crossing with the crawl is redone on every call.
    /// </summary>
    [HttpGet("search")]
    [ProducesResponseType(typeof(SearchDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> SearchFor(string domain, CancellationToken ct)
    {
        if (Site(domain) is not { } site) return NotFound();
        var (account, _, unreadable) = searchSettings.Current();
        if (account is null)
            return Ok(new SearchDto(false, unreadable ? "Den gemte Search Console-nøgle kan ikke længere læses (serverens Data Protection-nøgler er skiftet). Indsæt den igen under Indstillinger." : null, null, null, null));
        try
        {
            var (property, data, content) = await cache.GetOrCreateAsync($"lait-seo-search|{site.Domain}|{account.ClientEmail}", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(6);
                var property = SearchConsoleClient.MatchProperty(site.Url, await searchConsole.PropertiesAsync(account, ct))
                    ?? throw new SearchConsoleException($"{account.ClientEmail} har ikke adgang til en Search Console-property for {site.Domain}. Tilføj den som bruger på property'en (domæne eller URL-præfiks) i Search Console.");
                return (property, await searchConsole.FetchAsync(account, property, DateOnly.FromDateTime(DateTime.UtcNow), ct), await contentLookup.BuildAsync(site.Url));
            });
            var run = store.LatestRun(site.Domain, false);
            var ownPages = Crawled(run).GroupBy(p => ContentLookup.MatchKey(p.Url)).ToDictionary(g => g.Key, g => g.First());
            var findings = run is null ? [] : store.FindingRows(run.Id).Select(ToFinding).ToList();
            var competitors = site.Competitors.Take(SeoRunner.MaxCompetitors)
                .Where(u => Uri.TryCreate(u, UriKind.Absolute, out _))
                .Select(u => store.LatestRun(new Uri(u).Host, true)).OfType<RunRow>()
                .Select(r => (r.Domain, (IReadOnlyList<CrawledPage>)Crawled(r).ToList())).ToList();
            var insights = SearchInsightsBuilder.Build(data, ownPages, findings, competitors, SearchInsightsBuilder.Brand(site.Url));
            var urls = insights.NearTop.Concat(insights.LowCtr).Select(o => o.Row.Page)
                .Concat(insights.TopQueries.Select(q => q.Page)).Concat(insights.Declining.Select(d => d.Page))
                .Concat(insights.Cannibals.SelectMany(c => c.Pages.Select(p => p.Page))).Concat(insights.TrafficWithFindings.Select(t => t.Page))
                .Concat(insights.Gaps.Select(g => g.OwnUrl));
            var nodes = urls.Distinct().Where(u => Uri.TryCreate(u, UriKind.Absolute, out _))
                .Select(u => content.TryGetValue(ContentLookup.MatchKey(u), out var n) ? new PageNode(u, n.Key) : null).OfType<PageNode>().ToList();
            return Ok(new SearchDto(true, null, property, account.ClientEmail, insights) { Nodes = nodes });
        }
        catch (SearchConsoleException e) { return Ok(new SearchDto(true, e.Message, null, account.ClientEmail, null)); }
        catch (HttpRequestException e)
        {
            logger.LogWarning(e, "Search Console for {Domain} could not be reached", site.Domain);
            return Ok(new SearchDto(true, "Google Search Console kunne ikke nås. Prøv igen om lidt.", null, account.ClientEmail, null));
        }
    }

    /// <summary>The pages of a run that answered 200 (not sitemap-only URLs), as the Search Console insights compare them.</summary>
    IEnumerable<CrawledPage> Crawled(RunRow? run) => (run is null ? [] : store.PageRows(run.Id))
        .Where(p => p.Status == 200 && Uri.TryCreate(p.Url, UriKind.Absolute, out _))
        .Select(p => new CrawledPage(p.Url, p.Title, p.H1, p.WordCount, p.JsonLdHash.Length > 0));

    SearchSettingsDto SearchSettingsNow()
    {
        var (account, source, unreadable) = searchSettings.Current();
        return new SearchSettingsDto(source.ToString().ToLowerInvariant(), account?.ClientEmail, unreadable);
    }

    /// <summary>Saves (encrypted) or clears the service account key. Admin only.</summary>
    [HttpPut("settings/search")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdminAccess)]
    [ProducesResponseType(typeof(SearchSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(IEnumerable<string>), StatusCodes.Status400BadRequest)]
    public IActionResult SaveSearch([FromBody] SearchSaveRequest request)
    {
        if (request.Clear) searchSettings.Save(null);
        else if (request.ServiceAccountJson is not { Length: <= 20_000 } json || ServiceAccount.Parse(json) is null)
            return BadRequest(new[] { "Det ligner ikke en JSON-nøgle for en service-konto. Indsæt hele filen fra Google Cloud (den har client_email og private_key)." });
        else searchSettings.Save(json.Trim());
        return Ok(SearchSettingsNow());
    }

    /// <summary>Lists the properties the key can read, and which one each site would use. A typed key is tested without being saved. Admin only.</summary>
    [HttpPost("settings/search/test")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdminAccess)]
    [ProducesResponseType(typeof(SearchTestResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> TestSearch([FromBody] SearchTestRequest request, CancellationToken ct)
    {
        var typed = !string.IsNullOrWhiteSpace(request.ServiceAccountJson);
        var account = typed ? (request.ServiceAccountJson!.Length <= 20_000 ? ServiceAccount.Parse(request.ServiceAccountJson) : null) : searchSettings.Current().Account;
        if (account is null)
            return Ok(new SearchTestResult(false, typed ? "Det ligner ikke en JSON-nøgle for en service-konto." : "Der er ingen nøgle at teste.", [], []));
        try
        {
            var properties = await searchConsole.PropertiesAsync(account, ct);
            var matches = sites.Active().Select(s => new SiteProperty(s.Domain, SearchConsoleClient.MatchProperty(s.Url, properties))).ToList();
            return Ok(properties.Count == 0
                ? new SearchTestResult(false, $"Nøglen virker, men {account.ClientEmail} er ikke tilføjet som bruger på nogen property i Search Console endnu.", properties, matches)
                : new SearchTestResult(true, $"Forbindelsen virker. {account.ClientEmail} kan læse {properties.Count} {(properties.Count == 1 ? "property" : "properties")}.", properties, matches));
        }
        catch (SearchConsoleException e) { return Ok(new SearchTestResult(false, e.Message, [], [])); }
        catch (HttpRequestException) { return Ok(new SearchTestResult(false, "Google kunne ikke nås fra serveren.", [], [])); }
    }

    /// <summary>The media item behind an image URL, so "Ret med AI" can open it in Copilot. Also matches CDN URLs that drop the /media prefix.</summary>
    [HttpGet("media")]
    [ProducesResponseType(typeof(MediaRefDto), StatusCodes.Status200OK)]
    public IActionResult MediaFor(string src)
    {
        if (!Uri.TryCreate(src, UriKind.Absolute, out var u)) return NotFound();
        var path = Uri.UnescapeDataString(u.AbsolutePath);
        foreach (var candidate in new[] { path, "/media" + path })
            if (mediaService.GetMediaByPath(candidate) is { } media) return Ok(new MediaRefDto(media.Key, media.Name ?? ""));
        return NotFound();
    }

    /// <summary>The action plan: always the measured, rule-based items; AI additions from the latest "Skriv med AI" for this crawl are laid on top.</summary>
    [HttpGet("plan")]
    [ProducesResponseType(typeof(PlanDto), StatusCodes.Status200OK)]
    public IActionResult Plan(string domain) => Site(domain) is { } site ? Ok(BuildPlan(site, out _)) : NotFound();

    /// <summary>What "Skriv med AI" would cost at most, and whether it is allowed. Admin only.</summary>
    [HttpGet("plan/estimate")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdminAccess)]
    [ProducesResponseType(typeof(PlanEstimate), StatusCodes.Status200OK)]
    public IActionResult PlanEstimateFor(string domain)
    {
        if (Site(domain) is not { } site) return NotFound();
        return Ok(plans.Estimate(BuildPlan(site, out _).Items.Count));
    }

    /// <summary>Jev rates the items, Claude writes why/steps (whichever keys exist). Admin only, costs money, hard-capped by the monthly budget.</summary>
    [HttpPost("plan")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdminAccess)]
    [ProducesResponseType(typeof(PlanDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> WritePlan(string domain, CancellationToken ct)
    {
        if (Site(domain) is not { } site) return NotFound();
        var plan = BuildPlan(site, out var run);
        if (run is null) return Conflict(new { message = "Kør et crawl først." });
        try
        {
            var (row, blocked) = await plans.GenerateAsync(run.Id, site.Domain, plan.Items, ct);
            if (blocked is not null) return Conflict(new { message = blocked });
        }
        catch (AiException e) { return StatusCode(StatusCodes.Status502BadGateway, new { message = e.Message }); }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(e, "SEO action plan for {Domain} failed", domain);
            return StatusCode(StatusCodes.Status502BadGateway, new { message = "AI-udbyderen svarede ikke: " + (e.Message.Length > 200 ? e.Message[..200] : e.Message) });
        }
        return Ok(BuildPlan(site, out _));
    }

    AiDto? Ai(int runId)
    {
        if (store.ReportFor(runId) is not { } r) return null;
        AiReport? report = null;
        if (r.Json is not null)
            try { report = JsonSerializer.Deserialize<AiReport>(r.Json, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
            catch (JsonException) { /* stored by an older shape: show without report */ }
        return new AiDto(r.Status, r.Error, r.CreatedUtc, r.TriageProvider, r.ReportModel, r.ChatInputTokens + r.ChatOutputTokens, r.JevInputTokens, r.CostUsd, report);
    }

    /// <summary>Token upper bound for the next AI run, for the "Kør nu" confirmation, plus what is sent to whom. Admin only, like running.</summary>
    [HttpGet("estimate")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdminAccess)]
    [ProducesResponseType(typeof(EstimateDto), StatusCodes.Status200OK)]
    public IActionResult Estimate(string domain)
    {
        var site = Site(domain);
        if (site is null) return NotFound();
        var ai = aiOptions.CurrentValue;
        var e = TokenEstimator.Estimate(ai, Math.Min(site.Competitors.Count, SeoRunner.MaxCompetitors), store.TokensThisMonth(DateTime.UtcNow), store.SpendThisMonthUsd(DateTime.UtcNow));

        var flow = new List<string>();
        if (ai.Enabled) flow.Add("Claude (Anthropic): ændringer siden sidste kørsel (regel, URL og målt værdi) og for konkurrenter offentligt udtrukne felter som title, H1 og meta description. Aldrig persondata.");
        if (ai.UseJev) flow.Add("Jev (TypeSafe, US-baseret): de samme korte beskrivelser af hver ændring, kun til triage.");
        return Ok(new EstimateDto(ai.Enabled, e.TriageProvider, e.InputTokens, e.OutputTokens, e.CostUsd, e.MonthlyBudgetUsd, e.SpentThisMonthUsd, e.PricesFromList, flow));
    }

    /// <summary>Starts a run in the background (202); poll <c>domains</c> for the Running flag. <paramref name="dryRun"/> = no AI.</summary>
    [HttpPost("run")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdminAccess)]
    public IActionResult Run(string domain, bool dryRun = false)
    {
        var site = Site(domain);
        if (site is null) return NotFound();
        // Not tied to the request: the crawl outlives it. Errors are logged and shown in the dashboard (LastFailure).
        if (!runner.TryStart(site, !dryRun, e => logger.LogError(e, "Manual SEO run for {Domain} failed", site.Domain)))
            return Conflict("A run is already in progress.");
        logger.LogInformation("SEO run requested for {Domain} from the dashboard (dry run: {DryRun})", site.Domain, dryRun);
        return Accepted();
    }

    /// <summary>Settings tab data. Admin only (the tab hides itself on 403).</summary>
    [HttpGet("settings")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdminAccess)]
    [ProducesResponseType(typeof(SettingsDto), StatusCodes.Status200OK)]
    public IActionResult Settings() => Ok(new SettingsDto(
        sites.All().Select(s => new SiteDto(s.Url, s.Enabled, s.IntervalDays, s.MaxPages, s.Competitors, s.DisabledRules)).ToList(),
        sites.UmbracoDomainUrls(),
        RuleCatalog.All.Select(r => new RuleInfo(r.Id, r.Category.ToString(), r.DefaultSeverity.ToString(), r.Title, HealthScore.Weight(r.Id))).ToList(),
        AiDtoNow(), SearchSettingsNow()));

    AiSettingsDto AiDtoNow()
    {
        var ai = aiSettings.CurrentValue;
        var (claudeSource, jevSource) = aiSettings.KeySources();
        static string? Hint(string? key) => key is { Length: >= 12 } ? "…" + key[^4..] : null;
        var prices = AiPricing.Resolve(ai);
        var warnings = new List<string>();
        if (ai.MonthlyBudgetUsd is > 0 && AiPricing.Cost(prices, ai.UseJev, 1, 1, 1, 1, 1) is null)
            warnings.Add("Der er sat et månedligt budget, men en pris mangler for de modeller, der bruges. Indtil priserne er sat, springes AI-rapporter over.");
        return new AiSettingsDto(ai.Enabled, ai.UseJev ? "Jev" : "Claude",
            new ClaudeSettingsDto(claudeSource.ToString().ToLowerInvariant(), Hint(ai.Claude.ApiKey), ai.Claude.BaseUrl, ai.Claude.TriageModel, ai.Claude.ReportModel),
            new JevSettingsDto(jevSource.ToString().ToLowerInvariant(), Hint(ai.Jev.ApiKey), ai.Jev.Model),
            ai.MonthlyBudgetUsd, store.SpendThisMonthUsd(DateTime.UtcNow),
            new PricesDto(prices.ReportInput, prices.ReportOutput, prices.TriageInput, prices.TriageOutput, prices.JevInput),
            prices.FromList, AiPricing.ListPricesAsOf, warnings,
            aiSettings.StoredKeyUnreadable);
    }

    /// <summary>Saves the AI settings. Keys are encrypted before they are stored; empty fields fall back to appsettings.</summary>
    [HttpPut("settings/ai")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdminAccess)]
    [ProducesResponseType(typeof(AiSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(IEnumerable<string>), StatusCodes.Status400BadRequest)]
    public IActionResult SaveAi([FromBody] AiSaveRequest request)
    {
        var errors = AiSettingsValidator.Validate(request);
        if (errors.Count > 0) return BadRequest(errors);

        var stored = aiSettings.Stored();
        string? Blank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

        if (request.Claude.ClearKey) stored.ClaudeApiKey = null;
        else if (Blank(request.Claude.ApiKey) is { } ck) stored.ClaudeApiKey = aiSettings.Encrypt(ck);
        if (request.Jev.ClearKey) stored.JevApiKey = null;
        else if (Blank(request.Jev.ApiKey) is { } jk) stored.JevApiKey = aiSettings.Encrypt(jk);

        stored.ClaudeBaseUrl = Blank(request.Claude.BaseUrl);
        stored.TriageModel = Blank(request.Claude.TriageModel);
        stored.ReportModel = Blank(request.Claude.ReportModel);
        stored.JevModel = Blank(request.Jev.Model);
        stored.MonthlyBudgetUsd = request.MonthlyBudgetUsd;
        var p = request.Prices;
        (stored.ReportInputUsdPerMillion, stored.ReportOutputUsdPerMillion, stored.TriageInputUsdPerMillion, stored.TriageOutputUsdPerMillion, stored.JevInputUsdPerMillion) =
            (p?.ReportInput, p?.ReportOutput, p?.TriageInput, p?.TriageOutput, p?.JevInput);

        aiSettings.Save(stored);
        return Ok(AiDtoNow());
    }

    /// <summary>One tiny real call to the provider. A typed key is tested without being saved.</summary>
    [HttpPost("settings/ai/test")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdminAccess)]
    [ProducesResponseType(typeof(TestResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> TestAi([FromBody] TestRequest request, CancellationToken ct)
    {
        var ai = aiSettings.CurrentValue;
        string? Typed(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        var typed = Typed(request.ApiKey);
        var baseUrl = Typed(request.BaseUrl);
        var model = Typed(request.Model);
        if (baseUrl is not null && !(Uri.TryCreate(baseUrl, UriKind.Absolute, out var bu) && bu.Scheme is "http" or "https"))
            return BadRequest(new[] { "Adressen skal være en gyldig http(s)-adresse." });
        if (model is not null && !AiSettingsValidator.IsModelName(model))
            return BadRequest(new[] { "Modelnavnet er ugyldigt." });
        switch (request.Provider?.ToLowerInvariant())
        {
            case "claude":
            {
                // A saved key is only sent to the address it was saved for: testing another address needs the key typed in.
                if (typed is null && baseUrl is not null && !string.Equals(baseUrl.TrimEnd('/'), ai.Claude.BaseUrl?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                    return Ok(new TestResult(false, "Indtast nøglen for at teste en ny adresse. Den gemte nøgle sendes kun til den gemte adresse."));
                var key = typed ?? ai.Claude.ApiKey;
                return Ok(string.IsNullOrEmpty(key)
                    ? new TestResult(false, "Der er ingen Claude-nøgle at teste.")
                    : await tester.TestClaudeAsync(key, baseUrl ?? ai.Claude.BaseUrl, model ?? ai.Claude.TriageModel, ct));
            }
            case "jev":
            {
                var key = typed ?? ai.Jev.ApiKey;
                return Ok(string.IsNullOrEmpty(key)
                    ? new TestResult(false, "Der er ingen Jev-nøgle at teste.")
                    : await tester.TestJevAsync(key, ai.Jev.BaseUrl, model ?? ai.Jev.Model, ct));
            }
            default:
                return BadRequest(new[] { "Ukendt provider." });
        }
    }

    /// <summary>Saves the full site list. 400 with a list of Danish messages when something is invalid; nothing is saved then.</summary>
    [HttpPut("settings")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdminAccess)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(IEnumerable<string>), StatusCodes.Status400BadRequest)]
    public IActionResult SaveSettings([FromBody] SaveSettingsRequest request)
    {
        var input = (request?.Sites ?? []).Select(s => new SiteOptions
        {
            Url = s.Url ?? "", Enabled = s.Enabled, IntervalDays = s.IntervalDays, MaxPages = s.MaxPages,
            Competitors = s.Competitors ?? [], DisabledRules = s.DisabledRules ?? [],
        }).ToList();
        var (valid, errors) = SiteValidator.Validate(input);
        if (errors.Count > 0) return BadRequest(errors);
        if (runner.IsRunning) return Conflict("Der kører et crawl. Gem igen, når det er færdigt.");

        sites.Save(valid);
        return NoContent();
    }

    static Finding ToFinding(FindingRow r) =>
        new(r.RuleId, r.Url, (Severity)r.Severity, r.Evidence, (FindingStatus)r.Status) { NodeId = r.NodeId, NodeKey = r.NodeKey, DocumentType = r.DocumentType };

    static FindingDto Dto(FindingRow r)
    {
        Rules.TryGetValue(r.RuleId, out var rule);
        return new FindingDto(r.RuleId, rule?.Title ?? r.RuleId, rule?.Category.ToString() ?? "", ((Severity)r.Severity).ToString(), r.Url, r.Evidence,
            ((FindingStatus)r.Status).ToString(), r.IsNew, rule?.HowToFix ?? "", r.NodeId, r.NodeKey, r.DocumentType);
    }
}
