namespace PhoneBridge;

/// <summary>
/// What the top-bar pill shows, from the Android link and the iPhone link together.
/// The Android phone wins while it's linked or linking (it does more); otherwise a connected
/// iPhone shows as the linked phone.
/// </summary>
public static class PhonePill
{
    /// <param name="androidState">off, busy or on (as SetConn uses them)</param>
    /// <returns>the dot state (off/busy/on), the name, and the link kind ("" for none)</returns>
    public static (string State, string Title, string Kind) Pick(
        string androidState, string androidTitle, string androidKind, bool iphoneOn, string iphoneName)
    {
        if (androidState is "on" or "busy" || !iphoneOn)
            return (androidState, androidState switch { "on" => androidTitle, "busy" => "Connecting", _ => "No phone" },
                    androidState == "on" ? androidKind : "");
        return ("on", iphoneName.Trim().Length > 0 ? iphoneName.Trim() : "iPhone", "Bluetooth");
    }
}
