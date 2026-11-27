using System.Globalization;
using System.Text;

namespace PhoneBridge;

/// <summary>
/// Apple Notification Center Service (ANCS): the Bluetooth LE service an iPhone uses to share its
/// notifications with a paired accessory, here the laptop. This file is only the byte format,
/// following Apple's published specification, so it can be tested without an iPhone.
///
/// Flow: the iPhone sends an 8-byte event on Notification Source for each notification added,
/// changed or removed. For a new one we write a "get attributes" request to the Control Point,
/// and the answer arrives on Data Source, possibly split over several packets.
/// </summary>
public static class Ancs
{
    public static readonly Guid Service = new("7905F431-B5CE-4E99-A40F-4B1E122D00D0");
    public static readonly Guid NotificationSource = new("9FBF120D-6301-42D9-8C58-25E699A21DBD");
    public static readonly Guid ControlPoint = new("69D1D8F3-45E1-49A8-9821-9BBDFDAAD9D9");
    public static readonly Guid DataSource = new("22EAC6E9-24D6-4BB5-BE44-B36ACE7C7BFB");

    public enum EventId : byte { Added = 0, Modified = 1, Removed = 2 }

    [Flags]
    public enum EventFlags : byte { None = 0, Silent = 1, Important = 2, PreExisting = 4, PositiveAction = 8, NegativeAction = 16 }

    public enum Category : byte
    {
        Other = 0, IncomingCall = 1, MissedCall = 2, Voicemail = 3, Social = 4, Schedule = 5, Email = 6,
        News = 7, HealthAndFitness = 8, BusinessAndFinance = 9, Location = 10, Entertainment = 11
    }

    /// <summary>Notification attribute ids (Apple's numbering).</summary>
    public enum Attr : byte { AppIdentifier = 0, Title = 1, Subtitle = 2, Message = 3, MessageSize = 4, Date = 5 }

    public readonly record struct Event(EventId Id, EventFlags Flags, Category Category, byte CategoryCount, uint Uid);

    /// <summary>One 8-byte Notification Source packet, or null if it isn't one.</summary>
    public static Event? ParseEvent(ReadOnlySpan<byte> b)
    {
        if (b.Length < 8 || b[0] > 2) return null;
        return new Event((EventId)b[0], (EventFlags)b[1], (Category)b[2], b[3], ReadUInt32(b[4..]));
    }

    /// <summary>The attributes we ask for, in this order, with the longest text we take for each.</summary>
    static readonly (Attr Id, ushort Max)[] Wanted =
    {
        (Attr.AppIdentifier, 0), (Attr.Title, 128), (Attr.Subtitle, 64), (Attr.Message, 512), (Attr.Date, 0)
    };

    /// <summary>Control Point command 0: GetNotificationAttributes.</summary>
    public static byte[] GetNotificationAttributes(uint uid)
    {
        var b = new List<byte> { 0 };
        b.AddRange(BitConverter.GetBytes(uid)); // little-endian on Windows
        foreach (var (id, max) in Wanted)
        {
            b.Add((byte)id);
            if (id is Attr.Title or Attr.Subtitle or Attr.Message) b.AddRange(BitConverter.GetBytes(max));
        }
        return b.ToArray();
    }

    /// <summary>Control Point command 1: GetAppAttributes (the app's display name).</summary>
    public static byte[] GetAppAttributes(string appId)
    {
        var b = new List<byte> { 1 };
        b.AddRange(Encoding.UTF8.GetBytes(appId));
        b.Add(0);   // the identifier ends with a zero byte
        b.Add(0);   // attribute 0: DisplayName
        return b.ToArray();
    }

    /// <summary>Control Point command 2: PerformNotificationAction (positive, or negative = dismiss).</summary>
    public static byte[] PerformAction(uint uid, bool positive)
    {
        var b = new List<byte> { 2 };
        b.AddRange(BitConverter.GetBytes(uid));
        b.Add(positive ? (byte)0 : (byte)1);
        return b.ToArray();
    }

    /// <summary>ANCS dates look like 20261123T142501.</summary>
    public static DateTime? ParseDate(string s) =>
        DateTime.TryParseExact(s, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d) ? d : null;

    public static string CategoryName(Category c) => c switch
    {
        Category.IncomingCall => "Incoming call",
        Category.MissedCall => "Missed call",
        Category.Voicemail => "Voicemail",
        Category.Social => "Social",
        Category.Schedule => "Calendar",
        Category.Email => "Email",
        Category.News => "News",
        Category.HealthAndFitness => "Health",
        Category.BusinessAndFinance => "Finance",
        Category.Location => "Location",
        Category.Entertainment => "Entertainment",
        _ => "iPhone"
    };

    static uint ReadUInt32(ReadOnlySpan<byte> b) => (uint)(b[0] | b[1] << 8 | b[2] << 16 | b[3] << 24);

    // ------------------------------------------------------------------ answers

    /// <summary>A complete answer from Data Source.</summary>
    public abstract record Answer;
    public sealed record NotificationAnswer(uint Uid, IReadOnlyDictionary<Attr, string> Values) : Answer;
    public sealed record AppAnswer(string AppId, string DisplayName) : Answer;

    /// <summary>
    /// Data Source answers can be split across packets: collect bytes until a whole answer is
    /// there. Anything malformed or oversized is dropped, never trusted.
    /// </summary>
    public sealed class Assembler
    {
        const int MaxBuffer = 4096;
        readonly List<byte> _buf = new();

        /// <summary>Throws away a half-received answer (its request timed out).</summary>
        public void Reset() => _buf.Clear();

        /// <returns>Every answer completed by this packet (usually none or one).</returns>
        public List<Answer> Feed(ReadOnlySpan<byte> packet)
        {
            var done = new List<Answer>();
            _buf.AddRange(packet.ToArray());
            if (_buf.Count > MaxBuffer) { _buf.Clear(); return done; }
            while (_buf.Count > 0)
            {
                var (answer, used) = TryParse(_buf);
                if (used < 0) { _buf.Clear(); break; }   // garbage: start over
                if (answer == null) break;               // wait for more bytes
                done.Add(answer);
                _buf.RemoveRange(0, used);
            }
            return done;
        }

        /// <returns>(answer, bytes used); (null, 0) = incomplete; (null, -1) = malformed.</returns>
        static (Answer?, int) TryParse(List<byte> b)
        {
            int i = 0;
            if (b[0] == 0)
            {
                if (b.Count < 5) return (null, 0);
                uint uid = (uint)(b[1] | b[2] << 8 | b[3] << 16 | b[4] << 24);
                i = 5;
                var values = new Dictionary<Attr, string>();
                foreach (var _ in Wanted)
                {
                    if (b.Count < i + 3) return (null, 0);
                    byte id = b[i];
                    int len = b[i + 1] | b[i + 2] << 8;
                    if (id > 7 || len > 1024) return (null, -1);
                    if (b.Count < i + 3 + len) return (null, 0);
                    values[(Attr)id] = Encoding.UTF8.GetString(b.GetRange(i + 3, len).ToArray());
                    i += 3 + len;
                }
                return (new NotificationAnswer(uid, values), i);
            }
            if (b[0] == 1)
            {
                int zero = b.IndexOf(0, 1);
                if (zero < 0) return b.Count > 256 ? (null, -1) : (null, 0);
                string appId = Encoding.UTF8.GetString(b.GetRange(1, zero - 1).ToArray());
                i = zero + 1;
                if (b.Count < i + 3) return (null, 0);
                int len = b[i + 1] | b[i + 2] << 8;
                if (len > 1024) return (null, -1);
                if (b.Count < i + 3 + len) return (null, 0);
                string name = Encoding.UTF8.GetString(b.GetRange(i + 3, len).ToArray());
                return (new AppAnswer(appId, name), i + 3 + len);
            }
            return (null, -1);
        }
    }
}
