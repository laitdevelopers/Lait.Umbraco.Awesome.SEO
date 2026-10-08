namespace Lait.Umbraco.Awesome.SEO.Ai;

/// <summary>
/// Bound from "LaitSeo:Ai". API keys belong in user-secrets / environment variables (e.g. LaitSeo__Ai__Claude__ApiKey),
/// never in the database. No key at all = no AI: runs stay rule-based only.
/// </summary>
public sealed class AiOptions
{
    public const string SectionName = "LaitSeo:Ai";

    public ClaudeOptions Claude { get; set; } = new();
    public JevOptions Jev { get; set; } = new();
    /// <summary>
    /// Monthly AI budget in USD (set in the Indstillinger tab). An AI run is skipped when it could take the month over the budget.
    /// Not set = no budget limit. Needs prices: see <see cref="AiPricing"/>.
    /// </summary>
    public double? MonthlyBudgetUsd { get; set; }
    /// <summary>appsettings-only safety net: chat + Jev tokens per calendar month. Not set = no limit. The dollar budget is the one to use.</summary>
    public long? MonthlyTokenLimit { get; set; }

    /// <summary>Claude writes the prose (report, plan text). Without it, Jev alone can still triage and rate plan items.</summary>
    public bool CanWrite => !string.IsNullOrWhiteSpace(Claude.ApiKey);
    public bool Enabled => CanWrite || UseJev;
    public bool UseJev => !string.IsNullOrWhiteSpace(Jev.ApiKey);
}

public sealed class ClaudeOptions
{
    public string? ApiKey { get; set; }
    /// <summary>Optional endpoint override, e.g. an EU-region gateway.</summary>
    public string? BaseUrl { get; set; }
    /// <summary>Cheap model: triage fallback when Jev is off.</summary>
    public string TriageModel { get; set; } = "claude-haiku-5-5";
    /// <summary>Strong model: writes the report.</summary>
    public string ReportModel { get; set; } = "claude-sonnet-5-5";
    /// <summary>USD per million tokens, only used to show a cost estimate. Left empty = tokens only (prices change).</summary>
    public double? ReportInputUsdPerMillion { get; set; }
    public double? ReportOutputUsdPerMillion { get; set; }
    public double? TriageInputUsdPerMillion { get; set; }
    public double? TriageOutputUsdPerMillion { get; set; }
    public int ReportMaxOutputTokens { get; set; } = 4096;
}

public sealed class JevOptions
{
    /// <summary>Key from console.typesafe.ai/keys (apikey_…). Setting it makes Jev do the triage step.</summary>
    public string? ApiKey { get; set; }
    /// <summary>Official TypeSafe API (docs.typesafe.ai). Requests go to <c>POST {BaseUrl}/v1/systemone</c>.</summary>
    public string BaseUrl { get; set; } = "https://api.typesafe.ai";
    public string Model { get; set; } = "jev-latest";
    /// <summary>USD per million input tokens, for the estimate (TypeSafe lists 0.042 at launch; prices change, so set it yourself).</summary>
    public double? InputUsdPerMillion { get; set; }
}
