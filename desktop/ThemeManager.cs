using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;

namespace PhoneBridge;

public enum ThemeMode { Light, Dark, Auto }

/// <summary>
/// Owns light / dark / auto. Colours are defined in Colors.Light.xaml and Colors.Dark.xaml
/// and copied into one live palette of brushes that everything refers to; switching theme
/// animates each brush to its new colour. Auto follows Windows' "app mode" setting live.
/// </summary>
public static class ThemeManager
{
    static readonly string SettingsPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nyx", "settings.json");

    static ResourceDictionary? _palette;

    public static ThemeMode Mode { get; private set; } = ThemeMode.Auto;
    public static bool IsDark { get; private set; }

    /// <summary>Raised after the palette changes (mode change or Windows switching).</summary>
    public static event Action? Changed;

    /// <summary>
    /// Raised the moment an animated switch starts (true = going dark), before the palette
    /// changes at the end of it: the theme button's icon morphs right away.
    /// </summary>
    public static event Action<bool>? Switching;

    /// <summary>Call once from App startup, before any window is shown.</summary>
    public static void Initialize()
    {
        Mode = LoadMode();
        // "--theme light|dark|auto" overrides the saved choice for this run only (handy for screenshots)
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "--theme");
        if (i >= 0 && i + 1 < args.Length && Enum.TryParse<ThemeMode>(args[i + 1], true, out var forced)) Mode = forced;
        Apply(animateFrom: null);
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && Mode == ThemeMode.Auto)
                Application.Current.Dispatcher.InvokeAsync(() => Apply(Application.Current.MainWindow));
        };
    }

    /// <param name="origin">Where the new theme should spread from, in window coordinates.</param>
    /// <param name="sharp">The button that was pressed: it stays sharp and on top, and glides to its new colours.</param>
    public static void SetMode(ThemeMode mode, Window? animateFrom, Point? origin = null, System.Windows.Controls.Control? sharp = null)
    {
        if (mode == Mode) return;
        Diag.Log("theme: " + mode); // a breadcrumb, so a crash right after says what ran last
        Mode = mode;
        SaveMode(mode);
        Apply(animateFrom, origin, sharp);
    }

    static bool WindowsPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }

    static readonly ResourceDictionary LightSource = new() { Source = new Uri("Colors.Light.xaml", UriKind.Relative) };
    static readonly ResourceDictionary DarkSource = new() { Source = new Uri("Colors.Dark.xaml", UriKind.Relative) };


    /// <summary>How long the old theme takes to fade away.</summary>
    static readonly TimeSpan GlideTime = TimeSpan.FromMilliseconds(380);

    // the one theme animation that may be running: a new switch finishes it at once first,
    // so quick clicks never stack overlays on top of each other
    static Action? _finishCurrent;

    static void FinishCurrent()
    {
        var f = _finishCurrent;
        _finishCurrent = null;
        try { f?.Invoke(); } catch { }
    }

    /// <summary>Fades a still picture of the old look away (when there's no button to spread from).</summary>
    static void Crossfade(Window window)
    {
        if (window.Content is not FrameworkElement root || root.ActualWidth < 1) return;
        var layer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(root);
        if (layer == null) return;

        // Draw the element through a VisualBrush into a fresh visual. Rendering the element
        // straight into the bitmap would bake in its offset inside the window, which is
        // what shifted the old picture sideways.
        var dpi = VisualTreeHelper.GetDpi(root);
        var size = new Size(root.ActualWidth, root.ActualHeight);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            // the window's own background sits behind its content, so paint the old one first
            if (window.Background is SolidColorBrush bg)
                dc.DrawRectangle(new SolidColorBrush(bg.Color), null, new Rect(size));
            dc.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                null, new Rect(size));
        }
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(size.Width * dpi.DpiScaleX), (int)Math.Ceiling(size.Height * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();

        var veil = new Snapshot(root, bmp);
        layer.Add(veil);
        FinishCurrent();
        var fade = new DoubleAnimation(1, 0, GlideTime) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
        bool done = false;
        void Finish()
        {
            if (done) return;
            done = true;
            veil.BeginAnimation(UIElement.OpacityProperty, null);
            try { layer.Remove(veil); } catch { }
        }
        fade.Completed += (_, _) => Finish();
        _finishCurrent = Finish;
        veil.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    sealed class Snapshot : System.Windows.Documents.Adorner
    {
        readonly ImageSource _image;

        public Snapshot(UIElement el, ImageSource image) : base(el)
        {
            _image = image;
            IsHitTestVisible = false;
        }

        protected override void OnRender(DrawingContext dc) => dc.DrawImage(_image, new Rect(AdornedElement.RenderSize));
    }

    static System.Windows.Threading.DispatcherTimer? _titleFlip;

    static void Apply(Window? animateFrom, Point? origin = null, System.Windows.Controls.Control? sharp = null)
    {
        bool dark = Mode switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => WindowsPrefersDark()
        };
        if (_palette != null && dark == IsDark) { Changed?.Invoke(); return; }

        var target = dark ? DarkSource : LightSource;
        bool animate = _palette != null && animateFrom != null && Theme.Motion;

        if (_palette == null)
        {
            // the app's palette: one dictionary of locked brushes, swapped whole on every switch
            Recolour(target);
        }
        else
        {
            // Lay a still picture of the old theme over the window, switch every colour
            // underneath at once, then fade the picture out. Light and dark are opposites,
            // so blending the colours themselves would pass through a flat grey where text
            // vanishes; a crossfade keeps the text readable the whole way through.
            if (animate && origin != null)
            {
                // the goo: the new look spreads out of the button over the old one, then the
                // theme swaps underneath at the end. A switch already running finishes first.
                FinishCurrent();
                var old = IsDark ? DarkSource : LightSource;
                IsDark = dark;
                Switching?.Invoke(dark);
                void Swap()
                {
                    Recolour(target);
                    foreach (Window w in Application.Current.Windows) ApplyTitleBar(w);
                    Changed?.Invoke();
                }
                try
                {
                    Color Now(string k) => _palette?[k] is SolidColorBrush b ? b.Color : Colors.Gray;
                    Color To(string k) => target[k] is SolidColorBrush b ? b.Color : Colors.Gray;
                    var glide = sharp == null ? null : new GooTransition.ButtonGlide(
                        Now("Tile"), To("Tile"), Now("Line"), To("Line"), Now("Ink"), To("Ink"),
                        Now("DotOff"), To("DotOff"), Now("Red"), To("Red"));
                    // a picture of the window in its new colours, taken between two frames: switch,
                    // capture, switch back (the screen only ever shows the old colours meanwhile)
                    // prepared when the mouse came onto the button, if it's still fresh; else now
                    var newLook = TakePrepared(animateFrom!, dark);
                    if (newLook == null)
                    {
                        Recolour(target);
                        newLook = CaptureLook(animateFrom!);
                        Recolour(old);
                    }
                    var finish = newLook == null ? null
                        : GooTransition.Run(animateFrom!, origin.Value, sharp, newLook, Swap, glide, toLight: !dark);
                    if (finish == null) Swap();
                    else _finishCurrent = finish;
                }
                catch (Exception ex)
                {
                    // whatever went wrong, the theme still switches: just without the animation
                    Diag.Log("theme animation skipped: " + ex.Message);
                    Swap();
                }
                return;
            }
            if (animate) Crossfade(animateFrom!);
            Recolour(target);
        }

        IsDark = dark;
        // the title bar can't fade, so flip it halfway through the glide, when the window is mid-grey
        // (one timer: a newer switch replaces the pending flip instead of queueing another)
        if (animate)
        {
            _titleFlip?.Stop();
            var t = _titleFlip = new System.Windows.Threading.DispatcherTimer { Interval = GlideTime / 2 };
            t.Tick += (_, _) => { t.Stop(); foreach (Window w in Application.Current.Windows) ApplyTitleBar(w); };
            t.Start();
        }
        else
        {
            foreach (Window w in Application.Current.Windows) ApplyTitleBar(w);
        }
        Changed?.Invoke();
    }

    // the picture of the other theme, taken ahead of time (see Prepare)
    static System.Windows.Media.Imaging.BitmapSource? _prepared;
    static bool _preparedDark;
    static Size _preparedSize;
    static long _preparedAt;

    /// <summary>
    /// Takes the picture of the window in the other theme ahead of the click (call it when the
    /// pointer comes onto the theme button), so the switch starts the moment it's pressed. The
    /// screen keeps showing the current colours throughout.
    /// </summary>
    public static void Prepare(Window window)
    {
        try
        {
            if (_palette == null || !Theme.Motion || _finishCurrent != null) return;
            if (window.Content is not FrameworkElement root || root.ActualWidth < 1) return;
            bool dark = !IsDark;
            if (_prepared != null && _preparedDark == dark && Environment.TickCount64 - _preparedAt < 1500) return;
            var now = IsDark ? DarkSource : LightSource;
            Recolour(dark ? DarkSource : LightSource);
            var look = CaptureLook(window);
            Recolour(now);
            if (look == null) return;
            _prepared = look;
            _preparedDark = dark;
            _preparedSize = root.RenderSize;
            _preparedAt = Environment.TickCount64;
        }
        catch (Exception ex) { Diag.Log("theme prepare: " + ex.Message); }
    }

    /// <summary>The prepared picture, if it's of this theme, this window size, and recent (else null).</summary>
    static System.Windows.Media.Imaging.BitmapSource? TakePrepared(Window window, bool dark)
    {
        var p = _prepared;
        _prepared = null;
        if (p == null || _preparedDark != dark || Environment.TickCount64 - _preparedAt > 2500) return null;
        return window.Content is FrameworkElement root && root.RenderSize == _preparedSize ? p : null;
    }

    /// <summary>The window as it looks right now (its background and everything on it), as a picture.</summary>
    static System.Windows.Media.Imaging.BitmapSource? CaptureLook(Window window)
    {
        if (window.Content is not FrameworkElement root || root.ActualWidth < 1) return null;
        root.UpdateLayout();
        // the window's content sits at its top-left corner; rendering it directly is only right then
        if (VisualTreeHelper.GetOffset(root) != default) return null;
        var dpi = VisualTreeHelper.GetDpi(root);
        var size = new Size(root.ActualWidth, root.ActualHeight);
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(size.Width * dpi.DpiScaleX), (int)Math.Ceiling(size.Height * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        // the window's own background first, then the content straight on top
        if (window.Background is SolidColorBrush bg)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) dc.DrawRectangle(new SolidColorBrush(bg.Color), null, new Rect(size));
            bmp.Render(dv);
        }
        bmp.Render(root);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>
    /// Switches the whole app to <paramref name="target"/>'s colours: a fresh dictionary of
    /// locked (frozen) brushes replaces the old one in a single step, and every
    /// {DynamicResource} picks up its new brush. No brush that's already on screen is ever
    /// changed. (Nyx used to recolour its shared brushes in place; WPF's renderer then read a
    /// released drawing resource now and then and the app crashed, more so when switching
    /// fast. On NVIDIA laptops that showed up as a crash inside the graphics driver.)
    /// </summary>
    static void Recolour(ResourceDictionary target)
    {
        var next = new ResourceDictionary();
        foreach (var key in target.Keys)
        {
            if (target[key] is SolidColorBrush b)
            {
                var copy = new SolidColorBrush(b.Color);
                copy.Freeze();
                next[key] = copy;
            }
            else next[key] = target[key];
        }
        var merged = Application.Current.Resources.MergedDictionaries;
        int at = _palette == null ? -1 : merged.IndexOf(_palette);
        if (at >= 0) merged[at] = next;
        else merged.Insert(0, next);
        _palette = next;
    }

    // ------------------------------------------------------------ title bar

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Dark title bar to match (Windows 10 2004+ / 11). Safe to call before the window has a handle.</summary>
    public static void ApplyTitleBar(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero)
        {
            w.SourceInitialized -= OnSourceInitialized;
            w.SourceInitialized += OnSourceInitialized;
            return;
        }
        int on = IsDark ? 1 : 0;
        try { DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)); } catch { }
    }

    static void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is Window w) ApplyTitleBar(w);
    }

    // ------------------------------------------------------------ persistence

    static ThemeMode LoadMode()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return ThemeMode.Auto;
            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            if (doc.RootElement.TryGetProperty("theme", out var t) &&
                Enum.TryParse<ThemeMode>(t.GetString(), true, out var m))
                return m;
        }
        catch { }
        return ThemeMode.Auto;
    }

    static void SaveMode(ThemeMode mode)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { theme = mode.ToString() }));
        }
        catch { }
    }
}
