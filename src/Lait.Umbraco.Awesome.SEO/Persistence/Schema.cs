using NPoco;
using Umbraco.Cms.Infrastructure.Migrations;
using Umbraco.Cms.Core.Packaging;
using Umbraco.Cms.Infrastructure.Persistence.DatabaseAnnotations;

namespace Lait.Umbraco.Awesome.SEO.Persistence;

[TableName("laitSeoRun")]
[PrimaryKey("Id", AutoIncrement = true)]
[ExplicitColumns]
public class RunRow
{
    [Column("Id")][PrimaryKeyColumn(AutoIncrement = true)] public int Id { get; set; }
    /// <summary>The Umbraco domain (or competitor site) this run crawled.</summary>
    [Column("Domain")][Length(255)][Index(IndexTypes.NonClustered)] public string Domain { get; set; } = "";
    [Column("IsCompetitor")] public bool IsCompetitor { get; set; }
    [Column("StartedUtc")] public DateTime StartedUtc { get; set; }
    [Column("PageCount")] public int PageCount { get; set; }
    [Column("Score")] public double Score { get; set; }
    /// <summary>First run for the domain: findings are a baseline, nothing is "new" and no AI is called.</summary>
    [Column("IsBaseline")] public bool IsBaseline { get; set; }
    /// <summary>JSON <c>SiteMetrics</c> for the Sammenligning tab. Null for runs from before the comparison existed.</summary>
    [Column("MetricsJson")][NullSetting(NullSetting = NullSettings.Null)][SpecialDbType(SpecialDbTypes.NVARCHARMAX)] public string? MetricsJson { get; set; }
}

[TableName("laitSeoFinding")]
[PrimaryKey("Id", AutoIncrement = true)]
[ExplicitColumns]
public class FindingRow
{
    [Column("Id")][PrimaryKeyColumn(AutoIncrement = true)] public int Id { get; set; }
    [Column("RunId")][Index(IndexTypes.NonClustered)] public int RunId { get; set; }
    [Column("RuleId")][Length(16)] public string RuleId { get; set; } = "";
    [Column("Url")][Length(2048)] public string Url { get; set; } = "";
    [Column("Severity")] public int Severity { get; set; }
    [Column("Evidence")][SpecialDbType(SpecialDbTypes.NVARCHARMAX)] public string Evidence { get; set; } = "";
    /// <summary>Open / Resolved / Ignored (see FindingStatus).</summary>
    [Column("Status")] public int Status { get; set; }
    /// <summary>Not present in the previous run.</summary>
    [Column("IsNew")] public bool IsNew { get; set; }
    /// <summary>Own site only: the published Umbraco node behind <see cref="Url"/>.</summary>
    [Column("NodeId")][NullSetting(NullSetting = NullSettings.Null)] public int? NodeId { get; set; }
    [Column("NodeKey")][NullSetting(NullSetting = NullSettings.Null)] public Guid? NodeKey { get; set; }
    [Column("DocumentType")][NullSetting(NullSetting = NullSettings.Null)][Length(255)] public string? DocumentType { get; set; }
}

/// <summary>Dated per-URL snapshot; only snapshots can prove "unchanged" (CMP-04).</summary>
[TableName("laitSeoPage")]
[PrimaryKey("Id", AutoIncrement = true)]
[ExplicitColumns]
public class PageRow
{
    [Column("Id")][PrimaryKeyColumn(AutoIncrement = true)] public int Id { get; set; }
    [Column("RunId")][Index(IndexTypes.NonClustered)] public int RunId { get; set; }
    [Column("Url")][Length(2048)] public string Url { get; set; } = "";
    [Column("Status")] public int Status { get; set; }
    [Column("Title")][NullSetting(NullSetting = NullSettings.Null)][SpecialDbType(SpecialDbTypes.NVARCHARMAX)] public string? Title { get; set; }
    [Column("H1")][NullSetting(NullSetting = NullSettings.Null)][SpecialDbType(SpecialDbTypes.NVARCHARMAX)] public string? H1 { get; set; }
    [Column("MetaDescription")][NullSetting(NullSetting = NullSettings.Null)][SpecialDbType(SpecialDbTypes.NVARCHARMAX)] public string? MetaDescription { get; set; }
    [Column("Canonical")][NullSetting(NullSetting = NullSettings.Null)][Length(2048)] public string? Canonical { get; set; }
    [Column("JsonLdHash")][Length(64)] public string JsonLdHash { get; set; } = "";
    [Column("WordCount")] public int WordCount { get; set; }
    [Column("InSitemap")] public bool InSitemap { get; set; }
}

/// <summary>Competitor changes since the previous run (CMP-01/02).</summary>
[TableName("laitSeoChange")]
[PrimaryKey("Id", AutoIncrement = true)]
[ExplicitColumns]
public class ChangeRow
{
    [Column("Id")][PrimaryKeyColumn(AutoIncrement = true)] public int Id { get; set; }
    [Column("RunId")][Index(IndexTypes.NonClustered)] public int RunId { get; set; }
    [Column("Url")][Length(2048)] public string Url { get; set; } = "";
    /// <summary>new-page, removed-page or changed.</summary>
    [Column("Kind")][Length(16)] public string Kind { get; set; } = "";
    [Column("Field")][NullSetting(NullSetting = NullSettings.Null)][Length(32)] public string? Field { get; set; }
    [Column("OldValue")][NullSetting(NullSetting = NullSettings.Null)][SpecialDbType(SpecialDbTypes.NVARCHARMAX)] public string? OldValue { get; set; }
    [Column("NewValue")][NullSetting(NullSetting = NullSettings.Null)][SpecialDbType(SpecialDbTypes.NVARCHARMAX)] public string? NewValue { get; set; }
}

/// <summary>One AI attempt per run: the report JSON, or why there is none, plus what it cost in tokens.</summary>
[TableName("laitSeoReport")]
[PrimaryKey("Id", AutoIncrement = true)]
[ExplicitColumns]
public class ReportRow
{
    [Column("Id")][PrimaryKeyColumn(AutoIncrement = true)] public int Id { get; set; }
    [Column("RunId")][Index(IndexTypes.NonClustered)] public int RunId { get; set; }
    [Column("CreatedUtc")][Index(IndexTypes.NonClustered)] public DateTime CreatedUtc { get; set; }
    /// <summary>ok | nothing-important | skipped-limit | failed</summary>
    [Column("Status")][Length(24)] public string Status { get; set; } = "";
    [Column("Error")][NullSetting(NullSetting = NullSettings.Null)][SpecialDbType(SpecialDbTypes.NVARCHARMAX)] public string? Error { get; set; }
    [Column("Json")][NullSetting(NullSetting = NullSettings.Null)][SpecialDbType(SpecialDbTypes.NVARCHARMAX)] public string? Json { get; set; }
    [Column("TriageProvider")][Length(16)] public string TriageProvider { get; set; } = "";
    [Column("ReportModel")][Length(64)] public string ReportModel { get; set; } = "";
    [Column("ChatInputTokens")] public long ChatInputTokens { get; set; }
    [Column("ChatOutputTokens")] public long ChatOutputTokens { get; set; }
    [Column("JevInputTokens")] public long JevInputTokens { get; set; }
    /// <summary>What the run cost in USD, at the prices that applied then. Null = unknown (a price was missing).</summary>
    [Column("CostUsd")][NullSetting(NullSetting = NullSettings.Null)] public double? CostUsd { get; set; }
}

/// <summary>A site the plugin crawls, edited in the Indstillinger tab (appsettings only seeds this table once).</summary>
[TableName("laitSeoSite")]
[PrimaryKey("Id", AutoIncrement = true)]
[ExplicitColumns]
public class SiteRow
{
    [Column("Id")][PrimaryKeyColumn(AutoIncrement = true)] public int Id { get; set; }
    [Column("Url")][Length(2048)] public string Url { get; set; } = "";
    [Column("Enabled")] public bool Enabled { get; set; }
    [Column("IntervalDays")] public int IntervalDays { get; set; }
    [Column("MaxPages")] public int MaxPages { get; set; }
    /// <summary>JSON string array.</summary>
    [Column("Competitors")][SpecialDbType(SpecialDbTypes.NVARCHARMAX)] public string Competitors { get; set; } = "[]";
    /// <summary>JSON string array of rule ids.</summary>
    [Column("DisabledRules")][SpecialDbType(SpecialDbTypes.NVARCHARMAX)] public string DisabledRules { get; set; } = "[]";
}

public class CreateSeoTables(IMigrationContext context) : AsyncMigrationBase(context)
{
    protected override Task MigrateAsync()
    {
        if (!TableExists("laitSeoRun")) Create.Table<RunRow>().Do();
        if (!TableExists("laitSeoFinding")) Create.Table<FindingRow>().Do();
        if (!TableExists("laitSeoPage")) Create.Table<PageRow>().Do();
        if (!TableExists("laitSeoChange")) Create.Table<ChangeRow>().Do();
        return Task.CompletedTask;
    }
}

public class CreateSeoReportTable(IMigrationContext context) : AsyncMigrationBase(context)
{
    protected override Task MigrateAsync()
    {
        if (!TableExists("laitSeoReport")) Create.Table<ReportRow>().Do();
        return Task.CompletedTask;
    }
}

public class CreateSeoSiteTable(IMigrationContext context) : AsyncMigrationBase(context)
{
    protected override Task MigrateAsync()
    {
        if (!TableExists("laitSeoSite")) Create.Table<SiteRow>().Do();
        return Task.CompletedTask;
    }
}

/// <summary>Adds the node columns to laitSeoFinding on installs that created the table before they existed.</summary>
public class AddFindingNodeColumns(IMigrationContext context) : AsyncMigrationBase(context)
{
    protected override Task MigrateAsync()
    {
        if (!ColumnExists("laitSeoFinding", "NodeId")) Alter.Table("laitSeoFinding").AddColumn("NodeId").AsInt32().Nullable().Do();
        if (!ColumnExists("laitSeoFinding", "NodeKey")) Alter.Table("laitSeoFinding").AddColumn("NodeKey").AsGuid().Nullable().Do();
        if (!ColumnExists("laitSeoFinding", "DocumentType")) Alter.Table("laitSeoFinding").AddColumn("DocumentType").AsString(255).Nullable().Do();
        return Task.CompletedTask;
    }
}

/// <summary>Adds the cost column to laitSeoReport on installs that created the table before it existed.</summary>
public class AddReportCostColumn(IMigrationContext context) : AsyncMigrationBase(context)
{
    protected override Task MigrateAsync()
    {
        if (!ColumnExists("laitSeoReport", "CostUsd")) Alter.Table("laitSeoReport").AddColumn("CostUsd").AsDouble().Nullable().Do();
        return Task.CompletedTask;
    }
}

/// <summary>Adds the metrics column to laitSeoRun on installs that created the table before it existed.</summary>
public class AddRunMetricsColumn(IMigrationContext context) : AsyncMigrationBase(context)
{
    protected override Task MigrateAsync()
    {
        if (!ColumnExists("laitSeoRun", "MetricsJson")) AddColumn<RunRow>("MetricsJson");
        return Task.CompletedTask;
    }
}

/// <summary>Umbraco runs this unattended on startup (SQL Server and SQLite).</summary>
public class SeoMigrationPlan() : PackageMigrationPlan("Lait.Umbraco.Awesome.SEO")
{
    protected override void DefinePlan()
    {
        To<CreateSeoTables>(new Guid("6f2f2a54-5c9e-4b43-9a53-0a3b2c8f1d01"));
        To<CreateSeoReportTable>(new Guid("0b7d5a3e-3c41-4f0e-8a77-5d1e2f9c4b02"));
        To<CreateSeoSiteTable>(new Guid("c41a9e72-6d38-4b15-9f60-2e8b7a1d5c03"));
        To<AddFindingNodeColumns>(new Guid("e8d2b6f1-47a9-4c3e-b150-9a6c0d7e2f04"));
        To<AddReportCostColumn>(new Guid("2c9f4d17-8b6e-4a30-95e1-7d3a1b6c8e05"));
        To<AddRunMetricsColumn>(new Guid("9a1e6b44-3d72-4f08-a5c9-1b7e4d2f6a06"));
    }
}
