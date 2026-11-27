using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace PhoneBridge;

/// <summary>
/// Shared look and motion for the two floating panels. Motion rules: enter with a
/// strong ease-out from 96% scale (never from nothing), leave faster than you came,
/// and skip all of it when Windows animations are switched off.
/// </summary>
public abstract class FloatingPanel : Window
{
    protected readonly Border Card;
    readonly ScaleTransform _scale = new(1, 1);
    readonly TranslateTransform _shift = new(0, 0);
    bool _closing;

    protected FloatingPanel(string surfaceKey, double width)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Theme.Paint("Clear");
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Width = width;
        SizeToContent = SizeToContent.Height;
        FontFamily = Theme.Font("UiFont");
        FontSize = 14;
        UseLayoutRounding = true;

        Card = new Border
        {
            CornerRadius = new CornerRadius(22),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(18, 14, 18, 22), // room for the shadow
            RenderTransformOrigin = new Point(0.5, 1),
            RenderTransform = new TransformGroup { Children = { _scale, _shift } },
            Effect = new DropShadowEffect
            {
                Color = Colors.Black, Opacity = 0.22, BlurRadius = 26, ShadowDepth = 7, Direction = 270
            }
        };
        Content = Card;
        // resource references, so an open panel repaints when the theme switches
        Card.SetResourceReference(Border.BackgroundProperty, surfaceKey);
        Card.SetResourceReference(Border.BorderBrushProperty, "Line");
        ThemeManager.ApplyTitleBar(this);

        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth;
            Top = PlaceAtTop ? area.Top + 6 : area.Bottom - ActualHeight;
            Enter();
        };
    }

    protected virtual bool PlaceAtTop => false;

    static readonly KeySpline StrongEaseOut = new(0.23, 1, 0.32, 1);

    static DoubleAnimationUsingKeyFrames Ease(double from, double to, int ms)
    {
        var a = new DoubleAnimationUsingKeyFrames();
        a.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        a.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)), StrongEaseOut));
        return a;
    }

    void Enter()
    {
        if (!Theme.Motion) return;
        double rise = PlaceAtTop ? -10 : 10;
        Card.BeginAnimation(OpacityProperty, Ease(0, 1, 180));
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, Ease(0.96, 1, 220));
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, Ease(0.96, 1, 220));
        _shift.BeginAnimation(TranslateTransform.YProperty, Ease(rise, 0, 220));
    }

    /// <summary>Fade out quickly, then close.</summary>
    public void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        if (!Theme.Motion)
        {
            Close();
            return;
        }
        var fade = Ease(Card.Opacity, 0, 130);
        fade.Completed += (_, _) => Close();
        Card.BeginAnimation(OpacityProperty, fade);
    }

    protected static TextBlock MakeText(string text, string inkKey, double size, FontWeight weight) => new()
    {
        Text = text,
        Foreground = Theme.Paint(inkKey),
        FontSize = size,
        FontWeight = weight,
        TextTrimming = TextTrimming.CharacterEllipsis
    };
}

/// <summary>
/// Bottom-right notice for a new notification, text or Nyx message. Same surface as the
/// app's tiles: the source on a small line of its own, then the title and the text.
/// A thin red line runs out while it is shown; hovering stops the clock.
/// </summary>
public sealed class ToastWindow : FloatingPanel
{
    static ToastWindow? _current;

    ToastWindow(string from, string title, string body, Action? onClick, int seconds) : base("Tile", 380)
    {
        ShowActivated = false;
        Cursor = Cursors.Hand;
        Card.Padding = new Thickness(16, 14, 12, 0);
        Card.CornerRadius = new CornerRadius(18);
        Card.ClipToBounds = true;

        // --- source line: initial disc, NAME · now, close
        var disc = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(11),
            Background = Theme.Paint("Tile2"), Margin = new Thickness(0, 0, 9, 0),
            Child = new TextBlock
            {
                Text = Theme.Initial(from), FontSize = 11, FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Paint("Ink"),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        var source = MakeText(from.ToUpperInvariant() + "  ·  now", "Ink2", 11, FontWeights.SemiBold);
        source.VerticalAlignment = VerticalAlignment.Center;

        var close = new Button
        {
            Style = Theme.StyleOf("BtnQuiet"), Width = 26, Height = 26, Padding = new Thickness(0), Opacity = 0,
            Margin = new Thickness(6, -4, 0, -4),
            ToolTip = "Dismiss", Cursor = Cursors.Hand,
            Content = new LineIcon { Data = Theme.Geo("IconClose"), Width = 12, Height = 12 }
        };
        close.Click += (_, e) => { e.Handled = true; Dismiss(); };

        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8), LastChildFill = true };
        DockPanel.SetDock(disc, Dock.Left);
        DockPanel.SetDock(close, Dock.Right);
        head.Children.Add(disc);
        head.Children.Add(close);
        head.Children.Add(source);

        // --- title and text
        var panel = new StackPanel();
        panel.Children.Add(head);
        var words = new StackPanel { Margin = new Thickness(0, 0, 4, 14) };
        if (title.Length > 0) words.Children.Add(MakeText(title, "Ink", 14.5, FontWeights.SemiBold));
        if (body.Length > 0)
        {
            var text = MakeText(body, "Ink2", 13, FontWeights.Normal);
            text.TextWrapping = TextWrapping.Wrap;
            text.MaxHeight = 40;           // two lines, then …
            text.LineHeight = 19;
            text.Margin = new Thickness(0, title.Length > 0 ? 3 : 0, 0, 0);
            words.Children.Add(text);
        }
        panel.Children.Add(words);

        // --- time left: a 2px red line along the bottom edge that runs out
        var bar = new Border
        {
            Height = 2, CornerRadius = new CornerRadius(1), Background = Theme.Paint("Red"), Margin = new Thickness(0, 0, 4, 14),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            RenderTransformOrigin = new Point(0, 0.5),
        };
        var shrink = new ScaleTransform(1, 1);
        bar.RenderTransform = shrink;
        if (!Theme.Motion) bar.Opacity = 0;
        panel.Children.Add(bar);
        Card.Child = panel;

        var countdown = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(seconds));
        var clock = countdown.CreateClock();
        clock.Completed += (_, _) => Dismiss();
        shrink.ApplyAnimationClock(ScaleTransform.ScaleXProperty, clock);

        MouseEnter += (_, _) => { clock.Controller?.Pause(); close.Opacity = 1; };
        MouseLeave += (_, _) => { clock.Controller?.Resume(); close.Opacity = 0; };

        MouseLeftButtonUp += (_, _) =>
        {
            if (onClick != null) { onClick(); Dismiss(); return; }
            var main = Application.Current.MainWindow;
            if (main != null)
            {
                if (!main.IsVisible) main.Show();
                if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal;
                main.Activate();
            }
            Dismiss();
        };

        Closed += (_, _) =>
        {
            clock.Controller?.Stop();
            if (ReferenceEquals(_current, this)) _current = null;
        };
    }

    /// <param name="onClick">What a click does; by default it brings Nyx to the front.</param>
    /// <param name="from">Who it's from, shown small on top: an app name, a contact, or Nyx itself.</param>
    public static void Show(string title, string body, Action? onClick = null, int seconds = 6, string from = "Nyx", bool always = false)
    {
        // no pop-ups over a game or full-screen app (unless it must be seen, like a shutdown you can still cancel)
        if (!always && !Quiet.PopUpsAllowed()) return;
        try { _current?.Close(); } catch { }
        _current = new ToastWindow(from.Length > 0 ? from : "Nyx", title, body, onClick, seconds);
        _current.Show();
    }
}

/// <summary>Top-right incoming call panel. Not modal: the rest of the app stays usable.</summary>
public sealed class CallWindow : FloatingPanel
{
    protected override bool PlaceAtTop => true;

    public CallWindow(string name, string number, Action answer, Action decline) : base("Tile", 396)
    {
        Title = "Incoming call";
        ShowActivated = false; // never steal the keyboard from whatever the user is typing
        Card.Padding = new Thickness(18);
        Card.SetResourceReference(Border.BorderBrushProperty, "Line");
        Card.BorderThickness = new Thickness(1);

        // avatar with a ring that pulses while the phone rings: the one moving thing here,
        // and it means something (the call is still ringing)
        var ring = new Ellipse
        {
            Width = 46, Height = 46,
            Stroke = Theme.Paint("Red"), StrokeThickness = 1.5, Opacity = 0,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1)
        };
        var disc = new Border
        {
            Width = 46, Height = 46, CornerRadius = new CornerRadius(23),
            Background = Theme.Paint("Tile2"),
            Child = new TextBlock
            {
                Text = Theme.Initial(name.Length > 0 ? name : number),
                Foreground = Theme.Paint("Ink"), FontSize = 18, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        var avatar = new Grid { Width = 62, Height = 62, Margin = new Thickness(-8, -8, 6, -8) };
        avatar.Children.Add(ring);
        avatar.Children.Add(disc);

        var who = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        string headline = name.Length > 0 ? name : (number.Length > 0 ? number : "Unknown number");
        who.Children.Add(MakeText(headline, "Ink", 18, FontWeights.SemiBold));
        string under = name.Length > 0 && number.Length > 0 ? "Calling from " + number : "Calling your phone";
        var sub = MakeText(under, "Ink2", 13, FontWeights.Normal);
        sub.Margin = new Thickness(0, 2, 0, 0);
        who.Children.Add(sub);

        var top = new DockPanel();
        DockPanel.SetDock(avatar, Dock.Left);
        top.Children.Add(avatar);
        top.Children.Add(who);

        var declineBtn = ActionButton("Decline", "BtnRed", 135);
        var answerBtn = ActionButton("Answer", "BtnInk", 0);
        declineBtn.Click += (_, _) => { decline(); Dismiss(); };
        answerBtn.Click += (_, _) => { answer(); Dismiss(); };

        var buttons = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        buttons.ColumnDefinitions.Add(new ColumnDefinition());
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(declineBtn, 0);
        Grid.SetColumn(answerBtn, 2);
        buttons.Children.Add(declineBtn);
        buttons.Children.Add(answerBtn);

        var panel = new StackPanel();
        panel.Children.Add(top);
        panel.Children.Add(buttons);
        Card.Child = panel;

        Loaded += (_, _) => { if (Theme.Motion) Pulse(ring); };
    }

    static Button ActionButton(string label, string styleKey, double iconAngle)
    {
        var icon = new LineIcon
        {
            Data = Theme.Geo("IconPhone"), Width = 16, Height = 16, Margin = new Thickness(0, 0, 9, 0),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(iconAngle)
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(icon);
        row.Children.Add(new TextBlock { Text = label, FontSize = 14 });
        return new Button { Style = Theme.StyleOf(styleKey), Content = row, Height = 44, Tag = "22" };
    }

    static void Pulse(Ellipse ring)
    {
        var grow = new DoubleAnimation(1, 1.32, TimeSpan.FromSeconds(1.5))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var fade = new DoubleAnimation(0.55, 0, TimeSpan.FromSeconds(1.5)) { RepeatBehavior = RepeatBehavior.Forever };
        ring.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        ring.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        ring.BeginAnimation(OpacityProperty, fade);
    }
}
