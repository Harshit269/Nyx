using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PhoneBridge;

/// <summary>What the tray panel shows, read from the main window each time it opens.</summary>
public record FlyoutState(
    string Name, bool Connected, string State, ImageSource? Wallpaper, Brush? ScreenColour,
    bool Bluetooth, bool WiFi, string Battery, int Unread, IReadOnlyList<ContactItem> Recent);

/// <summary>
/// The small panel that slides up from the tray icon, like Phone Link's panel in the Start
/// menu: the phone, its status, shortcuts and recent people. Closes when you click away.
/// </summary>
public partial class TrayFlyout : Window
{
    readonly MainWindow _main;
    bool _closing;

    public TrayFlyout(MainWindow main, FlyoutState s)
    {
        InitializeComponent();
        _main = main;
        Fill(s);
        Loaded += (_, _) => { PlaceByTray(); Animate(true); };
    }

    void Fill(FlyoutState s)
    {
        PhoneLabel.Text = s.Connected ? s.Name : "Phone not connected";
        State.Text = s.State;
        StateDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, s.Connected ? "Red" : "LineStrong");
        Bt.Opacity = s.Bluetooth ? 1 : 0.3;
        Wifi.Opacity = s.WiFi ? 1 : 0.3;
        Battery.Text = s.Battery;
        Wall.Source = s.Wallpaper;
        if (s.Wallpaper == null && s.ScreenColour != null) Screen.Background = s.ScreenColour;
        Screen.Opacity = s.Connected ? 1 : 0.4;
        NotifLabel.Text = s.Unread > 0 ? "Notifications · " + s.Unread : "Notifications";
        Recent.ItemsSource = s.Recent;
        RecentCap.Visibility = Recent.Visibility = s.Recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Bottom-right, just above the taskbar (or by it, wherever the taskbar is).</summary>
    void PlaceByTray()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 4;
        Top = area.Bottom - ActualHeight - 4;
    }

    void Animate(bool opening, Action? done = null)
    {
        if (!Theme.Motion) { Opacity = opening ? 1 : 0; done?.Invoke(); return; }
        // quick and front-loaded: most of the movement lands in the first few frames
        IEasingFunction ease = opening ? new QuarticEase { EasingMode = EasingMode.EaseOut } : new CubicEase { EasingMode = EasingMode.EaseIn };
        var dur = TimeSpan.FromMilliseconds(opening ? 170 : 120);
        var fade = new DoubleAnimation(opening ? 0 : 1, opening ? 1 : 0, dur) { EasingFunction = ease };
        if (done != null) fade.Completed += (_, _) => done();
        BeginAnimation(OpacityProperty, fade);
        Slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(opening ? 12 : 0, opening ? 0 : 8, dur) { EasingFunction = ease });
    }

    /// <summary>Slide away, then close.</summary>
    public void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        Animate(false, Close);
    }

    void Window_Deactivated(object? sender, EventArgs e) => Dismiss();

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Dismiss();
    }

    void Go(int page) { Dismiss(); _main.ShowFromTray(page); }

    void Messages_Click(object sender, RoutedEventArgs e) => Go(MainWindow.PageSmsIndex);
    void Calls_Click(object sender, RoutedEventArgs e) => Go(MainWindow.PageCallIndex);
    void Notifs_Click(object sender, RoutedEventArgs e) => Go(MainWindow.PageNotificationsIndex);
    void Open_Click(object sender, RoutedEventArgs e) => Go(-1);
    void Mirror_Click(object sender, RoutedEventArgs e) => Go(MainWindow.PageScreenIndex);

    void SendFiles_Click(object sender, RoutedEventArgs e)
    {
        Dismiss();
        _main.PickAndSendFiles();
    }
    void Quit_Click(object sender, RoutedEventArgs e) { Dismiss(); _main.QuitFromTray(); }

    void Find_Click(object sender, RoutedEventArgs e)
    {
        _main.RingFromTray();
        FindLabel.Text = "Ringing…";
    }

    void Person_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ContactItem c)
        {
            Dismiss();
            _main.ShowFromTray(MainWindow.PageCallIndex, c.Number);
        }
    }
}
