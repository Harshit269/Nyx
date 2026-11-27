using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PhoneBridge;

/// <summary>
/// Which phone apps' notifications come to this laptop. Every app is on until you switch it
/// off in Settings. The list goes to the phone too, so a switched-off app's notifications
/// never leave the phone.
/// </summary>
public static class NotifApps
{
    static HashSet<string>? _muted;

    public static HashSet<string> Muted => _muted ??= Parse(AppPrefs.GetText("notifMuted", ""));

    /// <summary>Package names only (letters, digits, dots, underscores), so the list can't carry anything else.</summary>
    public static bool Valid(string pkg) =>
        pkg.Length is >= 1 and <= 200 && pkg.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_');

    public static HashSet<string> Parse(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(Valid).ToHashSet();

    public static bool IsMuted(string pkg) => pkg.Length > 0 && Muted.Contains(pkg);

    public static void Set(string pkg, bool on)
    {
        if (!Valid(pkg)) return;
        if (on) Muted.Remove(pkg); else Muted.Add(pkg);
        AppPrefs.SetText("notifMuted", string.Join(",", Muted.OrderBy(p => p)));
    }
}

/// <summary>One app in Settings > Notifications from apps.</summary>
public sealed class NotifAppRow : INotifyPropertyChanged
{
    bool _on;
    public string Pkg { get; init; } = "";
    public string Name { get; init; } = "";
    public string Code => AppTag.Code(Name);
    public bool On { get => _on; set { if (_on != value) { _on = value; Changed(); } } }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}
