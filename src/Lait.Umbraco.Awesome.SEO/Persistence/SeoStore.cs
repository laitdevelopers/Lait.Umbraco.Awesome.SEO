using Lait.Umbraco.Awesome.SEO.Rules;
using Umbraco.Cms.Infrastructure.Scoping;

namespace Lait.Umbraco.Awesome.SEO.Persistence;

/// <summary>Reads/writes runs. Each save is one scope = one transaction, so a run is stored whole or not at all.</summary>
public sealed class SeoStore(IScopeProvider scopeProvider)
{
    public DateTime? LastRunUtc(string domain, bool competitor)
    {
        using var scope = scopeProvider.CreateScope(autoComplete: true);
        return scope.Database.ExecuteScalar<DateTime?>(
            "SELECT MAX(StartedUtc) FROM laitSeoRun WHERE Domain = @0 AND IsCompetitor = @1", domain, competitor);
    }

    /// <summary>Open findings of the latest run, or null when the domain has never been crawled.</summary>
    public List<Finding>? PreviousFindings(string domain)
    {
        using var scope = scopeProvider.CreateScope(autoComplete: true);
        var runId = LatestRunId(scope.Database, domain, false);
        if (runId is null) return null;
        return scope.Database
            .Fetch<FindingRow>("WHERE RunId = @0 AND Status = @1", runId, (int)FindingStatus.Open)
            .Select(r => new Finding(r.RuleId, r.Url, (Severity)r.Severity, r.Evidence)).ToList();
    }

    /// <summary>Latest competitor snapshot, or null when it has never been crawled.</summary>
    public CompetitorSnapshot? PreviousSnapshot(string domain)
    {
        using var scope = scopeProvider.CreateScope(autoComplete: true);
        var runId = LatestRunId(scope.Database, domain, true);
        if (runId is null) return null;
        var rows = scope.Database.Fetch<PageRow>("WHERE RunId = @0", runId);
        return new CompetitorSnapshot(
            rows.Where(r => r.InSitemap).Select(r => r.Url).ToHashSet(),
            rows.ToDictionary(r => r.Url, r => new PageFingerprint(r.Url, r.Status, r.Title, r.H1, r.MetaDescription, r.Canonical, r.JsonLdHash, r.WordCount)));
    }

    public RunRow? LatestRun(string domain, bool competitor)
    {
        using var scope = scopeProvider.CreateScope(autoComplete: true);
        return LatestRunId(scope.Database, domain, competitor) is { } id ? scope.Database.SingleOrDefaultById<RunRow>(id) : null;
    }

    public List<FindingRow> FindingRows(int runId)
    {
        using var scope = scopeProvider.CreateScope(autoComplete: true);
        return scope.Database.Fetch<FindingRow>("WHERE RunId = @0", runId);
    }

    public List<PageRow> PageRows(int runId)
    {
        using var scope = scopeProvider.CreateScope(autoComplete: true);
        return scope.Database.Fetch<PageRow>("WHERE RunId = @0", runId);
    }

    public List<ChangeRow> ChangeRows(int runId)
    {
        using var scope = scopeProvider.CreateScope(autoComplete: true);
        return scope.Database.Fetch<ChangeRow>("WHERE RunId = @0", runId);
    }

    public List<SiteRow> SiteRows()
    {
        using var scope = scopeProvider.CreateScope(autoComplete: true);
        return scope.Database.Fetch<SiteRow>("ORDER BY Id");
    }

    /// <summary>Replaces the whole site list in one transaction (the settings tab always saves the full list).</summary>
    public void ReplaceSites(IEnumerable<SiteRow> sites)
    {
        using var scope = scopeProvider.CreateScope();
        scope.Database.Execute("DELETE FROM laitSeoSite");
        foreach (var s in sites) { s.Id = 0; scope.Database.Insert(s); }
        scope.Complete();
    }

    public ReportRow? ReportFor(int runId)
    {
        using var scope = scopeProvider.CreateScope(autoComplete: true);
        return scope.Database.Fetch<ReportRow>("WHERE RunId = @0 AND Status NOT LIKE 'plan%' ORDER BY Id DESC", runId).FirstOrDefault();
    }

    /// <summary>The newest AI additions to the action plan for this run (null = never written for it).</summary>
    public ReportRow? PlanFor(int runId)
    {
        using var scope = scopeProvider.CreateScope(autoComplete: true);
        return scope.Database.Fetch<ReportRow>("WHERE RunId = @0 AND Status = 'plan' ORDER BY Id DESC", runId).FirstOrDefault();
    }

    public void SaveReport(ReportRow report)
    {
        using var scope = scopeProvider.CreateScope();
        scope.Database.Insert(report);
        scope.Complete();
    }

    /// <summary>Chat + Jev input tokens spent since the start of the current UTC month (for the monthly limit).</summary>
    public long TokensThisMonth(DateTime nowUtc)
    {
        using var scope = scopeProvider.CreateScope(autoComplete: true);
        return scope.Database.ExecuteScalar<long?>(
            "SELECT SUM(ChatInputTokens + ChatOutputTokens + JevInputTokens) FROM laitSeoReport WHERE CreatedUtc >= @0",
            new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc)) ?? 0;
    }

    /// <summary>USD spent on AI since the start of the current UTC month (runs with unknown cost count as 0).</summary>
    public double SpendThisMonthUsd(DateTime nowUtc)
    {
        using var scope = scopeProvider.CreateScope(autoComplete: true);
        return scope.Database.ExecuteScalar<double?>(
            "SELECT SUM(CostUsd) FROM laitSeoReport WHERE CreatedUtc >= @0",
            new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc)) ?? 0;
    }

    public int Save(RunRow run, IEnumerable<FindingRow> findings, IEnumerable<PageRow> pages, IEnumerable<ChangeRow> changes)
    {
        using var scope = scopeProvider.CreateScope();
        var db = scope.Database;
        db.Insert(run);
        // ponytail: row-by-row inserts in one transaction; switch to bulk insert if runs reach tens of thousands of rows
        foreach (var f in findings) { f.RunId = run.Id; db.Insert(f); }
        foreach (var p in pages) { p.RunId = run.Id; db.Insert(p); }
        foreach (var c in changes) { c.RunId = run.Id; db.Insert(c); }

        // Only the latest runs are ever read (report, diff, competitor changes), so older runs keep their row (score history) but lose
        // findings, pages and changes. AI reports stay: they are the record of what was spent this month.
        var cutoff = db.Fetch<int>("SELECT Id FROM laitSeoRun WHERE Domain = @0 AND IsCompetitor = @1 ORDER BY Id DESC", run.Domain, run.IsCompetitor)
            .Skip(KeepRuns - 1).FirstOrDefault();
        if (cutoff > 0)
            foreach (var table in new[] { "laitSeoFinding", "laitSeoPage", "laitSeoChange" })
                db.Execute($"DELETE FROM {table} WHERE RunId IN (SELECT Id FROM laitSeoRun WHERE Domain = @0 AND IsCompetitor = @1 AND Id < @2)",
                    run.Domain, run.IsCompetitor, cutoff);
        scope.Complete();
        return run.Id;
    }

    /// <summary>Runs per site (own or competitor) whose findings, pages and changes are kept.</summary>
    public const int KeepRuns = 10;

    static int? LatestRunId(NPoco.IDatabase db, string domain, bool competitor) =>
        db.ExecuteScalar<int?>("SELECT MAX(Id) FROM laitSeoRun WHERE Domain = @0 AND IsCompetitor = @1", domain, competitor);
}
