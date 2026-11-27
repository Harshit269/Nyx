using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhoneBridge;

/// <summary>
/// The light/dark switch: the new theme spreads out of the theme button as a gooey, dotted
/// blob, and the page is never blank. A picture of the window in its NEW look is laid over
/// the (still old) window and revealed through a mask with two parts:
///  1. dotted goo: a growing circle plus 18 short wriggling tendrils, melted together like
///     SVG's goo filter (blur, then alpha × 22 − 9), seen through a 9 px dot grid whose dots
///     swell from 1.6 to 6.4 until they merge;
///  2. a solid core 90 px inside the blob's edge with a soft rim, so the new page reads
///     crisply inside and only the outer rim and tendrils are dotted.
/// At the end the theme swaps under the picture and the picture goes: both look the same at
/// that moment, so nothing flashes. The pressed button stays sharp on top and glides to its
/// new colours.
///
/// The masks run on the graphics card; only the goo's small grid (one value per 6 px) is
/// worked out each frame on the processor.
/// </summary>
public static class GooTransition
{
    const double FieldCell = 6;              // the goo is worked out on a 6 px grid
    const double Pitch = 9;                  // the dots' grid
    const double DotTo = 6.4; // 6.4 > 9·√2 / 2, so by the end the dots have merged
    const double SpreadMs = 1300;
    const double CoreInset = 90, CoreSoft = 18;

    /// <param name="newLook">The window as it will look in the new theme (a device-pixel picture).</param>
    /// <param name="swap">Switches the theme underneath; called once at the end (or when cut short).</param>
    /// <returns>Jumps straight to the end. Null if the overlay couldn't be set up (swap then yourself).</returns>
    public static Action? Run(Window window, Point origin, Control? sharp, BitmapSource newLook, Action swap, ButtonGlide? glide, bool toLight = false)
    {
        // Going to light uses the website's start: fine 1.6 px dots, thick tapering tendrils that
        // all shoot out at once, and the solid middle held back so the first part stays dotted.
        // (Light dots on dark read well when small; going to dark keeps the bolder 2.4 px start.)
        double dotFrom = toLight ? 1.6 : 2.4;
        if (window.Content is not FrameworkElement root || root.ActualWidth < 1) return null;
        var layer = AdornerLayer.GetAdornerLayer(root);
        if (layer == null) return null;

        double w = root.ActualWidth, h = root.ActualHeight;
        int cols = (int)Math.Ceiling(w / FieldCell) + 1, rows = (int)Math.Ceiling(h / FieldCell) + 1;
        // as far as the farthest corner, and a bit, so even its rim clears the window
        double maxR = new[] { new Point(0, 0), new Point(w, 0), new Point(0, h), new Point(w, h) }.Max(p => (p - origin).Length) + 80;

        // The picture of the new look is shown through masks the graphics card applies: the goo
        // (a small grid, one value per 6 px, the only thing uploaded each frame), a tiled dot,
        // and a soft-edged solid core. Nothing on screen is recoloured while it plays.
        ImageBrush Picture() => new(newLook) { Stretch = Stretch.Fill, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, w, h) };
        var field = new WriteableBitmap(cols, rows, 96, 96, PixelFormats.Pbgra32, null);
        var cells = new byte[cols * rows * 4];
        var gooMask = new ImageBrush(field)
        {
            Stretch = Stretch.Fill, ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, cols * FieldCell, rows * FieldCell) // cell i covers [6i, 6i+6]
        };
        RenderOptions.SetBitmapScalingMode(gooMask, BitmapScalingMode.Linear);
        var dot = new EllipseGeometry(new Point(Pitch / 2, Pitch / 2), dotFrom, dotFrom);
        var dotMask = new DrawingBrush(new GeometryDrawing(Brushes.Black, null, dot))
        {
            TileMode = TileMode.Tile, Stretch = Stretch.None,
            Viewbox = new Rect(0, 0, Pitch, Pitch), ViewboxUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, Pitch, Pitch), ViewportUnits = BrushMappingMode.Absolute
        };
        var dotted = new Border { OpacityMask = dotMask, Child = new System.Windows.Shapes.Rectangle { Fill = Picture(), OpacityMask = gooMask } };

        // the solid core: a soft-edged circle (a radial fade standing in for an 18 px blur)
        var coreFade = new RadialGradientBrush { MappingMode = BrushMappingMode.Absolute, Center = origin, GradientOrigin = origin, RadiusX = 1, RadiusY = 1 };
        var stopIn = new GradientStop(Colors.Black, 0);
        var stopMid = new GradientStop(Color.FromArgb(128, 0, 0, 0), 0.5);
        coreFade.GradientStops.Add(new GradientStop(Colors.Black, 0));
        coreFade.GradientStops.Add(stopIn);
        coreFade.GradientStops.Add(stopMid);
        coreFade.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        var core = new System.Windows.Shapes.Rectangle { Fill = Picture(), OpacityMask = coreFade, Opacity = 0 };

        var host = new Grid { IsHitTestVisible = false };
        host.Children.Add(dotted);
        host.Children.Add(core);
        if (sharp != null && sharp.IsVisible)
        {
            // the real button stays on top: cut its circle out of the overlay
            var c = sharp.TranslatePoint(new Point(sharp.ActualWidth / 2, sharp.ActualHeight / 2), root);
            double r = Math.Min(sharp.ActualWidth, sharp.ActualHeight) / 2;
            host.Clip = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, w, h)),
                new EllipseGeometry(c, r, r));
        }
        var veil = new Host(root, host);
        layer.Add(veil);

        var glider = glide != null && sharp != null ? new Glider(sharp, glide) : null;

        // 18 short tendrils, evenly spread with a little jitter
        var rnd = new Random();
        var tendrils = Enumerable.Range(0, 18).Select(i => new Tendril(
            i / 18.0 * Math.PI * 2 + (rnd.NextDouble() - 0.5) * (toLight ? 0.35 : 0.3), maxR * (0.25 + rnd.NextDouble() * 0.25),
            rnd.NextDouble() * 6,
            toLight ? 16 : 8 + rnd.NextDouble() * 22,        // website: a steady 16 px wriggle
            toLight ? 39 : 14 + rnd.NextDouble() * 16,       // website: 39 px at the root, ~6 px at the tip
            toLight ? 0 : rnd.NextDouble() * 0.08)).ToArray();
        var goo = new float[cols * rows];
        var tmp = new float[cols * rows];

        var clock = Stopwatch.StartNew();
        bool done = false;
        double worst = 0, last = 0, work = 0, workMax = 0, first = -1;
        int frames = 0;
        EventHandler? tick = null;

        void Finish()
        {
            if (done) return;
            done = true;
            CompositionTarget.Rendering -= tick;
            var sw = Stopwatch.StartNew();
            try { glider?.End(); } catch { }
            try { swap(); } catch (Exception ex) { Diag.Log("theme swap: " + ex.Message); }
            Diag.Log($"theme goo: {frames} frames in {clock.ElapsedMilliseconds} ms, longest gap {worst:0} ms, " +
                     $"work {work / Math.Max(1, frames):0.0} ms avg / {workMax:0.0} max, end swap {sw.ElapsedMilliseconds} ms, first frame +{first:0} ms");
            try { layer.Remove(veil); } catch { }
        }

        tick = (_, _) =>
        {
            if (done) return;
            try
            {
                double ms = clock.Elapsed.TotalMilliseconds;
                frames++;
                if (first < 0) first = ms;
                if (frames > 1) worst = Math.Max(worst, ms - last);
                last = ms;
                double t = Math.Clamp(ms / SpreadMs, 0, 1);
                // t^2.6 alone barely moves for the first ~0.4 s, so a fast ease-out "pop" leads
                // (bursting out at once, up to 22% of the way) and the curve takes over from it at
                // about t = 0.55; it starts 40 px wide so the very first frame already shows it
                double pop = 0.22 * (1 - Math.Pow(1 - Math.Min(1, t / 0.55), 3));
                double mainR = Math.Max(40 * Math.Min(1, t * 20), maxR * Math.Max(Math.Pow(t, 2.6), pop));
                var fw = Stopwatch.StartNew();
                Field(goo, tmp, cols, rows, origin, mainR, tendrils, t);
                for (int i = 0; i < goo.Length; i++)
                {
                    byte al = (byte)(goo[i] * 255);
                    int j = i * 4;
                    cells[j] = cells[j + 1] = cells[j + 2] = cells[j + 3] = al; // white, premultiplied
                }
                field.WritePixels(new Int32Rect(0, 0, cols, rows), cells, cols * 4, 0);
                dot.RadiusX = dot.RadiusY = dotFrom + (DotTo - dotFrom) * t * t * t;
                // to light: the solid middle starts further in and catches up by the end, so the
                // opening stays a dotted blob like the website's rather than filling in at once
                double coreR = Math.Max(0, mainR - CoreInset - (toLight ? 170 * (1 - t) : 0));
                if (coreR < 1) core.Opacity = 0;
                else
                {
                    // solid to coreR − 2σ, half at coreR, gone by coreR + 2σ
                    double outer = coreR + 2 * CoreSoft;
                    coreFade.RadiusX = coreFade.RadiusY = outer;
                    stopIn.Offset = Math.Max(0, coreR - 2 * CoreSoft) / outer;
                    stopMid.Offset = coreR / outer;
                    core.Opacity = 1;
                }
                double fms = fw.Elapsed.TotalMilliseconds; work += fms; workMax = Math.Max(workMax, fms);
                if (t >= 1) Finish();
            }
            catch (Exception ex)
            {
                // never leave the window half-covered: finish the switch instead
                Diag.Log("theme goo stopped: " + ex.Message);
                Finish();
            }
        };
        CompositionTarget.Rendering += tick;
        return Finish;
    }

    sealed record Tendril(double Angle, double Length, double Phase, double Wiggle, double Width, double Delay);

    /// <summary>One frame of the goo on its 6 px grid: rasterise, blur ~12 px, sharpen to 0..1.</summary>
    static void Field(float[] a, float[] tmp, int cols, int rows, Point o, double coreR, Tendril[] tendrils, double t)
    {
        Array.Clear(a);
        for (int y = 0; y < rows; y++)
        {
            double dy = y * FieldCell + FieldCell / 2 - o.Y;
            if (Math.Abs(dy) >= coreR) continue;
            double half = Math.Sqrt(coreR * coreR - dy * dy);
            int x0 = Math.Max(0, (int)Math.Ceiling((o.X - half - FieldCell / 2) / FieldCell));
            int x1 = Math.Min(cols - 1, (int)Math.Floor((o.X + half - FieldCell / 2) / FieldCell));
            for (int x = x0; x <= x1; x++) a[y * cols + x] = 1;
        }
        // 12 round-ended segments per tendril, tapering to the tip, wriggling, growing with ease-out
        foreach (var k in tendrils)
        {
            double u = Math.Clamp((t - k.Delay) / (1 - k.Delay), 0, 1);
            double len = k.Length * (1 - Math.Pow(1 - u, 3));
            if (len < 1) continue;
            double ca = Math.Cos(k.Angle), sa = Math.Sin(k.Angle), px0 = o.X, py0 = o.Y;
            for (int s = 0; s < 12; s++)
            {
                double q = (s + 1) / 12.0, along = len * q, off = Math.Sin(s * 0.8 + t * 10 + k.Phase) * k.Wiggle * q;
                double x = o.X + ca * along - sa * off, y = o.Y + sa * along + ca * off;
                Segment(a, cols, rows, px0, py0, x, y, Math.Max(1, k.Width * (1 - q * 0.85)) / 2);
                px0 = x; py0 = y;
            }
        }
        BoxBlur(a, tmp, cols, rows, 2); // two box passes of 2 cells ≈ a 12 px gaussian
        BoxBlur(a, tmp, cols, rows, 2);
        for (int i = 0; i < a.Length; i++) a[i] = Math.Clamp(a[i] * 22 - 9, 0, 1); // the goo filter's sharpening
    }

    static void Segment(float[] a, int cols, int rows, double x1, double y1, double x2, double y2, double r)
    {
        int cx0 = Math.Max(0, (int)((Math.Min(x1, x2) - r) / FieldCell) - 1), cx1 = Math.Min(cols - 1, (int)((Math.Max(x1, x2) + r) / FieldCell) + 1);
        int cy0 = Math.Max(0, (int)((Math.Min(y1, y2) - r) / FieldCell) - 1), cy1 = Math.Min(rows - 1, (int)((Math.Max(y1, y2) + r) / FieldCell) + 1);
        double dx = x2 - x1, dy = y2 - y1, ll = dx * dx + dy * dy;
        for (int cy = cy0; cy <= cy1; cy++)
            for (int cx = cx0; cx <= cx1; cx++)
            {
                double px = cx * FieldCell + FieldCell / 2, py = cy * FieldCell + FieldCell / 2;
                double k = ll == 0 ? 0 : Math.Clamp(((px - x1) * dx + (py - y1) * dy) / ll, 0, 1);
                double ex = px - (x1 + k * dx), ey = py - (y1 + k * dy);
                // soft edge over one cell, so thin tendrils still leave something to blur
                float cover = (float)Math.Clamp((r - Math.Sqrt(ex * ex + ey * ey)) / FieldCell + 0.5, 0, 1);
                int i = cy * cols + cx;
                if (cover > a[i]) a[i] = cover;
            }
    }

    static void BoxBlur(float[] a, float[] t, int w, int h, int r)
    {
        float n = 2 * r + 1;
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            float s = 0;
            for (int x = -r; x <= r; x++) s += a[row + Math.Clamp(x, 0, w - 1)];
            for (int x = 0; x < w; x++)
            {
                t[row + x] = s / n;
                s += a[row + Math.Min(w - 1, x + r + 1)] - a[row + Math.Max(0, x - r)];
            }
        }
        for (int x = 0; x < w; x++)
        {
            float s = 0;
            for (int y = -r; y <= r; y++) s += t[Math.Clamp(y, 0, h - 1) * w + x];
            for (int y = 0; y < h; y++)
            {
                a[y * w + x] = s / n;
                s += t[Math.Min(h - 1, y + r + 1) * w + x] - t[Math.Max(0, y - r) * w + x];
            }
        }
    }

    // ------------------------------------------------------------ the button's own change

    public sealed record ButtonGlide(Color TileFrom, Color TileTo, Color LineFrom, Color LineTo,
        Color InkFrom, Color InkTo, Color OffFrom, Color OffTo, Color RedFrom, Color RedTo);

    /// <summary>
    /// Glides the button (face, edge, and its dot icon) from its old colours to its new ones
    /// over the first half second, ahead of the page. WPF animates the colours itself, on
    /// brushes that belong to this button alone. (Swapping brushes in the button's resources
    /// and recolouring them every frame instead made WPF's renderer crash.)
    /// </summary>
    sealed class Glider
    {
        static readonly Duration Time = TimeSpan.FromMilliseconds(480);
        readonly Control _c;
        readonly List<DotIcon> _icons = new();

        public Glider(Control c, ButtonGlide g)
        {
            _c = c;
            c.Background = Animated(g.TileFrom, g.TileTo);
            c.BorderBrush = Animated(g.LineFrom, g.LineTo);
            Collect(c);
            foreach (var icon in _icons)
            {
                icon.InkBrush = Animated(g.InkFrom, g.InkTo);
                icon.OffBrush = Animated(g.OffFrom, g.OffTo);
                icon.RedBrush = Animated(g.RedFrom, g.RedTo);
                icon.InvalidateVisual();
            }
        }

        static SolidColorBrush Animated(Color from, Color to)
        {
            var b = new SolidColorBrush(from);
            b.BeginAnimation(SolidColorBrush.ColorProperty, new System.Windows.Media.Animation.ColorAnimation(to, Time)
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            });
            return b;
        }

        void Collect(DependencyObject d)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            {
                var ch = VisualTreeHelper.GetChild(d, i);
                if (ch is DotIcon icon) _icons.Add(icon);
                Collect(ch);
            }
        }

        /// <summary>Back to the shared palette (which by now has the same colours).</summary>
        public void End()
        {
            _c.SetResourceReference(Control.BackgroundProperty, "Tile");
            _c.ClearValue(Control.BorderBrushProperty);
            foreach (var icon in _icons)
            {
                icon.InkBrush = icon.OffBrush = icon.RedBrush = null;
                icon.InvalidateVisual();
            }
        }
    }

    /// <summary>Lays one element over the whole window.</summary>
    sealed class Host : Adorner
    {
        readonly UIElement _child;
        public Host(UIElement adorned, UIElement child) : base(adorned)
        {
            _child = child;
            IsHitTestVisible = false;
            AddVisualChild(child);
        }
        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => _child;
        protected override Size MeasureOverride(Size constraint) { _child.Measure(AdornedElement.RenderSize); return AdornedElement.RenderSize; }
        protected override Size ArrangeOverride(Size finalSize) { _child.Arrange(new Rect(AdornedElement.RenderSize)); return finalSize; }
    }
}
