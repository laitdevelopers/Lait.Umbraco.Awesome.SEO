# Awesome SEO for Umbraco

An **SEO report and action plan inside the Umbraco 17 backoffice**. The package crawls your site and up to five
competitors on a schedule, runs 40 SEO checks, and tells your editors in plain words what to fix first, what is
going well, and where the competitors are ahead.

- **Technical SEO score** for the site, weighted by how much each check matters for rankings, split per category and
  with what pulls it down the most; plus critical issues, warnings and info, and what is new or fixed since last time.
- **Action plan** sorted into *critical*, *quick wins*, *high impact*, *long term* and *nice to have*, each item with
  effort, how to fix it and links straight to the affected pages in Umbraco.
- **Competitor comparison**: the same checks run on the competitors, side by side, with *ahead / level / behind*
  per measurement, structured data types and site sections they have and you don't, and what they changed.
- **Rankings from Google Search Console** (optional, free): searches, positions, clicks and CTR for the last 28 days,
  where a page is close to the top 3, high positions with few clicks, declining pages, several pages competing for one
  search, and which findings sit on pages that actually get traffic.
- **Free and self-contained**: everything above runs inside your Umbraco, with no external service and no key
  (Search Console and AI are optional add-ons).
- **Optional AI** (Claude and/or Jev by TypeSafe) writes a report of what changed and makes the action plan concrete
  (why it matters, step by step). You always see the maximum price first, and a monthly USD cap stops it.

> The dashboard is in **Danish**.

## What it checks

40 checks in four groups, on your site and on the competitors alike:

- **Crawl and indexing**: 5xx/4xx pages, broken internal links, redirect chains and loops, noindex, orphan pages,
  click depth, robots.txt blocking pages in the sitemap, sitemap problems, indexable pages missing from the sitemap.
- **Technical**: canonical (missing, pointing elsewhere, header vs HTML), HTTPS and mixed content, security headers,
  viewport, `<html lang>`, slow TTFB, JavaScript-only pages, structured data (Organization/WebSite on the start page,
  JSON-LD missing, invalid JSON-LD).
- **On-page**: title and meta description (missing, length, generic, duplicates), H1 (missing, several, duplicates),
  skipped heading levels, images without alt, generic link text ("læs mere"), Open Graph.
- **Content**: thin content, duplicate content, pages without links.

**The score** is 100 × (1 − Σ weight × share of pages failing / Σ weight). Each check has a weight from 0 to 5 for how
much it matters for rankings in Google: indexing, titles and duplicate content weigh most; meta description, Open Graph
and structured data less; security headers and heading order not at all (they are still reported). Site-wide checks
(sitemap, structured data on the start page) fail as a whole. The weights are shown next to each check in the settings.

Findings on your own site are linked to the Umbraco node (looked up in the published content cache), so editors
can open the page directly from the report. Each check can be switched off per site.

**Limits:** pages are read as raw HTML (like most crawlers), so content rendered only by JavaScript is not seen.
Up to 300 pages per site by default (configurable), so competitor numbers are samples, not an exact census.
The crawl does not measure rankings or traffic; that is what Search Console is for (below).

## Install

```bash
dotnet add package Lait.Umbraco.Awesome.SEO
```

Requires Umbraco 17 (.NET 10). Database tables are created automatically on startup (SQL Server and SQLite).

Then open **Content → SEO** in the backoffice as an administrator:

1. **Indstillinger → Sites og konkurrenter**: pick your site (Umbraco's domains are listed), how often to analyse,
   and up to five competitor URLs. Save.
2. Press **Analysér (gratis)**, or wait for the scheduler. A run takes a few minutes and shows its progress.
3. Read **Overblik**, work through **Handlingsplan**, and compare in **Konkurrenter**.

Editors can read the report; only administrators can change settings, start runs or use AI.

## AI (optional)

Under **Indstillinger → AI** an administrator can add keys:

| Key | What it does |
| --- | --- |
| **Claude** (Anthropic) | Writes text: a report of what changed since the last run, and *why* + *steps* for each item in the action plan. |
| **Jev** (TypeSafe) | Does not write text. It judges: which changes matter, and how much impact each plan item has (1-5). |

Use one, the other or both (with both, Jev judges and Claude writes). The AI can only add to the measured items;
it never invents findings. Keys are encrypted with ASP.NET Data Protection and never sent back to the browser.
**Test forbindelse** checks a key with one tiny call.

- **Monthly cap** in USD (empty = no cap). Every AI run stores its cost; a run is skipped if the worst case could
  take the month over the cap. Prices default to list prices for the default models and can be overridden.
- The first run per site is a baseline and never calls AI. *Analysér (gratis)* never calls AI.
- Data sent: rule, URL and measured value for your own site; public title/H1/meta description for competitors.
  Never personal data.

On several servers, in containers or on Umbraco Cloud, persist and share the Data Protection key ring, otherwise a
saved key becomes unreadable after a redeploy (the settings tab then asks you to enter it again).

## Google Search Console (optional)

The **Søgninger** tab shows what Google itself reports for your site (read-only, free), crossed with the crawl:

- totals for the last 28 days with final data, against the 28 days before (clicks, impressions, CTR, average position);
- **close to the top 3**: searches where a page is at position 4-20, and whether the words of the search are in its title;
- **high position, few clicks**: top 5 with a CTR far below normal for the position (title/description to rewrite);
- **findings on pages people find**: pages with Google traffic that have findings counting in the score;
- **declining pages** (30 % fewer clicks or more), **several pages for one search**, and the top searches.

With Umbraco AI Copilot installed, "Ret med AI" writes a new title (and description) for the page from the search.

Setup, under **Indstillinger → Google Search Console**:

1. In Google Cloud Console, pick or create a project and enable the **Google Search Console API**.
2. Create a **service account** (no roles needed) and download a **JSON key** for it.
3. In Search Console, add the service account's e-mail as a user on your property (restricted access is enough).
4. Paste the JSON, press **Test forbindelse** (it lists which property each site will use), and save.

The key is encrypted with Data Protection like the AI keys, or can come from `LaitSeo:SearchConsole:ServiceAccountJson`.
The property is found automatically: a URL-prefix property for the site (www or not), else a domain property. Google is
asked at most every 6 hours per site. Search Console has no data for competitors.

## Configuration (appsettings, optional)

Everything can be set in the dashboard. `appsettings.json` can seed the sites on first start and hold the AI keys
(user-secrets or environment variables, e.g. `LaitSeo__Ai__Claude__ApiKey`); values saved in the dashboard win.

```json
"LaitSeo": {
  "Sites": [
    { "Url": "https://example.dk/", "IntervalDays": 7, "MaxPages": 300,
      "Competitors": [ "https://competitor.dk/" ], "DisabledRules": [] }
  ],
  "Ai": {
    "Claude": { "ApiKey": "", "TriageModel": "claude-haiku-5-5", "ReportModel": "claude-sonnet-5-5" },
    "Jev": { "ApiKey": "", "Model": "jev-latest" },
    "MonthlyBudgetUsd": 5
  }
}
```

The scheduler runs on the server with the scheduling role only, starts after the first request and checks every
five minutes which sites are due.

## Umbraco Cloud

The package runs on Umbraco Cloud like any NuGet package (no file system writes, tables are created by a package
migration on each environment's database at startup). Things to know:

- **Protected environments** (development/staging use basic auth): the crawler sends Umbraco's basic-auth shared
  secret header (`Umbraco:CMS:BasicAuth:SharedSecret`) when it crawls *your own* site, so the analysis works there too.
  It is never sent to competitors. If no shared secret is configured, the run stops with a clear message instead of
  saving an empty report.
- **Web Application Firewall / bot protection** (Cloudflare on custom hostnames): if the start page answers with a bot
  challenge, the run stops and says so. Allow the user agent `LaitSeoReport` in the WAF or Bot Fight Mode settings.
- **AI keys**: keys saved in the dashboard are encrypted with ASP.NET Data Protection. If the key ring does not survive a
  redeploy, the dashboard asks you to enter them again. On Cloud you can instead use Secrets management
  (environment variables such as `LaitSeo__Ai__Claude__ApiKey` and `LaitSeo__Ai__Jev__ApiKey`).
- **Per environment**: sites, competitors, AI settings and reports live in each environment's database and are not
  moved by Umbraco Deploy. Set the site up on the environment you want to analyse (usually Live).

## How the crawler behaves

It identifies itself as `LaitSeoReport/1.0`, respects robots.txt (RFC 9309), waits 250 ms between requests, reads
the sitemap first and then follows links. It stays on the configured site: redirects to other domains and sitemaps
on other hosts are not followed. Each response is capped at 5 MB and 30 seconds, and a page that fails never stops
the run.

## Develop

```bash
dotnet test
```

- `test/setup-local-test.ps1` creates a throwaway Umbraco 17 site (SQLite) that references the project.
- `test/pack-local.ps1` packs to a local NuGet folder feed so any site on your machine can `dotnet add package` it.

Release: bump `<Version>` in the `.csproj` and `version` in `wwwroot/umbraco-package.json` (plus the `?v=` cache
busters in the `.js` imports), then pack and push with your nuget.org API key:

```bash
dotnet pack src/Lait.Umbraco.Awesome.SEO -c Release -o artifacts
dotnet nuget push artifacts/Lait.Umbraco.Awesome.SEO.<version>.nupkg --api-key <key> --source https://api.nuget.org/v3/index.json
```

The Umbraco Marketplace picks it up from NuGet and reads `umbraco-marketplace.json` from this repository.

## Author

Made by [LAIT](https://lait.dk), Anders Bootsmann Overvad.

## License

MIT
