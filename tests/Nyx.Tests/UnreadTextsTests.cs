using PhoneBridge;
using Xunit;

namespace Nyx.Tests;

public class UnreadTextsTests
{
    static SmsItem Text(DateTime t, bool? read, bool inbox = true) => new() { Time = t, Read = read, Inbox = inbox, Body = "x" };

    [Fact]
    public void Unread_texts_follow_the_phone_but_only_recent_ones_count()
    {
        var now = new DateTime(2026, 11, 24, 23, 45, 0);
        var items = new[]
        {
            Text(now.AddMinutes(-2), read: false),             // new: counts
            Text(now.AddHours(-5), read: false),               // today: counts
            Text(now.AddDays(-20), read: false),               // old unread bank text: doesn't
            Text(now.AddMinutes(-1), read: true),              // read on the phone
            Text(now.AddMinutes(-3), read: false, inbox: false) // sent by you
        };
        Assert.Equal(2, MainWindow.UnreadIn(items, default, now));
        // opened here at 23:44: only what came after that is unread
        Assert.Equal(1, MainWindow.UnreadIn(items, now.AddMinutes(-4), now));
        Assert.Equal(0, MainWindow.UnreadIn(items, now, now));
    }
}
