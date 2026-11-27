using System.Runtime.InteropServices;

namespace PhoneBridge;

/// <summary>
/// Catches the second click of a double-click on Nyx's icon in Windows 11's hidden-icons
/// menu. That menu closes on the first click, so the icon itself never gets the second one.
/// For the double-click time only, a low-level mouse hook watches for one more left click
/// near the first; that click is swallowed (it would land on whatever was under the menu)
/// and counts as the double-click. Nothing else is looked at, and the hook is gone right after.
/// </summary>
public static class SecondClick
{
    static IntPtr _hook = IntPtr.Zero;
    static HookProc? _proc;           // kept alive while the hook is set
    static Action? _onDouble;
    static Action? _pending; // the double-click, run when the button comes up

    /// <summary>A double-click was caught and is waiting for the button to come up.</summary>
    public static bool Caught => _pending != null;
    static POINT _first;
    static long _until;
    static bool _swallowUp;
    static System.Windows.Threading.DispatcherTimer? _timer;

    /// <summary>Starts listening (on the UI thread) for a second click within the double-click time.</summary>
    public static bool Arm(Action onDouble)
    {
        Disarm();
        // the hook reports real pixels, so the first point must be in real pixels too
        if (!GetPhysicalCursorPos(out _first)) return false;
        int ms = System.Windows.Forms.SystemInformation.DoubleClickTime;
        _until = Environment.TickCount64 + ms;
        _onDouble = onDouble;
        _proc = Hook;
        _hook = SetWindowsHookEx(14 /* WH_MOUSE_LL */, _proc, GetModuleHandle(null), 0);
        _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms + 300) };
        _timer.Tick += (_, _) => Disarm();
        _timer.Start();
        return _hook != IntPtr.Zero;
    }

    /// <summary>Within the double-click distance Windows uses (a little wider: the menu is closing under it).</summary>
    public static bool Near(int x1, int y1, int x2, int y2, int cx, int cy) =>
        Math.Abs(x1 - x2) <= cx * 2 && Math.Abs(y1 - y2) <= cy * 2;

    // Windows calls this directly: an error escaping it would end Nyx on the spot (no catch can
    // stop that), so everything inside is guarded and the click simply passes on.
    static IntPtr Hook(int code, IntPtr wParam, IntPtr lParam)
    {
        try { return HookBody(code, wParam, lParam); }
        catch { return CallNextHookEx(_hook, code, wParam, lParam); }
    }

    static IntPtr HookBody(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int msg = wParam.ToInt32();
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if (msg == 0x0201 /* WM_LBUTTONDOWN */ && Environment.TickCount64 <= _until && _onDouble != null &&
                Near(info.pt.X, info.pt.Y, _first.X, _first.Y,
                     System.Windows.Forms.SystemInformation.DoubleClickSize.Width,
                     System.Windows.Forms.SystemInformation.DoubleClickSize.Height))
            {
                _pending = _onDouble;
                _onDouble = null;
                _swallowUp = true;
                Diag.Log("tray: double-click caught");
                return new IntPtr(1); // swallow: it was meant for Nyx's icon
            }
            if (msg == 0x0202 /* WM_LBUTTONUP */ && _swallowUp)
            {
                // act once the button is up: Windows won't switch windows while it's held
                _swallowUp = false;
                var act = _pending;
                _pending = null;
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => { Disarm(); act?.Invoke(); });
                return new IntPtr(1);
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    static void Disarm()
    {
        _timer?.Stop();
        _timer = null;
        if (_swallowUp) return; // wait for the matching button-up first
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _onDouble = null;
    }

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr extra; }
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] static extern bool GetPhysicalCursorPos(out POINT p);
}
