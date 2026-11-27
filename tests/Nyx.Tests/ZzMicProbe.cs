using PhoneBridge;
using Xunit;

namespace Nyx.Tests;

// manual check of a virtual cable: NYX_MIC_PROBE=1 plays a 1 kHz tone into it for 20 s
public class ZzMicProbe
{
    [Fact]
    public void Tone()
    {
        if (Environment.GetEnvironmentVariable("NYX_MIC_PROBE") != "1") return;
        var cable = PhoneMic.FindCable();
        Assert.NotNull(cable);
        PhoneMic.ProbeTone(cable!.Device, TimeSpan.FromSeconds(20));
    }
}
