namespace Lait.Umbraco.Awesome.SEO.Models;

public sealed record DomainSummary(string Domain, string Url, DateTime? LastRunUtc, double? Score, int? PageCount, bool? IsBaseline, bool Running, Services.RunProgress? Progress, string? LastError);

public sealed record FindingDto(string RuleId, string Title, string Category, string Severity, string Url, string Evidence, string Status, bool IsNew, string HowToFix, int? NodeId, Guid? NodeKey, string? DocumentType);

public sealed record ChangeDto(string Url, string Kind, string? Field, string? OldValue, string? NewValue);

public sealed record CompetitorDto(string Domain, DateTime StartedUtc, bool IsBaseline, int PageCount, IReadOnlyList<ChangeDto> Changes);

public sealed record RunDto(int Id, DateTime StartedUtc, double Score, int PageCount, bool IsBaseline);

public sealed record Counts(int New, int Open, int Resolved, int Critical, int Warning, int Info);

public sealed record AiDto(string Status, string? Error, DateTime CreatedUtc, string TriageProvider, string ReportModel, long ChatTokens, long JevTokens, double? CostUsd, Ai.AiReport? Report);

public sealed record ReportDto(RunDto? Run, Counts Counts, IReadOnlyList<FindingDto> Findings, IReadOnlyList<CompetitorDto> Competitors, AiDto? Ai)
{
    /// <summary>Checks that ran and found nothing on the own site (enabled rules without open findings), for the "I orden" list.</summary>
    public IReadOnlyList<PassedCheck> Passed { get; init; } = [];
    /// <summary>The weighted score per category and the rules that cost the most points. Null before the first run.</summary>
    public Rules.ScoreBreakdown? Score { get; init; }
    /// <summary>Competitors whose last crawl failed (the comparison then uses their last good run).</summary>
    public IReadOnlyList<Services.RunFailure> CompetitorFailures { get; init; } = [];
}

public sealed record PassedCheck(string RuleId, string Title, string Category);

public sealed record EstimateDto(bool Configured, string TriageProvider, long InputTokens, long OutputTokens, double? CostUsd, double? MonthlyBudgetUsd, double SpentThisMonthUsd, bool PricesFromList, IReadOnlyList<string> Dataflow);

public sealed record SiteDto(string Url, bool Enabled, int IntervalDays, int MaxPages, List<string> Competitors, List<string> DisabledRules);

/// <summary>Weight: how much the rule counts in the score (0-5, 0 = not counted).</summary>
public sealed record RuleInfo(string Id, string Category, string Severity, string Title, int Weight);

public sealed record ClaudeSettingsDto(string KeySource, string? KeyHint, string? BaseUrl, string TriageModel, string ReportModel);

public sealed record JevSettingsDto(string KeySource, string? KeyHint, string Model);

public sealed record PricesDto(double? ReportInput, double? ReportOutput, double? TriageInput, double? TriageOutput, double? JevInput);

/// <summary>Effective AI settings for the tab. API keys are never included, only where they come from and the last 4 characters.</summary>
/// <summary>Prices are the effective ones (own value or list price). PricesFromList tells the tab to say so.</summary>
public sealed record AiSettingsDto(bool Configured, string TriageProvider, ClaudeSettingsDto Claude, JevSettingsDto Jev, double? MonthlyBudgetUsd, double SpentThisMonthUsd, PricesDto Prices, bool PricesFromList, string ListPricesAsOf, IReadOnlyList<string> Warnings, bool StoredKeyUnreadable);

/// <summary>ApiKey empty/null = keep what is saved; ClearKey = delete the saved key (falls back to appsettings/secrets).</summary>
public sealed record ClaudeSaveDto(string? ApiKey, bool ClearKey, string? BaseUrl, string? TriageModel, string? ReportModel);

public sealed record JevSaveDto(string? ApiKey, bool ClearKey, string? Model);

public sealed record AiSaveRequest(ClaudeSaveDto Claude, JevSaveDto Jev, double? MonthlyBudgetUsd, PricesDto Prices);

/// <summary>Provider is "claude" or "jev". Everything typed in the form (key, address, model) is tested without saving; empty = use the saved/effective value.</summary>
public sealed record TestRequest(string Provider, string? ApiKey, string? BaseUrl, string? Model);

public sealed record SettingsDto(IReadOnlyList<SiteDto> Sites, IReadOnlyList<string> UmbracoDomains, IReadOnlyList<RuleInfo> Rules, AiSettingsDto Ai, SearchSettingsDto Search);

/// <summary>Configured = a service account key exists. Message (Danish) says why there is no data; Insights is null then. Nodes: the Umbraco page behind each URL in Insights, for "Ret med AI".</summary>
public sealed record SearchDto(bool Configured, string? Message, string? Property, string? ServiceAccount, Search.SearchInsights? Insights)
{
    public IReadOnlyList<PageNode> Nodes { get; init; } = [];
}

public sealed record PageNode(string Url, Guid Key);

/// <summary>The key itself is never sent back; ServiceAccount is its e-mail, which is what gets added as a user in Search Console.</summary>
public sealed record SearchSettingsDto(string KeySource, string? ServiceAccount, bool Unreadable);

/// <summary>Clear = delete the saved key. Otherwise ServiceAccountJson is the full key file from Google Cloud.</summary>
public sealed record SearchSaveRequest(string? ServiceAccountJson, bool Clear);

/// <summary>Empty JSON = test the saved key.</summary>
public sealed record SearchTestRequest(string? ServiceAccountJson);

public sealed record SiteProperty(string Domain, string? Property);

public sealed record SearchTestResult(bool Ok, string Message, IReadOnlyList<string> Properties, IReadOnlyList<SiteProperty> Sites);

/// <summary>Ready = the own site and at least one competitor have a run with metrics. Otherwise Message says what is missing.</summary>
public sealed record BenchmarkDto(bool Ready, string? Message, DateTime? OwnRunUtc, Rules.Benchmark? Benchmark);

/// <summary>Source: rules (no AI), jev (impact rated), claude (text written) or jev+claude. Items are always the measured ones.</summary>
public sealed record PlanDto(string Source, string? Summary, DateTime? WrittenUtc, double? CostUsd, IReadOnlyList<Ai.PlanItem> Items, string? Message);

public sealed record MediaRefDto(Guid Key, string Name);

public sealed record SaveSettingsRequest(List<SiteDto> Sites);
