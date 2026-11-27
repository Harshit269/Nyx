using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhoneBridge;

/// <summary>
/// The phone's screen in a window, over Wi-Fi, without developer options: the phone shares
/// its screen (Android asks first, every time), seals JPEG frames with AES-256-GCM under a
/// fresh key sent inside Nyx's link, and streams them. Clicks become taps on the phone.
/// </summary>
public sealed class PhoneMirror : IDisposable
{
    const int MaxFrame = 8 * 1024 * 1024;
    readonly CancellationTokenSource _stop = new();
    TcpClient? _tcp;

    public byte[] Key { get; } = RandomNumberGenerator.GetBytes(32);
    public bool Running { get; private set; }
    /// <summary>"h264": video from the phone's encoder; otherwise a JPEG per frame (older phones).</summary>
    public string Codec { get; set; } = "jpeg";
    public event Action<BitmapSource>? Frame;
    /// <summary>A decoded video picture: 32-bit pixels, width, height. Lock the array while reading it.</summary>
    public event Action<byte[], int, int>? Picture;
    /// <summary>Pictures shown per second, for the window's corner (updated once a second).</summary>
    public int Fps { get; private set; }
    public event Action? Ended;

    public async Task RunAsync(string phoneIp, int port)
    {
        Running = true;
        try
        {
            using var tcp = new TcpClient { NoDelay = true, ReceiveBufferSize = 1 << 20 };
            _tcp = tcp;
            using (var t = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
                await tcp.ConnectAsync(phoneIp, port, t.Token);
            var net = tcp.GetStream();
            await Task.Factory.StartNew(() => Pump(net), _stop.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        catch (Exception ex) when (!_stop.IsCancellationRequested) { Diag.Log("mirror: stopped by " + ex.GetType().Name + ": " + ex.Message); }
        catch { }
        finally
        {
            Running = false;
            Ended?.Invoke();
        }
    }

    void Pump(NetworkStream net)
    {
        if (Codec == "h264") { PumpVideo(net); return; }
        using var gcm = new AesGcm(Key, 16);
        var lenBytes = new byte[4];
        long last = 0;
        while (!_stop.IsCancellationRequested)
        {
            net.ReadExactly(lenBytes);
            int len = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(lenBytes);
            if (len < 12 + 16 + 2 || len > MaxFrame) throw new IOException("bad frame");
            var buf = new byte[len];
            net.ReadExactly(buf);
            var jpeg = PhoneCamera.Open(gcm, buf, ref last);
            if (jpeg.Length < 2) continue;
            _jpegCount++;
            long tick = Environment.TickCount64;
            if (tick - _jpegSecond >= 1000)
            {
                Fps = _jpegCount; _jpegCount = 0; _jpegSecond = tick;
                if (++_logTick % 10 == 0) Diag.Log($"mirror: pictures {Fps} fps");
            }
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = new MemoryStream(jpeg);
            img.EndInit();
            img.Freeze(); // decoded here, shown on the window's thread
            Frame?.Invoke(img);
        }
    }

    /// <summary>Video: each piece is [1 = keyframe][8-byte time, microseconds][H.264], sealed like the rest.</summary>
    void PumpVideo(NetworkStream net)
    {
        using var gcm = new AesGcm(Key, 16);
        using var decoder = new H264Decoder();
        var lenBytes = new byte[4];
        long last = 0;
        // two picture buffers: the window copies one while the next is being decoded into the other
        var bufs = new byte[2][];
        int which = 0, count = 0;
        long second = Environment.TickCount64;
        while (!_stop.IsCancellationRequested)
        {
            net.ReadExactly(lenBytes);
            int len = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(lenBytes);
            if (len < 12 + 16 + 10 || len > MaxFrame) throw new IOException("bad frame");
            var buf = new byte[len];
            net.ReadExactly(buf);
            var piece = PhoneCamera.Open(gcm, buf, ref last);
            if (piece.Length < 10) continue;
            long us = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(piece.AsSpan(1, 8));
            var target = bufs[which];
            bool made = false;
            void Got(byte[] px, int w, int h)
            {
                bufs[which] = px; // reused next time round (or replaced if the size changed)
                made = true;
                count++;
                Picture?.Invoke(px, w, h);
            }
            if (target != null) lock (target) decoder.Push(piece, 9, piece.Length - 9, us * 10, target, Got);
            else decoder.Push(piece, 9, piece.Length - 9, us * 10, null, Got); // a brand-new array, not shared yet
            if (made) which ^= 1;
            long now = Environment.TickCount64;
            if (now - second >= 1000)
            {
                Fps = count; count = 0; second = now;
                if (++_logTick % 10 == 0) Diag.Log($"mirror: video {Fps} fps, {decoder.Width}x{decoder.Height}");
            }
        }
    }

    public void Stop()
    {
        if (!_stop.IsCancellationRequested) _stop.Cancel();
        try { _tcp?.Close(); } catch { }
        Running = false;
    }

    public void Dispose() => Stop();
    int _jpegCount, _logTick;
    long _jpegSecond = Environment.TickCount64;

    /// <summary>A click or drag in the window as a command for the phone (x, y run 0..1).</summary>
    public static JsonObject Gesture(Point down, Point up, TimeSpan held, Size area)
    {
        double x = Clamp(down.X / area.Width), y = Clamp(down.Y / area.Height);
        double x2 = Clamp(up.X / area.Width), y2 = Clamp(up.Y / area.Height);
        bool moved = Math.Abs(up.X - down.X) > 6 || Math.Abs(up.Y - down.Y) > 6;
        if (!moved)
            return new JsonObject { ["t"] = "mirror_input", ["a"] = held.TotalMilliseconds >= 500 ? "long" : "tap", ["x"] = x, ["y"] = y };
        return new JsonObject
        {
            ["t"] = "mirror_input", ["a"] = "swipe", ["x"] = x, ["y"] = y, ["x2"] = x2, ["y2"] = y2,
            ["ms"] = (int)Math.Clamp(held.TotalMilliseconds, 80, 1500)
        };
    }

    static double Clamp(double v) => double.IsFinite(v) ? Math.Clamp(v, 0, 1) : 0.5;
}

/// <summary>The window that shows the phone, nothing else: click to tap, drag to swipe, wheel to scroll, right-click for Back.</summary>
public sealed class MirrorWindow : Window
{
    readonly Image _screen = new() { Stretch = Stretch.Uniform, Cursor = Cursors.Hand };
    readonly TextBlock _note;
    readonly Func<JsonObject, Task> _send;
    Point _down;
    DateTime _downAt;
    bool _pressed;
    bool _sized;

    public MirrorWindow(Func<JsonObject, Task> send, bool canControl)
    {
        _send = send;
        Title = "Your phone";
        Foreground = Theme.Paint("Ink");
        FontFamily = Theme.Font("UiFont");
        var area = SystemParameters.WorkArea;
        Height = Math.Round(area.Height * 0.86);
        Width = Math.Round(Height * 9 / 20);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ThemeManager.ApplyTitleBar(this);

        _note = new TextBlock
        {
            Text = canControl ? "Waiting for your phone…"
                              : "Waiting for your phone… (To control it from here, on the phone: Nyx > Allow laptop control.)",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24), Foreground = Theme.Paint("Ink2"),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center
        };

        // just the phone, edge to edge: swipe up for Home, in from the side for Back, as on the
        // phone, or right-click for Back
        Background = Brushes.Black;
        RenderOptions.SetBitmapScalingMode(_screen, BitmapScalingMode.HighQuality);
        _stage.Children.Add(_note);
        _stage.Children.Add(_screen);
        _stage.Children.Add(_mute);
        _stage.Children.Add(_hint);
        Content = _stage;
        _mute.Click += (_, _) => SetMuted(!_muted, tell: true);
        SourceInitialized += (_, _) =>
            System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle)?.AddHook(KeepShape);

        if (canControl)
        {
            _screen.MouseLeftButtonDown += (_, e) => { _down = e.GetPosition(_screen); _downAt = DateTime.UtcNow; _pressed = true; _screen.CaptureMouse(); };
            _screen.MouseLeftButtonUp += async (_, e) =>
            {
                if (!_pressed) return;
                _pressed = false;
                _screen.ReleaseMouseCapture();
                var size = new Size(_screen.ActualWidth, _screen.ActualHeight);
                if (size.Width < 1 || size.Height < 1) return;
                await _send(PhoneMirror.Gesture(_down, e.GetPosition(_screen), DateTime.UtcNow - _downAt, size));
            };
            _screen.MouseRightButtonUp += async (_, _) => await _send(new JsonObject { ["t"] = "mirror_input", ["a"] = "back" });
            _screen.MouseWheel += async (_, e) =>
            {
                // wheel up = see what's above = finger moves down the screen
                var p = e.GetPosition(_screen);
                double x = _screen.ActualWidth > 0 ? Math.Clamp(p.X / _screen.ActualWidth, 0, 1) : 0.5;
                double from = e.Delta > 0 ? 0.35 : 0.65, to = e.Delta > 0 ? 0.65 : 0.35;
                await _send(new JsonObject { ["t"] = "mirror_input", ["a"] = "swipe", ["x"] = x, ["y"] = from, ["x2"] = x, ["y2"] = to, ["ms"] = 220 });
            };
        }
    }

    readonly Grid _stage = new();

    // ---- a short note along the bottom (e.g. this app's sound can't be shared)
    readonly Border _hint = new()
    {
        Margin = new Thickness(12, 0, 12, 14), Padding = new Thickness(12, 8, 12, 8), CornerRadius = new CornerRadius(10),
        Background = new SolidColorBrush(Color.FromArgb(0xD9, 0, 0, 0)), VerticalAlignment = VerticalAlignment.Bottom,
        HorizontalAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed, IsHitTestVisible = false,
        Child = new TextBlock { Foreground = Brushes.White, FontSize = 12, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center }
    };
    System.Windows.Threading.DispatcherTimer? _hintTimer;

    /// <summary>Shows a note over the bottom of the phone for a few seconds; null hides it.</summary>
    public void Hint(string? text)
    {
        _hintTimer?.Stop();
        if (text == null) { _hint.Visibility = Visibility.Collapsed; return; }
        ((TextBlock)_hint.Child).Text = text;
        _hint.Visibility = Visibility.Visible;
        _hintTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _hintTimer.Tick += (_, _) => { _hintTimer?.Stop(); _hint.Visibility = Visibility.Collapsed; };
        _hintTimer.Start();
    }

    // ---- the small mute button, top-right over the phone, only while sound comes through
    readonly Button _mute = new()
    {
        Width = 34, Height = 34, Margin = new Thickness(0, 10, 10, 0), Padding = new Thickness(0),
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
        Visibility = Visibility.Collapsed, Cursor = Cursors.Hand, ToolTip = "Sound plays here. Click to send it back to the phone",
        Opacity = 0.85, Focusable = false
    };
    bool _muted;

    /// <summary>Asked when the mute button is pressed: true = mute the phone's sound on the laptop.</summary>
    public event Action<bool>? MuteChanged;

    /// <summary>Shows the mute button (sound is coming through) or hides it.</summary>
    public void ShowSound(bool on)
    {
        _mute.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on) SetMuted(_muted, tell: false);
    }

    void SetMuted(bool muted, bool tell)
    {
        _muted = muted;
        // a round, see-through dark button with a speaker (crossed out when muted)
        var icon = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(muted
                ? "M11 5 6 9H2v6h4l5 4V5z M22 9l-6 6 M16 9l6 6"
                : "M11 5 6 9H2v6h4l5 4V5z M15.5 8.5a5 5 0 0 1 0 7 M19 5a10 10 0 0 1 0 14"),
            Stroke = muted ? Theme.Paint("Red") : Brushes.White, StrokeThickness = 1.8,
            StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Width = 16, Height = 16, Stretch = Stretch.Uniform
        };
        _mute.Template = MuteTemplate();
        _mute.Content = icon;
        _mute.ToolTip = muted ? "Sound is on the phone. Click to play it here" : "Sound plays here. Click to send it back to the phone";
        if (tell) MuteChanged?.Invoke(muted);
    }

    static ControlTemplate MuteTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(17));
        border.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0xB3, 0, 0, 0)));
        border.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        t.VisualTree = border;
        return t;
    }
    double _ratio;     // phone width / height

    WriteableBitmap? _canvas;
    /// <summary>Video pictures actually put on screen (for measuring smoothness).</summary>
    public int Painted { get; private set; }

    /// <summary>A video picture (32-bit pixels): copied into one bitmap that's reused while the size stays.</summary>
    public void ShowPixels(byte[] px, int w, int h)
    {
        if (w <= 0 || h <= 0 || px.Length < w * h * 4) return;
        if (_canvas == null || _canvas.PixelWidth != w || _canvas.PixelHeight != h)
        {
            _canvas = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
            _screen.Source = _canvas;
        }
        lock (px) _canvas.WritePixels(new Int32Rect(0, 0, w, h), px, w * 4, 0);
        Painted++;
        _note.Visibility = Visibility.Collapsed;
        FitTo(w, h);
    }

    public void Show(BitmapSource frame)
    {
        _screen.Source = frame;
        _canvas = null;
        _note.Visibility = Visibility.Collapsed;
        if (frame.PixelHeight <= 0) return;
        FitTo(frame.PixelWidth, frame.PixelHeight);
    }

    void FitTo(int pw, int ph)
    {
        if (ph <= 0) return;
        double ratio = (double)pw / ph;
        if (_sized && Math.Abs(ratio - _ratio) < 0.01) return;
        // the window takes the phone's exact shape (again if the phone turns sideways)
        _sized = true;
        _ratio = ratio;
        var area = SystemParameters.WorkArea;
        double chromeW = ActualWidth - _stage.ActualWidth, chromeH = ActualHeight - _stage.ActualHeight;
        double h = ratio < 1 ? area.Height * 0.86 : Math.Min(area.Height * 0.86, area.Width * 0.7 / ratio);
        Width = Math.Round(h * ratio + chromeW);
        Height = Math.Round(h + chromeH);
        Left = Math.Max(area.Left, area.Left + (area.Width - Width) / 2);
        Top = Math.Max(area.Top, area.Top + (area.Height - Height) / 2);
    }

    /// <summary>While resizing, keep the phone's shape, so there are never black bands (like scrcpy).</summary>
    IntPtr KeepShape(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_SIZING = 0x0214;
        if (msg != WM_SIZING || _ratio <= 0) return IntPtr.Zero;
        try { return Shape(wParam, lParam, ref handled); } catch { return IntPtr.Zero; } // never break resizing
    }

    IntPtr Shape(IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        double cw = (ActualWidth - _stage.ActualWidth) * dpi.DpiScaleX, ch = (ActualHeight - _stage.ActualHeight) * dpi.DpiScaleY;
        var r = System.Runtime.InteropServices.Marshal.PtrToStructure<RECT>(lParam);
        int edge = wParam.ToInt32(); // 1 left, 2 right, 3 top, 6 bottom, 4/5/7/8 corners
        double w = r.Right - r.Left - cw, h = r.Bottom - r.Top - ch;
        if (edge is 3 or 6) w = h * _ratio;          // dragging top or bottom: width follows
        else h = w / _ratio;                          // sides and corners: height follows
        if (edge is 1 or 4 or 7) r.Left = r.Right - (int)Math.Round(w + cw); else r.Right = r.Left + (int)Math.Round(w + cw);
        if (edge is 3 or 4 or 5) r.Top = r.Bottom - (int)Math.Round(h + ch); else r.Bottom = r.Top + (int)Math.Round(h + ch);
        System.Runtime.InteropServices.Marshal.StructureToPtr(r, lParam, false);
        handled = true;
        return new IntPtr(1);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }
}
