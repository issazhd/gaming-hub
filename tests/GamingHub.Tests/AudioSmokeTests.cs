using GamingHub.Infrastructure.Audio;
using Xunit;
using Xunit.Abstractions;

namespace GamingHub.Tests;

/// <summary>Read-only smoke test against this PC's real Core Audio devices (never changes the default).</summary>
public class AudioSmokeTests(ITestOutputHelper output)
{
    [Fact]
    public void ListsPlaybackDevicesWithAtMostOneDefault()
    {
        var list = AudioOutputs.List();
        foreach (var d in list) output.WriteLine($"{(d.IsDefault ? "*" : " ")} {d.Name}");
        Assert.True(list.Count(d => d.IsDefault) <= 1);
        Assert.All(list, d => Assert.False(string.IsNullOrWhiteSpace(d.Name)));
    }
}
