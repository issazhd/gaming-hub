using GamingHub.Core.Games;

namespace GamingHub.Core.Discovery;

/// <summary>A game found by a platform scanner. Launch via platform URI where the platform requires it (no DRM bypass).</summary>
public sealed record DiscoveredGame(string Source, string ExternalId, string Name, string? InstallDir,
    string? LaunchUri = null, string? Executable = null, string? Arguments = null, string? WorkingDirectory = null,
    string? CoverPath = null, string? BackgroundPath = null);

public interface IGameScanner
{
    string Source { get; }
    /// <summary>Returns null when the platform is not installed (so existing entries are not marked removed).</summary>
    IReadOnlyList<DiscoveredGame>? Scan();
}

public sealed record MergeResult(List<Game> Added, List<Game> Updated, List<Game> MarkedMissing);

/// <summary>Merges scan results into the library: dedupes by (source, id) and by executable path,
/// refreshes install metadata, marks vanished installs as not installed — never deleting user customization.</summary>
public static class LibraryMerger
{
    public static MergeResult Merge(IReadOnlyList<Game> existing, IReadOnlyDictionary<string, IReadOnlyList<DiscoveredGame>> scans)
    {
        var r = new MergeResult([], [], []);
        var byKey = existing.Where(g => g.ExternalId is not null)
            .GroupBy(g => Key(g.Source, g.ExternalId!)).ToDictionary(x => x.Key, x => x.First());
        var exePaths = existing.Where(g => g.Source == "manual" && g.ExecutablePath.Length > 0)
            .Select(g => Norm(g.ExecutablePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Shortcut-folder entries must not duplicate games another source already knows (same URI, exe, or install folder).
        var others = existing.Where(g => g.Source != ShortcutSource && g.Installed)
            .Select(g => (Uri: g.LaunchUri, Exe: g.ExecutablePath, Dir: g.InstallDir))
            .Concat(scans.Where(s => s.Key != ShortcutSource).SelectMany(s => s.Value).Select(d => (Uri: d.LaunchUri, Exe: d.Executable ?? "", Dir: d.InstallDir)))
            .ToList();
        bool KnownElsewhere(DiscoveredGame d) => others.Any(o =>
            (d.LaunchUri is not null && SameUri(d.LaunchUri, o.Uri)) ||
            (d.Executable is not null && o.Exe.Length > 0 && Norm(d.Executable).Equals(Norm(o.Exe), StringComparison.OrdinalIgnoreCase)) ||
            (d.Executable is not null && o.Dir is { Length: > 3 } dir && Norm(d.Executable).StartsWith(Norm(dir).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)));

        foreach (var (source, found) in scans)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in found)
            {
                var key = Key(source, d.ExternalId);
                if (!seen.Add(key)) continue;                      // duplicate within one scan
                if (byKey.TryGetValue(key, out var g))
                {
                    bool changed = !g.Installed || g.InstallDir != d.InstallDir || g.LaunchUri != d.LaunchUri ||
                                   (d.Executable is not null && g.ExecutablePath != d.Executable) ||
                                   (g.CoverPath is null && d.CoverPath is not null) || (g.BackgroundPath is null && d.BackgroundPath is not null);
                    if (!changed) continue;
                    g.Installed = true;                            // moved or reinstalled: keep name/favorite/targets
                    g.InstallDir = d.InstallDir;
                    g.LaunchUri = d.LaunchUri;
                    if (d.Executable is not null) g.ExecutablePath = d.Executable;
                    g.CoverPath ??= d.CoverPath;
                    g.BackgroundPath ??= d.BackgroundPath;
                    r.Updated.Add(g);
                }
                else if (d.Executable is not null && exePaths.Contains(Norm(d.Executable)))
                    continue;                                       // user already added this exe manually
                else if (source == ShortcutSource && KnownElsewhere(d))
                    continue;                                       // already in the library from Steam/Epic/etc.
                else
                    r.Added.Add(new Game
                    {
                        Name = d.Name, Source = source, ExternalId = d.ExternalId, InstallDir = d.InstallDir,
                        LaunchUri = d.LaunchUri, ExecutablePath = d.Executable ?? "", Arguments = d.Arguments ?? "",
                        WorkingDirectory = d.WorkingDirectory, CoverPath = d.CoverPath, BackgroundPath = d.BackgroundPath,
                    });
            }
            foreach (var g in existing.Where(g => g.Source == source && g.Installed && g.ExternalId is not null && !seen.Contains(Key(source, g.ExternalId))))
            {
                g.Installed = false;
                r.MarkedMissing.Add(g);
            }
        }
        return r;
    }

    public const string ShortcutSource = "shortcut";

    /// <summary>steam://rungameid/X ≈ steam://run/X; Epic URIs compared without their query string.</summary>
    static bool SameUri(string a, string? b)
    {
        if (b is null) return false;
        static string N(string u) => u.Split('?')[0].Replace("steam://run/", "steam://rungameid/", StringComparison.OrdinalIgnoreCase).TrimEnd('/');
        return N(a).Equals(N(b), StringComparison.OrdinalIgnoreCase);
    }

    static string Key(string source, string id) => $"{source}:{id}";
    static string Norm(string p) { try { return Path.GetFullPath(p.Trim('"')); } catch { return p; } }
}
