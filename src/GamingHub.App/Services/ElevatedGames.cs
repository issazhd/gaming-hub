using GamingHub.Core.Games;
using GamingHub.Infrastructure.Games;

namespace GamingHub.App.Services;

/// <summary>A game's "run as administrator without a prompt" task (D-015), which starts the game through Gaming Hub's
/// companion (D-019) so the game can be paused and force-closed even though it runs as administrator.</summary>
public static class ElevatedGames
{
    public static string TaskName(Game g) => ElevatedTask.Name(g.Id, App.IsDev);

    /// <exception cref="InvalidOperationException">Store-link games, or no executable.</exception>
    public static TaskAction ActionFor(Game g) =>
        ElevatedTask.ViaCompanion(ElevatedTask.ActionFor(LaunchCommandBuilder.Build(g)), Environment.ProcessPath!, g.Id);

    /// <summary>The task exists and starts the current command through this copy of Gaming Hub.</summary>
    public static bool IsReady(Game g)
    {
        try { return ElevatedTask.Matches(ElevatedTasks.Query(TaskName(g)), ActionFor(g)); }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>Creates or replaces the task (one Windows approval). Null on success, else a message.</summary>
    public static string? Create(Game g)
    {
        TaskAction a;
        try { a = ActionFor(g); }
        catch (InvalidOperationException ex) { return ex.Message; }
        return ElevatedTasks.Create(TaskName(g), ElevatedTask.Xml(a, $"{Environment.UserDomainName}\\{Environment.UserName}", g.Name));
    }

    public static string? Delete(Game g) => ElevatedTasks.Delete(TaskName(g));
}
