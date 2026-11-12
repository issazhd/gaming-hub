using System.Diagnostics;
using GamingHub.Core.Discovery;
using GamingHub.Core.Games;
using Microsoft.Extensions.Logging;

namespace GamingHub.App.Services;

/// <summary>Runs platform scanners off the UI thread and merges results into the library.</summary>
public sealed class LibraryService(IEnumerable<IGameScanner> scanners, IGameRepository repo, SettingsService settings, ILogger<LibraryService> log)
{
    readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<(string Message, bool Changed)> ScanAsync()
    {
        if (!await _gate.WaitAsync(0)) return ("A scan is already running", false);
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
                var existing = repo.GetAll();
                var missing = existing.Where(g => !g.Installed).Select(g => g.Id).ToHashSet();
                var r = LibraryMerger.Merge(existing, scans);
                foreach (var g in r.Added.Concat(r.Updated).Concat(r.MarkedMissing)) repo.Save(g);
                settings.Current.LastScanUtc = DateTime.UtcNow;
                settings.Save();
                var total = repo.GetAll().Count(g => g.Installed && !g.Hidden);
                var changes = new List<string>();
                void Count(int count, string label)
                {
                    if (count > 0) changes.Add($"{count} {label}");
                }
                Count(r.Added.Count(g => !g.Hidden), "added");
                Count(r.Updated.Count(g => !g.Hidden && missing.Contains(g.Id)), "restored");
                Count(r.Updated.Count(g => !g.Hidden && !missing.Contains(g.Id)), "updated");
                Count(r.MarkedMissing.Count(g => !g.Hidden), "no longer installed");
                var detail = changes.Count == 0 ? "No library changes" : string.Join(", ", changes);
                var summary = $"Scan complete. {detail}. {total} {(total == 1 ? "game" : "games")} in your library.";
                log.LogInformation("{Summary} in {Ms} ms", summary, sw.ElapsedMilliseconds);
                return (summary, changes.Count > 0);
            });
        }
        finally { _gate.Release(); }
    }
}
