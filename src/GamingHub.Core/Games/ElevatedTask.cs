using System.Security;

namespace GamingHub.Core.Games;

/// <summary>What a "run as administrator without a prompt" scheduled task executes.</summary>
public sealed record TaskAction(string Command, string Arguments, string WorkingDirectory);

/// <summary>
/// Builds the Task Scheduler definition used to start a game with admin rights and no UAC prompt.
/// The task is created once (that creation needs the user's approval); afterwards the same user can start it.
/// </summary>
public static class ElevatedTask
{
    public static string Name(long gameId, bool dev) => $@"\GamingHub{(dev ? "-Dev" : "")}\Game-{gameId}";
    // ---- Companion (D-019): the task starts Gaming Hub's own exe with administrator rights, which starts the game and can
    // pause/resume only that game on request. The game command travels as Base64 JSON, so no nested quoting is needed. ----
    public const string CompanionSwitch = "--run-game";

    public static TaskAction ViaCompanion(TaskAction game, string companionExe, long gameId) =>
        new(companionExe, $"{CompanionSwitch} {gameId} {Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(game)))}", game.WorkingDirectory);

    /// <summary>Reads the companion's command line: game id and the game's own launch action.</summary>
    public static (long Id, TaskAction Game)? ParseCompanion(string[] args)
    {
        if (args.Length < 3 || args[0] != CompanionSwitch || !long.TryParse(args[1], out var id)) return null;
        try
        {
            var game = System.Text.Json.JsonSerializer.Deserialize<TaskAction>(Convert.FromBase64String(args[2]));
            return game is null || string.IsNullOrWhiteSpace(game.Command) ? null : (id, game);
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException) { return null; }
    }

    /// <summary>Private channel between Gaming Hub and the companion of one game.</summary>
    public static string PipeName(long gameId, bool dev) => $"GamingHub{(dev ? "-Dev" : "")}.Game.{gameId}";

    /// <summary>Task for a controller helper (e.g. ZerkSense5, which asks for admin rights every start).</summary>
    public static string HelperName(string helper, bool dev) => $@"\GamingHub{(dev ? "-Dev" : "")}\{helper}";

    /// <summary>Scripts run through cmd.exe; programs run directly. Platform links (steam://…) are not supported.</summary>
    public static TaskAction ActionFor(LaunchCommand c)
    {
        if (c.FileName.Contains("://")) throw new InvalidOperationException("Games started through a store launcher can't run as administrator from Gaming Hub.");
        var ext = Path.GetExtension(c.FileName).ToLowerInvariant();
        return ext is ".bat" or ".cmd"
            ? new("cmd.exe", $"/c \"\"{c.FileName}\"{(c.Arguments.Length > 0 ? " " + c.Arguments : "")}\"", c.WorkingDirectory)
            : new(c.FileName, c.Arguments, c.WorkingDirectory);
    }

    public static string Xml(TaskAction a, string user, string gameName)
    {
        static string E(string s) => SecurityElement.Escape(s) ?? "";
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>{E($"Gaming Hub: starts {gameName} as administrator without a prompt. Turn it off in the game's settings in Gaming Hub.")}</Description></RegistrationInfo>
              <Principals><Principal id="Author"><UserId>{E(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{E(a.Command)}</Command><Arguments>{E(a.Arguments)}</Arguments><WorkingDirectory>{E(a.WorkingDirectory)}</WorkingDirectory></Exec></Actions>
            </Task>
            """;
    }

    /// <summary>True when an existing task (its XML from schtasks /query /xml) runs exactly this action.</summary>
    public static bool Matches(string? xml, TaskAction a)
    {
        if (string.IsNullOrEmpty(xml)) return false;
        try
        {
            var doc = System.Xml.Linq.XDocument.Parse(xml);
            System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            var exec = doc.Descendants(ns + "Exec").FirstOrDefault();
            string V(string n) => exec?.Element(ns + n)?.Value ?? "";
            return exec is not null && V("Command") == a.Command && V("Arguments") == a.Arguments && V("WorkingDirectory") == a.WorkingDirectory;
        }
        catch (System.Xml.XmlException) { return false; }
    }
}
