using Lait.Umbraco.Awesome.SEO.Configuration;
using Lait.Umbraco.Awesome.SEO.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Sync;

namespace Lait.Umbraco.Awesome.SEO.Services;

/// <summary>Wakes up regularly and runs every site that is due. Only the scheduling-role server runs jobs.</summary>
public sealed class SeoScheduler(
    SeoRunner runner, SeoStore store, SiteSettings sites,
    IRuntimeState runtime, IServerRoleAccessor serverRole, ILogger<SeoScheduler> logger) : BackgroundService
{
    static readonly TimeSpan Tick = TimeSpan.FromMinutes(5); // short enough that a tick lost to "runtime/server role not ready yet" is retried soon
    static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(6);

    readonly Dictionary<string, DateTime> _lastFailure = [];

    public static bool IsDue(DateTime? lastRunUtc, int intervalDays, DateTime nowUtc) =>
        lastRunUtc is null || nowUtc - lastRunUtc >= TimeSpan.FromDays(Math.Max(1, intervalDays));

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromMinutes(1), ct); // let Umbraco finish booting
        using var timer = new PeriodicTimer(Tick);
        do
        {
            try { await RunDueSitesAsync(ct); }
            catch (Exception e) when (!ct.IsCancellationRequested) { logger.LogError(e, "SEO scheduler tick failed"); }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    async Task RunDueSitesAsync(CancellationToken ct)
    {
        if (runtime.Level != RuntimeLevel.Run) { logger.LogDebug("SEO scheduler: runtime level {Level}, skipping", runtime.Level); return; }
        if (serverRole.CurrentServerRole is not (ServerRole.Single or ServerRole.SchedulingPublisher))
        {
            logger.LogDebug("SEO scheduler: server role {Role}, skipping", serverRole.CurrentServerRole);
            return;
        }

        foreach (var site in sites.Active())
        {
            var now = DateTime.UtcNow;
            if (!IsDue(store.LastRunUtc(site.Domain, false), site.IntervalDays, now)) continue;
            // A failed crawl stores nothing, so without this it would be retried every tick.
            if (_lastFailure.TryGetValue(site.Domain, out var failed) && now - failed < RetryAfterFailure) continue;

            try { await runner.RunAsync(site, ct); _lastFailure.Remove(site.Domain); }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                _lastFailure[site.Domain] = now;
                logger.LogError(e, "SEO run for {Domain} failed", site.Domain);
            }
        }
    }
}
