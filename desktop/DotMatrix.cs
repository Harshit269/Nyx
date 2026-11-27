using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PhoneBridge;

/// <summary>
/// Text drawn as a 5x7 dot matrix. When the text changes the dots light up in a
/// quick diagonal sweep. Lit dots use Foreground, unlit dots the theme's DotOff.
/// </summary>
public class DotText : FrameworkElement
{
    static readonly Dictionary<char, string[]> Glyphs = BuildGlyphs();

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(DotText),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure, (d, _) => ((DotText)d).Sweep()));

    public static readonly DependencyProperty DotProperty = DependencyProperty.Register(
        nameof(Dot), typeof(double), typeof(DotText),
        new FrameworkPropertyMetadata(3.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(DotText),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OffProperty = DependencyProperty.Register(
        nameof(Off), typeof(Brush), typeof(DotText),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowOffProperty = DependencyProperty.Register(
        nameof(ShowOff), typeof(bool), typeof(DotText),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RevealProperty = DependencyProperty.Register(
        nameof(Reveal), typeof(double), typeof(DotText),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    /// <summary>Dot diameter in DIPs; the gap is half a dot.</summary>
    public double Dot { get => (double)GetValue(DotProperty); set => SetValue(DotProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public Brush? Off { get => (Brush?)GetValue(OffProperty); set => SetValue(OffProperty, value); }
    /// <summary>Draw the unlit dots of each cell too, like a real LED panel.</summary>
    public bool ShowOff { get => (bool)GetValue(ShowOffProperty); set => SetValue(ShowOffProperty, value); }
    public double Reveal { get => (double)GetValue(RevealProperty); set => SetValue(RevealProperty, value); }

    public DotText()
    {
        SnapsToDevicePixels = true;
        SetResourceReference(ForegroundProperty, "Ink");
        SetResourceReference(OffProperty, "DotOff");
    }

    double Pitch => Dot * 1.5;

    string Upper => (Text ?? "").ToUpperInvariant();

    static int CellWidth(char c) => c == ' ' ? 3 : (c == ':' || c == '.') ? 2 : 5;

    protected override Size MeasureOverride(Size available)
    {
        string t = Upper;
        int cols = 0;
        for (int i = 0; i < t.Length; i++) cols += CellWidth(t[i]) + (i < t.Length - 1 ? 1 : 0);
        return new Size(Math.Max(0, cols * Pitch - (Pitch - Dot)), 7 * Pitch - (Pitch - Dot));
    }

    void Sweep()
    {
        if (!IsLoaded || !Theme.Motion) { Reveal = 1; return; }
        var a = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(520)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        BeginAnimation(RevealProperty, a);
    }

    protected override void OnRender(DrawingContext dc)
    {
        string t = Upper;
        double p = Pitch, r = Dot / 2;
        double total = Math.Max(1, DesiredSize.Width + DesiredSize.Height);
        double x0 = 0;
        foreach (char raw in t)
        {
            char c = Glyphs.ContainsKey(raw) ? raw : ' ';
            int w = CellWidth(c);
            var rows = Glyphs[c];
            for (int y = 0; y < 7; y++)
                for (int x = 0; x < w; x++)
                {
                    int src = w == 2 ? x + 1 : x;  // narrow glyphs take the centre columns
                    bool on = src < rows[y].Length && rows[y][src] == '1';
                    double cx = x0 + x * p + r, cy = y * p + r;
                    bool lit = on && (cx + cy) / total <= Reveal * 1.02;
                    if (lit) dc.DrawEllipse(Foreground, null, new Point(cx, cy), r, r);
                    else if (ShowOff && Off != null) dc.DrawEllipse(Off, null, new Point(cx, cy), r, r);
                }
            x0 += (w + 1) * p;
        }
    }

    static Dictionary<char, string[]> BuildGlyphs()
    {
        string data = @"
A 01110 10001 10001 11111 10001 10001 10001
B 11110 10001 10001 11110 10001 10001 11110
C 01110 10001 10000 10000 10000 10001 01110
D 11110 10001 10001 10001 10001 10001 11110
E 11111 10000 10000 11110 10000 10000 11111
F 11111 10000 10000 11110 10000 10000 10000
G 01110 10001 10000 10111 10001 10001 01111
H 10001 10001 10001 11111 10001 10001 10001
I 01110 00100 00100 00100 00100 00100 01110
J 00111 00010 00010 00010 00010 10010 01100
K 10001 10010 10100 11000 10100 10010 10001
L 10000 10000 10000 10000 10000 10000 11111
M 10001 11011 10101 10101 10001 10001 10001
N 10001 10001 11001 10101 10011 10001 10001
O 01110 10001 10001 10001 10001 10001 01110
P 11110 10001 10001 11110 10000 10000 10000
Q 01110 10001 10001 10001 10101 10010 01101
R 11110 10001 10001 11110 10100 10010 10001
S 01111 10000 10000 01110 00001 00001 11110
T 11111 00100 00100 00100 00100 00100 00100
U 10001 10001 10001 10001 10001 10001 01110
V 10001 10001 10001 10001 10001 01010 00100
W 10001 10001 10001 10101 10101 10101 01010
X 10001 10001 01010 00100 01010 10001 10001
Y 10001 10001 01010 00100 00100 00100 00100
Z 11111 00001 00010 00100 01000 10000 11111
0 01110 10001 10011 10101 11001 10001 01110
1 00100 01100 00100 00100 00100 00100 01110
2 01110 10001 00001 00010 00100 01000 11111
3 11111 00010 00100 00010 00001 10001 01110
4 00010 00110 01010 10010 11111 00010 00010
5 11111 10000 11110 00001 00001 10001 01110
6 00110 01000 10000 11110 10001 10001 01110
7 11111 00001 00010 00100 01000 01000 01000
8 01110 10001 10001 01110 10001 10001 01110
9 01110 10001 10001 01111 00001 00010 01100
: 00000 01100 01100 00000 01100 01100 00000
. 00000 00000 00000 00000 00000 01100 01100
- 00000 00000 00000 11111 00000 00000 00000
+ 00000 00100 00100 11111 00100 00100 00000
% 11000 11001 00010 00100 01000 10011 00011
/ 00001 00001 00010 00100 01000 10000 10000
# 01010 01010 11111 01010 11111 01010 01010";
        var map = new Dictionary<char, string[]> { [' '] = Enumerable.Repeat("000", 7).ToArray() };
        foreach (var line in data.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            map[parts[0][0]] = parts.Skip(1).ToArray();
        }
        return map;
    }
}

/// <summary>
/// The theme button's face: a 7x7 dot sun that redraws itself into a moon (and back)
/// in a diagonal sweep, the sun's centre dot in red. Lit dots use Ink, the rest DotOff.
/// </summary>
public class DotIcon : FrameworkElement
{
    static readonly string[] Sun = { "1001001", "0100010", "0011100", "1111111", "0011100", "0100010", "1001001" };
    static readonly string[] Moon = { "0011100", "0110000", "1100000", "1100000", "1100000", "0110000", "0011100" };

    public static readonly DependencyProperty IsMoonProperty = DependencyProperty.Register(
        nameof(IsMoon), typeof(bool), typeof(DotIcon),
        new FrameworkPropertyMetadata(false, (d, e) => ((DotIcon)d).Morph((bool)e.OldValue)));

    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(DotIcon),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public bool IsMoon { get => (bool)GetValue(IsMoonProperty); set => SetValue(IsMoonProperty, value); }
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }

    /// <summary>
    /// Colours for just this icon (lit dots, dim dots, the sun's red centre), used while the
    /// theme changes so the icon can glide ahead of the page. Null: the theme's own colours.
    /// </summary>
    public Brush? InkBrush, OffBrush, RedBrush;

    bool _from;

    public DotIcon()
    {
        Width = 20; Height = 20;
        IsHitTestVisible = false;
        // listen only while on screen: a static event would otherwise keep closed windows alive
        Loaded += (_, _) => { ThemeManager.Changed -= IconChanged; ThemeManager.Changed += IconChanged; };
        Unloaded += (_, _) => ThemeManager.Changed -= IconChanged;
    }

    // only redraw while on screen; drawing happens on the next frame anyway
    void IconChanged() { if (IsVisible && PresentationSource.FromVisual(this) != null) InvalidateVisual(); }

    /// <summary>
    /// A fixed (frozen) copy of a brush's colour. The icon draws with these rather than the
    /// shared live palette brushes: redrawing with the live ones while the theme was
    /// recolouring them crashed WPF's renderer.
    /// </summary>
    static Brush Fixed(Brush? b, Color fallback)
    {
        var c = b is SolidColorBrush s ? s.Color : fallback;
        var f = new SolidColorBrush(c);
        f.Freeze();
        return f;
    }

    void Morph(bool from)
    {
        _from = from;
        if (!IsLoaded || !Theme.Motion) { Progress = 1; return; }
        BeginAnimation(ProgressProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(560))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    protected override void OnRender(DrawingContext dc)
    {
        // the glide's own animated brushes are used as they are; the palette's are copied
        var ink = InkBrush ?? Fixed(TryFindResource("Ink") as Brush, Colors.Black);
        var off = OffBrush ?? Fixed(TryFindResource("DotOff") as Brush, Colors.Gray);
        var red = RedBrush ?? Fixed(TryFindResource("Red") as Brush, Colors.Red);
        double pitch = Math.Min(ActualWidth, ActualHeight) / 7, rOn = pitch * 0.38, rOff = pitch * 0.2;
        string[] a = _from ? Moon : Sun, b = IsMoon ? Moon : Sun;
        for (int y = 0; y < 7; y++)
            for (int x = 0; x < 7; x++)
            {
                // each dot flips a little after the one up-left of it
                double t = Math.Clamp((Progress * 1.7 - (x + y) / 12.0 * 0.7) / 0.9, 0, 1);
                bool was = a[y][x] == '1', now = b[y][x] == '1';
                double on = was == now ? (now ? 1 : 0) : (now ? t : 1 - t);
                double r = rOff + (rOn - rOff) * on;
                bool centre = x == 3 && y == 3 && !IsMoon;
                var fill = on > 0.5 ? (centre ? red : ink) : off;
                dc.DrawEllipse(fill, null, new Point(x * pitch + pitch / 2, y * pitch + pitch / 2), r, r);
            }
    }
}

/// <summary>
/// Stand-in for the phone's wallpaper when the phone can only share its main colour:
/// a plain fill in that colour.
/// </summary>
public class DotField : FrameworkElement
{
    public static readonly DependencyProperty DotBrushProperty = DependencyProperty.Register(
        nameof(DotBrush), typeof(Brush), typeof(DotField),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush? DotBrush { get => (Brush?)GetValue(DotBrushProperty); set => SetValue(DotBrushProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        // no pattern: a plain screen in the wallpaper's colour until the wallpaper itself arrives
        var brush = DotBrush ?? TryFindResource("Tile2") as Brush ?? Brushes.Gray;
        dc.DrawRectangle(brush, null, new Rect(RenderSize));
    }}

/// <summary>
/// A bar chart made of dots: one column per value, the newest column in red.
/// Columns grow upward in a staggered rise when the values change.
/// </summary>
public class DotBars : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(int[]), typeof(DotBars),
        new FrameworkPropertyMetadata(Array.Empty<int>(), FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((DotBars)d).Rise()));

    public static readonly DependencyProperty RowsProperty = DependencyProperty.Register(
        nameof(Rows), typeof(int), typeof(DotBars), new FrameworkPropertyMetadata(8, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GrowProperty = DependencyProperty.Register(
        nameof(Grow), typeof(double), typeof(DotBars), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public int[] Values { get => (int[])GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public int Rows { get => (int)GetValue(RowsProperty); set => SetValue(RowsProperty, value); }
    public double Grow { get => (double)GetValue(GrowProperty); set => SetValue(GrowProperty, value); }

    Brush _on = Brushes.Black, _off = Brushes.Gray, _hot = Brushes.Red;

    public DotBars()
    {
        Loaded += (_, _) =>
        {
            Pick(); InvalidateVisual(); Rise();
            ThemeManager.Changed -= Repaint;
            ThemeManager.Changed += Repaint;
        };
        Unloaded += (_, _) => ThemeManager.Changed -= Repaint; // no leaks through the static event
    }

    void Repaint() { Pick(); InvalidateVisual(); }

    void Pick()
    {
        _on = TryFindResource("Ink") as Brush ?? _on;
        _off = TryFindResource("DotOff") as Brush ?? _off;
        _hot = TryFindResource("Red") as Brush ?? _hot;
    }

    void Rise()
    {
        if (!IsLoaded || !Theme.Motion) { Grow = 1; return; }
        BeginAnimation(GrowProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(700))
        {
            EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut }
        });
    }

    protected override void OnRender(DrawingContext dc)
    {
        var v = Values ?? Array.Empty<int>();
        if (v.Length == 0 || ActualWidth <= 0) return;
        int max = Math.Max(1, v.Max());
        double cellW = ActualWidth / v.Length;
        double pitch = Math.Min(cellW, ActualHeight / Rows);
        double r = pitch * 0.32;
        for (int i = 0; i < v.Length; i++)
        {
            // each column starts a little after the one before it
            double local = Math.Clamp(Grow * 1.6 - i * 0.6 / v.Length, 0, 1);
            int lit = v[i] == 0 ? 0 : Math.Max(1, (int)Math.Round(v[i] / (double)max * Rows * local));
            double cx = i * cellW + cellW / 2;
            for (int row = 0; row < Rows; row++)
            {
                double cy = ActualHeight - row * pitch - pitch / 2;
                Brush b = row < lit ? (i == v.Length - 1 ? _hot : _on) : _off;
                dc.DrawEllipse(b, null, new Point(cx, cy), r, r);
            }
        }
    }
}
