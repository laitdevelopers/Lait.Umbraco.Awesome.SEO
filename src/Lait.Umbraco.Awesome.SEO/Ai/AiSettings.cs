using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Services;

namespace Lait.Umbraco.Awesome.SEO.Ai;

/// <summary>What the Indstillinger tab saves. API keys are stored encrypted (ASP.NET Data Protection), never as plain text.</summary>
public sealed class StoredAi
{
    public string? ClaudeApiKey { get; set; }
    public string? ClaudeBaseUrl { get; set; }
    public string? TriageModel { get; set; }
    public string? ReportModel { get; set; }
    public string? JevApiKey { get; set; }
    public string? JevModel { get; set; }
    /// <summary>Legacy: older versions saved a token limit here. It is ignored now; the dollar budget replaces it.</summary>
    public long? MonthlyTokenLimit { get; set; }
    public double? MonthlyBudgetUsd { get; set; }
    public double? ReportInputUsdPerMillion { get; set; }
    public double? ReportOutputUsdPerMillion { get; set; }
    public double? TriageInputUsdPerMillion { get; set; }
    public double? TriageOutputUsdPerMillion { get; set; }
    public double? JevInputUsdPerMillion { get; set; }
}

public enum KeySource { None, Config, Database }

/// <summary>
/// The effective AI options: appsettings/secrets as the base, anything saved in the Indstillinger tab on top.
/// Registered as <see cref="IOptionsMonitor{AiOptions}"/>, so the rest of the plugin just reads "the options".
/// </summary>
public sealed class AiSettings(IConfiguration config, IKeyValueService keyValues, IDataProtectionProvider dataProtection, ILogger<AiSettings> logger)
    : IOptionsMonitor<AiOptions>
{
    const string StoreKey = "Lait.Umbraco.Awesome.SEO.Ai";
    static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60); // other servers pick up a change within a minute

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly IDataProtector _protector = dataProtection.CreateProtector("Lait.Umbraco.Awesome.SEO.AiKeys");

    AiOptions? _cache;
    DateTime _cacheUntil;

    public AiOptions CurrentValue => _cache is not null && DateTime.UtcNow < _cacheUntil ? _cache : Refresh();
    public AiOptions Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<AiOptions, string?> listener) => null;

    /// <summary>True when a key was saved but cannot be decrypted (the Data Protection key ring changed): the user must enter it again.</summary>
    public bool StoredKeyUnreadable { get; private set; }

    public AiOptions FromConfig() => config.GetSection(AiOptions.SectionName).Get<AiOptions>() ?? new AiOptions();

    public StoredAi Stored()
    {
        var json = keyValues.GetValue(StoreKey);
        if (string.IsNullOrWhiteSpace(json)) return new StoredAi();
        try { return JsonSerializer.Deserialize<StoredAi>(json, Json) ?? new StoredAi(); }
        catch (JsonException) { return new StoredAi(); }
    }

    public (KeySource Claude, KeySource Jev) KeySources()
    {
        var stored = Stored();
        var cfg = FromConfig();
        return (Source(stored.ClaudeApiKey, cfg.Claude.ApiKey), Source(stored.JevApiKey, cfg.Jev.ApiKey));

        KeySource Source(string? enc, string? fromConfig) =>
            Decrypt(enc) is not null ? KeySource.Database : !string.IsNullOrWhiteSpace(fromConfig) ? KeySource.Config : KeySource.None;
    }

    /// <summary>Saves. Key fields in <paramref name="stored"/> must already be encrypted via <see cref="Encrypt"/> (or kept from <see cref="Stored"/>).</summary>
    public void Save(StoredAi stored)
    {
        keyValues.SetValue(StoreKey, JsonSerializer.Serialize(stored, Json));
        _cache = null;
    }

    public string Encrypt(string plain) => _protector.Protect(plain);

    string? Decrypt(string? encrypted)
    {
        if (string.IsNullOrWhiteSpace(encrypted)) return null;
        try { return _protector.Unprotect(encrypted); }
        catch (CryptographicException e)
        {
            StoredKeyUnreadable = true;
            logger.LogWarning(e, "A saved SEO API key could not be decrypted (Data Protection key ring changed?). Enter the key again in the Indstillinger tab.");
            return null;
        }
    }

    AiOptions Refresh()
    {
        StoredKeyUnreadable = false;
        var o = FromConfig();
        var s = Stored();

        if (Decrypt(s.ClaudeApiKey) is { } claudeKey) o.Claude.ApiKey = claudeKey;
        if (Decrypt(s.JevApiKey) is { } jevKey) o.Jev.ApiKey = jevKey;
        if (!string.IsNullOrWhiteSpace(s.ClaudeBaseUrl)) o.Claude.BaseUrl = s.ClaudeBaseUrl;
        if (!string.IsNullOrWhiteSpace(s.TriageModel)) o.Claude.TriageModel = s.TriageModel;
        if (!string.IsNullOrWhiteSpace(s.ReportModel)) o.Claude.ReportModel = s.ReportModel;
        if (!string.IsNullOrWhiteSpace(s.JevModel)) o.Jev.Model = s.JevModel;
        if (s.MonthlyBudgetUsd is { } budget) o.MonthlyBudgetUsd = budget == 0 ? null : budget; // 0 = explicitly no budget limit
        o.Claude.ReportInputUsdPerMillion = s.ReportInputUsdPerMillion ?? o.Claude.ReportInputUsdPerMillion;
        o.Claude.ReportOutputUsdPerMillion = s.ReportOutputUsdPerMillion ?? o.Claude.ReportOutputUsdPerMillion;
        o.Claude.TriageInputUsdPerMillion = s.TriageInputUsdPerMillion ?? o.Claude.TriageInputUsdPerMillion;
        o.Claude.TriageOutputUsdPerMillion = s.TriageOutputUsdPerMillion ?? o.Claude.TriageOutputUsdPerMillion;
        o.Jev.InputUsdPerMillion = s.JevInputUsdPerMillion ?? o.Jev.InputUsdPerMillion;

        _cache = o;
        _cacheUntil = DateTime.UtcNow + CacheFor;
        return o;
    }
}

/// <summary>Validates what the AI form submits (admin input still reaches provider URLs and model names).</summary>
public static class AiSettingsValidator
{
    static readonly System.Text.RegularExpressions.Regex ModelName = new(@"^[A-Za-z0-9._:/\-]{1,100}$");

    public static bool IsModelName(string model) => ModelName.IsMatch(model);

    public static List<string> Validate(Models.AiSaveRequest? r)
    {
        var errors = new List<string>();
        if (r?.Claude is null || r.Jev is null) return ["Ugyldig forespørgsel."];

        CheckKey("Claude-nøglen", r.Claude.ApiKey);
        CheckKey("Jev-nøglen", r.Jev.ApiKey);
        CheckModel("Triage-modellen", r.Claude.TriageModel);
        CheckModel("Rapport-modellen", r.Claude.ReportModel);
        CheckModel("Jev-modellen", r.Jev.Model);

        if (!string.IsNullOrWhiteSpace(r.Claude.BaseUrl) &&
            !(Uri.TryCreate(r.Claude.BaseUrl.Trim(), UriKind.Absolute, out var u) && u.Scheme is "http" or "https"))
            errors.Add("Claude-adressen skal være en gyldig http(s)-adresse.");
        if (r.MonthlyBudgetUsd is < 0) errors.Add("Månedligt budget kan ikke være negativt (0 = ingen grænse).");
        if (r.MonthlyBudgetUsd is > 1_000_000) errors.Add("Månedligt budget er urealistisk højt.");
        foreach (var (name, v) in new[] { ("Rapport input", r.Prices?.ReportInput), ("Rapport output", r.Prices?.ReportOutput), ("Triage input", r.Prices?.TriageInput), ("Triage output", r.Prices?.TriageOutput), ("Jev input", r.Prices?.JevInput) })
            if (v is < 0) errors.Add($"Prisen \"{name}\" kan ikke være negativ.");
        return errors;

        void CheckKey(string label, string? key)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            if (key.Trim().Length > 400 || key.Any(char.IsWhiteSpace) && key.Trim().Any(char.IsWhiteSpace)) errors.Add($"{label} ser ikke gyldig ud (for lang eller med mellemrum).");
        }
        void CheckModel(string label, string? model)
        {
            if (!string.IsNullOrWhiteSpace(model) && !ModelName.IsMatch(model.Trim())) errors.Add($"{label} må kun indeholde bogstaver, tal og . _ : / -");
        }
    }
}
