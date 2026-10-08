using Lait.Umbraco.Awesome.SEO.Ai;
using Lait.Umbraco.Awesome.SEO.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Umbraco.Cms.Core.Services;

namespace Lait.Umbraco.Awesome.SEO.Tests;

public class AiSettingsTests
{
    sealed class MemoryKeyValues : IKeyValueService
    {
        public Dictionary<string, string> Data { get; } = [];
        public string? GetValue(string key) => Data.GetValueOrDefault(key);
        public void SetValue(string key, string value) => Data[key] = value;
        public void SetValue(string key, string originValue, string newValue) => Data[key] = newValue;
        public bool TrySetValue(string key, string originValue, string newValue) { Data[key] = newValue; return true; }
        public IReadOnlyDictionary<string, string?>? FindByKeyPrefix(string keyPrefix) => Data.Where(kv => kv.Key.StartsWith(keyPrefix)).ToDictionary(kv => kv.Key, kv => (string?)kv.Value);
    }

    static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build();

    static AiSettings Make(MemoryKeyValues kv, IConfiguration config, IDataProtectionProvider? dp = null) =>
        new(config, kv, dp ?? new EphemeralDataProtectionProvider(), NullLogger<AiSettings>.Instance);

    [Fact]
    public void Saved_key_is_encrypted_at_rest_decrypted_for_use_and_overrides_appsettings()
    {
        var kv = new MemoryKeyValues();
        var settings = Make(kv, Config(("LaitSeo:Ai:Claude:ApiKey", "from-config-key-123456"), ("LaitSeo:Ai:Claude:TriageModel", "config-model")));
        Assert.Equal("from-config-key-123456", settings.CurrentValue.Claude.ApiKey);
        Assert.Equal(KeySource.Config, settings.KeySources().Claude);

        var stored = settings.Stored();
        stored.ClaudeApiKey = settings.Encrypt("sk-ant-secret-from-the-ui");
        stored.TriageModel = "ui-model";
        settings.Save(stored);

        Assert.DoesNotContain("sk-ant-secret-from-the-ui", kv.Data.Values.Single());
        Assert.Equal("sk-ant-secret-from-the-ui", settings.CurrentValue.Claude.ApiKey);
        Assert.Equal("ui-model", settings.CurrentValue.Claude.TriageModel);
        Assert.Equal(KeySource.Database, settings.KeySources().Claude);
        Assert.True(settings.CurrentValue.Enabled);
    }

    [Fact]
    public void Clearing_the_saved_key_falls_back_to_appsettings_and_without_either_ai_is_off()
    {
        var kv = new MemoryKeyValues();
        var settings = Make(kv, Config(("LaitSeo:Ai:Jev:ApiKey", "apikey_from_config")));
        var stored = settings.Stored();
        stored.JevApiKey = settings.Encrypt("apikey_from_ui");
        settings.Save(stored);
        Assert.Equal("apikey_from_ui", settings.CurrentValue.Jev.ApiKey);

        stored.JevApiKey = null;
        settings.Save(stored);
        Assert.Equal("apikey_from_config", settings.CurrentValue.Jev.ApiKey);

        Assert.False(Make(new MemoryKeyValues(), Config()).CurrentValue.Enabled);
        Assert.Equal((KeySource.None, KeySource.None), Make(new MemoryKeyValues(), Config()).KeySources());
    }

    [Fact]
    public void A_key_that_cannot_be_decrypted_is_ignored_and_flagged()
    {
        var kv = new MemoryKeyValues();
        var writer = Make(kv, Config());
        var stored = writer.Stored();
        stored.ClaudeApiKey = writer.Encrypt("sk-ant-lost");
        writer.Save(stored);

        // A different key ring (new ephemeral provider) cannot read it, like after a redeploy without a persisted ring.
        var reader = Make(kv, Config(("LaitSeo:Ai:Claude:ApiKey", "config-fallback-key")));
        Assert.Equal("config-fallback-key", reader.CurrentValue.Claude.ApiKey);
        Assert.True(reader.StoredKeyUnreadable);
    }

    [Fact]
    public void Monthly_budget_zero_means_no_limit_and_overrides_a_configured_budget_and_a_legacy_stored_token_limit_is_ignored()
    {
        var kv = new MemoryKeyValues();
        var settings = Make(kv, Config(("LaitSeo:Ai:MonthlyBudgetUsd", "10"), ("LaitSeo:Ai:MonthlyTokenLimit", "1000")));
        Assert.Equal(10, settings.CurrentValue.MonthlyBudgetUsd);

        var stored = settings.Stored();
        stored.MonthlyBudgetUsd = 0;
        settings.Save(stored);
        Assert.Null(settings.CurrentValue.MonthlyBudgetUsd);

        stored.MonthlyBudgetUsd = 25.5;
        settings.Save(stored);
        Assert.Equal(25.5, settings.CurrentValue.MonthlyBudgetUsd);

        // Older versions saved a token limit in the tab (users could end up with "5"). It no longer applies;
        // only an appsettings token limit still does.
        stored.MonthlyTokenLimit = 5;
        settings.Save(stored);
        Assert.Equal(1000, settings.CurrentValue.MonthlyTokenLimit);
        var noConfigLimit = Make(new MemoryKeyValues(), Config());
        var s2 = noConfigLimit.Stored(); s2.MonthlyTokenLimit = 5; noConfigLimit.Save(s2);
        Assert.Null(noConfigLimit.CurrentValue.MonthlyTokenLimit);
    }

    static AiSaveRequest Req(Action<ClaudeSaveDto, JevSaveDto>? _ = null, ClaudeSaveDto? claude = null, JevSaveDto? jev = null, long? limit = null, PricesDto? prices = null) =>
        new(claude ?? new ClaudeSaveDto(null, false, null, "claude-haiku-5-5", "claude-sonnet-5-5"), jev ?? new JevSaveDto(null, false, "jev-latest"), limit, prices ?? new PricesDto(null, null, null, null, null));

    [Fact]
    public void Validator_accepts_normal_input_and_rejects_bad_models_urls_keys_and_numbers()
    {
        Assert.Empty(AiSettingsValidator.Validate(Req()));

        var errors = AiSettingsValidator.Validate(Req(
            claude: new ClaudeSaveDto("has a space", false, "ftp://x", "bad model!", "ok-model"),
            jev: new JevSaveDto(new string('k', 500), false, "jev latest"),
            limit: -1,
            prices: new PricesDto(-1, null, null, null, null)));

        Assert.Contains(errors, e => e.Contains("Claude-nøglen"));
        Assert.Contains(errors, e => e.Contains("Jev-nøglen"));
        Assert.Contains(errors, e => e.Contains("Triage-modellen"));
        Assert.Contains(errors, e => e.Contains("Jev-modellen"));
        Assert.Contains(errors, e => e.Contains("adresse"));
        Assert.Contains(errors, e => e.Contains("negativ"));
        Assert.Equal(2, errors.Count(e => e.Contains("negativ")));
    }

    [Fact]
    public void Provider_error_text_never_echoes_the_key()
    {
        var shown = ConnectionTester.Clean("401 invalid x-api-key: sk-ant-supersecret", "sk-ant-supersecret");
        Assert.DoesNotContain("supersecret", shown);
        Assert.Contains("***", shown);
        Assert.True(ConnectionTester.Clean(new string('x', 1000), "k").Length <= 251);
    }
}
