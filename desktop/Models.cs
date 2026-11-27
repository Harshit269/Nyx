using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace PhoneBridge;

public record DeviceEntry(string Id, string Name);

/// <summary>
/// Gives each app or sender a stable short code and one of eight tag colours, so a
/// row says where it came from before it is read. Known apps get their familiar code;
/// everything else gets letters from its name and a colour from a hash of it.
/// </summary>
public static class AppTag
{
    static readonly Dictionary<string, (string Code, int Hue)> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WhatsApp"] = ("WA", 3), ["WhatsApp Business"] = ("WB", 3), ["Gmail"] = ("GM", 1),
        ["Messages"] = ("SMS", 2), ["Phone"] = ("TEL", 3), ["Calendar"] = ("CAL", 2),
        ["Google Pay"] = ("GPay", 5), ["GPay"] = ("GPay", 5), ["PhonePe"] = ("PPe", 5), ["Paytm"] = ("PTM", 2),
        ["Instagram"] = ("IG", 6), ["Telegram"] = ("TG", 2), ["Signal"] = ("SIG", 2),
        ["Outlook"] = ("OL", 2), ["Teams"] = ("MT", 5), ["Slack"] = ("SL", 5), ["Discord"] = ("DC", 5),
        ["YouTube"] = ("YT", 1), ["Spotify"] = ("SP", 3), ["Swiggy"] = ("SWG", 4), ["Zomato"] = ("ZO", 1),
        ["Amazon"] = ("AMZ", 4), ["Uber"] = ("UB", 0), ["Ola"] = ("OLA", 7), ["X"] = ("X", 0),
        ["LinkedIn"] = ("IN", 2), ["Snapchat"] = ("SC", 7), ["Clock"] = ("CLK", 0), ["Google"] = ("G", 2)
    };

    public const int Hues = 8;

    public static string Code(string name)
    {
        name = name.Trim();
        if (Known.TryGetValue(name, out var k)) return k.Code;
        var words = name.Split(new[] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries)
                        .Where(w => w.Length > 0 && char.IsLetterOrDigit(w[0])).ToArray();
        if (words.Length == 0) return "#";
        if (words[0].StartsWith('+') || char.IsDigit(words[0][0])) return "#";
        if (words.Length == 1) return words[0].Length <= 3 ? words[0].ToUpperInvariant() : char.ToUpperInvariant(words[0][0]) + words[0].Substring(1, 1).ToLowerInvariant();
        return string.Concat(words.Take(2).Select(w => char.ToUpperInvariant(w[0])));
    }

    /// <summary>Index 0..7 into the Tag0..Tag7 brushes of the current theme.</summary>
    public static int Hue(string name)
    {
        name = name.Trim();
        if (Known.TryGetValue(name, out var k)) return k.Hue;
        uint h = 2166136261;
        foreach (char c in name.ToLowerInvariant()) h = (h ^ c) * 16777619;
        return (int)(h % Hues);
    }
}

/// <param name="Detail">For recent people: "Missed call · 2 h ago". Empty for plain contacts.</param>
public record ContactItem(string Name, string Number, string Detail = "")
{
    public string Initial => Theme.Initial(Name);
    /// <summary>The second line: what happened last for recent people, the number otherwise.</summary>
    public string Sub => Detail.Length > 0 ? Detail : Number;
}

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class NavItem : Observable
{
    public string Title { get; init; } = "";
    public Geometry? Icon { get; init; }

    int _count;
    public int Count
    {
        get => _count;
        set
        {
            _count = value;
            Raise();
            Raise(nameof(CountText));
        }
    }

    public string CountText => _count > 99 ? "99+" : _count.ToString();
}

public class NotifItem
{
    public string Id { get; set; } = "";
    public string App { get; set; } = "";
    /// <summary>The phone app's package name (com.whatsapp...), for switching an app off.</summary>
    public string Pkg { get; set; } = "";
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime Time { get; set; }
    /// <summary>The notification has its own "Mark as read" button, which Nyx can press on the phone.</summary>
    public bool CanRead { get; set; }
    public string TimeText => Time.ToString("HH:mm");
    public string Code => AppTag.Code(App);
}

/// <summary>
/// One row of the Overview's Messages tile: an unread text conversation, or a message from
/// a chat app (WhatsApp, Instagram, Telegram…) that's still in the phone's notifications.
/// </summary>
public class MessageRow
{
    public string Display { get; init; } = "";
    public string Last { get; init; } = "";
    public string Initial { get; init; } = "";
    public string Badge { get; init; } = "";
    public DateTime Time { get; init; }
    public Conversation? Conv { get; init; }
    public NotifItem? Notif { get; init; }
    public string TimeText => Time == default ? "" : Time.Date == DateTime.Today ? Time.ToString("HH:mm") : Time.ToString("d MMM");

    static readonly HashSet<string> ChatApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "WhatsApp", "WhatsApp Business", "Instagram", "Telegram", "Messenger", "Signal", "Discord", "Snapchat",
        "Slack", "Teams", "Microsoft Teams", "LINE", "Viber", "WeChat", "Threads", "X", "Truecaller Chat", "Element"
    };

    // the phone's own texting apps: their texts already appear as conversations
    static readonly HashSet<string> TextApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "Messages", "Google Messages", "Message", "Messaging", "SMS"
    };

    /// <summary>A notification from a chat app: it belongs with messages, not other notifications.</summary>
    public static bool IsChat(NotifItem n) => ChatApps.Contains(n.App.Trim());

    /// <summary>A notification from the phone's texting app (shown through the text conversations instead).</summary>
    public static bool IsText(NotifItem n) => TextApps.Contains(n.App.Trim());

    public static MessageRow From(Conversation c) => new()
    {
        Display = c.Display, Last = c.Last, Initial = c.Initial, Badge = c.Unread.ToString(), Time = c.LastTime, Conv = c
    };

    public static MessageRow From(NotifItem n) => new()
    {
        Display = n.Title.Length > 0 ? n.Title : n.App,
        Last = n.Text.Length > 0 ? n.Text : n.App,
        Initial = n.Code, Badge = n.App, Time = n.Time, Notif = n
    };
}

public class SmsItem
{
    public string Address { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTime Time { get; set; }
    public bool Inbox { get; set; }
    /// <summary>Read on the phone; null when the phone didn't say (older Nyx on the phone).</summary>
    public bool? Read { get; set; }

    public string TimeText => Time.Date == DateTime.Today ? Time.ToString("HH:mm") : Time.ToString("d MMM, HH:mm");
}

public class Conversation : Observable
{
    public string Key { get; set; } = "";
    public string Address { get; set; } = "";
    /// <summary>A WhatsApp chat rather than an SMS thread.</summary>
    public bool IsWhatsApp { get; set; }
    public bool IsGroup { get; set; }
    /// <summary>WhatsApp only lets Nyx reply while the chat's notification is on the phone.</summary>
    public bool CanReply { get; set; } = true;
    public List<SmsItem> Items { get; } = new();

    string _display = "";
    public string Display
    {
        get => _display;
        set
        {
            _display = value;
            Raise();
            Raise(nameof(Initial));
        }
    }

    public string Initial => Theme.Initial(_display);

    int _unread;
    public int Unread
    {
        get => _unread;
        set { _unread = value; Raise(); Raise(nameof(HasUnread)); }
    }

    public bool HasUnread => _unread > 0;

    string _last = "";
    public string Last
    {
        get => _last;
        set { _last = value; Raise(); }
    }

    DateTime _lastTime;
    public DateTime LastTime
    {
        get => _lastTime;
        set { _lastTime = value; Raise(); Raise(nameof(TimeText)); }
    }

    public string TimeText =>
        _lastTime == default ? "" :
        _lastTime.Date == DateTime.Today ? _lastTime.ToString("HH:mm") : _lastTime.ToString("d MMM");
}
