using Lait.Umbraco.Awesome.SEO.Ai;

namespace Lait.Umbraco.Awesome.SEO.Tests;

public class BudgetTests
{
    static AiOptions Ai(bool jev = false) => new() { Claude = new ClaudeOptions { ApiKey = "k" }, Jev = new JevOptions { ApiKey = jev ? "j" : null } };

    [Fact]
    public void Default_models_use_list_prices_own_prices_win_and_unknown_models_have_none()
    {
        var p = AiPricing.Resolve(Ai());
        Assert.Equal((2.0, 10.0, 0.10, 0.50), (p.ReportInput, p.ReportOutput, p.TriageInput, p.TriageOutput));
        Assert.Equal(0.042, p.JevInput);
        Assert.True(p.FromList);

        var own = Ai();
        own.Claude.ReportInputUsdPerMillion = 3; own.Claude.ReportOutputUsdPerMillion = 15; own.Claude.TriageInputUsdPerMillion = 1; own.Claude.TriageOutputUsdPerMillion = 5; own.Jev.InputUsdPerMillion = 0.05;
        var q = AiPricing.Resolve(own);
        Assert.Equal((3.0, 15.0, 1.0, 5.0, 0.05), (q.ReportInput, q.ReportOutput, q.TriageInput, q.TriageOutput, q.JevInput));
        Assert.False(q.FromList);

        var unknown = Ai();
        unknown.Claude.ReportModel = "mystery-model";
        var u = AiPricing.Resolve(unknown);
        Assert.Null(u.ReportInput);
        Assert.Null(u.ReportOutput);
    }

    [Fact]
    public void Cost_is_tokens_times_price_per_part_and_jev_output_is_free()
    {
        var p = AiPricing.Resolve(Ai());
        // Claude triage (haiku 0.10/0.50) + report (sonnet 2/10): 1M triage in, 100K triage out, 500K report in, 50K report out
        var claude = AiPricing.Cost(p, false, 1_000_000, 100_000, 500_000, 50_000, 0);
        Assert.Equal(0.10 + 0.05 + 1.0 + 0.5, claude!.Value, 6);

        // Jev triage: only input tokens are billed (0.042/M), chat tokens in the triage slot are not used
        var jev = AiPricing.Cost(p, true, 0, 0, 500_000, 50_000, 2_000_000);
        Assert.Equal(0.084 + 1.0 + 0.5, jev!.Value, 6);
    }

    [Fact]
    public void Cost_is_unknown_only_when_a_price_that_is_needed_is_missing()
    {
        var unknown = Ai();
        unknown.Claude.ReportModel = "mystery-model";
        var p = AiPricing.Resolve(unknown);
        Assert.Null(AiPricing.Cost(p, false, 100, 10, 100, 10, 0));            // report tokens used, report price missing
        Assert.NotNull(AiPricing.Cost(p, false, 100, 10, 0, 0, 0));            // only triage used: the report price is not needed
        Assert.NotNull(AiPricing.Cost(AiPricing.Resolve(Ai()), false, 0, 0, 0, 0, 0)); // nothing used costs nothing
    }

    static TokenEstimate Est(double? cost) => new(1000, 500, cost, "Claude", null, 0);

    [Fact]
    public void Budget_check_lets_runs_through_until_the_worst_case_would_pass_the_budget()
    {
        var ai = Ai();
        Assert.Null(AiBudget.Check(ai, Est(0.07), 0, 0));                       // no budget: anything goes

        ai.MonthlyBudgetUsd = 1.00;
        Assert.Null(AiBudget.Check(ai, Est(0.07), 0, 0.90));                    // 0.90 + 0.07 <= 1.00
        var over = AiBudget.Check(ai, Est(0.20), 0, 0.90);                      // 0.90 + 0.20 > 1.00
        Assert.NotNull(over);
        Assert.Contains("USD", over);
        Assert.Contains("budget", over, StringComparison.OrdinalIgnoreCase);

        ai.MonthlyBudgetUsd = 0;                                                // 0 = no limit
        Assert.Null(AiBudget.Check(ai, Est(5), 0, 100));
    }

    [Fact]
    public void Budget_without_a_known_price_blocks_the_run_instead_of_silently_allowing_it()
    {
        var ai = Ai();
        ai.MonthlyBudgetUsd = 5;
        var reason = AiBudget.Check(ai, Est(null), 0, 0);
        Assert.NotNull(reason);
        Assert.Contains("pris", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_appsettings_token_limit_still_works_as_a_safety_net()
    {
        var ai = Ai();
        ai.MonthlyTokenLimit = 10_000;
        Assert.Null(AiBudget.Check(ai, Est(0.01), 8_000, 0));       // 8000 + 1000 + 500 <= 10000
        Assert.NotNull(AiBudget.Check(ai, Est(0.01), 9_000, 0));    // 9000 + 1500 > 10000
    }

    [Fact]
    public void A_typical_two_competitor_run_costs_cents_so_a_few_dollars_covers_many_runs()
    {
        var est = TokenEstimator.Estimate(Ai(), 2, 0);
        Assert.InRange(est.CostUsd!.Value, 0.01, 0.20);              // worst case upper bound
        Assert.True(est.PricesFromList);
        Assert.Equal(0.5, TokenEstimator.Estimate(WithBudget(0.5), 2, 0, 0.1).MonthlyBudgetUsd);
        Assert.Equal(0.1, TokenEstimator.Estimate(WithBudget(0.5), 2, 0, 0.1).SpentThisMonthUsd);
    }

    static AiOptions WithBudget(double b) { var a = Ai(); a.MonthlyBudgetUsd = b; return a; }
}
