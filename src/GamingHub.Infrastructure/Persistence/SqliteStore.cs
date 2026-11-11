using System.Text.Json;
using System.Text.Json.Serialization;
using GamingHub.Core.Displays;
using GamingHub.Core.Games;
using GamingHub.Core.Settings;
using Microsoft.Data.Sqlite;

namespace GamingHub.Infrastructure.Persistence;

/// <summary>SQLite-backed settings + game library. Settings are a versioned JSON document.</summary>
public sealed class SqliteStore : ISettingsStore, IGameRepository
{
    static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    readonly string _cs;

    public SqliteStore(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
        _cs = new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString();
        Exec("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS games(
              id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, source TEXT NOT NULL,
              exe TEXT NOT NULL, args TEXT NOT NULL DEFAULT '', workdir TEXT, cover TEXT, background TEXT,
              categories TEXT NOT NULL DEFAULT '', favorite INTEGER NOT NULL DEFAULT 0,
              target_mode TEXT NOT NULL, target_display TEXT, last_display TEXT, target_fallback TEXT NOT NULL,
              added_utc TEXT NOT NULL, last_played_utc TEXT, playtime_s INTEGER NOT NULL DEFAULT 0);
            """);
        // Additive migrations (v2: discovery + placement). Existing columns raise "duplicate column" → ignored.
        foreach (var col in new[] { "external_id TEXT", "launch_uri TEXT", "install_dir TEXT",
                     "installed INTEGER NOT NULL DEFAULT 1", "auto_place INTEGER NOT NULL DEFAULT 1", "hidden INTEGER NOT NULL DEFAULT 0", "ds4windows INTEGER NOT NULL DEFAULT 0", "show_home INTEGER NOT NULL DEFAULT 1", "custom_exe TEXT", "home_order INTEGER NOT NULL DEFAULT 0", "helper TEXT", "run_elevated INTEGER NOT NULL DEFAULT 0", "allow_pause INTEGER NOT NULL DEFAULT 0" })
            try { Exec($"ALTER TABLE games ADD COLUMN {col}"); } catch (SqliteException) { }
    }

    SqliteConnection Open() { var c = new SqliteConnection(_cs); c.Open(); return c; }

    void Exec(string sql, params (string, object?)[] p)
    {
        using var c = Open();
        using var cmd = Cmd(c, sql, p);
        cmd.ExecuteNonQuery();
    }

    static SqliteCommand Cmd(SqliteConnection c, string sql, (string, object?)[] p)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in p) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return cmd;
    }

    public AppSettings Load()
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT json FROM settings WHERE key='app'", []);
        return cmd.ExecuteScalar() is string s ? JsonSerializer.Deserialize<AppSettings>(s, Json) ?? new() : new();
    }

    public void Save(AppSettings settings) =>
        Exec("INSERT INTO settings(key,json) VALUES('app',$j) ON CONFLICT(key) DO UPDATE SET json=$j",
            ("$j", JsonSerializer.Serialize(settings, Json)));

    static string? Ser(DisplayIdentity? d) => d is null ? null : JsonSerializer.Serialize(d, Json);
    static DisplayIdentity? De(object v) => v is string s ? JsonSerializer.Deserialize<DisplayIdentity>(s, Json) : null;

    public IReadOnlyList<Game> GetAll()
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT * FROM games ORDER BY name COLLATE NOCASE", []);
        using var r = cmd.ExecuteReader();
        var list = new List<Game>();
        while (r.Read())
            list.Add(new Game
            {
                Id = (long)r["id"], Name = (string)r["name"], Source = (string)r["source"], ExecutablePath = (string)r["exe"],
                Arguments = (string)r["args"], WorkingDirectory = r["workdir"] as string, CoverPath = r["cover"] as string,
                BackgroundPath = r["background"] as string, Categories = (string)r["categories"], Favorite = (long)r["favorite"] != 0,
                TargetMode = Enum.Parse<GameTargetMode>((string)r["target_mode"]), TargetDisplay = De(r["target_display"]),
                LastUsedDisplay = De(r["last_display"]), TargetFallback = Enum.Parse<GameTargetFallback>((string)r["target_fallback"]),
                AddedUtc = DateTime.Parse((string)r["added_utc"]).ToUniversalTime(),
                LastPlayedUtc = r["last_played_utc"] is string lp ? DateTime.Parse(lp).ToUniversalTime() : null,
                PlaytimeSeconds = (long)r["playtime_s"], ExternalId = r["external_id"] as string, LaunchUri = r["launch_uri"] as string,
                InstallDir = r["install_dir"] as string, Installed = (long)r["installed"] != 0, AutoPlace = (long)r["auto_place"] != 0, Hidden = (long)r["hidden"] != 0, UseControllerHelper = (long)r["ds4windows"] != 0, ShowOnHome = (long)r["show_home"] != 0, CustomExe = r["custom_exe"] as string, HomeOrder = (int)(long)r["home_order"], HelperName = r["helper"] as string, RunElevated = (long)r["run_elevated"] != 0, AllowPause = (long)r["allow_pause"] != 0,
            });
        return list;
    }

    public Game Save(Game g)
    {
        (string, object?)[] p =
        [
            ("$id", g.Id), ("$name", g.Name), ("$src", g.Source), ("$exe", g.ExecutablePath), ("$args", g.Arguments),
            ("$wd", g.WorkingDirectory), ("$cover", g.CoverPath), ("$bg", g.BackgroundPath), ("$cat", g.Categories),
            ("$fav", g.Favorite ? 1 : 0), ("$tm", g.TargetMode.ToString()), ("$td", Ser(g.TargetDisplay)),
            ("$ld", Ser(g.LastUsedDisplay)), ("$tf", g.TargetFallback.ToString()), ("$added", g.AddedUtc.ToString("O")),
            ("$lp", g.LastPlayedUtc?.ToString("O")), ("$pt", g.PlaytimeSeconds), ("$ext", g.ExternalId), ("$uri", g.LaunchUri),
            ("$idir", g.InstallDir), ("$inst", g.Installed ? 1 : 0), ("$ap", g.AutoPlace ? 1 : 0), ("$hid", g.Hidden ? 1 : 0), ("$ds4", g.UseControllerHelper ? 1 : 0), ("$home", g.ShowOnHome ? 1 : 0), ("$cexe", g.CustomExe), ("$hord", g.HomeOrder), ("$helper", g.HelperName), ("$elev", g.RunElevated ? 1 : 0), ("$pause", g.AllowPause ? 1 : 0),
        ];
        using var c = Open();
        if (g.Id == 0)
        {
            using var ins = Cmd(c, """
                INSERT INTO games(name,source,exe,args,workdir,cover,background,categories,favorite,target_mode,
                  target_display,last_display,target_fallback,added_utc,last_played_utc,playtime_s,external_id,launch_uri,install_dir,installed,auto_place,hidden,ds4windows,show_home,custom_exe,home_order,helper,run_elevated,allow_pause)
                VALUES($name,$src,$exe,$args,$wd,$cover,$bg,$cat,$fav,$tm,$td,$ld,$tf,$added,$lp,$pt,$ext,$uri,$idir,$inst,$ap,$hid,$ds4,$home,$cexe,$hord,$helper,$elev,$pause);
                SELECT last_insert_rowid();
                """, p);
            g.Id = (long)ins.ExecuteScalar()!;
        }
        else
        {
            using var up = Cmd(c, """
                UPDATE games SET name=$name,source=$src,exe=$exe,args=$args,workdir=$wd,cover=$cover,background=$bg,
                  categories=$cat,favorite=$fav,target_mode=$tm,target_display=$td,last_display=$ld,
                  target_fallback=$tf,last_played_utc=$lp,playtime_s=$pt,external_id=$ext,launch_uri=$uri,
                  install_dir=$idir,installed=$inst,auto_place=$ap,hidden=$hid,ds4windows=$ds4,show_home=$home,custom_exe=$cexe,home_order=$hord,helper=$helper,run_elevated=$elev,allow_pause=$pause WHERE id=$id
                """, p);
            up.ExecuteNonQuery();
        }
        return g;
    }

    public void BackupTo(string path)
    {
        using var c = Open();
        using var dst = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        c.BackupDatabase(dst);
    }

    public void RestoreFrom(string path)
    {
        using var src = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        src.Open();
        using var c = Open();
        src.BackupDatabase(c);
    }

    public void Delete(long id) => Exec("DELETE FROM games WHERE id=$id", ("$id", id));

    public void AddPlaytime(long id, TimeSpan played, DateTime endedUtc) =>
        Exec("UPDATE games SET playtime_s=playtime_s+$s, last_played_utc=$t WHERE id=$id",
            ("$s", (long)played.TotalSeconds), ("$t", endedUtc.ToString("O")), ("$id", id));
}
