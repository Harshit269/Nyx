using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

namespace PhoneBridge;

/// <summary>
/// Lets the phone's Wake reach this laptop while it's asleep: Windows' own "Wake on Magic Packet"
/// switch on the Wi-Fi card. Reading it needs nothing; turning it on needs an administrator, so
/// Windows asks you first (Nyx never changes it by itself).
/// </summary>
public static class WakeSetup
{
    /// <summary>The Wi-Fi adapter's name ("Wi-Fi"), or null. Only plain names are used in commands.</summary>
    public static string? WifiAdapter()
    {
        var ni = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 && n.OperationalStatus == OperationalStatus.Up)
            ?? NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
        return ni != null && SafeName(ni.Name) ? ni.Name : null;
    }

    /// <summary>Letters, digits, spaces and a few separators only: it goes inside a command.</summary>
    public static bool SafeName(string name) => Regex.IsMatch(name, @"^[A-Za-z0-9 _\-()]{1,64}$");

    /// <summary>True / false for Windows' Wake on Magic Packet on this adapter, null when it can't tell.</summary>
    public static async Task<bool?> IsOnAsync(string adapter)
    {
        if (!SafeName(adapter)) return null;
        var r = await Mirror.RunAsync("powershell.exe", TimeSpan.FromSeconds(20), "-NoProfile", "-NonInteractive", "-Command",
            $"(Get-NetAdapterAdvancedProperty -Name '{adapter}' -RegistryKeyword '*WakeOnMagicPacket' -ErrorAction Stop).RegistryValue");
        var v = r.Output.Trim();
        return v == "1" ? true : v == "0" ? false : null;
    }

    /// <summary>
    /// Turns Wake on Magic Packet (and on Pattern, if the card has it) on, after Windows asks you.
    /// The Wi-Fi drops for a few seconds while the card restarts. Null = done; otherwise why not.
    /// </summary>
    public static async Task<string?> TurnOnAsync(string adapter)
    {
        if (!SafeName(adapter)) return "The Wi-Fi card's name looks unusual, so Nyx won't put it in a command.";
        string cmd =
            $"Set-NetAdapterAdvancedProperty -Name '{adapter}' -RegistryKeyword '*WakeOnMagicPacket' -RegistryValue 1 -ErrorAction Stop; " +
            $"Set-NetAdapterAdvancedProperty -Name '{adapter}' -RegistryKeyword '*WakeOnPattern' -RegistryValue 1 -ErrorAction SilentlyContinue";
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"{cmd}\"")
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            if (p == null) return "Windows didn't start the change.";
            await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
            return p.ExitCode == 0 ? null : "Windows couldn't change the Wi-Fi card's setting.";
        }
        catch (System.ComponentModel.Win32Exception) { return "Not changed: Windows' permission question was declined."; }
        catch (TimeoutException) { return "Windows took too long to change the setting."; }
    }
}
