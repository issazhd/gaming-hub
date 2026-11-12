using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using GamingHub.App.Services;
using GamingHub.App.ViewModels;
using GamingHub.Core.Discovery;
using GamingHub.Core.Displays;
using GamingHub.Core.Games;
using GamingHub.Core.Settings;
using GamingHub.Infrastructure.Discovery;
using GamingHub.Infrastructure.Displays;
using GamingHub.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace GamingHub.App;

public partial class App : Application
{
    /// <summary>Development build (Debug, e.g. <c>dotnet run</c>): separate data folder and single-instance name so it can run
    /// next to the installed app without touching its library, settings or "start with Windows".</summary>
#if DEBUG
    public const bool IsDev = true;
#else
    public const bool IsDev = false;
#endif
    static readonly string Suffix = IsDev ? "-Dev" : "";
    static readonly string RealDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GamingHub");
    public static readonly string DataDir = Environment.GetEnvironmentVariable("GAMINGHUB_DATA") ?? RealDataDir + Suffix;
    static readonly string InstanceName = $@"Local\GamingHub{Suffix}.SingleInstance", ActivateName = $@"Local\GamingHub{Suffix}.Activate";

    /// <summary>First development run: copy the real library database once, so testing starts from realistic data.</summary>
    static void SeedDevData()
    {
        if (!IsDev || Directory.Exists(DataDir) || !Directory.Exists(RealDataDir)) return;
        Directory.CreateDirectory(DataDir);
        var db = Path.Combine(RealDataDir, "gaminghub.db");
        if (File.Exists(db)) File.Copy(db, Path.Combine(DataDir, "gaminghub.db"));
        var art = Path.Combine(RealDataDir, "art");
        if (Directory.Exists(art))
        {
            Directory.CreateDirectory(Path.Combine(DataDir, "art"));
            // The database points at the real artwork files (read-only use); only the "already looked up" markers are copied.
            foreach (var f in Directory.EnumerateFiles(art, "tried-*")) File.Copy(f, Path.Combine(DataDir, "art", Path.GetFileName(f)));
        }
    }

    IHost? _host;
    Mutex? _single;
    EventWaitHandle? _activate;
    public static bool ShuttingDown { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length > 0 && e.Args[0] == ElevatedTask.CompanionSwitch)   // game companion: no window, no single-instance lock
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(ElevatedCompanion.Run(e.Args));
            return;
        }
        SeedDevData();
        Startup2(e);
    }

    void Startup2(StartupEventArgs e)
    {
        Log.Logger = new LoggerConfiguration().MinimumLevel.Is(IsDev ? Serilog.Events.LogEventLevel.Debug : Serilog.Events.LogEventLevel.Information)
            .WriteTo.File(Path.Combine(DataDir, "logs", "gaminghub-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        _host = Host.CreateDefaultBuilder().UseSerilog().ConfigureServices(s =>
        {
            var store = new SqliteStore(Path.Combine(DataDir, "gaminghub.db"));
            s.AddSingleton(store).AddSingleton<ISettingsStore>(store).AddSingleton<IGameRepository>(store)
             .AddSingleton<IDisplayProvider, WindowsDisplayProvider>()
             .AddSingleton<IGameScanner, SteamScanner>().AddSingleton<IGameScanner, EpicScanner>()
             .AddSingleton<IGameScanner, GogScanner>().AddSingleton<IGameScanner, UbisoftScanner>()
             .AddSingleton<IGameScanner>(sp => new ShortcutScanner(() => sp.GetRequiredService<SettingsService>().Current.EffectiveGameFolders))
             .AddSingleton<SettingsService>().AddSingleton<DisplayWatcher>().AddSingleton<LauncherPlacementService>()
             .AddSingleton<GameLaunchService>().AddSingleton<ControllerService>().AddSingleton<LibraryService>()
             .AddSingleton<SystemService>().AddSingleton<TrayService>().AddSingleton<ArtworkService>().AddSingleton<ControllerHelperService>().AddSingleton<AudioFollowService>()
             .AddSingleton<MainViewModel>().AddSingleton<MainWindow>();
        }).Build();
        var sp = _host.Services;

        // Diagnostics: dump what Windows reports, then exit (used to verify enumeration on real hardware).
        if (e.Args.Contains("--diagnose"))
        {
            File.WriteAllText(Path.Combine(DataDir, "display-diagnostics.json"),
                JsonSerializer.Serialize(sp.GetRequiredService<IDisplayProvider>().GetSnapshot(), new JsonSerializerOptions { WriteIndented = true }));
            Shutdown();
            return;
        }

        // Scripted add: GamingHub.exe --add-game "C:\path\game.exe" [name]
        if (Array.IndexOf(e.Args, "--add-game") is var i and >= 0 && i + 1 < e.Args.Length && File.Exists(e.Args[i + 1]))
            sp.GetRequiredService<IGameRepository>().Save(new Game
            {
                Name = i + 2 < e.Args.Length ? e.Args[i + 2] : Path.GetFileNameWithoutExtension(e.Args[i + 1]),
                ExecutablePath = e.Args[i + 1],
            });

        // Single instance: a second launch asks the running one to come forward.
        _single = new Mutex(true, InstanceName, out bool first);
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateName);
        if (!first && e.Args.Contains("--restart")) first = WaitForPrevious();
        if (!first) { _activate.Set(); Shutdown(); return; }

        DispatcherUnhandledException += (_, ex) => { Log.Error(ex.Exception, "Unhandled UI exception"); ex.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, _) => sp.GetService<GameLaunchService>()?.ResumeGame();   // crash: never leave a game paused
        var settings = sp.GetRequiredService<SettingsService>().Current;
        Log.Information("Gaming Hub {Version} starting, data dir {Dir}", typeof(App).Assembly.GetName().Version, DataDir);

        var window = sp.GetRequiredService<MainWindow>();
#if DEBUG
        if (e.Args.Contains("--display-settings"))
            sp.GetRequiredService<MainViewModel>().ShowSettingsCommand.Execute(null);
#endif
        var audio = sp.GetRequiredService<AudioFollowService>();   // before the window is placed, so the first move counts
        audio.Notify += sp.GetRequiredService<MainViewModel>().ShowToast;
        audio.Start();
        bool toTray = e.Args.Contains("--startup") && settings.StartMinimizedToTray && !settings.ConsoleMode;
        if (toTray) window.InitializeHidden(); else window.Show();
        var pad = sp.GetRequiredService<ControllerService>();
        sp.GetRequiredService<TrayService>().Start(window, pad, sp.GetRequiredService<MainViewModel>());
        pad.Start();

        new Thread(() =>
        {
            while (_activate.WaitOne() && !ShuttingDown) Dispatcher.BeginInvoke(window.BringToFront);
        }) { IsBackground = true, Name = "Activation listener" }.Start();
    }

    bool WaitForPrevious()
    {
        try { return _single!.WaitOne(TimeSpan.FromSeconds(15)); }
        catch (AbandonedMutexException) { return true; }
    }

    public static void Restart()
    {
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--restart") { UseShellExecute = true });
        Current.Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ShuttingDown = true;
        _host?.Services.GetService<GameLaunchService>()?.ResumeGame();   // never leave a game paused
        _activate?.Set();
        _host?.Services.GetService<TrayService>()?.Dispose();
        _host?.Services.GetService<DisplayWatcher>()?.Dispose();
        _host?.Services.GetService<ControllerService>()?.Dispose();
        _host?.Dispose();
        _single?.Dispose();   // releases the mutex so Restart() can start a new instance
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
