using System.Text.Json;
using Lait.Umbraco.Awesome.SEO.Ai;
using Lait.Umbraco.Awesome.SEO.Persistence;
using Microsoft.Extensions.Options;

namespace Lait.Umbraco.Awesome.SEO.Services;

public sealed record PlanEstimate(bool CanScore, bool CanWrite, int Items, long InputTokens, long OutputTokens, double? CostUsd, double? MonthlyBudgetUsd, double SpentThisMonthUsd, bool PricesFromList, string? Blocked);

/// <summary>
/// The AI layer of the action plan: Jev (if keyed) rates each item's impact, Claude (if keyed) writes why/steps. Either one alone works;
/// with neither the plan is simply the rule-based one. Stored as a ReportRow with Status "plan", so spend counts towards the monthly budget.
/// </summary>
public sealed class PlanService(SeoStore store, JevImpactScorer scorer, PlanWriter writer, IOptionsMonitor<AiOptions> options)
{
    public const string Status = "plan";
    const int JevTokensPerItem = 250;
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public PlanEstimate Estimate(int itemCount)
    {
        var ai = options.CurrentValue;
        var n = Math.Min(itemCount, PlanWriter.MaxItems);
        long jevIn = ai.UseJev ? n * JevTokensPerItem : 0;
        long writeIn = ai.CanWrite ? 900 + n * 150 : 0, writeOut = ai.CanWrite ? PlanWriter.OutputTokensFor(n) : 0;
        var prices = AiPricing.Resolve(ai);
        var cost = AiPricing.Cost(prices, ai.UseJev, 0, 0, writeIn, writeOut, jevIn);
        var used = store.TokensThisMonth(DateTime.UtcNow);
        var spent = store.SpendThisMonthUsd(DateTime.UtcNow);
        var raw = new TokenEstimate(jevIn + writeIn, writeOut, cost, ai.UseJev ? "Jev" : "Claude", ai.MonthlyTokenLimit, used, ai.MonthlyBudgetUsd, spent, prices.FromList);
        return new PlanEstimate(ai.UseJev, ai.CanWrite, n, jevIn + writeIn, writeOut, cost, ai.MonthlyBudgetUsd, spent, prices.FromList,
            !ai.Enabled ? "Der er ingen API-nøgle. Sæt en Claude- eller Jev-nøgle under Indstillinger." : n == 0 ? "Der er ingen punkter i planen." : AiBudget.Check(ai, raw, used, spent));
    }

    /// <summary>Null Blocked = done. Throws <see cref="AiException"/> when a provider rejects the key.</summary>
    public async Task<(ReportRow? Row, string? Blocked)> GenerateAsync(int runId, string domain, IReadOnlyList<PlanItem> items, CancellationToken ct)
    {
        await AiBudget.Gate.WaitAsync(ct);
        try { return await GenerateLockedAsync(runId, domain, items, ct); }
        finally { AiBudget.Gate.Release(); }
    }

    async Task<(ReportRow? Row, string? Blocked)> GenerateLockedAsync(int runId, string domain, IReadOnlyList<PlanItem> items, CancellationToken ct)
    {
        var est = Estimate(items.Count);
        if (est.Blocked is not null) return (null, est.Blocked);

        var ai = options.CurrentValue;
        var use = items.Take(PlanWriter.MaxItems).ToList();
        var scores = new Dictionary<string, double>();
        long jevTokens = 0, writeIn = 0, writeOut = 0;
        StoredPlan? written = null;

        if (ai.UseJev) (scores, jevTokens) = await scorer.ScoreAsync(use, ct);
        try
        {
            if (ai.CanWrite) (written, writeIn, writeOut) = await writer.WriteAsync(domain, use, ct);
        }
        catch (Exception) when (jevTokens > 0)
        {
            // Jev was already paid for: record it (not shown as a plan) so the monthly budget stays right.
            store.SaveReport(new ReportRow
            {
                RunId = runId, CreatedUtc = DateTime.UtcNow, Status = "plan-failed", TriageProvider = "Jev", ReportModel = ai.Claude.ReportModel,
                JevInputTokens = jevTokens, CostUsd = AiPricing.Cost(AiPricing.Resolve(ai), true, 0, 0, 0, 0, jevTokens),
            });
            throw;
        }

        var byKey = written?.Items.ToDictionary(e => e.Key) ?? [];
        var merged = use.Select(i => new PlanEnrichment(i.Key, scores.TryGetValue(i.Key, out var s) ? s : null,
            byKey.GetValueOrDefault(i.Key)?.Why, byKey.GetValueOrDefault(i.Key)?.Steps ?? [])).ToList();

        var row = new ReportRow
        {
            RunId = runId, CreatedUtc = DateTime.UtcNow, Status = Status,
            Json = JsonSerializer.Serialize(new StoredPlan(written?.Summary, merged), Json),
            TriageProvider = ai.UseJev && ai.CanWrite ? "Jev+Claude" : ai.UseJev ? "Jev" : "Claude",
            ReportModel = ai.CanWrite ? ai.Claude.ReportModel : "",
            ChatInputTokens = writeIn, ChatOutputTokens = writeOut, JevInputTokens = jevTokens,
            CostUsd = AiPricing.Cost(AiPricing.Resolve(ai), ai.UseJev, 0, 0, writeIn, writeOut, jevTokens),
        };
        store.SaveReport(row);
        return (row, null);
    }
}
