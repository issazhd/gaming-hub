using System.Text.Json;
using System.Text.RegularExpressions;
using GamingHub.Core.Discovery;
using Microsoft.Win32;

namespace GamingHub.Infrastructure.Discovery;

/// <summary>Steam: libraryfolders.vdf → appmanifest_*.acf. Launch via steam://rungameid (Steam handles DRM/overlay).</summary>
public sealed class SteamScanner(string? rootOverride = null) : IGameScanner
{
    static readonly Regex NotAGame = new(@"^(Steamworks|Steam Linux Runtime|Proton|SteamVR|Steam Audio)|Redistributable|Dedicated Server|\bSDK\b", RegexOptions.IgnoreCase);
    public string Source => "steam";

    public static string? FindRoot()
    {
        string?[] candidates =
        [
            Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string,
            Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
        ];
        return candidates.FirstOrDefault(c => c is not null && File.Exists(Path.Combine(c, "steamapps", "libraryfolders.vdf")));
    }

    public IReadOnlyList<DiscoveredGame>? Scan()
    {
        var root = rootOverride ?? FindRoot();
        if (root is null) return null;
        var libs = new List<string> { root };
        var lf = Vdf.Parse(File.ReadAllText(Path.Combine(root, "steamapps", "libraryfolders.vdf")))["libraryfolders"];
        if (lf is not null) libs.AddRange(lf.Children.Values.Select(c => c.Get("path")).OfType<string>());

        var result = new List<DiscoveredGame>();
        foreach (var lib in libs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                try
                {
                    var st = Vdf.Parse(File.ReadAllText(acf))["AppState"];
                    var (id, name, dir) = (st?.Get("appid"), st?.Get("name"), st?.Get("installdir"));
                    if (id is null || name is null || dir is null || NotAGame.IsMatch(name)) continue;
                    var installDir = Path.Combine(apps, "common", dir);
                    if (!Directory.Exists(installDir)) continue;
                    result.Add(new DiscoveredGame(Source, id, name, installDir, LaunchUri: $"steam://rungameid/{id}",
                        CoverPath: Art(root, id, "library_600x900", "library_capsule", "header", "library_header"),
                        BackgroundPath: Art(root, id, "library_hero")));
                }
                catch (IOException) { /* manifest being written by Steam; next scan picks it up */ }
            }
        }
        return result;
    }

    /// <summary>Steam's local library art cache: new layout librarycache/&lt;id&gt;/[hash/]name.jpg, old layout &lt;id&gt;_name.jpg.</summary>
    static string? Art(string root, string id, params string[] names)
    {
        var cache = Path.Combine(root, "appcache", "librarycache");
        var dir = Path.Combine(cache, id);
        foreach (var name in names)
        {
            var flat = Path.Combine(cache, $"{id}_{name}.jpg");
            if (File.Exists(flat)) return flat;
            if (Directory.Exists(dir) && Directory.EnumerateFiles(dir, name + ".jpg", SearchOption.AllDirectories).FirstOrDefault() is { } f) return f;
        }
        return null;
    }
}

/// <summary>Epic: ProgramData manifests (*.item JSON). Launch via the Epic launcher protocol.</summary>
public sealed class EpicScanner(string? manifestDir = null) : IGameScanner
{
    public string Source => "epic";

    public IReadOnlyList<DiscoveredGame>? Scan()
    {
        var dir = manifestDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
        if (!Directory.Exists(dir)) return null;
        var result = new List<DiscoveredGame>();
        foreach (var f in Directory.EnumerateFiles(dir, "*.item"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                var j = doc.RootElement;
                string? S(string k) => j.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                if (j.TryGetProperty("bIsIncompleteInstall", out var inc) && inc.ValueKind == JsonValueKind.True) continue;
                if (j.TryGetProperty("AppCategories", out var cats) && cats.ValueKind == JsonValueKind.Array &&
                    !cats.EnumerateArray().Any(c => c.GetString() == "games")) continue;
                var (app, ns, item, name, loc, exe) = (S("AppName"), S("CatalogNamespace"), S("CatalogItemId"), S("DisplayName"), S("InstallLocation"), S("LaunchExecutable"));
                if (app is null || name is null || loc is null || !Directory.Exists(loc)) continue;
                var uri = ns is not null && item is not null
                    ? $"com.epicgames.launcher://apps/{ns}%3A{item}%3A{app}?action=launch&silent=true"
                    : $"com.epicgames.launcher://apps/{app}?action=launch&silent=true";
                result.Add(new DiscoveredGame(Source, app, name, loc, LaunchUri: uri,
                    Executable: exe is null ? null : Path.Combine(loc, exe)));
            }
            catch (Exception ex) when (ex is IOException or JsonException) { }
        }
        return result;
    }
}

/// <summary>GOG: registry entries written by GOG installers/Galaxy. DRM-free → launch the game executable directly.</summary>
public sealed class GogScanner : IGameScanner
{
    public string Source => "gog";

    public IReadOnlyList<DiscoveredGame>? Scan()
    {
        using var games = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
        if (games is null) return null;
        var result = new List<DiscoveredGame>();
        foreach (var id in games.GetSubKeyNames())
        {
            using var k = games.OpenSubKey(id);
            var (name, exe, path) = (k?.GetValue("gameName") as string, k?.GetValue("exe") as string, k?.GetValue("path") as string);
            if (name is null || exe is null || !File.Exists(exe)) continue;
            result.Add(new DiscoveredGame(Source, id, name, path, Executable: exe,
                Arguments: k!.GetValue("launchParam") as string, WorkingDirectory: k.GetValue("workingDir") as string));
        }
        return result;
    }
}

/// <summary>Ubisoft Connect: registry install list; launch via uplay:// so Ubisoft handles login/DRM.
/// The registry has no display name, so the install folder name is used.</summary>
public sealed class UbisoftScanner : IGameScanner
{
    public string Source => "ubisoft";

    public IReadOnlyList<DiscoveredGame>? Scan()
    {
        using var installs = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs");
        if (installs is null) return null;
        var result = new List<DiscoveredGame>();
        foreach (var id in installs.GetSubKeyNames())
        {
            using var k = installs.OpenSubKey(id);
            if (k?.GetValue("InstallDir") is not string dir || !Directory.Exists(dir)) continue;
            result.Add(new DiscoveredGame(Source, id, new DirectoryInfo(dir).Name, dir, LaunchUri: $"uplay://launch/{id}/0"));
        }
        return result;
    }
}
