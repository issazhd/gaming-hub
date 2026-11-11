using GamingHub.Core.Games;
using Xunit;

namespace GamingHub.Tests;

public class ElevatedTaskTests
{
    [Fact]
    public void Scripts_run_through_cmd_and_programs_directly()
    {
        var bat = ElevatedTask.ActionFor(new(@"C:\Games\X\!start.bat", "", @"C:\Games\X"));
        Assert.Equal("cmd.exe", bat.Command);
        Assert.Equal("/c \"\"C:\\Games\\X\\!start.bat\"\"", bat.Arguments);
        var exe = ElevatedTask.ActionFor(new(@"C:\Games\Y\y.exe", "-dx12", @"C:\Games\Y"));
        Assert.Equal((@"C:\Games\Y\y.exe", "-dx12", @"C:\Games\Y"), (exe.Command, exe.Arguments, exe.WorkingDirectory));
    }

    [Fact]
    public void Store_links_are_refused() =>
        Assert.Throws<InvalidOperationException>(() => ElevatedTask.ActionFor(new("steam://rungameid/1", "", "")));

    [Fact]
    public void Xml_round_trips_through_matches_with_escaping()
    {
        var a = new TaskAction(@"C:\Games\A & B\a.exe", "-name \"<x>\"", @"C:\Games\A & B");
        var xml = ElevatedTask.Xml(a, @"PC\user", "A & B");
        Assert.StartsWith("<?xml", xml);
        Assert.Contains("<RunLevel>HighestAvailable</RunLevel>", xml);
        Assert.True(ElevatedTask.Matches(xml, a));
        Assert.False(ElevatedTask.Matches(xml, a with { Arguments = "" }));
        Assert.False(ElevatedTask.Matches(null, a));
    }

    [Fact]
    public void Companion_command_round_trips_the_game_action()
    {
        var game = new TaskAction("cmd.exe", "/c \"\"C:\\Games\\A & B\\!start.bat\" -x\"", @"C:\Games\A & B");
        var task = ElevatedTask.ViaCompanion(game, @"C:\Users\me\AppData\Local\Programs\GamingHub\GamingHub.exe", 42);
        Assert.Equal(@"C:\Users\me\AppData\Local\Programs\GamingHub\GamingHub.exe", task.Command);
        Assert.Equal(game.WorkingDirectory, task.WorkingDirectory);
        var parsed = ElevatedTask.ParseCompanion(task.Arguments.Split(' '));   // Base64 has no spaces: three plain arguments
        Assert.NotNull(parsed);
        Assert.Equal(42, parsed.Value.Id);
        Assert.Equal(game, parsed.Value.Game);
        Assert.True(ElevatedTask.Matches(ElevatedTask.Xml(task, @"PC\me", "A & B"), task));
    }

    [Theory]
    [InlineData("--run-game")]
    [InlineData("--run-game x abc")]
    [InlineData("--run-game 5 not-base64!")]
    [InlineData("--other 5 e30=")]
    public void Companion_rejects_bad_command_lines(string line) => Assert.Null(ElevatedTask.ParseCompanion(line.Split(' ')));

    [Fact]
    public void Task_names_separate_dev_and_installed() =>
        Assert.NotEqual(ElevatedTask.Name(5, true), ElevatedTask.Name(5, false));
}
