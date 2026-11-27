using System.Runtime.InteropServices;

namespace PhoneBridge;

/// <summary>
/// When to hold back pop-ups: a game or another full-screen app is in front (or Windows is
/// in presentation mode). Notifications still land in Nyx's list; only the pop-up is skipped.
/// </summary>
public static class Quiet
{
    /// <summary>True while a game or full-screen app is in front.</summary>
    public static bool FullScreenAppInFront()
    {
        try
        {
            // what Windows itself uses to hold its notifications back
            if (SHQueryUserNotificationState(out int state) == 0 && state is 2 or 3 or 4)
                return true; // QUNS_BUSY, QUNS_RUNNING_D3D_FULL_SCREEN, QUNS_PRESENTATION_MODE

            // borderless "full-screen windowed" games aren't always counted: a window in front
            // that covers its whole monitor is treated the same (but not the desktop itself)
            var h = GetForegroundWindow();
            if (h == IntPtr.Zero || h == GetShellWindow() || h == GetDesktopWindow()) return false;
            var cls = new System.Text.StringBuilder(64);
            GetClassName(h, cls, cls.Capacity);
            if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;
            if (!GetWindowRect(h, out var r)) return false;
            var mon = MonitorFromWindow(h, 2 /* MONITOR_DEFAULTTONEAREST */);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(mon, ref mi)) return false;
            return Covers(r, mi.rcMonitor);
        }
        catch { return false; }
    }

    /// <summary>A window rectangle that covers the whole monitor (or more).</summary>
    public static bool Covers(RECT window, RECT monitor) =>
        window.Left <= monitor.Left && window.Top <= monitor.Top &&
        window.Right >= monitor.Right && window.Bottom >= monitor.Bottom;

    /// <summary>
    /// True when the app a notification came from is the window in front (the WhatsApp app,
    /// or WhatsApp Web in a browser): its pop-up would only repeat what's on screen.
    /// </summary>
    public static bool AppInFront(string app)
    {
        try
        {
            string want = Squash(app);
            if (want.Length < 3) return false;
            var h = GetForegroundWindow();
            if (h == IntPtr.Zero) return false;
            var title = new System.Text.StringBuilder(256);
            GetWindowText(h, title, title.Capacity);
            GetWindowThreadProcessId(h, out uint pid);
            string proc = "";
            try { using var p = System.Diagnostics.Process.GetProcessById((int)pid); proc = p.ProcessName; } catch { }
            return Matches(want, title.ToString(), proc);
        }
        catch { return false; }
    }

    /// <summary>The app's name in the window title or the program's name ("WhatsApp" in "WhatsApp.Root").</summary>
    public static bool Matches(string app, string windowTitle, string process)
    {
        string want = Squash(app);
        if (want.Length < 3) return false;
        return Squash(process).Contains(want) || Squash(windowTitle).Contains(want);
    }

    static string Squash(string s) => new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int max);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    /// <summary>Whether a pop-up may show now (Settings can allow them during games too).</summary>
    public static bool PopUpsAllowed() =>
        AppPrefs.Get("popupsInGames", false) || !FullScreenAppInFront();

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }

    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int max);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
}
