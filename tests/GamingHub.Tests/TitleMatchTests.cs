using GamingHub.Core.Games;
using Xunit;

namespace GamingHub.Tests;

public class TitleMatchTests
{
    [Theory]
    [InlineData("Cyberpunk 2077", "Cyberpunk 2077", true)]
    [InlineData("Grand Theft Auto V Enhanced", "Grand Theft Auto V", true)]
    [InlineData("Control", "Control Ultimate Edition", true)]
    [InlineData("Tom Clancy's Rainbow Six® Siege X", "Tom Clancy’s Rainbow Six Siege X", true)]
    [InlineData("Age Of Empires 4", "Age of Empires IV: Anniversary Edition", true)]
    [InlineData("Ghost of Tsushima DC", "Ghost of Tsushima DIRECTOR'S CUT", true)]
    [InlineData("Death Stranding", "DEATH STRANDING DIRECTOR'S CUT", true)]
    [InlineData("HITMAN - WoA", "HITMAN World of Assassination", true)]
    [InlineData("Resident Evil - Village", "Resident Evil Village", true)]
    [InlineData("Mafia - Definitive Edition", "Mafia: Definitive Edition", true)]
    [InlineData("Alan Wake 2", "Alan Wake II", true)]
    [InlineData("Control", "CONTROL Resonant", false)]
    [InlineData("Company of Heroes", "Company of Heroes 2", false)]
    [InlineData("Alan Wake 2", "Alan Wake", false)]
    [InlineData("Grand Theft Auto V", "Grand Theft Auto IV", false)]
    [InlineData("Command & Conquer Generals", "Command & Conquer Generals Zero Hour", false)]
    [InlineData("Halo", "Halo Infinite", false)]
    [InlineData("The Edition", "Edition", false)]
    [InlineData("", "", false)]
    public void SameGame(string a, string b, bool expected) => Assert.Equal(expected, TitleMatch.SameGame(a, b));

    [Theory]
    [InlineData("Resident Evil - Village", "Resident Evil Village")]
    [InlineData("HITMAN + Peacock", "HITMAN")]
    [InlineData("STAR WARS Jedi - Survivor™", "STAR WARS Jedi Survivor")]
    public void SearchTermDropsPunctuation(string name, string term) => Assert.Equal(term, TitleMatch.SearchTerm(name));

    [Theory]
    [InlineData("Ghost of Tsushima DC", "Ghost of Tsushima")]
    [InlineData("HITMAN - WoA", "HITMAN world of assassination")]
    [InlineData("Mafia - Definitive Edition", "Mafia")]
    public void CoreTermForSecondSearch(string name, string term) => Assert.Equal(term, TitleMatch.CoreTerm(name));
}
