using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using GamingHub.Core.Games;
using Microsoft.Extensions.Logging;

namespace GamingHub.App.Services;

/// <summary>
/// Finds cover/background art for games without it using Steam's public store search and CDN (no account or key).
/// Only exact or near-exact title matches are accepted so a wrong game's art is never applied silently.
/// Images are cached under the data folder; nothing is fabricated when no match exists.
/// </summary>
public sealed class ArtworkService(ILogger<ArtworkService> log)
{
    static readonly HttpClient Http = CreateClient();
    public static string ArtDir => Path.Combine(App.DataDir, "art");

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("GamingHub/0.1");
        return c;
    }

    public sealed record Result(string? Cover, string? Background, string MatchedName);

    /// <summary>1) Steam store (cover + background) by app id or title search; 2) PCGamingWiki (covers for games not on
    /// Steam, e.g. Epic/EA exclusives — or the Steam app id it lists). Only confident title matches are used.</summary>
    public async Task<Result?> FindAsync(string name, string? steamAppId = null, bool backgroundOnly = false)
    {
        try
        {
            var (id, matched) = steamAppId is not null ? (steamAppId, name) : await SearchSteamAsync(name);
            string? wikiCover = null;
            if (id is null && await SearchWikiAsync(name) is { } wiki) { (id, wikiCover, matched) = wiki; }
            if (id is null && wikiCover is null) { log.LogInformation("Artwork: no confident match for {Name} on Steam or PCGamingWiki", name); return null; }

            string key = Guid.NewGuid().ToString("N")[..10];
            if (id is null)
            {
                var c = await DownloadFirstAsync($"{key}-cover", wikiCover);
                log.LogInformation("Artwork for {Name}: PCGamingWiki \"{Matched}\" cover={Cover}", name, matched, c is not null);
                return c is null ? null : new Result(c, null, matched);
            }
            // Steam's store API lists the real (hashed) paths of the tall library cover and the wide hero.
            var assets = await SteamAssetsAsync(id);
            string Cdn(string file) => $"https://cdn.cloudflare.steamstatic.com/steam/apps/{id}/{file}";
            var cover = backgroundOnly ? null : await DownloadFirstAsync($"{key}-cover", assets("library_capsule_2x"), assets("library_capsule"),
                Cdn("library_600x900_2x.jpg"), Cdn("library_600x900.jpg"), wikiCover);
            // Background: the game's wide key art (library hero, 3840 or 1920 wide); a 16:9 screenshot only when there is none.
            var hero = await DownloadFirstAsync($"{key}-hero", assets("library_hero_2x"), assets("library_hero"), Cdn("library_hero.jpg"))
                       ?? await DownloadFirstAsync($"{key}-hero", await SteamScreenshotAsync(id));
            // Last resort only: the landscape store banner / page background.
            if (!backgroundOnly) cover ??= await DownloadFirstAsync($"{key}-cover", Cdn("header.jpg"));
            hero ??= await DownloadFirstAsync($"{key}-hero", Cdn("page_bg_generated_v6b.jpg"));
            log.LogInformation("Artwork for {Name}: Steam app {Id} \"{Matched}\" cover={Cover} hero={Hero}", name, id, matched, cover is not null, hero is not null);
            return cover is null && hero is null ? null : new Result(cover, hero, matched);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Artwork lookup failed for {Name}", name);
            return null;
        }
    }


    /// <summary>PCGamingWiki (public MediaWiki API, no key): search → infobox → Steam app id or cover file URL.</summary>
    async Task<(string? SteamId, string? Cover, string Title)?> SearchWikiAsync(string name)
    {
        const string Api = "https://www.pcgamingwiki.com/w/api.php?format=json&";
        using var search = JsonDocument.Parse(await Http.GetStringAsync(
            $"{Api}action=query&list=search&srlimit=5&srsearch={Uri.EscapeDataString(TitleMatch.SearchTerm(name))}"));
        foreach (var hit in search.RootElement.GetProperty("query").GetProperty("search").EnumerateArray())
        {
            var title = hit.GetProperty("title").GetString() ?? "";
            if (!TitleMatch.SameGame(TitleMatch.SearchTerm(name), title)) continue;
            using var page = JsonDocument.Parse(await Http.GetStringAsync(
                $"{Api}action=parse&redirects=1&prop=wikitext&section=0&page={Uri.EscapeDataString(title)}"));
            var text = page.RootElement.GetProperty("parse").GetProperty("wikitext").GetProperty("*").GetString() ?? "";
            var steam = Regex.Match(text, @"\|steam appid\s*=\s*(\d+)").Groups[1].Value;
            var file = Regex.Match(text, @"\|cover\s*=\s*([^\n|]+)").Groups[1].Value.Trim();
            string? cover = null;
            if (file.Length > 0)
            {
                using var info = JsonDocument.Parse(await Http.GetStringAsync(
                    $"{Api}action=query&prop=imageinfo&iiprop=url&titles={Uri.EscapeDataString("File:" + file)}"));
                foreach (var p in info.RootElement.GetProperty("query").GetProperty("pages").EnumerateObject())
                    if (p.Value.TryGetProperty("imageinfo", out var ii)) cover = ii[0].GetProperty("url").GetString();
            }
            if (steam.Length > 0 || cover is not null) return (steam.Length > 0 ? steam : null, cover, title);
        }
        return null;
    }

    /// <summary>IStoreBrowseService/GetItems (public, no key): returns a lookup from asset name to full image URL.</summary>
    async Task<Func<string, string?>> SteamAssetsAsync(string appId)
    {
        try
        {
            var input = $$$"""{"ids":[{"appid":{{{appId}}}}],"context":{"language":"english","country_code":"US"},"data_request":{"include_assets":true}}""";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(
                $"https://api.steampowered.com/IStoreBrowseService/GetItems/v1/?input_json={Uri.EscapeDataString(input)}"));
            var a = doc.RootElement.GetProperty("response").GetProperty("store_items")[0].GetProperty("assets");
            var format = a.GetProperty("asset_url_format").GetString()!;
            var map = a.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String)
                .ToDictionary(p => p.Name, p => p.Value.GetString()!);
            return name => map.TryGetValue(name, out var file)
                ? "https://shared.cloudflare.steamstatic.com/store_item_assets/" + format.Replace("${FILENAME}", file) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            return _ => null;
        }
    }


    /// <summary>First full-HD store screenshot (1920×1080, 16:9) — a sharp full-screen fallback background.</summary>
    static async Task<string?> SteamScreenshotAsync(string appId)
    {
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://store.steampowered.com/api/appdetails?appids={appId}&filters=screenshots"));
            if (!doc.RootElement.GetProperty(appId).TryGetProperty("data", out var d) || !d.TryGetProperty("screenshots", out var shots)) return null;
            foreach (var shot in shots.EnumerateArray())
                if (shot.TryGetProperty("path_full", out var p) && p.GetString() is { } url) return url;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException) { }
        return null;
    }

    /// <summary>Background worth replacing: too small (under 1900 px wide) or a 16:9 image — a store screenshot or page
    /// background rather than the game's wide key art.</summary>
    public static bool IsPoorBackground(string? path)
    {
        if (path is null || !File.Exists(path)) return true;
        var (w, h) = Size(path);
        return w < 1900 || (double)w / h < 1.9;
    }

    static bool IsKeyArt(string path) { var (w, h) = Size(path); return w >= 1900 && (double)w / h >= 2.5; }

    static (int W, int H) Size(string path)
    {
        try
        {
            using var s = File.OpenRead(path);
            var f = BitmapDecoder.Create(s, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            return (f.PixelWidth, Math.Max(1, f.PixelHeight));
        }
        catch { return (0, 1); }
    }
    async Task<(string? Id, string Name)> SearchSteamAsync(string name)
    {
        // Full title first; then the core words (abbreviations expanded, edition words dropped) — Steam's search
        // finds nothing for e.g. "Ghost of Tsushima DC" but does for "Ghost of Tsushima".
        foreach (var term in new[] { TitleMatch.SearchTerm(name), TitleMatch.CoreTerm(name) }.Distinct())
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(
                $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(term)}&l=english&cc=US"));
            (string? Id, string Name) best = (null, "");
            foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                string n = item.GetProperty("name").GetString() ?? "";
                string id = item.GetProperty("id").GetRawText();
                if (TitleMatch.Normalize(n) == TitleMatch.Normalize(name)) return (id, n);
                if (best.Id is null && TitleMatch.SameGame(TitleMatch.SearchTerm(name), n)) best = (id, n);
            }
            if (best.Id is not null) return best;
        }
        return (null, "");
    }

    async Task<string?> DownloadFirstAsync(string baseName, params string?[] urls)
    {
        foreach (var url in urls)
        {
            if (url is null) continue;
            try
            {
                using var resp = await Http.GetAsync(url);
                if (!resp.IsSuccessStatusCode) continue;
                var saved = SaveImage(await resp.Content.ReadAsByteArrayAsync(), baseName);
                if (!IsBlank(saved)) return saved;
                File.Delete(saved);   // Steam answers some old image addresses with a plain grey placeholder
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or NotSupportedException) { }
        }
        return null;
    }

    /// <summary>Decodes (validating it is a real image) and stores it as JPEG in the art cache.</summary>
    public static string SaveImage(byte[] data, string baseName)
    {
        using var ms = new MemoryStream(data);
        var frame = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        Directory.CreateDirectory(ArtDir);
        var path = Path.Combine(ArtDir, baseName + ".jpg");
        var enc = new JpegBitmapEncoder { QualityLevel = 90 };
        enc.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgr24, null, 0)));
        using var f = File.Create(path);
        enc.Save(f);
        return path;
    }

    /// <summary>Fills in missing art for library games once each (a marker file remembers misses).</summary>
    const string NoMatch = "no match v3";   // bump when matching improves: older misses get retried once

    readonly SemaphoreSlim _fillGate = new(1, 1);

    public async Task<int> FillMissingAsync(IGameRepository repo)
    {
        if (!await _fillGate.WaitAsync(0)) return 0;   // a pass is already running
        try { return await FillCoreAsync(repo); } finally { _fillGate.Release(); }
    }

    const string AssetsVersion = "assets-v4";

    /// <summary>True for flat placeholder images (e.g. Steam's grey stand-in): almost no brightness variation.</summary>
    public static bool IsBlank(string path)
    {
        try
        {
            var b = new BitmapImage();
            b.BeginInit(); b.UriSource = new Uri(path); b.DecodePixelWidth = 32; b.CacheOption = BitmapCacheOption.OnLoad; b.EndInit();
            var gray = new FormatConvertedBitmap(b, System.Windows.Media.PixelFormats.Gray8, null, 0);
            var px = new byte[gray.PixelWidth * gray.PixelHeight];
            gray.CopyPixels(px, gray.PixelWidth, 0);
            double mean = px.Average(v => (double)v);
            return Math.Sqrt(px.Average(v => (v - mean) * (v - mean))) < 4;
        }
        catch { return false; }
    }

    static bool IsLandscape(string path)
    {
        try
        {
            using var s = File.OpenRead(path);
            var frame = BitmapDecoder.Create(s, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            return frame.PixelWidth > frame.PixelHeight;
        }
        catch { return false; }
    }

    /// <summary>Full-screen Home needs sharp backgrounds: replace a small one (incl. Steam's local cache) once per game.</summary>
    async Task<bool> UpgradeBackgroundAsync(GamingHub.Core.Games.Game g, IGameRepository repo)
    {
        var bgMarker = Path.Combine(ArtDir, $"bg-v6-{g.Id}");
        if (File.Exists(bgMarker) || g.BackgroundPath is null || !IsPoorBackground(g.BackgroundPath)) return false;
        var better = await FindAsync(g.Name, g.Source == "steam" ? g.ExternalId : null, backgroundOnly: true);
        File.WriteAllText(bgMarker, better?.Background ?? "none");
        if (better?.Background is not { } bg || !IsKeyArt(bg)) return false;   // only swap in real key art
        g.BackgroundPath = bg;
        repo.Save(g);
        return true;
    }

    async Task<int> FillCoreAsync(IGameRepository repo)
    {
        int found = 0;
        Directory.CreateDirectory(ArtDir);
        foreach (var g in repo.GetAll().Where(g => g.Installed && !g.Hidden))
        {
            var marker = Path.Combine(ArtDir, $"tried-{g.Id}");
            bool Ours(string? p) => p?.StartsWith(ArtDir, StringComparison.OrdinalIgnoreCase) == true;
            if (File.Exists(marker))
            {
                var m = File.ReadAllText(marker);
                var matched = m.Split('\n')[0];
                bool goodMatch = !matched.StartsWith("no match") && TitleMatch.SameGame(TitleMatch.SearchTerm(g.Name), matched);
                // Earlier versions sometimes saved Steam's small landscape banner as the cover: fetch the tall one once.
                bool banner = Ours(g.CoverPath) && IsLandscape(g.CoverPath!) && !m.EndsWith("\n" + AssetsVersion);
                bool blank = (Ours(g.CoverPath) && IsBlank(g.CoverPath!)) || (Ours(g.BackgroundPath) && IsBlank(g.BackgroundPath!));
                banner |= blank;
                if (m == NoMatch || (goodMatch && !banner)) { if (await UpgradeBackgroundAsync(g, repo)) found++; continue; }   // old-rule misses / wrong matches / banners are retried
                if (Ours(g.CoverPath)) g.CoverPath = null;
                if (Ours(g.BackgroundPath)) g.BackgroundPath = null;
                repo.Save(g);
            }
            if (g.CoverPath is not null && g.BackgroundPath is not null) { if (await UpgradeBackgroundAsync(g, repo)) found++; continue; }
            var r = await FindAsync(g.Name, g.Source == "steam" ? g.ExternalId : null);
            File.WriteAllText(marker, r is null ? NoMatch : r.MatchedName + "\n" + AssetsVersion);
            if (r is null) continue;
            g.CoverPath ??= r.Cover;
            g.BackgroundPath ??= r.Background;
            repo.Save(g);
            found++;
        }
        return found;
    }
}
