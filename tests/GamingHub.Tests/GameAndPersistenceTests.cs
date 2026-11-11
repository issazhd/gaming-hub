using GamingHub.Core.Displays;
using GamingHub.Core.Games;
using GamingHub.Infrastructure.Persistence;
using static GamingHub.Tests.TestData;

namespace GamingHub.Tests;

public class GameTargetTests
{
    [Fact]
    public void DefaultFollowsLauncherDisplay() =>
        Assert.Same(Tv, GameTargetResolver.Resolve(new Game(), Snap(Aoc, Tv), Tv).Display);

    [Fact]
    public void SpecificOverrideTargetsOtherMonitor()
    {
        var g = new Game { TargetMode = GameTargetMode.SpecificDisplay, TargetDisplay = Third.Identity };
        Assert.Same(Third, GameTargetResolver.Resolve(g, Snap(Aoc, Tv, Third), Tv).Display);
    }

    [Theory]
    [InlineData(GameTargetFallback.FollowLauncher, "TCL")]
    [InlineData(GameTargetFallback.WindowsPrimary, "AOC")]
    public void UnavailableSpecificUsesFallback(GameTargetFallback fb, string expectedMfr)
    {
        var g = new Game { TargetMode = GameTargetMode.SpecificDisplay, TargetDisplay = Third.Identity, TargetFallback = fb };
        Assert.Equal(expectedMfr, GameTargetResolver.Resolve(g, Snap(Aoc, Tv), Tv).Display!.Identity.EdidManufacturer);
    }

    [Fact]
    public void AskFallbackFlagsUser()
    {
        var g = new Game { TargetMode = GameTargetMode.LastUsed, TargetFallback = GameTargetFallback.Ask };
        var r = GameTargetResolver.Resolve(g, Snap(Aoc), Aoc);
        Assert.True(r.AskUser);
        Assert.Null(r.Display);
    }
}

public class LaunchCommandTests
{
    [Fact]
    public void DefaultsWorkingDirectoryToExeFolder()
    {
        var c = LaunchCommandBuilder.Build(new Game { ExecutablePath = "\"C:\\Games\\X\\x.exe\"", Arguments = " -fullscreen " });
        Assert.Equal(("C:\\Games\\X\\x.exe", "-fullscreen", "C:\\Games\\X"), (c.FileName, c.Arguments, c.WorkingDirectory));
    }

    [Fact]
    public void ExplicitWorkingDirectoryWins() =>
        Assert.Equal("D:\\wd", LaunchCommandBuilder.Build(new Game { ExecutablePath = "C:\\a.exe", WorkingDirectory = "D:\\wd" }).WorkingDirectory);

    [Fact]
    public void CustomExeOverridesStoreLink()
    {
        var c = LaunchCommandBuilder.Build(new Game { Name = "x", LaunchUri = "steam://rungameid/1", CustomExe = @"D:\Mods\x\launcher.exe", Arguments = "-fs" });
        Assert.Equal((@"D:\Mods\x\launcher.exe", "-fs", @"D:\Mods\x"), (c.FileName, c.Arguments, c.WorkingDirectory));
    }

    [Fact]
    public void MissingExeThrows() => Assert.Throws<InvalidOperationException>(() => LaunchCommandBuilder.Build(new Game { Name = "x" }));
}

public sealed class PersistenceTests : IDisposable
{
    readonly string _db = Path.Combine(Path.GetTempPath(), $"gh-test-{Guid.NewGuid():N}.db");

    [Fact]
    public void SettingsRoundTrip()
    {
        var s = new SqliteStore(_db).Load();
        Assert.True(s.Display.AutoSwitchToPreferred);   // defaults
        Assert.False(s.Display.CloseOnDisconnect);
        s.Display.Preferred = Tv.Identity;
        s.Display.FallbackMode = FallbackMode.SpecificDisplay;
        new SqliteStore(_db).Save(s);
        var back = new SqliteStore(_db).Load();
        Assert.Equal(Tv.Identity, back.Display.Preferred);
        Assert.Equal(FallbackMode.SpecificDisplay, back.Display.FallbackMode);
    }

    [Fact]
    public void GameCrudAndPlaytime()
    {
        var store = new SqliteStore(_db);
        var g = store.Save(new Game { Name = "Test", ExecutablePath = "C:\\t.exe", TargetMode = GameTargetMode.SpecificDisplay, TargetDisplay = Aoc.Identity });
        Assert.NotEqual(0, g.Id);
        store.AddPlaytime(g.Id, TimeSpan.FromMinutes(2), DateTime.UtcNow);
        var back = Assert.Single(new SqliteStore(_db).GetAll());
        Assert.Equal((120L, Aoc.Identity), (back.PlaytimeSeconds, back.TargetDisplay));
        Assert.NotNull(back.LastPlayedUtc);
        store.Delete(g.Id);
        Assert.Empty(store.GetAll());
    }

    [Fact]
    public void MigratesPhase1DatabaseAndRoundTripsDiscoveryFields()
    {
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_db};Pooling=False"))
        {
            c.Open();
            var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE games(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, source TEXT NOT NULL,
                  exe TEXT NOT NULL, args TEXT NOT NULL DEFAULT '', workdir TEXT, cover TEXT, background TEXT,
                  categories TEXT NOT NULL DEFAULT '', favorite INTEGER NOT NULL DEFAULT 0, target_mode TEXT NOT NULL,
                  target_display TEXT, last_display TEXT, target_fallback TEXT NOT NULL, added_utc TEXT NOT NULL,
                  last_played_utc TEXT, playtime_s INTEGER NOT NULL DEFAULT 0);
                INSERT INTO games(name,source,exe,target_mode,target_fallback,added_utc) VALUES('Old','manual','C:\o.exe','FollowLauncher','FollowLauncher','2026-01-01T00:00:00Z');
                """;
            cmd.ExecuteNonQuery();
        }
        var store = new SqliteStore(_db);
        var old = Assert.Single(store.GetAll());
        Assert.True(old.Installed);
        store.Save(new Game { Name = "CS2", Source = "steam", ExternalId = "730", LaunchUri = "steam://rungameid/730", InstallDir = "D:\\cs", Hidden = true, AutoPlace = false, UseControllerHelper = true, ShowOnHome = false, CustomExe = @"D:\x.exe", HomeOrder = 3, HelperName = "ZerkSense5" });
        var cs = new SqliteStore(_db).GetAll().Single(g => g.Source == "steam");
        Assert.Equal(("730", "steam://rungameid/730", "D:\\cs", true, false, true), (cs.ExternalId, cs.LaunchUri, cs.InstallDir, cs.Hidden, cs.AutoPlace, cs.UseControllerHelper));
        Assert.False(cs.ShowOnHome);
        Assert.Equal(@"D:\x.exe", cs.CustomExe);
        Assert.Equal(3, cs.HomeOrder);
        Assert.Equal("ZerkSense5", cs.HelperName);
    }

    [Fact]
    public void BackupAndRestoreRoundTrip()
    {
        var store = new SqliteStore(_db);
        store.Save(new Game { Name = "Keep", ExecutablePath = "C:\\k.exe" });
        var backup = _db + ".bak";
        try
        {
            store.BackupTo(backup);
            store.Save(new Game { Name = "Later", ExecutablePath = "C:\\l.exe" });
            store.RestoreFrom(backup);
            Assert.Equal(["Keep"], new SqliteStore(_db).GetAll().Select(g => g.Name));
        }
        finally { File.Delete(backup); }
    }

    public void Dispose()
    {
        foreach (var f in new[] { _db, _db + "-wal", _db + "-shm" }) try { File.Delete(f); } catch { }
    }
}
