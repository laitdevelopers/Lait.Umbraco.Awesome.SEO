using System.Net;
using System.Text.Json.Nodes;
using Lait.Umbraco.Awesome.SEO.Ai;
using Lait.Umbraco.Awesome.SEO.Rules;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lait.Umbraco.Awesome.SEO.Tests;

public class PlanTests
{
    sealed class Monitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    sealed class JevHandler(string body) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }
    }

    sealed class Http(HttpMessageHandler h) : IHttpClientFactory { public HttpClient CreateClient(string name) => new(h, false); }

    static Finding F(string rule, Severity sev, string url = "https://own.dk/a") => new(rule, url, sev, "x");

    static Benchmark Bench(params Gap[] gaps) => new(["own.dk", "rival.dk"], [], gaps, [new SectionGap("case", "rival.dk", 36, 500)]);

    static Gap G(string key, int affected = 0) => new(key, key, "%", $"{key}: 0 % mod 100 %", "Gør noget", 0, 100, "rival.dk", [], affected);

    [Fact]
    public void Plan_puts_critical_findings_first_gaps_by_effort_and_sections_last()
    {
        var critical = RuleCatalog.All.First(r => r.DefaultSeverity == Severity.Critical).Id;
        var items = PlanBuilder.Build(Bench(G("words"), G("canonical"), G("meta")), [F(critical, Severity.Critical, "https://own.dk/1"), F(critical, Severity.Critical, "https://own.dk/2"), F("ONP-05", Severity.Warning)]);

        Assert.Equal(["critical", "quick", "high", "long", "long"], items.Select(i => i.Bucket));
        Assert.Equal(2, items[0].PagesAffected);
        Assert.Equal(["gap:canonical", "gap:meta", "gap:words"], items.Where(i => i.Kind == "gap").Select(i => i.Key));
        Assert.Equal("section:case:rival.dk", items[^1].Key);
    }

    [Fact]
    public void Plan_without_competitors_still_lists_critical_findings_and_skips_resolved_ones()
    {
        var critical = RuleCatalog.All.First(r => r.DefaultSeverity == Severity.Critical).Id;
        var items = PlanBuilder.Build(null, [F(critical, Severity.Critical), new Finding(critical, "https://own.dk/old", Severity.Critical, "x", FindingStatus.Resolved)]);

        Assert.Single(items);
        Assert.Equal(1, items[0].PagesAffected);
    }

    [Fact]
    public void A_gap_already_covered_by_a_critical_finding_is_not_listed_twice()
    {
        var broken = BenchmarkMetrics.Definitions.First(d => d.Key == "broken").RuleIds[0];
        var items = PlanBuilder.Build(Bench(G("broken")), [F(broken, Severity.Critical)]);

        Assert.DoesNotContain(items, i => i.Key == "gap:broken");
    }

    [Fact]
    public void Apply_lays_ai_text_on_items_and_orders_by_jev_impact_within_a_bucket()
    {
        var items = PlanBuilder.Build(Bench(G("meta"), G("h1"), G("alt")), []);   // all "high"
        var stored = new StoredPlan("Opsummering", [new("gap:alt", 4.5, "Fordi", ["Trin 1"]), new("gap:h1", 2.0, null, [])]);

        var applied = PlanBuilder.Apply(items, stored).Where(i => i.Bucket == "high").ToList();

        Assert.Equal(["gap:alt", "gap:h1", "gap:meta"], applied.Select(i => i.Key));
        Assert.Equal("Fordi", applied[0].Why);
        Assert.Equal(["Trin 1"], applied[0].Steps);
        Assert.Null(applied[2].Impact);
    }

    [Fact]
    public async Task Jev_scores_each_item_with_a_typed_score_question()
    {
        var h = new JevHandler("""{"answers":{"impact":{"type":"score","score":3.6}},"usage":{"input_tokens":120}}""");
        var ai = new AiOptions { Jev = new JevOptions { ApiKey = "apikey_x" } };
        var items = PlanBuilder.Build(Bench(G("meta"), G("h1")), []);

        var (scores, tokens) = await new JevImpactScorer(new Http(h), new Monitor<AiOptions>(ai)).ScoreAsync(items.Where(i => i.Kind == "gap").ToList(), default);

        Assert.Equal(3.6, scores["gap:meta"]);
        Assert.Equal(2, scores.Count);
        Assert.Equal(240, tokens);
    }

    [Fact]
    public void Writer_keeps_only_keys_it_was_given_so_the_ai_cannot_invent_items()
    {
        var p = PlanWriter.Parse("""Her: {"summary":"S","items":[{"key":"gap:meta","why":"W","steps":["a","b"]},{"key":"gap:made-up","why":"X","steps":[]}]}""", new HashSet<string> { "gap:meta" });

        Assert.Equal("S", p!.Summary);
        var e = Assert.Single(p.Items);
        Assert.Equal(["a", "b"], e.Steps);
        Assert.Null(PlanWriter.Parse("ikke json", new HashSet<string>()));
    }

    [Fact]
    public async Task Jev_alone_without_claude_key_gives_a_report_of_its_verdicts_and_never_calls_claude()
    {
        var h = new JevHandler("""{"answers":{"importance":{"choice":"critical"}},"usage":{"input_tokens":70}}""");
        var ai = new AiOptions { Jev = new JevOptions { ApiKey = "apikey_x" } };
        Assert.True(ai.Enabled);
        Assert.False(ai.CanWrite);
        var pipeline = new AiPipeline(new NoChat(), new Http(h), new Monitor<AiOptions>(ai), NullLogger<AiPipeline>.Instance);

        var o = await pipeline.RunAsync(new AiInput("own.dk", 70, 5, 0, [new("ONP-01", "T", "Critical", "https://own.dk/", "ev")], []), default);

        Assert.Equal("Jev", o.TriageProvider);
        Assert.Contains("Jev har vurderet 1", o.Report!.Summary);
        Assert.Single(o.Report.TopProblems);
        Assert.Equal(70, o.JevInputTokens);
        Assert.Equal(0, o.ChatInputTokens);
    }

    sealed class NoChat : IChatClientFactory
    {
        public IChatClient Create(string model, int? maxOutputTokens = null) => throw new InvalidOperationException("Claude must not be used without a key");
    }
}
