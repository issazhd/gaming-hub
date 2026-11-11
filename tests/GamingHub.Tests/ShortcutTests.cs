using GamingHub.Core.Discovery;
using GamingHub.Core.Games;
using Xunit;

namespace GamingHub.Tests;

public class ShortcutTests
{
    [Theory]
    [InlineData(@"C:\G\Play Samson.lnk", @"D:\Games\Samson\CJSteam.exe", "Samson")]
    [InlineData(@"C:\G\AlanWake2 - Shortcut.lnk", @"C:\Program Files\Games\Alan Wake 2\AlanWake2.exe", "Alan Wake 2")]
    [InlineData(@"C:\G\ds - Shortcut.lnk", @"D:\Games\Death Stranding\gds.exe", "Death Stranding")]
    [InlineData(@"C:\G\mafiadefinitiveedition - Shortcut.lnk", @"D:\Games\Mafia - Definitive Edition\mafiadefinitiveedition.exe", "Mafia - Definitive Edition")]
    [InlineData(@"C:\G\Cyberpunk 2077.lnk", @"C:\Program Files\Games\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe", "Cyberpunk 2077")]
    [InlineData(@"C:\G\Hell Let Loose.url", null, "Hell Let Loose")]
    public void CleansNames(string file, string? target, string expected) => Assert.Equal(expected, ShortcutRules.Name(file, target));

    [Theory]
    [InlineData(@"D:\Games\007 First Light\007 First Light\Retail\007FirstLight.exe", @"D:\Games\007 First Light")]
    [InlineData(@"C:\Program Files\Games\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe", @"C:\Program Files\Games\Cyberpunk 2077")]
    [InlineData(@"D:\game.exe", null)]
    public void FindsGameRoot(string exe, string? root) => Assert.Equal(root, ShortcutRules.GameRoot(exe));

    [Theory]
    [InlineData(@"C:\Program Files (x86)\Steam\Steam.exe", true)]
    [InlineData(@"C:\Program Files (x86)\Epic Games\Launcher\Portal\Binaries\Win32\EpicGamesLauncher.exe", true)]
    [InlineData(@"C:\Program Files\Rockstar Games\Launcher\LauncherPatcher.exe", true)]
    [InlineData(@"E:\Games Repack\DS4Windows\DS4Windows.exe", true)]
    [InlineData(@"D:\SteamLibrary\steamapps\common\Control\Control.exe", true)]
    [InlineData(@"D:\Games\Red Dead Redemption 2\Launcher.exe", false)]
    [InlineData(@"D:\Games\Resident Evil 2\re2.exe", false)]
    public void SkipsLaunchersAndStoreInstalls(string target, bool skip) => Assert.Equal(skip, ShortcutRules.IsNotAGame(target));

    [Fact]
    public void SkipsSteamAndEpicLinks()
    {
        Assert.True(ShortcutRules.IsStoreUri("steam://rungameid/1604270"));
        Assert.True(ShortcutRules.IsStoreUri("com.epicgames.launcher://apps/x?action=launch"));
        Assert.False(ShortcutRules.IsStoreUri("uplay://launch/635/0"));
    }

    [Fact]
    public void ShortcutToGameAlreadyAddedOrInStoreFolderIsNotDuplicated()
    {
        var existing = new[]
        {
            new Game { Id = 1, Name = "Cyberpunk 2077", Source = "manual", ExecutablePath = @"C:\Program Files\Games\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe" },
            new Game { Id = 2, Name = "Ubi", Source = "ubisoft", ExternalId = "1", InstallDir = @"D:\Ubi\Game", LaunchUri = "uplay://launch/1/0" },
        };
        var scans = new Dictionary<string, IReadOnlyList<DiscoveredGame>>
        {
            [LibraryMerger.ShortcutSource] =
            [
                new("shortcut", "a", "Cyberpunk 2077", null, Executable: @"C:\Program Files\Games\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe"),
                new("shortcut", "b", "Ubi", null, Executable: @"D:\Ubi\Game\bin\game.exe"),
                new("shortcut", "c", "Ubi link", null, LaunchUri: "uplay://launch/1/0"),
                new("shortcut", "d", "Resident Evil 2", @"D:\Games\Resident Evil 2", Executable: @"D:\Games\Resident Evil 2\re2.exe"),
            ],
        };
        var r = LibraryMerger.Merge(existing, scans);
        Assert.Equal("Resident Evil 2", Assert.Single(r.Added).Name);
    }
}
