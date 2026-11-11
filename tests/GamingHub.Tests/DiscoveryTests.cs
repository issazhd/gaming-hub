using GamingHub.Core.Discovery;
using GamingHub.Core.Games;
using GamingHub.Infrastructure.Discovery;

namespace GamingHub.Tests;

public class VdfTests
{
    [Fact]
    public void ParsesNestedValuesCommentsAndEscapes()
    {
        var v = Vdf.Parse("""
            "libraryfolders" { // comment
              "0" { "path" "C:\\Program Files (x86)\\Steam" "apps" { "730" "1" } }
              "1" { "path" "D:\\Games" }
            }
            """);
        Assert.Equal(@"C:\Program Files (x86)\Steam", v["LIBRARYFOLDERS"]!["0"]!.Get("path"));
        Assert.Equal("1", v["libraryfolders"]!["0"]!["apps"]!.Get("730"));
        Assert.Equal(@"D:\Games", v["libraryfolders"]!["1"]!.Get("path"));
    }
}

public class LibraryMergerTests
{
    static DiscoveredGame Steam(string id, string name, string dir = "D:\\x") => new("steam", id, name, dir, $"steam://rungameid/{id}");
    static Dictionary<string, IReadOnlyList<DiscoveredGame>> Scan(params DiscoveredGame[] g) => new() { ["steam"] = g };

    [Fact]
    public void AddsNewAndDedupesWithinScan()
    {
        var r = LibraryMerger.Merge([], Scan(Steam("1", "A"), Steam("1", "A again"), Steam("2", "B")));
        Assert.Equal(["A", "B"], r.Added.Select(g => g.Name));
    }

    [Fact]
    public void RescanKeepsCustomizationAndDoesNotDuplicate()
    {
        var existing = new Game { Id = 5, Source = "steam", ExternalId = "1", Name = "My Name", Favorite = true, InstallDir = "D:\\x", LaunchUri = "steam://rungameid/1" };
        var r = LibraryMerger.Merge([existing], Scan(Steam("1", "Store Name")));
        Assert.Empty(r.Added);
        Assert.Empty(r.Updated);   // nothing changed
        Assert.Equal("My Name", existing.Name);
    }

    [Fact]
    public void MovedInstallUpdatesPathOnly()
    {
        var existing = new Game { Id = 5, Source = "steam", ExternalId = "1", Name = "Mine", Favorite = true, InstallDir = "C:\\old" };
        var r = LibraryMerger.Merge([existing], Scan(Steam("1", "A", "E:\\new")));
        Assert.Same(existing, Assert.Single(r.Updated));
        Assert.Equal(("E:\\new", "Mine", true), (existing.InstallDir, existing.Name, existing.Favorite));
    }

    [Fact]
    public void UninstalledIsMarkedNotDeletedAndComesBack()
    {
        var g = new Game { Id = 5, Source = "steam", ExternalId = "1", Name = "A", InstallDir = "D:\\x" };
        Assert.Same(g, Assert.Single(LibraryMerger.Merge([g], Scan()).MarkedMissing));
        Assert.False(g.Installed);
        Assert.Same(g, Assert.Single(LibraryMerger.Merge([g], Scan(Steam("1", "A"))).Updated));
        Assert.True(g.Installed);
    }

    [Fact]
    public void PlatformNotScannedLeavesEntriesAlone()
    {
        var epic = new Game { Source = "epic", ExternalId = "x", Name = "E" };
        LibraryMerger.Merge([epic], Scan());
        Assert.True(epic.Installed);
    }

    [Fact]
    public void ManualExeIsNotDuplicatedByDiscovery()
    {
        var manual = new Game { Source = "manual", Name = "Control", ExecutablePath = @"D:\Games\Epic\Control\Control.exe" };
        var r = LibraryMerger.Merge([manual], new Dictionary<string, IReadOnlyList<DiscoveredGame>>
        {
            ["epic"] = [new DiscoveredGame("epic", "Calluna", "Control", @"D:\Games\Epic\Control", "uri", @"D:\Games\Epic\Control\Control.exe")],
        });
        Assert.Empty(r.Added);
    }

    [Fact]
    public void LaunchUriTakesPrecedence() =>
        Assert.Equal("steam://rungameid/1", LaunchCommandBuilder.Build(new Game { LaunchUri = "steam://rungameid/1", ExecutablePath = "x.exe" }).FileName);
}

public sealed class ScannerTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("gh-scan-").FullName;

    [Fact]
    public void SteamFindsGamesAcrossLibrariesAndSkipsTools()
    {
        var lib2 = Path.Combine(_root, "lib2");
        Write("steam/steamapps/libraryfolders.vdf", $$"""
            "libraryfolders" { "0" { "path" "{{Esc(Path.Combine(_root, "steam"))}}" } "1" { "path" "{{Esc(lib2)}}" } }
            """);
        Write("steam/steamapps/appmanifest_228980.acf", """ "AppState" { "appid" "228980" "name" "Steamworks Common Redistributables" "installdir" "Steamworks Shared" } """);
        Directory.CreateDirectory(Path.Combine(_root, "steam/steamapps/common/Steamworks Shared"));
        Write("lib2/steamapps/appmanifest_730.acf", """ "AppState" { "appid" "730" "name" "Counter-Strike 2" "installdir" "CS" } """);
        Directory.CreateDirectory(Path.Combine(lib2, "steamapps/common/CS"));
        Write("lib2/steamapps/appmanifest_999.acf", """ "AppState" { "appid" "999" "name" "Half Installed" "installdir" "Gone" } """);
        Write("steam/appcache/librarycache/730/abc/library_600x900.jpg", "x");

        var g = Assert.Single(new SteamScanner(Path.Combine(_root, "steam")).Scan()!);
        Assert.Equal(("730", "Counter-Strike 2", "steam://rungameid/730"), (g.ExternalId, g.Name, g.LaunchUri));
        Assert.EndsWith("library_600x900.jpg", g.CoverPath);
    }

    [Fact]
    public void EpicReadsManifestsSkipsIncomplete()
    {
        var loc = Directory.CreateDirectory(Path.Combine(_root, "Control")).FullName;
        Write("m/a.item", $$"""{"AppName":"Calluna","CatalogNamespace":"calluna","CatalogItemId":"9afb","DisplayName":"Control","InstallLocation":"{{Esc(loc)}}","LaunchExecutable":"Control.exe","AppCategories":["games"]}""");
        Write("m/b.item", $$"""{"AppName":"X","DisplayName":"Partial","InstallLocation":"{{Esc(loc)}}","bIsIncompleteInstall":true}""");
        var g = Assert.Single(new EpicScanner(Path.Combine(_root, "m")).Scan()!);
        Assert.Equal("com.epicgames.launcher://apps/calluna%3A9afb%3ACalluna?action=launch&silent=true", g.LaunchUri);
    }

    [Fact] public void MissingPlatformReturnsNull() => Assert.Null(new EpicScanner(Path.Combine(_root, "nope")).Scan());

    static string Esc(string p) => p.Replace("\\", "\\\\");
    void Write(string rel, string text)
    {
        var p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
