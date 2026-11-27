using Windows.ApplicationModel.Calls;
using Windows.Devices.Enumeration;

namespace PhoneBridge;

/// <summary>
/// Asks Windows to act as a Bluetooth hands-free unit for the phone, so call
/// audio uses the laptop mic and speakers (same API family Phone Link uses).
/// </summary>
public static class HandsFree
{
    static PhoneLineTransportDevice? _device;

    const string NeedsInstall =
        "Windows only hands call audio to an installed app, and this copy of Nyx is running straight from its build folder. " +
        "Turn on Settings > System > For developers > Developer Mode, then run register-package.ps1 in the desktop folder " +
        "and open Nyx from the Start menu. Calls can still be answered and placed from here meanwhile; the sound stays on the phone.";

    /// <summary>True when Nyx runs as an installed (packaged) app, which call audio requires.</summary>
    public static bool IsInstalledApp
    {
        get
        {
            try { return Windows.ApplicationModel.Package.Current != null; }
            catch { return false; } // no package identity
        }
    }

    /// <summary>Phone Link keeps running in the background and holds the hands-free link.</summary>
    public static bool PhoneLinkRunning =>
        System.Diagnostics.Process.GetProcessesByName("PhoneExperienceHost").Length > 0;

    /// <summary>
    /// Windows 11's own "Mobile devices" feature (CrossDeviceService) with Calls on for a phone.
    /// It claims the phone's hands-free link the same way Phone Link does, without Phone Link running.
    /// </summary>
    public static bool WindowsMobileDevicesTakesCalls
    {
        get
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Mobility");
                if (key == null || Convert.ToInt32(key.GetValue("CrossDeviceEnabled", 0)) != 1) return false;
                return key.GetValueNames().Any(n => n.StartsWith("CallsEnabled_") && Convert.ToInt32(key.GetValue(n, 0)) == 1);
            }
            catch { return false; }
        }
    }

    const string MobileDevicesHint =
        "Windows' own Mobile devices feature is taking your phone's calls, so Windows turns Nyx away. " +
        "Open Settings > Bluetooth & devices > Mobile devices and turn off \"Allow this PC to access your mobile devices\" " +
        "(or open Manage devices and turn off Calls for your phone). Then press Use this laptop again.";

    /// <summary>Whether the phone has a classic Bluetooth connection to this laptop; null when unknown.</summary>
    static async Task<bool?> BluetoothConnected(string? mac)
    {
        if (mac == null || mac.Length != 12) return null;
        try
        {
            var bt = await Windows.Devices.Bluetooth.BluetoothDevice.FromBluetoothAddressAsync(Convert.ToUInt64(mac, 16));
            if (bt == null) return null;
            using (bt) return bt.ConnectionStatus == Windows.Devices.Bluetooth.BluetoothConnectionStatus.Connected;
        }
        catch { return null; }
    }

    static string Norm(string s) => new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>The 12 hex digits of a Bluetooth address found in a Windows device id, if any.</summary>
    public static string? MacOf(string deviceId)
    {
        // drop the {GUID}s first: their last block is 12 hex digits too and would pass for an address
        string id = System.Text.RegularExpressions.Regex.Replace(deviceId, @"\{[0-9a-fA-F-]{36}\}", "#");
        var m = System.Text.RegularExpressions.Regex.Matches(id, "([0-9a-fA-F]{2}[:_]?){5}[0-9a-fA-F]{2}");
        return m.Count == 0 ? null : new string(m[^1].Value.Where(char.IsAsciiHexDigit).ToArray()).ToLowerInvariant();
    }

    /// <summary>
    /// Picks the hands-free device that is this phone: by Bluetooth address when we know it,
    /// then by name (Windows may call it "Nothing Phone (3)" while it's "Sam's Nothing
    /// Phone (3)" elsewhere). Never guesses between several phones.
    /// </summary>
    public static DeviceInformation? Pick(IReadOnlyList<DeviceInformation> infos, IEnumerable<string> names, IEnumerable<string> btIds)
    {
        var macs = btIds.Select(MacOf).Where(m => m != null).ToHashSet();
        var byMac = infos.FirstOrDefault(i => macs.Any(m => Norm(i.Id).Contains(m!)));
        if (byMac != null) return byMac;
        var wanted = names.Select(Norm).Where(n => n.Length > 0).ToList();
        var exact = infos.FirstOrDefault(i => wanted.Contains(Norm(i.Name)));
        if (exact != null) return exact;
        var close = infos.Where(i => Norm(i.Name).Length > 0 && wanted.Any(w => w.Contains(Norm(i.Name)) || Norm(i.Name).Contains(w))).ToList();
        if (close.Count == 1) return close[0];
        return infos.Count == 1 ? infos[0] : null;
    }

    /// <returns>A status line for the UI.</returns>
    public static async Task<string> ConnectAsync(IEnumerable<string> names, IEnumerable<string> btIds)
    {
        if (!IsInstalledApp) return NeedsInstall;
        try
        {
            string selector = PhoneLineTransportDevice.GetDeviceSelector(PhoneLineTransport.Bluetooth);
            var infos = await DeviceInformation.FindAllAsync(selector);
            if (infos.Count == 0)
                return "Windows does not see a phone that offers hands-free audio. Pair the phone again and allow calls during pairing.";
            var info = Pick(infos, names, btIds);
            if (info == null)
                return "Windows knows several phones for call audio (" + string.Join(", ", infos.Select(i => i.Name)) +
                       ") and Nyx couldn't tell which is yours. Connect over Bluetooth once, then try again.";

            var device = PhoneLineTransportDevice.FromId(info.Id);
            if (device == null)
                return "Windows could not open the phone's hands-free link. Turn Bluetooth off and on, then try again.";

            var access = await device.RequestAccessAsync();
            if (access == DeviceAccessStatus.DeniedByUser)
                return "Call audio is switched off for Nyx. Turn it on in Settings > Privacy & security > Calls > Let apps make phone calls > Nyx.";
            if (access == DeviceAccessStatus.DeniedBySystem)
                return MobileDevicesHint;
            if (access != DeviceAccessStatus.Allowed)
                return "Windows refused the phone line (" + access + "). Close Microsoft Phone Link (it holds the same link), then try again.";

            if (!device.IsRegistered())
                device.RegisterApp();

            // The phone often opens its own calls channel to the laptop at the same moment (when
            // Bluetooth reconnects, or when it's tapped in the phone's Bluetooth list). Both ends
            // then collide and both attempts die. Once the phone's attempt is over, a retry gets through.
            bool ok = false;
            for (int attempt = 0; attempt < 4 && !ok; attempt++)
            {
                if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(3 + 2 * attempt));
                ok = await device.ConnectAsync();
                Diag.Log("call audio: attempt " + (attempt + 1) + (ok ? " connected" : " refused"));
            }
            _device = ok ? device : null;
            if (ok) return "On this laptop: calls with " + info.Name + " now use its mic and speakers.";
            if (PhoneLinkRunning)
                return "Microsoft Phone Link is running in the background and holding your phone's call audio. Quit it (Phone Link > Settings, or end \"Phone Link\" in Task Manager), then press Use this laptop again.";
            if (WindowsMobileDevicesTakesCalls)
                return MobileDevicesHint;
            // call audio rides on the classic Bluetooth connection, which is separate from Nyx's
            // own link (often Wi-Fi). If the phone isn't connected that way, say exactly that.
            if (await BluetoothConnected(MacOf(info.Id)) == false)
                return "The laptop can't reach your phone over Bluetooth right now. It's paired but not connected (Nyx's own link can be Wi-Fi, but call audio needs Bluetooth). " +
                       "Turn this laptop's Bluetooth off and on (Action Center), then on the phone open Bluetooth and tap " + Environment.MachineName +
                       ". When Windows Settings > Bluetooth & devices shows the phone as Connected, press Use this laptop again.";
            return "The phone is connected over Bluetooth but refused call audio. On the phone: Bluetooth > the gear next to " + Environment.MachineName +
                   " > switch on \"Phone calls\". Then press Use this laptop again.";
        }
        catch (UnauthorizedAccessException)
        {
            return NeedsInstall;
        }
        catch (Exception ex)
        {
            return "Still on the phone. Windows said: " + ex.Message;
        }
    }
}
