using System.Diagnostics;
using GamingHub.Core.Discovery;
using GamingHub.Core.Games;
using Microsoft.Extensions.Logging;

namespace GamingHub.App.Services;

/// <summary>Runs platform scanners off the UI thread and merges results into the library.</summary>
public sealed class LibraryService(IEnumerable<IGameScanner> scanners, IGameRepository repo, SettingsService settings, ILogger<LibraryService> log)
{
    readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<string> ScanAsync()
    {
        if (!await _gate.WaitAsync(0)) return "A scan is already running";
        try
        {
            return await Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                var scans = new Dictionary<string, IReadOnlyList<DiscoveredGame>>();
                foreach (var s in scanners)
                {
                    try
                    {
                        if (s.Scan() is { } found) scans[s.Source] = found;
                        else log.LogInformation("{Source}: not installed", s.Source);
                    }
                    catch (Exception ex) { log.LogWarning(ex, "{Source} scan failed; keeping existing entries", s.Source); }
                }
                var r = LibraryMerger.Merge(repo.GetAll(), scans);
                foreach (var g in r.Added.Concat(r.Updated).Concat(r.MarkedMissing)) repo.Save(g);
                settings.Current.LastScanUtc = DateTime.UtcNow;
                settings.Save();
                var summary = $"Scan complete: {r.Added.Count} new, {r.Updated.Count} updated, {r.MarkedMissing.Count} no longer installed " +
                              $"({string.Join(", ", scans.Select(kv => $"{kv.Key} {kv.Value.Count}"))})";
                log.LogInformation("{Summary} in {Ms} ms", summary, sw.ElapsedMilliseconds);
                return summary;
            });
        }
        finally { _gate.Release(); }
    }
}
