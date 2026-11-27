using PhoneBridge;
using Xunit;

namespace Nyx.Tests;

/// <summary>The top-bar pill with an Android phone, an iPhone, both or neither.</summary>
public class PhonePillTests
{
    [Fact]
    public void Nothing_linked_shows_no_phone() =>
        Assert.Equal(("off", "No phone", ""), PhonePill.Pick("off", "Pixel", "Wi-Fi", false, ""));

    [Fact]
    public void Android_alone_is_unchanged()
    {
        Assert.Equal(("on", "Pixel 8", "Wi-Fi"), PhonePill.Pick("on", "Pixel 8", "Wi-Fi", false, ""));
        Assert.Equal(("busy", "Connecting", ""), PhonePill.Pick("busy", "Pixel 8", "USB", false, ""));
    }

    [Fact]
    public void IPhone_alone_shows_online_over_bluetooth() =>
        Assert.Equal(("on", "Sam's iPhone", "Bluetooth"), PhonePill.Pick("off", "", "", true, " Sam's iPhone "));

    [Fact]
    public void IPhone_without_a_name_is_called_iphone() =>
        Assert.Equal(("on", "iPhone", "Bluetooth"), PhonePill.Pick("off", "", "", true, ""));

    [Fact]
    public void Android_keeps_the_pill_when_both_are_linked()
    {
        Assert.Equal(("on", "Pixel 8", "USB"), PhonePill.Pick("on", "Pixel 8", "USB", true, "iPhone"));
        Assert.Equal(("busy", "Connecting", ""), PhonePill.Pick("busy", "Pixel 8", "Wi-Fi", true, "iPhone"));
    }
}
