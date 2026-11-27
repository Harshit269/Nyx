using System.Windows;
using PhoneBridge;
using Xunit;

namespace Nyx.Tests;

public class PhoneMirrorTests
{
    static readonly Size Area = new(400, 800);

    [Fact]
    public void A_quick_click_is_a_tap_and_a_held_one_a_long_press()
    {
        var tap = PhoneMirror.Gesture(new Point(100, 200), new Point(102, 201), TimeSpan.FromMilliseconds(120), Area);
        Assert.Equal("tap", tap["a"]!.ToString());
        Assert.Equal(0.25, (double)tap["x"]!, 3);
        Assert.Equal(0.25, (double)tap["y"]!, 3);
        Assert.Equal("long", PhoneMirror.Gesture(new Point(100, 200), new Point(100, 200), TimeSpan.FromMilliseconds(800), Area)["a"]!.ToString());
    }

    [Fact]
    public void A_drag_is_a_swipe_and_everything_stays_on_the_screen()
    {
        var s = PhoneMirror.Gesture(new Point(200, 700), new Point(200, -50), TimeSpan.FromMilliseconds(5000), Area);
        Assert.Equal("swipe", s["a"]!.ToString());
        Assert.Equal(0.875, (double)s["y"]!, 3);
        Assert.Equal(0.0, (double)s["y2"]!, 3);          // dragged off the top: clamped
        Assert.Equal(1500, (int)s["ms"]!);               // a long hold is capped
    }
}
