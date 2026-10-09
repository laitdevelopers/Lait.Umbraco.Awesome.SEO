using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lait.Umbraco.Awesome.SEO.Ai;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Services;

namespace Lait.Umbraco.Awesome.SEO.Search;

/// <summary>The two fields of a Google service account key file (JSON) that signing needs.</summary>
public sealed record ServiceAccount(string ClientEmail, string PrivateKey)
{
    /// <summary>Null when the text is not a service account key.</summary>
    public static ServiceAccount? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var o = JsonNode.Parse(json);
            var email = o?["client_email"]?.GetValue<string>();
            var key = o?["private_key"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(email) || key?.Contains("PRIVATE KEY") != true ? null : new(email.Trim(), key);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return null; }
    }
}

/// <summary>The service account key: saved in the dashboard (encrypted with Data Protection), else from appsettings/secrets.</summary>
public sealed class SearchConsoleSettings(IConfiguration config, IKeyValueService keyValues, IDataProtectionProvider dataProtection, ILogger<SearchConsoleSettings> logger)
{
    const string StoreKey = "Lait.Umbraco.Awesome.SEO.SearchConsole";
    public const string ConfigKey = "LaitSeo:SearchConsole:ServiceAccountJson";
    readonly IDataProtector _protector = dataProtection.CreateProtector("Lait.Umbraco.Awesome.SEO.SearchConsoleKey");

    /// <summary>Unreadable = a key was saved but cannot be decrypted (Data Protection key ring changed): it must be entered again.</summary>
    public (ServiceAccount? Account, KeySource Source, bool Unreadable) Current()
    {
        var unreadable = false;
        if (keyValues.GetValue(StoreKey) is { Length: > 0 } stored)
        {
            try { if (ServiceAccount.Parse(_protector.Unprotect(stored)) is { } saved) return (saved, KeySource.Database, false); }
            catch (CryptographicException e)
            {
                unreadable = true;
                logger.LogWarning(e, "The saved Search Console key could not be decrypted (Data Protection key ring changed?). Enter it again in the Indstillinger tab.");
            }
        }
        return ServiceAccount.Parse(config[ConfigKey]) is { } fromConfig ? (fromConfig, KeySource.Config, unreadable) : (null, KeySource.None, unreadable);
    }

    /// <summary>null clears the saved key (appsettings/secrets then apply, if set).</summary>
    public void Save(string? json) => keyValues.SetValue(StoreKey, json is null ? "" : _protector.Protect(json));
}

public sealed record SearchRow(IReadOnlyList<string> Keys, double Clicks, double Impressions, double Ctr, double Position);

/// <summary>What one fetch from Search Console holds: the last 28 days with final data, and the 28 days before.</summary>
public sealed record SearchData(
    DateOnly From, DateOnly To, SearchRow? Totals, SearchRow? PreviousTotals,
    IReadOnlyList<SearchRow> Pages, IReadOnlyList<SearchRow> PreviousPages, IReadOnlyList<SearchRow> QueryPages);

/// <summary>A message for the dashboard (Danish) about why Search Console could not be read.</summary>
public sealed class SearchConsoleException(string message) : Exception(message);

/// <summary>Search Console API with a service account (OAuth 2 JWT bearer grant), read-only scope. No Google SDK: the JWT is signed with RSA from .NET.</summary>
public sealed class SearchConsoleClient(IHttpClientFactory httpFactory)
{
    public const string HttpClientName = "LaitSeoSearchConsole";
    const string TokenUrl = "https://oauth2.googleapis.com/token";   // fixed: never the token_uri from a pasted file
    const string Api = "https://searchconsole.googleapis.com/webmasters/v3/";
    const string Scope = "https://www.googleapis.com/auth/webmasters.readonly";
    /// <summary>Search Console data is final after about two days.</summary>
    const int LagDays = 3;

    readonly SemaphoreSlim _tokenLock = new(1, 1);
    (string Email, string Token, DateTime Until)? _token;

    /// <summary>Properties (sites) the service account has been added to in Search Console.</summary>
    public async Task<IReadOnlyList<string>> PropertiesAsync(ServiceAccount account, CancellationToken ct)
    {
        var json = await SendAsync(account, new HttpRequestMessage(HttpMethod.Get, Api + "sites"), ct);
        return json?["siteEntry"]?.AsArray().Select(s => s?["siteUrl"]?.GetValue<string>()).OfType<string>().ToList() ?? [];
    }

    public async Task<SearchData> FetchAsync(ServiceAccount account, string property, DateOnly today, CancellationToken ct)
    {
        var to = today.AddDays(-LagDays);
        var from = to.AddDays(-27);
        var (prevFrom, prevTo) = (from.AddDays(-28), from.AddDays(-1));
        var calls = new[]
        {
            QueryAsync(account, property, from, to, [], ct), QueryAsync(account, property, prevFrom, prevTo, [], ct),
            QueryAsync(account, property, from, to, ["page"], ct), QueryAsync(account, property, prevFrom, prevTo, ["page"], ct),
            QueryAsync(account, property, from, to, ["query", "page"], ct),
        };
        var r = await Task.WhenAll(calls);
        return new SearchData(from, to, r[0].FirstOrDefault(), r[1].FirstOrDefault(), r[2], r[3], r[4]);
    }

    async Task<List<SearchRow>> QueryAsync(ServiceAccount account, string property, DateOnly from, DateOnly to, string[] dimensions, CancellationToken ct)
    {
        // ponytail: one page of up to 25,000 rows; page with startRow if a site has more query/page pairs than that in 28 days
        var body = new { startDate = from.ToString("yyyy-MM-dd"), endDate = to.ToString("yyyy-MM-dd"), dimensions, rowLimit = 25000 };
        var request = new HttpRequestMessage(HttpMethod.Post, $"{Api}sites/{Uri.EscapeDataString(property)}/searchAnalytics/query") { Content = JsonContent.Create(body) };
        var json = await SendAsync(account, request, ct);
        return json?["rows"]?.AsArray().OfType<JsonNode>().Select(r => new SearchRow(
            r["keys"]?.AsArray().Select(k => k?.GetValue<string>() ?? "").ToList() ?? [],
            r["clicks"]?.GetValue<double>() ?? 0, r["impressions"]?.GetValue<double>() ?? 0,
            r["ctr"]?.GetValue<double>() ?? 0, r["position"]?.GetValue<double>() ?? 0)).ToList() ?? [];
    }

    async Task<JsonNode?> SendAsync(ServiceAccount account, HttpRequestMessage request, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(HttpClientName);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(account, http, ct));
        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (response.IsSuccessStatusCode) return JsonNode.Parse(text);

        var error = TryParse(text)?["error"];
        var message = error?["message"]?.GetValue<string>() ?? text;
        throw new SearchConsoleException(response.StatusCode switch
        {
            HttpStatusCode.Forbidden when message.Contains("has not been used", StringComparison.OrdinalIgnoreCase) || message.Contains("disabled", StringComparison.OrdinalIgnoreCase)
                => "Google Search Console API er ikke slået til i Google Cloud-projektet for service-kontoen. Slå den til under APIs & Services, og prøv igen om et par minutter.",
            HttpStatusCode.Forbidden or HttpStatusCode.NotFound
                => $"Service-kontoen har ikke adgang til property'en. Tilføj {account.ClientEmail} som bruger (begrænset adgang er nok) i Search Console under Indstillinger → Brugere og tilladelser.",
            _ => $"Search Console svarede {(int)response.StatusCode}: {(message.Length > 200 ? message[..200] : message)}",
        });
    }

    async Task<string> TokenAsync(ServiceAccount account, HttpClient http, CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_token is { } t && t.Email == account.ClientEmail && DateTime.UtcNow < t.Until) return t.Token;
            using var response = await http.PostAsync(TokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                ["assertion"] = Assertion(account, DateTimeOffset.UtcNow),
            }), ct);
            var json = TryParse(await response.Content.ReadAsStringAsync(ct));
            if (!response.IsSuccessStatusCode || json?["access_token"]?.GetValue<string>() is not { } token)
                throw new SearchConsoleException("Google afviste nøglen. Er det den nyeste JSON-nøgle for service-kontoen, og er den ikke slettet i Google Cloud? " +
                    $"({json?["error_description"]?.GetValue<string>() ?? json?["error"]?.GetValue<string>() ?? ((int)response.StatusCode).ToString()})");
            var seconds = json["expires_in"]?.GetValue<int>() ?? 3600;
            _token = (account.ClientEmail, token, DateTime.UtcNow.AddSeconds(seconds - 300));
            return token;
        }
        finally { _tokenLock.Release(); }
    }

    /// <summary>The signed JWT for Google's token endpoint (RS256, valid one hour).</summary>
    internal static string Assertion(ServiceAccount account, DateTimeOffset now)
    {
        static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = B64(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var claims = B64(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = account.ClientEmail, scope = Scope, aud = TokenUrl, iat = now.ToUnixTimeSeconds(), exp = now.AddHours(1).ToUnixTimeSeconds(),
        }));
        using var rsa = RSA.Create();
        try { rsa.ImportFromPem(account.PrivateKey); }
        catch (Exception e) when (e is ArgumentException or CryptographicException) { throw new SearchConsoleException("Den private nøgle i JSON-filen kan ikke læses. Indsæt hele filen, som den blev hentet fra Google Cloud."); }
        var signature = rsa.SignData(Encoding.ASCII.GetBytes($"{header}.{claims}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{header}.{claims}.{B64(signature)}";
    }

    static JsonNode? TryParse(string text)
    {
        try { return JsonNode.Parse(text); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// The property that holds data for <paramref name="siteUrl"/>: a URL-prefix property for the same host (www or not) and a parent path
    /// (the longest wins), else a domain property (sc-domain:) covering the host. Null when the service account has neither.
    /// </summary>
    public static string? MatchProperty(string siteUrl, IEnumerable<string> properties)
    {
        var site = new Uri(siteUrl);
        static string Bare(string host) => host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
        var host = Bare(site.Host).ToLowerInvariant();
        var list = properties.ToList();

        var prefix = list
            .Select(p => (Property: p, Uri: Uri.TryCreate(p, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? u : null))
            .Where(x => x.Uri is not null && Bare(x.Uri.Host).Equals(host, StringComparison.OrdinalIgnoreCase)
                        && site.AbsolutePath.StartsWith(x.Uri.AbsolutePath, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Uri!.AbsolutePath.Length)
            .ThenByDescending(x => x.Uri!.Host.Equals(site.Host, StringComparison.OrdinalIgnoreCase))   // exact host before the www variant
            .ThenByDescending(x => x.Uri!.Scheme == site.Scheme)
            .Select(x => x.Property).FirstOrDefault();
        if (prefix is not null) return prefix;

        return list.Where(p => p.StartsWith("sc-domain:", StringComparison.OrdinalIgnoreCase))
            .Select(p => (Property: p, Domain: p["sc-domain:".Length..].ToLowerInvariant()))
            .Where(x => host == x.Domain || host.EndsWith("." + x.Domain))
            .OrderByDescending(x => x.Domain.Length)
            .Select(x => x.Property).FirstOrDefault();
    }
}
