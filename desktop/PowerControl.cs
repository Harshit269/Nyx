using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace PhoneBridge;

/// <summary>
/// What the phone may do to this laptop: lock, sleep, restart, shut down (restart and
/// shut down wait 15 seconds so they can be cancelled), and read the power log.
/// </summary>
public static class PowerControl
{
    public const int GraceSeconds = 15;

    [DllImport("user32.dll")] static extern bool LockWorkStation();
    [DllImport("powrprof.dll")] static extern bool SetSuspendState(bool hibernate, bool force, bool disableWake);

    /// <returns>A short message for the phone, or null when the action is unknown.</returns>
    public static string? Run(string action)
    {
        switch (action)
        {
            case "lock":
                return LockWorkStation() ? "Laptop locked." : "Windows would not lock.";
            case "sleep":
                // run after we reply, so the phone hears back before the Wi-Fi drops
                _ = Task.Delay(1200).ContinueWith(_ => SetSuspendState(false, false, false));
                return "Laptop is going to sleep.";
            case "restart":
                return Shutdown("/r") ? $"Restarting in {GraceSeconds} seconds." : "Windows refused to restart.";
            case "shutdown":
                return Shutdown("/s") ? $"Shutting down in {GraceSeconds} seconds." : "Windows refused to shut down.";
            case "cancel":
                return Exec("shutdown", "/a") ? "Cancelled." : "Nothing to cancel.";
            default:
                return null;
        }
    }

    static bool Shutdown(string mode) =>
        Exec("shutdown", $"{mode} /t {GraceSeconds} /c \"Requested from your phone with Nyx.\"");

    static bool Exec(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true });
            p?.WaitForExit(5000);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Recent power events from Windows' System log, newest first:
    /// on (boot), off (shut down), sleep, wake, crash (lost power or froze).
    /// Covers classic sleep and Modern Standby.
    /// </summary>
    public static JsonArray ReadLog(int max = 60)
    {
        const string query =
            "*[System[(Provider[@Name='Microsoft-Windows-Kernel-General'] and (EventID=12 or EventID=13)) or " +
            "(Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=41 or EventID=42 or EventID=107 or EventID=506 or EventID=507)) or " +
            "(Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1)]]";
        var items = new JsonArray();
        try
        {
            using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, query) { ReverseDirection = true });
            string? last = null;
            for (EventRecord? r = reader.ReadEvent(); r != null && items.Count < max; r = reader.ReadEvent())
            {
                using (r)
                {
                    string kind = (r.ProviderName, r.Id) switch
                    {
                        ("Microsoft-Windows-Kernel-General", 12) => "on",
                        ("Microsoft-Windows-Kernel-General", 13) => "off",
                        ("Microsoft-Windows-Kernel-Power", 41) => "crash",
                        ("Microsoft-Windows-Kernel-Power", 42) or ("Microsoft-Windows-Kernel-Power", 506) => "sleep",
                        _ => "wake"
                    };
                    if (r.TimeCreated is not DateTime t) continue;
                    // Windows logs several events for one wake; keep one per minute per kind
                    string stamp = kind + t.ToString("yyyyMMddHHmm");
                    if (stamp == last) continue;
                    last = stamp;
                    items.Add(new JsonObject
                    {
                        ["kind"] = kind,
                        ["time"] = new DateTimeOffset(t).ToUnixTimeMilliseconds()
                    });
                }
            }
        }
        catch { }
        return items;
    }
}
