using System.Diagnostics;
using GamingHub.Infrastructure.Games;
using Xunit;

namespace GamingHub.Tests;

public class ProcessFreezerTests
{
    /// <summary>Suspends a harmless child process (all threads end up suspended), then resumes it.</summary>
    [Fact]
    public void Suspend_then_resume_a_process()
    {
        using var p = Process.Start(new ProcessStartInfo("ping.exe", "-n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            Thread.Sleep(300);
            var done = ProcessFreezer.Suspend([p.Id]);
            Assert.Equal([p.Id], done);
            p.Refresh();
            Assert.All(p.Threads.Cast<ProcessThread>(), t => Assert.Equal(ThreadWaitReason.Suspended, t.WaitReason));

            ProcessFreezer.Resume(done);
            Thread.Sleep(200);
            p.Refresh();
            Assert.Contains(p.Threads.Cast<ProcessThread>(), t => t.ThreadState != System.Diagnostics.ThreadState.Wait || t.WaitReason != ThreadWaitReason.Suspended);
        }
        finally { p.Kill(); }
    }

    /// <summary>One process refuses (Windows' protected System process, pid 4): nothing stays paused.</summary>
    [Fact]
    public void SuspendAll_is_all_or_nothing()
    {
        using var p = Process.Start(new ProcessStartInfo("ping.exe", "-n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            Thread.Sleep(300);
            var (paused, refused) = ProcessFreezer.SuspendAll([p.Id, 4]);
            Assert.Empty(paused);
            Assert.Contains("System", refused);
            p.Refresh();
            Assert.DoesNotContain(p.Threads.Cast<ProcessThread>(), t => t.ThreadState == System.Diagnostics.ThreadState.Wait && t.WaitReason == ThreadWaitReason.Suspended);

            var (ok, none) = ProcessFreezer.SuspendAll([p.Id]);
            Assert.Equal([p.Id], ok);
            Assert.Empty(none);
            ProcessFreezer.Resume(ok);
        }
        finally { p.Kill(); }
    }
}
