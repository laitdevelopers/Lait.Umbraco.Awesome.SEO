namespace Lait.Umbraco.Awesome.SEO.Ai;

/// <summary>USD per million tokens as used for estimates and the monthly budget. Null = unknown.</summary>
public sealed record Prices(double? ReportInput, double? ReportOutput, double? TriageInput, double? TriageOutput, double? JevInput)
{
    /// <summary>At least one of the prices comes from the built-in list prices (not set by the admin).</summary>
    public bool FromList { get; init; }
}

/// <summary>
/// Prices the admin has not set fall back to list prices for the models this plugin defaults to.
/// Prices change: the admin can always override them in the Indstillinger tab, and the tab says which ones are list prices.
/// </summary>
public static class AiPricing
{
    public const string ListPricesAsOf = "2026-10-06";

    // Anthropic API list prices per million tokens (prompts up to 100K tokens, far above what this plugin sends).
    static readonly Dictionary<string, (double In, double Out)> ClaudeList = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-haiku-5-5"] = (0.10, 0.50),
        ["claude-sonnet-5-5"] = (2.00, 10.00),
        ["claude-sonnet-5"] = (2.00, 10.00),
        ["claude-opus-5-5"] = (4.00, 20.00),
    };

    /// <summary>TypeSafe lists Jev at 0.042 USD per million input tokens; output tokens are free (typesafe.ai blog).</summary>
    public const double JevListInput = 0.042;

    public static Prices Resolve(AiOptions ai)
    {
        var c = ai.Claude;
        var list = false;

        double? Pick(double? set, double? fromList)
        {
            if (set is not null) return set;
            if (fromList is not null) list = true;
            return fromList;
        }

        (double In, double Out)? For(string? model) => model is not null && ClaudeList.TryGetValue(model, out var p) ? p : null;
        var report = For(c.ReportModel);
        var triage = For(c.TriageModel);

        return new Prices(
            Pick(c.ReportInputUsdPerMillion, report?.In),
            Pick(c.ReportOutputUsdPerMillion, report?.Out),
            Pick(c.TriageInputUsdPerMillion, triage?.In),
            Pick(c.TriageOutputUsdPerMillion, triage?.Out),
            Pick(ai.Jev.InputUsdPerMillion, JevListInput))
        { FromList = list };
    }

    /// <summary>
    /// Cost of the tokens used, or null when a price that is needed for them is unknown. Prices are only needed for the parts that were
    /// actually used (no Jev price is needed when Jev is off). Jev output tokens are free.
    /// </summary>
    public static double? Cost(Prices p, bool useJev, long triageChatIn, long triageChatOut, long reportIn, long reportOut, long jevIn)
    {
        var sum = 0.0;
        bool Add(long tokens, double? price)
        {
            if (tokens == 0) return true;
            if (price is null) return false;
            sum += tokens * price.Value / 1e6;
            return true;
        }

        var ok = Add(reportIn, p.ReportInput) && Add(reportOut, p.ReportOutput)
                 && (useJev ? Add(jevIn, p.JevInput) : Add(triageChatIn, p.TriageInput) && Add(triageChatOut, p.TriageOutput));
        return ok ? sum : null;
    }
}

/// <summary>Decides whether an AI run may start: monthly dollar budget first, then the (appsettings-only) token limit.</summary>
public static class AiBudget
{
    /// <summary>One AI spend at a time (scheduled report, manual report, action plan), so two can't both pass the budget check.</summary>
    public static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Null = go ahead, otherwise the reason it is skipped (Danish, shown in the dashboard).</summary>
    public static string? Check(AiOptions ai, TokenEstimate estimate, long usedTokens, double spentUsd)
    {
        if (ai.MonthlyBudgetUsd is { } budget && budget > 0)
        {
            if (estimate.CostUsd is not { } cost)
                return "Der er sat et månedligt budget, men prisen for de valgte modeller er ukendt. Sæt priserne under AI-indstillinger.";
            if (spentUsd + cost > budget)
                return $"Månedligt budget på {budget:0.00} USD ville blive overskredet (brugt {spentUsd:0.00} USD, dette forløb koster højst ca. {cost:0.00} USD).";
        }
        if (ai.MonthlyTokenLimit is { } limit && usedTokens + estimate.InputTokens + estimate.OutputTokens > limit)
            return $"Månedlig grænse på {limit} tokens ville blive overskredet (brugt {usedTokens}).";
        return null;
    }
}
