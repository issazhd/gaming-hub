using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using GamingHub.Core.Games;
using GamingHub.Infrastructure.Games;
using Serilog;

namespace GamingHub.App.Services;

/// <summary>
/// Companion mode (D-019): <c>GamingHub.exe --run-game &lt;id&gt; &lt;action&gt;</c>, started with administrator rights by the
/// game's "run as administrator without a prompt" task. It starts the game, then — while the game runs — answers "pause" and
/// "resume" from Gaming Hub over a pipe only the user can open. It can pause nothing but its own game's processes, resumes them
/// whenever Gaming Hub disconnects (closed or crashed), and exits when the game exits. No window, no single-instance lock.
/// </summary>
public static class ElevatedCompanion
{
    public static int Run(string[] args)
    {
        if (ElevatedTask.ParseCompanion(args) is not { } p) return 2;
        var (id, game) = p;
        Log.Logger = new LoggerConfiguration().MinimumLevel.Information()
            .WriteTo.File(Path.Combine(App.DataDir, "logs", "companion-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
            .CreateLogger();
        try
        {
            var dir = GameFolder(game);
            Log.Information("Companion for game {Id}: starting {Cmd} {Args} (folder {Dir})", id, game.Command, game.Arguments, dir ?? "none");
            var child = Process.Start(new ProcessStartInfo(game.Command, game.Arguments) { WorkingDirectory = game.WorkingDirectory, UseShellExecute = true });
            var state = new State(dir, child);
            new Thread(() => Serve(ElevatedTask.PipeName(id, App.IsDev), state)) { IsBackground = true, Name = "Companion pipe" }.Start();

            // Live as long as the game: same rule as Gaming Hub's tracking (gone twice in a row once seen; give up after 3 min unseen).
            var sw = Stopwatch.StartNew();
            bool seen = false;
            int empty = 0;
            while (true)
            {
                Thread.Sleep(3000);
                if (state.Pids().Count > 0) { seen = true; empty = 0; }
                else if ((seen || sw.Elapsed.TotalMinutes > 3) && ++empty >= 2) break;
            }
            state.Resume();
            Log.Information("Companion for game {Id}: game exited", id);
            return 0;
        }
        catch (Exception ex) { Log.Error(ex, "Companion failed"); return 1; }
        finally { Log.CloseAndFlush(); }
    }

    sealed class State(string? dir, Process? child)
    {
        readonly object _gate = new();
        List<int> _paused = [];

        public HashSet<int> Pids()
        {
            var set = dir is not null && Directory.Exists(dir) ? GameWindows.ProcessesUnder(dir) : [];
            try { if (child is { HasExited: false }) set.Add(child.Id); } catch { }
            set.Remove(Environment.ProcessId);
            return set;
        }

        public string Pause()
        {
            lock (_gate)
            {
                if (_paused.Count > 0) return "ok";
                var pids = Pids();
                if (pids.Count == 0) return "none";
                var (paused, refused) = ProcessFreezer.SuspendAll(pids);
                _paused = paused;
                Log.Information("Pause: {Paused} of {All} processes paused; refused: {Refused}", paused.Count, pids.Count, string.Join(", ", refused));
                return refused.Count > 0 ? "refused:" + string.Join(",", refused) : "ok";
            }
        }

        /// <summary>Force-closes the game (End task) — only on the user's explicit request from the quick menu.</summary>
        public string Kill()
        {
            lock (_gate)
            {
                int n = ForceClose(Pids());
                _paused = [];
                Log.Information("Force-closed {Count} processes", n);
                return n > 0 ? "ok" : "none";
            }
        }

        public void Resume()
        {
            lock (_gate)
            {
                if (_paused.Count == 0) return;
                ProcessFreezer.Resume(_paused);
                Log.Information("Resumed {Count} processes", _paused.Count);
                _paused = [];
            }
        }
    }

    static void Serve(string name, State state)
    {
        // Only this Windows user may connect (the companion runs as the same user, with administrator rights).
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        while (true)
        {
            try
            {
                using var pipe = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.FirstPipeInstance, 256, 256, security);
                pipe.WaitForConnection();
                using var reader = new StreamReader(pipe);
                using var writer = new StreamWriter(pipe) { AutoFlush = true };
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    if (line == "pause") writer.WriteLine(state.Pause());
                    else if (line == "resume") { state.Resume(); writer.WriteLine("ok"); }
                    else if (line == "kill") writer.WriteLine(state.Kill());
                    else writer.WriteLine("unknown");
                }
            }
            catch (IOException) { }                       // Gaming Hub went away mid-conversation
            catch (UnauthorizedAccessException) { Thread.Sleep(2000); }   // name taken by someone else: try again later
            state.Resume();                               // whatever happened: never leave the game paused
        }
    }

    /// <summary>Ends each process with its child processes (like Task Manager's End task). Returns how many were ended.</summary>
    public static int ForceClose(IEnumerable<int> pids)
    {
        int n = 0;
        foreach (var pid in pids)
            try { using var p = Process.GetProcessById(pid); p.Kill(entireProcessTree: true); n++; }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        return n;
    }

    /// <summary>The game's folder for finding its processes — never a broad folder like Program Files or the Windows folder.</summary>
    static string? GameFolder(TaskAction game)
    {
        var dir = Path.GetFileName(game.Command).Equals("cmd.exe", StringComparison.OrdinalIgnoreCase) ? game.WorkingDirectory : Path.GetDirectoryName(game.Command);
        if (string.IsNullOrWhiteSpace(dir)) return null;
        var full = Path.GetFullPath(dir).TrimEnd('\\');
        string[] broad =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ];
        bool tooBroad = Path.GetPathRoot(full)!.TrimEnd('\\') == full || broad.Any(b => full.Equals(b, StringComparison.OrdinalIgnoreCase))
            || full.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows) + "\\", StringComparison.OrdinalIgnoreCase);
        return tooBroad ? null : full;
    }
}
