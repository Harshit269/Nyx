using PhoneBridge;
using Xunit;

namespace Nyx.Tests;

public class PhoneMicTests
{
    [Theory]
    [InlineData("CABLE Input (VB-Audio Virtual C", "CABLE Output (VB-Audio Virtual Cable)")] // names cut at 31 characters
    [InlineData("Voicemeeter Input (VB-Audio Vo", "Voicemeeter Out B1 (VB-Audio Voicemeeter VAIO)")]
    public void Virtual_cables_are_recognised(string playback, string pick) =>
        Assert.Equal(pick, PhoneMic.Match(3, playback)?.PickInCall);

    [Theory]
    [InlineData("Speakers (Realtek(R) Audio)")]
    [InlineData("Speakers (Steam Streaming Micro")] // only works while Steam streams
    [InlineData("Speakers (Razer BlackShark V2 X")]
    public void Real_speakers_are_never_used(string playback) =>
        Assert.Null(PhoneMic.Match(1, playback));
}
