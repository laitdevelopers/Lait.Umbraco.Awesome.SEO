using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Microsoft.Extensions.AI;

namespace Lait.Umbraco.Awesome.SEO.Ai;

public sealed record TestResult(bool Ok, string Message);

/// <summary>"Test forbindelse": one tiny real call per provider, so an expired key or a wrong model name shows up immediately.</summary>
public sealed class ConnectionTester(IHttpClientFactory httpFactory)
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public async Task<TestResult> TestClaudeAsync(string apiKey, string? baseUrl, string model, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            var client = string.IsNullOrWhiteSpace(baseUrl) ? new AnthropicClient { ApiKey = apiKey } : new AnthropicClient { ApiKey = apiKey, BaseUrl = baseUrl };
            using var chat = client.AsIChatClient(model, 16);
            await chat.GetResponseAsync("Reply with the single word: ok", new ChatOptions { MaxOutputTokens = 16 }, timeout.Token);
            return new TestResult(true, $"Forbindelsen til Claude virker (model {model}).");
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            return new TestResult(false, "Claude svarede ikke: " + Clean(e.Message, apiKey));
        }
    }

    public async Task<TestResult> TestJevAsync(string apiKey, string baseUrl, string model, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            var http = httpFactory.CreateClient(JevTriageClient.HttpClientName);
            var body = new JsonObject
            {
                ["model"] = model,
                ["state"] = "ping",
                ["questions"] = new JsonObject { ["ok"] = new JsonObject { ["type"] = "noul", ["instructions"] = "Is the state the word ping?" } },
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + JevTriageClient.Path) { Content = JsonContent.Create(body) };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var resp = await http.SendAsync(req, timeout.Token);

            return resp.StatusCode switch
            {
                HttpStatusCode.OK => new TestResult(true, "Forbindelsen til Jev virker."),
                HttpStatusCode.Unauthorized => new TestResult(false, "Jev afviste nøglen (401): den er ugyldig eller tilbagekaldt. Opret en ny på console.typesafe.ai/keys."),
                HttpStatusCode.PaymentRequired => new TestResult(false, "Jev: der er ikke flere credits (402)."),
                HttpStatusCode.Forbidden => new TestResult(false, "Jev: adgang nægtet (403)."),
                HttpStatusCode.UnprocessableEntity => new TestResult(false, "Jev afviste forespørgslen (422): tjek modelnavnet."),
                (HttpStatusCode)429 => new TestResult(false, "Jev: for mange forespørgsler (429), prøv igen om lidt."),
                (HttpStatusCode)529 => new TestResult(false, "Jev er midlertidigt overbelastet (529), prøv igen om lidt."),
                _ => new TestResult(false, $"Jev svarede med status {(int)resp.StatusCode}."),
            };
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            return new TestResult(false, "Jev svarede ikke: " + Clean(e.Message, apiKey));
        }
    }

    /// <summary>Provider error text is shown to the admin; make sure it can never echo the key back.</summary>
    internal static string Clean(string message, string secret)
    {
        var m = string.IsNullOrEmpty(secret) ? message : message.Replace(secret, "***");
        return m.Length > 250 ? m[..250] + "…" : m;
    }
}
