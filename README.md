# Awesome SEO for Umbraco

An **SEO report and action plan inside the Umbraco 17 backoffice**. The package crawls your site and up to five
competitors on a schedule, runs 38 SEO checks, and tells your editors in plain words what to fix first, what is
going well, and where the competitors are ahead.

- **Health score** for the site, with critical issues, warnings and info, and what is new or fixed since last time.
- **Action plan** sorted into *critical*, *quick wins*, *high impact*, *long term* and *nice to have*, each item with
  effort, how to fix it and links straight to the affected pages in Umbraco.
- **Competitor comparison**: the same checks run on the competitors, side by side, with *ahead / level / behind*
  per measurement, structured data types and site sections they have and you don't, and what they changed.
- **Free and self-contained**: everything above runs inside your Umbraco, with no external service and no key.
- **Optional AI** (Claude and/or Jev by TypeSafe) writes a report of what changed and makes the action plan concrete
  (why it matters, step by step). You always see the maximum price first, and a monthly USD cap stops it.

> The dashboard is in **Danish**.

## What it checks

38 checks in four groups, on your site and on the competitors alike:

- **Crawl and indexing**: 5xx/4xx pages, broken internal links, redirect chains and loops, noindex, orphan pages,
  click depth, robots.txt blocking pages in the sitemap, sitemap problems, indexable pages missing from the sitemap.
- **Technical**: canonical (missing, pointing elsewhere, header vs HTML), HTTPS and mixed content, security headers,
  viewport, `<html lang>`, slow TTFB, JavaScript-only pages, invalid JSON-LD.
- **On-page**: title and meta description (missing, length, generic, duplicates), H1 (missing, several, duplicates),
  skipped heading levels, images without alt, generic link text ("læs mere"), Open Graph.
- **Content**: thin content, duplicate content, pages without links.

Findings on your own site are linked to the Umbraco node (looked up in the published content cache), so editors
can open the page directly from the report. Each check can be switched off per site.

**Limits:** pages are read as raw HTML (like most crawlers), so content rendered only by JavaScript is not seen.
Up to 300 pages per site by default (configurable), so competitor numbers are samples, not an exact census.
Nothing here measures rankings or traffic.

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
busters in the `.js` imports), then `dotnet pack -c Release` and push the `.nupkg` to nuget.org. The Umbraco
Marketplace picks it up from NuGet and reads `umbraco-marketplace.json` from this repository.

## Author

Made by [LAIT](https://lait.dk), Anders Bootsmann Overvad.

## License

MIT
