using PhoneBridge;
using Xunit;

namespace Nyx.Tests;

public class QuietTests
{
    static Quiet.RECT R(int l, int t, int r, int b) => new() { Left = l, Top = t, Right = r, Bottom = b };

    [Theory]
    [InlineData("WhatsApp", "WhatsApp", "WhatsApp.Root", true)]                       // the Windows app
    [InlineData("WhatsApp", "(3) WhatsApp - Google Chrome", "chrome", true)]          // WhatsApp Web
    [InlineData("Instagram", "Instagram • Chats - Vivaldi", "vivaldi", true)]
    [InlineData("Discord", "#general | Server - Discord", "Discord", true)]
    [InlineData("WhatsApp", "Nyx", "Nyx", false)]
    [InlineData("Gmail", "Inbox - Outlook", "olk", false)]
    [InlineData("X", "Xbox", "XboxPcApp", false)]                                     // too short to judge
    public void A_pop_up_is_skipped_only_for_the_app_in_front(string app, string title, string process, bool skip) =>
        Assert.Equal(skip, Quiet.Matches(app, title, process));

    [Fact]
    public void Only_a_window_covering_the_whole_monitor_counts_as_full_screen()
    {
        var monitor = R(0, 0, 1920, 1080);
        Assert.True(Quiet.Covers(R(0, 0, 1920, 1080), monitor));          // borderless game
        Assert.True(Quiet.Covers(R(-8, -8, 1928, 1088), monitor));        // a bit bigger, still covers
        Assert.False(Quiet.Covers(R(-8, -8, 1928, 1040), monitor));       // maximised window: taskbar shows
        Assert.False(Quiet.Covers(R(100, 100, 900, 700), monitor));       // ordinary window
        Assert.False(Quiet.Covers(R(1920, 0, 3286, 768), monitor));       // full on the other screen
    }
}
