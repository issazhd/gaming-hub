using GamingHub.Core.Displays;
using Xunit;

namespace GamingHub.Tests;

public class AudioRoutingTests
{
    static readonly (string, string)[] Outputs =
    [
        ("aoc", "2590G4 (NVIDIA High Definition Audio)"),
        ("tv", "Beyond TV (NVIDIA High Definition Audio)"),
        ("spk", "Speakers (Realtek(R) Audio)"),
    ];

    [Fact] public void FindsTheScreensOwnOutput() => Assert.Equal("tv", AudioRouting.OutputFor("Beyond TV", Outputs));
    [Fact] public void NoOutputForScreenWithoutAudio() => Assert.Null(AudioRouting.OutputFor("DELL U2723QE", Outputs));

    [Fact]
    public void MovingToTvSwitchesAndRemembersPrevious() =>
        Assert.Equal(new AudioDecision("tv", "spk", false), AudioRouting.Decide(true, "tv", "spk", null, ["aoc", "tv", "spk"]));

    [Fact]
    public void AlreadyOnTvAudioDoesNothing() =>
        Assert.Equal(new AudioDecision(null, null, false), AudioRouting.Decide(true, "tv", "tv", "spk", ["tv", "spk"]));

    [Fact]
    public void TvWithoutAudioDoesNothing() =>
        Assert.Equal(new AudioDecision(null, null, false), AudioRouting.Decide(true, null, "spk", null, ["spk"]));

    [Fact]
    public void BackOnMonitorRestoresPrevious() =>
        Assert.Equal(new AudioDecision("spk", null, true), AudioRouting.Decide(false, null, "tv", "spk", ["tv", "spk"]));

    [Fact]
    public void RestoreSkipsUnpluggedDevice() =>
        Assert.Equal(new AudioDecision(null, null, true), AudioRouting.Decide(false, null, "tv", "usb-headset", ["tv", "spk"]));

    [Fact]
    public void NothingRememberedNothingRestored() =>
        Assert.Equal(new AudioDecision(null, null, false), AudioRouting.Decide(false, null, "spk", null, ["spk"]));
}
