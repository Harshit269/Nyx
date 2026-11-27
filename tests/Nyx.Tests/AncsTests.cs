using System.Text;
using PhoneBridge;
using Xunit;

namespace Nyx.Tests;

/// <summary>The iPhone notification protocol (ANCS), byte for byte against Apple's specification.</summary>
public class AncsTests
{
    [Fact]
    public void Notification_source_event_is_decoded()
    {
        // Added, flags Important|PositiveAction|NegativeAction, category Social, count 3, uid 0x01020304
        var e = Ancs.ParseEvent(new byte[] { 0, 0x1A, 4, 3, 0x04, 0x03, 0x02, 0x01 });
        Assert.NotNull(e);
        Assert.Equal(Ancs.EventId.Added, e!.Value.Id);
        Assert.Equal(Ancs.Category.Social, e.Value.Category);
        Assert.Equal((byte)3, e.Value.CategoryCount);
        Assert.Equal(0x01020304u, e.Value.Uid);
        Assert.True(e.Value.Flags.HasFlag(Ancs.EventFlags.Important));
        Assert.True(e.Value.Flags.HasFlag(Ancs.EventFlags.NegativeAction));
        Assert.False(e.Value.Flags.HasFlag(Ancs.EventFlags.Silent));
    }

    [Fact]
    public void Short_or_unknown_events_are_ignored()
    {
        Assert.Null(Ancs.ParseEvent(new byte[] { 0, 0, 0 }));
        Assert.Null(Ancs.ParseEvent(new byte[] { 9, 0, 0, 0, 1, 0, 0, 0 }));
    }

    [Fact]
    public void Get_notification_attributes_request_matches_the_spec()
    {
        var b = Ancs.GetNotificationAttributes(7);
        Assert.Equal(new byte[]
        {
            0,              // CommandIDGetNotificationAttributes
            7, 0, 0, 0,     // NotificationUID, little-endian
            0,              // AppIdentifier
            1, 128, 0,      // Title, max 128
            2, 64, 0,       // Subtitle, max 64
            3, 0, 2,        // Message, max 512
            5               // Date
        }, b);
    }

    [Fact]
    public void Get_app_attributes_and_actions_match_the_spec()
    {
        Assert.Equal(new byte[] { 1, (byte)'a', (byte)'.', (byte)'b', 0, 0 }, Ancs.GetAppAttributes("a.b"));
        Assert.Equal(new byte[] { 2, 9, 0, 0, 0, 1 }, Ancs.PerformAction(9, positive: false));
        Assert.Equal(new byte[] { 2, 9, 0, 0, 0, 0 }, Ancs.PerformAction(9, positive: true));
    }

    static byte[] Attribute(byte id, string value)
    {
        var v = Encoding.UTF8.GetBytes(value);
        return new[] { id, (byte)(v.Length & 0xFF), (byte)(v.Length >> 8) }.Concat(v).ToArray();
    }

    static byte[] NotificationAnswer(uint uid) =>
        new byte[] { 0 }.Concat(BitConverter.GetBytes(uid))
            .Concat(Attribute(0, "net.whatsapp.WhatsApp"))
            .Concat(Attribute(1, "Mum"))
            .Concat(Attribute(2, ""))
            .Concat(Attribute(3, "Call when you're free 😊"))
            .Concat(Attribute(5, "20261123T142501"))
            .ToArray();

    [Fact]
    public void A_whole_answer_in_one_packet()
    {
        var a = new Ancs.Assembler().Feed(NotificationAnswer(42));
        var n = Assert.IsType<Ancs.NotificationAnswer>(Assert.Single(a));
        Assert.Equal(42u, n.Uid);
        Assert.Equal("Mum", n.Values[Ancs.Attr.Title]);
        Assert.Equal("Call when you're free 😊", n.Values[Ancs.Attr.Message]);
        Assert.Equal("net.whatsapp.WhatsApp", n.Values[Ancs.Attr.AppIdentifier]);
        Assert.Equal(new DateTime(2026, 11, 23, 14, 25, 1), Ancs.ParseDate(n.Values[Ancs.Attr.Date]));
    }

    [Fact]
    public void An_answer_split_over_small_packets_is_reassembled()
    {
        var bytes = NotificationAnswer(5);
        var asm = new Ancs.Assembler();
        var got = new List<Ancs.Answer>();
        for (int i = 0; i < bytes.Length; i += 7) got.AddRange(asm.Feed(bytes.AsSpan(i, Math.Min(7, bytes.Length - i))));
        var n = Assert.IsType<Ancs.NotificationAnswer>(Assert.Single(got));
        Assert.Equal("Mum", n.Values[Ancs.Attr.Title]);
    }

    [Fact]
    public void Two_answers_in_one_packet_both_come_out()
    {
        var both = NotificationAnswer(1).Concat(NotificationAnswer(2)).ToArray();
        var got = new Ancs.Assembler().Feed(both);
        Assert.Equal(2, got.Count);
        Assert.Equal(2u, ((Ancs.NotificationAnswer)got[1]).Uid);
    }

    [Fact]
    public void App_name_answer_is_decoded()
    {
        var bytes = new byte[] { 1 }.Concat(Encoding.UTF8.GetBytes("com.apple.MobileSMS")).Concat(new byte[] { 0 })
            .Concat(Attribute(0, "Messages")).ToArray();
        var app = Assert.IsType<Ancs.AppAnswer>(Assert.Single(new Ancs.Assembler().Feed(bytes)));
        Assert.Equal("com.apple.MobileSMS", app.AppId);
        Assert.Equal("Messages", app.DisplayName);
    }

    [Fact]
    public void Garbage_is_dropped_and_the_next_answer_still_works()
    {
        var asm = new Ancs.Assembler();
        Assert.Empty(asm.Feed(new byte[] { 0x7F, 1, 2, 3 }));          // unknown command
        Assert.Empty(asm.Feed(new byte[] { 0, 1, 0, 0, 0, 0, 0xFF, 0xFF })); // impossible length
        Assert.Single(asm.Feed(NotificationAnswer(3)));
    }

    [Fact]
    public void A_flood_never_grows_without_limit()
    {
        var asm = new Ancs.Assembler();
        // a never-ending app identifier: must be thrown away, not buffered forever
        for (int i = 0; i < 100; i++) asm.Feed(Enumerable.Repeat((byte)'a', 100).Prepend((byte)1).ToArray());
        Assert.Single(asm.Feed(NotificationAnswer(4)));
    }

    [Fact]
    public void Reset_drops_a_half_answer_so_the_next_one_parses()
    {
        var asm = new Ancs.Assembler();
        var whole = NotificationAnswer(8);
        Assert.Empty(asm.Feed(whole.AsSpan(0, 10)));   // the rest never comes (request timed out)
        asm.Reset();
        var n = Assert.IsType<Ancs.NotificationAnswer>(Assert.Single(asm.Feed(NotificationAnswer(9))));
        Assert.Equal(9u, n.Uid);
    }

    [Fact]
    public void Removed_and_modified_events_are_decoded()
    {
        Assert.Equal(Ancs.EventId.Removed, Ancs.ParseEvent(new byte[] { 2, 0, 6, 0, 1, 0, 0, 0 })!.Value.Id);
        var m = Ancs.ParseEvent(new byte[] { 1, 0x04, 1, 1, 0xFF, 0xFF, 0xFF, 0xFF })!.Value;
        Assert.Equal(Ancs.EventId.Modified, m.Id);
        Assert.Equal(Ancs.Category.IncomingCall, m.Category);
        Assert.True(m.Flags.HasFlag(Ancs.EventFlags.PreExisting));
        Assert.Equal(uint.MaxValue, m.Uid);
    }

    [Fact]
    public void Empty_attribute_values_and_utf8_split_mid_character_survive()
    {
        var bytes = NotificationAnswer(11);
        var asm = new Ancs.Assembler();
        var got = new List<Ancs.Answer>();
        foreach (var b in bytes) got.AddRange(asm.Feed(new[] { b }));   // one byte at a time
        var n = Assert.IsType<Ancs.NotificationAnswer>(Assert.Single(got));
        Assert.Equal("", n.Values[Ancs.Attr.Subtitle]);
        Assert.EndsWith("😊", n.Values[Ancs.Attr.Message]);
    }

    [Fact]
    public void Bad_dates_are_rejected() => Assert.Null(Ancs.ParseDate("yesterday"));
}
