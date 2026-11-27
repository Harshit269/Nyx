using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace PhoneBridge;

/// <summary>
/// The phone as a second screen. Windows extends the desktop onto an extra display (a
/// virtual one, from a virtual display driver); Nyx captures that display, compresses each
/// frame to JPEG, encrypts it and streams it to the phone over Wi-Fi. Taps and drags on the
/// phone come back over Nyx's link as mouse input on that display.
///
/// Stream format, per frame: 4-byte big-endian length, then a 12-byte nonce and the
/// AES-256-GCM ciphertext with its 16-byte tag. The key is fresh for every session and only
/// ever travels inside Nyx's own encrypted link.
/// </summary>
public sealed class SecondScreen : IDisposable
{
    public const int MaxWidth = 1920;          // wider frames cost bandwidth the phone can't show anyway
    const int FrameMs = 50;                    // about 20 frames a second
    const long JpegQuality = 65;

    readonly byte[] _key;
    readonly CancellationTokenSource _stop = new();
    Rectangle _bounds;                         // the display, in physical pixels
    long _counter;

    public event Action<string>? Status;
    public event Action? Ended;
    public bool Running { get; private set; }
    public byte[] Key => _key;

    public SecondScreen() => _key = RandomNumberGenerator.GetBytes(32);

    // ------------------------------------------------------------ displays

    public sealed record Display(string Name, Rectangle Bounds, string Device = "");

    /// <summary>The display to send: the first one that isn't the main screen, in physical pixels.</summary>
    public static Display? FindTarget()
    {
        using var _ = PerMonitorDpi();
        var s = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(x => !x.Primary);
        return s == null ? null : new Display(s.DeviceName.Replace(@"\\.\", ""), s.Bounds, s.DeviceName);
    }

    /// <summary>
    /// The size to give the extra display, from the sizes it offers: wide like the phone, and
    /// no bigger than 1366 across so text stays readable on a phone. Null to leave it be
    /// (it's already a sensible size, or nothing suits).
    /// </summary>
    public static (int W, int H)? PickSize(int currentW, int currentH, IEnumerable<(int W, int H)> offered)
    {
        bool sensible = currentW >= 1280 && currentW > currentH && (double)currentW / currentH >= 1.5;
        if (sensible) return null;
        var wide = offered.Where(m => m.W > m.H && (double)m.W / m.H >= 1.5).Distinct().ToList();
        if (wide.Count == 0) return null;
        var fits = wide.Where(m => m.W <= 1366).OrderByDescending(m => m.W).ToList();
        return fits.Count > 0 ? fits[0] : wide.OrderBy(m => m.W).First();
    }

    /// <summary>The sizes a display offers.</summary>
    public static List<(int W, int H)> SizesOf(string device)
    {
        var list = new List<(int, int)>();
        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        for (int i = 0; EnumDisplaySettings(device, i, ref dm); i++) list.Add((dm.dmPelsWidth, dm.dmPelsHeight));
        return list.Distinct().ToList();
    }

    /// <summary>
    /// Gives the extra display a phone-friendly size if it has an odd one (a virtual display
    /// starts at 800×600). Only ever that display; the laptop's own screen is never touched.
    /// </summary>
    public static Display? PrepareTarget()
    {
        var t = ExtendAndFind();
        if (t == null || t.Device.Length == 0) return t;
        var size = PickSize(t.Bounds.Width, t.Bounds.Height, SizesOf(t.Device));
        if (size == null) return t;
        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        if (!EnumDisplaySettings(t.Device, -1, ref dm)) return t; // ENUM_CURRENT_SETTINGS
        dm.dmPelsWidth = size.Value.W;
        dm.dmPelsHeight = size.Value.H;
        dm.dmFields = 0x80000 | 0x100000; // DM_PELSWIDTH | DM_PELSHEIGHT
        int r = ChangeDisplaySettingsEx(t.Device, ref dm, IntPtr.Zero, 0x01, IntPtr.Zero); // CDS_UPDATEREGISTRY
        if (r != 0) return t;
        Thread.Sleep(400); // Windows moves things around; let it settle
        return FindTarget() ?? t;
    }

    // ------------------------------------------------------------ streaming

    /// <summary>Connects to the phone's viewer and streams until stopped or the phone goes away.</summary>
    public async Task RunAsync(string phoneIp, int port, Display target)
    {
        _bounds = target.Bounds;
        Running = true;
        try
        {
            using var tcp = new TcpClient { NoDelay = true, SendBufferSize = 1 << 20 };
            _tcp = tcp;
            using (var t = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
                await tcp.ConnectAsync(phoneIp, port, t.Token);
            var net = tcp.GetStream();
            Status?.Invoke("On your phone: " + target.Name + ", " + target.Bounds.Width + "×" + target.Bounds.Height);

            // capture on a thread of its own (no awaits inside), aware of each monitor's real pixels
            await Task.Factory.StartNew(() => Pump(net), _stop.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            Status?.Invoke(_stop.IsCancellationRequested ? "Stopped." : "The phone closed the second screen.");
        }
        catch (Exception ex)
        {
            Status?.Invoke("Second screen stopped: " + ex.Message);
        }
        finally
        {
            Running = false;
            Ended?.Invoke();
        }
    }

    void Pump(NetworkStream net)
    {
        using var _ = PerMonitorDpi();
        var jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var opts = new EncoderParameters(1);
        opts.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, JpegQuality);
        byte[]? last = null;
        var lastSent = DateTime.MinValue;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        while (!_stop.IsCancellationRequested)
        {
            long started = clock.ElapsedMilliseconds;
            var b = _bounds;
            byte[] frame;
            using (var shot = new Bitmap(b.Width, b.Height, PixelFormat.Format24bppRgb))
            {
                using (var g = Graphics.FromImage(shot))
                {
                    g.CopyFromScreen(b.Left, b.Top, 0, 0, b.Size, CopyPixelOperation.SourceCopy);
                    DrawCursor(g, b);
                }
                using var ms = new MemoryStream();
                if (b.Width > MaxWidth)
                {
                    int h = (int)Math.Round(b.Height * (double)MaxWidth / b.Width);
                    using var small = new Bitmap(shot, MaxWidth, h);
                    small.Save(ms, jpeg, opts);
                }
                else shot.Save(ms, jpeg, opts);
                frame = ms.ToArray();
            }

            // an unchanged screen isn't sent again, except now and then so the phone knows we're here
            bool same = last != null && frame.AsSpan().SequenceEqual(last);
            if (!same || DateTime.UtcNow - lastSent > TimeSpan.FromSeconds(2))
            {
                byte[] sealedFrame = Seal(_key, Interlocked.Increment(ref _counter), frame);
                var len = new byte[4];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(len, sealedFrame.Length);
                net.Write(len);
                net.Write(sealedFrame);
                last = frame;
                lastSent = DateTime.UtcNow;
            }

            int wait = FrameMs - (int)(clock.ElapsedMilliseconds - started);
            if (wait > 0) _stop.Token.WaitHandle.WaitOne(wait);
        }
    }

    /// <summary>nonce (4 zero bytes + 8-byte counter) ‖ ciphertext ‖ tag. A counter never repeats under one key.</summary>
    public static byte[] Seal(byte[] key, long counter, byte[] plain)
    {
        var nonce = new byte[12];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(nonce.AsSpan(4), counter);
        var output = new byte[12 + plain.Length + 16];
        nonce.CopyTo(output, 0);
        using var gcm = new AesGcm(key, 16);
        gcm.Encrypt(nonce, plain, output.AsSpan(12, plain.Length), output.AsSpan(12 + plain.Length, 16));
        return output;
    }

    TcpClient? _tcp;

    public void Stop()
    {
        try { LiftAll(); } catch { }
        if (!_stop.IsCancellationRequested) _stop.Cancel();
        try { _tcp?.Close(); } catch { } // unblocks a write the phone stopped reading
        Running = false;
    }

    public void Dispose() => Stop();

    // ------------------------------------------------------------ touch

    /// <summary>A point given as 0..1 across the phone's picture, as a pixel on the display.</summary>
    public static Point Map(Rectangle bounds, double x, double y)
    {
        x = double.IsFinite(x) ? Math.Clamp(x, 0, 1) : 0.5;
        y = double.IsFinite(y) ? Math.Clamp(y, 0, 1) : 0.5;
        return new Point(bounds.Left + (int)Math.Round(x * (bounds.Width - 1)),
                         bounds.Top + (int)Math.Round(y * (bounds.Height - 1)));
    }

    /// <summary>
    /// A touch from the phone: "down"/"move"/"up" (a drag), "click", "right" (long press)
    /// or "scroll" (two fingers; <paramref name="amount"/> is up/down in screen heights).
    /// Only while a session runs, and only ever on the extra display.
    /// </summary>
    public void Touch(string action, double x, double y, double amount)
    {
        if (!Running) return;
        using var _ = PerMonitorDpi();
        var p = Map(_bounds, x, y);
        // Windows has one mouse pointer. A touch borrows it for a moment, then it goes back to
        // where it was on the laptop, so the laptop stays usable while the phone is in use.
        if (_home == null && action is "down" or "click" or "right" or "scroll" or "move")
            _home = GetCursorPos(out var was) ? new Point(was.X, was.Y) : null;
        switch (action)
        {
            case "move": SetCursorPos(p.X, p.Y); break;
            case "down": SetCursorPos(p.X, p.Y); Mouse(MOUSEEVENTF_LEFTDOWN); break;
            case "up": SetCursorPos(p.X, p.Y); Mouse(MOUSEEVENTF_LEFTUP); GoHome(); break;
            case "click": SetCursorPos(p.X, p.Y); Mouse(MOUSEEVENTF_LEFTDOWN); Mouse(MOUSEEVENTF_LEFTUP); GoHome(); break;
            case "right": SetCursorPos(p.X, p.Y); Mouse(MOUSEEVENTF_RIGHTDOWN); Mouse(MOUSEEVENTF_RIGHTUP); GoHome(); break;
            case "scroll":
                SetCursorPos(p.X, p.Y);
                if (double.IsFinite(amount))
                    Mouse(MOUSEEVENTF_WHEEL, (int)Math.Clamp(amount * 1200, -2400, 2400));
                GoHome();
                break;
        }
    }

    Point? _home;

    // ------------------------------------------------------------ real touch

    static int _touchReady; // 0 unknown, 1 yes, -1 no
    readonly HashSet<int> _down = new();

    /// <summary>Whether Windows accepts touch input from Nyx (it does from Windows 8 on, without a touchscreen).</summary>
    static bool TouchReady()
    {
        if (_touchReady == 0)
        {
            try { _touchReady = InitializeTouchInjection(10, 1 /* TOUCH_FEEDBACK_DEFAULT */) ? 1 : -1; }
            catch { _touchReady = -1; }
        }
        return _touchReady == 1;
    }

    /// <summary>
    /// The phone's fingers as real touch on the extra display, so it behaves like a
    /// touchscreen: swipe scrolls, pinch zooms, long-press right-clicks, and the laptop's own
    /// mouse pointer never moves. <paramref name="action"/> is "down"/"up" for finger
    /// <paramref name="changed"/>, "move", or "cancel"; <paramref name="fingers"/> lists every
    /// finger on the glass as (id, x 0..1, y 0..1).
    /// </summary>
    public void Fingers(string action, int changed, IReadOnlyList<(int Id, double X, double Y)> fingers)
    {
        if (!Running) return;
        var valid = fingers.Where(f => f.Id is >= 0 and < 10).GroupBy(f => f.Id).Select(g => g.First()).ToList();
        if (!TouchReady())
        {
            // no touch injection: one finger as the mouse
            if (valid.Count == 0) return;
            var f = valid.FirstOrDefault(v => v.Id == changed);
            if (f == default) f = valid[0];
            Touch(action == "cancel" ? "up" : action, f.X, f.Y, 0);
            return;
        }
        using var _ = PerMonitorDpi();
        var infos = new List<POINTER_TOUCH_INFO>();
        foreach (var f in valid)
        {
            int flags;
            if (action == "cancel") { if (!_down.Contains(f.Id)) continue; flags = PF_UP | PF_CANCELED; }
            else if (action == "down" && f.Id == changed && !_down.Contains(f.Id)) flags = PF_DOWN | PF_INRANGE | PF_INCONTACT;
            else if (action == "up" && f.Id == changed) { if (!_down.Contains(f.Id)) continue; flags = PF_UP; }
            else if (_down.Contains(f.Id)) flags = PF_UPDATE | PF_INRANGE | PF_INCONTACT;
            else continue; // a finger Windows hasn't seen go down
            infos.Add(Contact(f.Id, Map(_bounds, f.X, f.Y), flags));
        }
        if (infos.Count == 0) return;
        if (InjectTouchInput((uint)infos.Count, infos.ToArray()))
            foreach (var i in infos)
            {
                if ((i.pointerInfo.pointerFlags & PF_DOWN) != 0) _down.Add((int)i.pointerInfo.pointerId);
                if ((i.pointerInfo.pointerFlags & PF_UP) != 0) _down.Remove((int)i.pointerInfo.pointerId);
            }
        else if (action is "up" or "cancel") _down.Clear(); // never leave a finger stuck down
    }

    /// <summary>Lifts any finger still down (the session ended mid-touch).</summary>
    void LiftAll()
    {
        if (_down.Count == 0 || _touchReady != 1) return;
        using var _ = PerMonitorDpi();
        var infos = _down.Select(id => Contact(id, new Point(_bounds.Left + 1, _bounds.Top + 1), PF_UP | PF_CANCELED)).ToArray();
        try { InjectTouchInput((uint)infos.Length, infos); } catch { }
        _down.Clear();
    }

    static POINTER_TOUCH_INFO Contact(int id, Point p, int flags) => new()
    {
        pointerInfo = new POINTER_INFO
        {
            pointerType = 2, // PT_TOUCH
            pointerId = (uint)id,
            pointerFlags = flags,
            ptPixelLocation = new POINT { X = p.X, Y = p.Y }
        },
        touchMask = 0x7, // contact area, orientation, pressure: some apps ignore touch without them
        rcContact = new RECT { Left = p.X - 2, Top = p.Y - 2, Right = p.X + 2, Bottom = p.Y + 2 },
        orientation = 90,
        pressure = 32000
    };

    const int PF_INRANGE = 0x2, PF_INCONTACT = 0x4, PF_CANCELED = 0x8000, PF_DOWN = 0x10000, PF_UPDATE = 0x20000, PF_UP = 0x40000;

    [StructLayout(LayoutKind.Sequential)]
    struct POINTER_INFO
    {
        public int pointerType; public uint pointerId, frameId; public int pointerFlags;
        public IntPtr sourceDevice, hwndTarget;
        public POINT ptPixelLocation, ptHimetricLocation, ptPixelLocationRaw, ptHimetricLocationRaw;
        public uint dwTime, historyCount; public int InputData; public uint dwKeyStates;
        public ulong PerformanceCount; public int ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINTER_TOUCH_INFO
    {
        public POINTER_INFO pointerInfo;
        public int touchFlags, touchMask;
        public RECT rcContact, rcContactRaw;
        public uint orientation, pressure;
    }

    [DllImport("user32.dll", SetLastError = true)] static extern bool InitializeTouchInjection(uint maxCount, uint mode);
    [DllImport("user32.dll", SetLastError = true)] static extern bool InjectTouchInput(uint count, [In] POINTER_TOUCH_INFO[] contacts);

    /// <summary>The pointer back where the laptop's user left it (unless they had it on the phone's screen themselves).</summary>
    void GoHome()
    {
        if (_home is Point h && !_bounds.Contains(h))
        {
            Thread.Sleep(30); // let the click land before the pointer leaves
            SetCursorPos(h.X, h.Y);
        }
        _home = null;
    }

    // ------------------------------------------------------------ cleaning up

    /// <summary>
    /// After a session: windows on the phone's display come back to the main screen, the
    /// pointer too, and a virtual display (one Nyx extended onto) is switched off again so
    /// nothing can get lost on a screen nobody can see. A real monitor is never touched.
    /// </summary>
    public static void Tidy(Display target)
    {
        try
        {
            foreach (var w in ListWindows(target.Bounds).Where(w => w.OnPhone))
                MoveWindowBack(w.Id);
            using (PerMonitorDpi())
                if (GetCursorPos(out var c) && target.Bounds.Contains(c.X, c.Y))
                {
                    var main = System.Windows.Forms.Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 800, 600);
                    SetCursorPos(main.Left + main.Width / 2, main.Top + main.Height / 2);
                }
            if (IsVirtual(target.Device) && System.Windows.Forms.Screen.AllScreens.Length == 2)
                RunDisplaySwitch("/internal"); // "PC screen only": the virtual one is the only other
        }
        catch { }
    }

    static void MoveWindowBack(long id)
    {
        using var _ = PerMonitorDpi();
        var h = new IntPtr(id);
        var main = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea;
        if (main == null || !IsWindow(h)) return;
        bool wasMax = IsZoomed(h);
        ShowWindow(h, 9);
        GetWindowRect(h, out var r);
        var d = main.Value;
        int w = Math.Min(r.Right - r.Left, d.Width), hgt = Math.Min(r.Bottom - r.Top, d.Height);
        SetWindowPos(h, IntPtr.Zero, d.Left + (d.Width - w) / 2, d.Top + (d.Height - hgt) / 2, w, hgt, 0x0014 /* NOZORDER|NOACTIVATE */);
        if (wasMax) ShowWindow(h, 3);
    }

    /// <summary>A display from a virtual display driver (Virtual Display Driver's monitor id is MTT1337).</summary>
    public static bool IsVirtual(string device)
    {
        if (device.Length == 0) return false;
        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint i = 0; EnumDisplayDevices(device, i, ref dd, 0); i++)
            if (dd.DeviceID.Contains("MTT1337", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static void RunDisplaySwitch(string arg)
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "DisplaySwitch.exe"), arg)
            { UseShellExecute = false, CreateNoWindow = true });
        p?.WaitForExit(5000);
    }

    // ------------------------------------------------------------ windows

    public sealed record WindowItem(long Id, string Title, bool OnPhone);

    /// <summary>
    /// The laptop's open app windows (what Alt+Tab shows), and whether each sits on the
    /// phone's display. Each id is only good until the next list.
    /// </summary>
    public static List<WindowItem> ListWindows(Rectangle phone)
    {
        using var _ = PerMonitorDpi();
        var list = new List<WindowItem>();
        uint self = (uint)Environment.ProcessId;
        // Windows calls this directly: an error escaping it would end Nyx outright, so it's guarded
        EnumWindows((h, _) => { try { return Visit(h); } catch { return true; } }, IntPtr.Zero);
        return list.Take(40).ToList();

        bool Visit(IntPtr h)
        {
            if (!IsWindowVisible(h) || GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero) return true;
            long ex = GetWindowLongPtr(h, -20).ToInt64(); // GWL_EXSTYLE
            if ((ex & 0x80) != 0) return true;            // WS_EX_TOOLWINDOW
            if (DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out int cloaked, 4) == 0 && cloaked != 0) return true;
            int n = GetWindowTextLength(h);
            if (n == 0) return true;
            var sb = new System.Text.StringBuilder(n + 1);
            GetWindowText(h, sb, sb.Capacity);
            string title = sb.ToString();
            if (title == "Program Manager") return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == self && title != "Nyx") return true; // Nyx's own pop-ups
            GetWindowRect(h, out var r);
            var centre = new Point((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
            list.Add(new WindowItem(h.ToInt64(), title.Length > 80 ? title[..80] + "…" : title, phone.Contains(centre)));
            return true;
        }
    }

    /// <summary>Moves a window onto the phone's display (or back to the main screen) and maximises it there.</summary>
    public static bool MoveWindow(long id, Rectangle phone, bool toPhone)
    {
        using var _ = PerMonitorDpi();
        var h = new IntPtr(id);
        if (!IsWindow(h)) return false;
        Rectangle dest = phone;
        if (!toPhone)
        {
            var main = System.Windows.Forms.Screen.PrimaryScreen;
            if (main == null) return false;
            dest = main.WorkingArea;
        }
        ShowWindow(h, 9);  // SW_RESTORE: a maximised window can't be moved
        GetWindowRect(h, out var r);
        int w = Math.Min(r.Right - r.Left, dest.Width), hgt = Math.Min(r.Bottom - r.Top, dest.Height);
        SetWindowPos(h, IntPtr.Zero, dest.Left + (dest.Width - w) / 2, dest.Top + (dest.Height - hgt) / 2, w, hgt, 0x0004 /* NOZORDER */);
        ShowWindow(h, 3);  // SW_MAXIMIZE, on the display it now sits on
        SetForegroundWindow(h);
        return true;
    }

    /// <summary>Asks Windows to extend the desktop again (it falls back to one screen after sleep or a restart).</summary>
    public static Display? ExtendAndFind()
    {
        var t = FindTarget();
        if (t != null) return t;
        try { RunDisplaySwitch("/extend"); }
        catch { return null; }
        for (int i = 0; i < 10 && t == null; i++) { Thread.Sleep(400); t = FindTarget(); }
        return t;
    }

    // ------------------------------------------------------------ Win32

    delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int max);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevices(string device, uint index, ref DISPLAY_DEVICE dd, uint flags);

    static void DrawCursor(Graphics g, Rectangle b)
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || (ci.flags & 1) == 0) return;
        if (!b.Contains(ci.ptScreenPos.X, ci.ptScreenPos.Y)) return;
        int hx = 0, hy = 0;
        if (GetIconInfo(ci.hCursor, out var ii))
        {
            hx = ii.xHotspot; hy = ii.yHotspot;
            if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
            if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
        }
        var hdc = g.GetHdc();
        try { DrawIconEx(hdc, ci.ptScreenPos.X - b.Left - hx, ci.ptScreenPos.Y - b.Top - hy, ci.hCursor, 0, 0, 0, IntPtr.Zero, 3); }
        finally { g.ReleaseHdc(hdc); }
    }

    static void Mouse(uint flags, int data = 0)
    {
        var input = new INPUT { type = 0, mi = new MOUSEINPUT { dwFlags = flags, mouseData = data } };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    /// <summary>Real pixels on every monitor for the current thread, undone on Dispose.</summary>
    static IDisposable PerMonitorDpi()
    {
        IntPtr old = IntPtr.Zero;
        try { old = SetThreadDpiAwarenessContext(new IntPtr(-4)); } catch { } // PER_MONITOR_AWARE_V2
        return new Restore(old);
    }

    sealed class Restore(IntPtr old) : IDisposable
    {
        public void Dispose() { if (old != IntPtr.Zero) try { SetThreadDpiAwarenessContext(old); } catch { } }
    }

    const uint MOUSEEVENTF_LEFTDOWN = 0x02, MOUSEEVENTF_LEFTUP = 0x04,
               MOUSEEVENTF_RIGHTDOWN = 0x08, MOUSEEVENTF_RIGHTUP = 0x10, MOUSEEVENTF_WHEEL = 0x800;

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public POINT ptScreenPos; }
    [StructLayout(LayoutKind.Sequential)] struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy, mouseData; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] struct INPUT { [FieldOffset(0)] public int type; [FieldOffset(8)] public MOUSEINPUT mi; }

    [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO pci);
    [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);
    [DllImport("user32.dll")] static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon, int cx, int cy, int step, IntPtr brush, int flags);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettingsEx(string device, ref DEVMODE dm, IntPtr hwnd, int flags, IntPtr param);
}
