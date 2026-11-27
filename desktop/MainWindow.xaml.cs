using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace PhoneBridge;

public partial class MainWindow : Window
{
    const int PageHome = 0, PageNotifications = 1, PageSms = 2, PageCall = 3, PageScreen = 4, PageSetup = 5;
    static readonly string[] PageNames = { "Overview", "Notifications", "Messages", "Calls", "Mirror", "Settings" };

    readonly BridgeClient _client = new();
    readonly ObservableCollection<NavItem> _nav = new();
    readonly ObservableCollection<NotifItem> _notifs = new();
    readonly ObservableCollection<MessageRow> _recent = new();
    readonly ObservableCollection<Conversation> _convs = new();
    readonly ObservableCollection<MessageRow> _unread = new();
    readonly ObservableCollection<SmsItem> _thread = new();
    readonly ObservableCollection<ContactItem> _contactView = new();
    readonly List<ContactItem> _contacts = new();
    readonly List<DateTime> _arrivals = new(); // every notification received, for the Today tile
    readonly string[] _args = Environment.GetCommandLineArgs();
    readonly bool _demo;
    FrameworkElement[] _pages = Array.Empty<FrameworkElement>();
    int _lastPage = -1;

    DeviceEntry? _device;
    bool _userDisconnected = true;
    bool _connecting;
    bool _closing;
    bool _composing;
    bool _syncingMode;
    string _callState = "idle";
    CallWindow? _callWindow;
    readonly LanDiscovery _lan = new();
    bool _autoPaused;          // the user pressed Disconnect: don't jump back on Wi-Fi until they connect again
    string _linkKind = "";     // Wi-Fi or Bluetooth for the current (or last) link
    string _phoneId = "";
    /// <summary>What this laptop app expects from the phone app; older phones get an update note.</summary>
    const int PhoneProto = 3;
    int _phoneProto;      // the phone's Nyx id, from its hello
    string _callWindowWho = "";

    public MainWindow()
    {
        _demo = _args.Contains("--demo");
        InitializeComponent();
        LoadCachedWallpaper();
        ThemeManager.ApplyTitleBar(this);

        _nav.Add(new NavItem { Title = "Overview", Icon = Theme.Geo("IconGrid") });
        _nav.Add(new NavItem { Title = "Notifications", Icon = Theme.Geo("IconBell") });
        _nav.Add(new NavItem { Title = "Messages", Icon = Theme.Geo("IconMessage") });
        _nav.Add(new NavItem { Title = "Calls", Icon = Theme.Geo("IconPhone") });
        _nav.Add(new NavItem { Title = "Screen mirror", Icon = Theme.Geo("IconMonitor") });
        _nav.Add(new NavItem { Title = "Settings", Icon = Theme.Geo("IconSettings") });
        NavList.ItemsSource = _nav;
        _pages = new FrameworkElement[] { PageOverview, PageNotifs, PageMessages, PageCalls, PageMirror, PageSettings };

        NotifList.ItemsSource = _notifs;
        RecentList.ItemsSource = _recent;
        ConvList.ItemsSource = WhatsAppEnabled ? _waConvs : _convs;
        UnreadList.ItemsSource = _unread;
        ThreadList.ItemsSource = _thread;
        ContactList.ItemsSource = _contactView;

        _client.MessageReceived += msg => Dispatcher.InvokeAsync(() => OnMessage(msg));
        _client.Disconnected += () => Dispatcher.InvokeAsync(() => OnDisconnected());

        ThemeManager.Changed += SyncThemeControls;
        ThemeManager.Switching += OnThemeSwitching;
        // the picture of the other theme is taken as the pointer arrives, so the click starts at once
        ThemeBtn.MouseEnter += (_, _) => ThemeManager.Prepare(this);
        SyncThemeControls();

        _lan.PhoneSeen += (id, ep, name) => Dispatcher.InvokeAsync(() => OnPhoneSeen(id, ep, name));
        AllowControlBox.IsChecked = AppPrefs.Get("allowControl", true);
        StartWithWindowsBox.IsChecked = AppPrefs.StartWithWindows;
        PopupsInGamesBox.IsChecked = AppPrefs.Get("popupsInGames", false);
        TodayWholeDayBox.IsChecked = AppPrefs.Get("todayWholeDay", true);
        MirrorSoundBox.IsChecked = AppPrefs.Get("mirrorSound", true);
        WifiAdbBox.IsChecked = AppPrefs.Get("wifiAdb", true);
        var v = typeof(MainWindow).Assembly.GetName().Version;
        VersionText.Text = v == null ? "Nyx" : v.Build > 0 ? $"Nyx {v.Major}.{v.Minor}.{v.Build}" : $"Nyx {v.Major}.{v.Minor}";
        if (v != null) VersionDots.Text = $"V{v.Major}.{v.Minor}"; // the big dotted badge, never stale again
        // USB quality over Wi-Fi: whenever the phone is plugged in, make sure its Wi-Fi debugging is on
        _wifiAdbTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _wifiAdbTimer.Tick += async (_, _) =>
        {
            await ArmWifiAdb();
            // you chose the cable: relink through it whenever it's plugged in
            if (LinkPrefer == "usb" && _client.Kind != "USB" && !_connecting && !_closing) await ConnectUsbAsync(quiet: true);
        };
        _wifiAdbTimer.Start();
        UsbSoundBox.IsChecked = AppPrefs.Get("usbSound", true);
        VirtualCam.FillFrame = AppPrefs.Get("webcamFill", false);
        WebcamFillBox.IsChecked = VirtualCam.FillFrame;
        RefreshPaired();
        // at sign-in (or with --minimized): straight to the tray, nothing on screen or taskbar
        if (_args.Contains("--minimized") || AppPrefs.StartedAtSignIn)
        {
            WindowState = WindowState.Minimized;
            EventHandler? once = null;
            once = (_, _) => { ContentRendered -= once; Hide(); WindowState = WindowState.Normal; };
            ContentRendered += once;
        }

        PageSub.Text = DateTime.Now.ToString("dddd, d MMMM");
        ResetThread();
        RefreshEmptyStates();
        RefreshToday();
        SetupNotifApps();
        // once a minute: the dots move along with the clock, and the date turns over at midnight
        var minute = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        minute.Tick += (_, _) => { PageSub.Text = DateTime.Now.ToString("dddd, d MMMM"); RefreshToday(); };
        minute.Start();

        int start = PageHome;
        int i = Array.IndexOf(_args, "--page");
        if (i >= 0 && i + 1 < _args.Length && int.TryParse(_args[i + 1], out int p) && p >= 0 && p < PageNames.Length) start = p;
        NavList.SelectedIndex = start;
    }

    // ------------------------------------------------------------ navigation

    void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_pages.Length == 0 || NavList.SelectedIndex < 0) return;        int page = NavList.SelectedIndex;
        for (int i = 0; i < _pages.Length; i++)
            _pages[i].Visibility = i == page ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = PageNames[page].ToUpperInvariant();
        // Messages always opens on the list with no conversation open; pick one to read it
        if (page == PageSms && _lastPage != PageSms && !_composing && ConvList.SelectedItem != null)
        {
            ConvList.SelectedItem = null;
            ResetThread();
            RefreshEmptyStates();
        }
        _lastPage = page;
        if (page == PageNotifications) _nav[PageNotifications].Count = 0;
        if (page == PageScreen) RefreshMirror();
        if (_pages[page] == PageSettings) _ = RefreshWakeSetup();
        Enter(_pages[page]);
        Dispatcher.InvokeAsync(MovePuck, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    void Go(int page) => NavList.SelectedIndex = page;

    void GoMessages_Click(object sender, RoutedEventArgs e) => Go(PageSms);
    void GoNotifs_Click(object sender, RoutedEventArgs e) => Go(PageNotifications);
    System.Windows.Controls.Primitives.Popup? _linkPicker;

    /// <summary>The top-bar pill: a small Nyx card to pick how the phone is linked, Wi-Fi or the USB cable.</summary>
    void LinkPill_Click(object sender, RoutedEventArgs e)
    {
        if (_linkPicker?.IsOpen == true) { _linkPicker.IsOpen = false; return; }
        string now = _client.IsConnected ? _client.Kind : "";
        var list = new StackPanel();
        var head = new TextBlock { Text = "LINK", Style = Theme.StyleOf("Cap"), Margin = new Thickness(12, 6, 12, 8) };
        list.Children.Add(head);

        var popup = new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = LinkPill, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            VerticalOffset = 8, StaysOpen = false, AllowsTransparency = true,
            PopupAnimation = Theme.Motion ? System.Windows.Controls.Primitives.PopupAnimation.Fade : System.Windows.Controls.Primitives.PopupAnimation.None
        };

        Border Row(string icon, string title, string sub, bool current, Action pick)
        {
            var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 4, 0) };
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Online");
            dot.Visibility = current ? Visibility.Visible : Visibility.Hidden;
            var iconBox = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(18), Margin = new Thickness(0, 0, 12, 0) };
            iconBox.SetResourceReference(Border.BackgroundProperty, "Tile2");
            var glyph = new LineIcon { Data = Theme.Geo(icon), Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            glyph.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Ink");
            iconBox.Child = glyph;
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var t = new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            var st = new TextBlock { Text = sub, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            st.SetResourceReference(TextBlock.ForegroundProperty, "Ink2");
            words.Children.Add(t);
            words.Children.Add(st);
            var dock = new DockPanel();
            DockPanel.SetDock(iconBox, Dock.Left);
            DockPanel.SetDock(dot, Dock.Right);
            dock.Children.Add(iconBox);
            dock.Children.Add(dot);
            dock.Children.Add(words);
            var row = new Border { Child = dock, Padding = new Thickness(10), CornerRadius = new CornerRadius(14), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "Tile2");
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += (_, _) => { popup.IsOpen = false; pick(); };
            System.Windows.Automation.AutomationProperties.SetName(row, title);
            return row;
        }

        list.Children.Add(Row("IconWifi", "Wi-Fi", "Links by itself on the same Wi-Fi", now == "Wi-Fi",
            async () => await SwitchLink("wifi", fromPhone: false)));
        list.Children.Add(Row("IconPlug", "USB cable", "Through the cable, steady and fast", now == "USB",
            async () => await SwitchLink("usb", fromPhone: false)));
        list.Children.Add(Row("IconBluetooth", _iphone.Connected ? _iphone.DeviceName : "iPhone",
            _iphone.Connected ? "Notifications over Bluetooth" : "Notifications over Bluetooth, no app needed", _iphone.Connected,
            async () => await ConnectIphoneAsync(quiet: false)));

        var card = new Border
        {
            Child = list, Width = 300, Padding = new Thickness(6), CornerRadius = new CornerRadius(20), BorderThickness = new Thickness(1),
            Margin = new Thickness(12), // room for the shadow
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.35, Direction = 270 }
        };
        card.SetResourceReference(Border.BackgroundProperty, "Tile");
        card.SetResourceReference(Border.BorderBrushProperty, "Line");
        System.Windows.Documents.TextElement.SetFontFamily(card, Theme.Font("UiFont"));
        popup.Child = card;
        popup.HorizontalOffset = LinkPill.ActualWidth - 300 - 12; // right edges line up
        _linkPicker = popup;
        popup.IsOpen = true;
    }

    string LinkPrefer => AppPrefs.GetText("linkPrefer", "wifi");

    /// <summary>Moves the link to Wi-Fi or the USB cable (from the pill here or the phone's pill).</summary>
    async Task SwitchLink(string kind, bool fromPhone)
    {
        AppPrefs.SetText("linkPrefer", kind);
        string? problem = kind == "usb" ? await ConnectUsbAsync(quiet: false) : await ConnectWifiNowAsync();
        Diag.Log("link: switch to " + kind + " " + (problem ?? "ok"));
        if (problem != null)
        {
            ToastWindow.Show(kind == "usb" ? "Couldn't link over USB" : "Couldn't link over Wi-Fi", problem, always: true);
            if (fromPhone) await _client.TrySendAsync(new JsonObject { ["t"] = "pc_result", ["action"] = "link_" + kind, ["ok"] = false, ["msg"] = problem });
        }
    }

    /// <summary>Links through the USB cable: adb forwards a laptop port to the phone's internal-only door. Null = linked.</summary>
    async Task<string?> ConnectUsbAsync(bool quiet)
    {
        if (_connecting) return "Already linking, try again in a moment.";
        if (_client.IsConnected && _client.Kind == "USB") return null;
        var scrcpy = Mirror.FindScrcpy(ScrcpyPath.Text);
        var adb = scrcpy == null ? null : Mirror.FindAdb(scrcpy);
        if (adb == null) return "The USB link uses adb, which comes with scrcpy. Install scrcpy on the Mirror page first.";
        var pair = PairStore.Find(_phoneId) ?? PairStore.Phones.FirstOrDefault();
        if (pair == null) return "Pair the phone first.";
        _connecting = true;
        try
        {
            var list = await Mirror.RunAsync(adb, TimeSpan.FromSeconds(6), "devices");
            var serial = Mirror.UsbSerial(list.Output);
            if (serial == null) return "Plug the phone in with a USB cable, with USB debugging on (and tap Allow on the phone).";
            var fwd = await Mirror.RunAsync(adb, TimeSpan.FromSeconds(8), "-s", serial, "forward", "tcp:0", "tcp:47823");
            if (!int.TryParse(fwd.Output.Trim(), out int port) || port is <= 0 or > 65535)
                return "adb couldn't open the cable link: " + fwd.Output.Trim();
            if (!quiet && !_client.IsConnected) SetConn("busy", pair.Name, "Linking through the USB cable...");
            await _client.ConnectLanAsync(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port), pair, kind: "USB");
            _linkKind = "USB";
            _userDisconnected = false;
            SetConn("on", pair.Name, "Connected over the USB cable, encrypted.");
            return null;
        }
        catch (Exception ex) { return "The phone didn't answer through the cable (" + ex.Message + "). Update Nyx on the phone and keep it unlocked."; }
        finally { _connecting = false; }
    }

    /// <summary>Links over Wi-Fi straight away, using the phone's last Wi-Fi address. Null = linked.</summary>
    async Task<string?> ConnectWifiNowAsync()
    {
        if (_client.IsConnected && _client.Kind == "Wi-Fi") return null;
        string ip = PhoneIpBox.Text.Trim();
        var pair = PairStore.Find(_phoneId) ?? PairStore.Phones.FirstOrDefault();
        if (pair == null) return "Pair the phone first.";
        if (!InputCheck.IsIPv4(ip)) return "The phone's Wi-Fi address isn't known yet. Put both on the same Wi-Fi; Nyx links by itself.";
        if (_connecting) return "Already linking, try again in a moment.";
        _connecting = true;
        try
        {
            await _client.ConnectLanAsync(new System.Net.IPEndPoint(System.Net.IPAddress.Parse(ip), 47823), pair);
            _linkKind = "Wi-Fi";
            _userDisconnected = false;
            SetConn("on", pair.Name, "Connected over Wi-Fi, encrypted.");
            return null;
        }
        catch (Exception ex) { return "The phone isn't reachable on Wi-Fi (" + ex.Message + "). Are both on the same Wi-Fi?"; }
        finally { _connecting = false; }
    }

    static readonly IEasingFunction Out = new ExponentialEase { Exponent = 5, EasingMode = EasingMode.EaseOut };

    /// <summary>The selection puck in the rail glides to the chosen icon.</summary>
    void MovePuck()
    {
        if (NavList.ItemContainerGenerator.ContainerFromIndex(NavList.SelectedIndex) is not FrameworkElement item) return;
        double y = item.TranslatePoint(new Point(0, 0), NavList).Y;
        if (!Theme.Motion || !RailPuck.IsLoaded)
        {
            RailPuck.BeginAnimation(Canvas.TopProperty, null);
            Canvas.SetTop(RailPuck, y);
            return;
        }
        RailPuck.BeginAnimation(Canvas.TopProperty,
            new DoubleAnimation(y, TimeSpan.FromMilliseconds(460)) { EasingFunction = new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut } });
    }

    /// <summary>A page rises into place; its tiles follow one after another.</summary>
    void Enter(FrameworkElement page)
    {
        if (!Theme.Motion || !IsLoaded) return;
        var tiles = FindTiles(page).Take(8).Cast<FrameworkElement>().ToList();
        if (tiles.Count == 0) tiles.Add(page);
        for (int i = 0; i < tiles.Count; i++) Rise(tiles[i], i * 45);
    }

    static void Rise(UIElement el, int delayMs, double from = 18)
    {
        var shift = new TranslateTransform(0, from);
        el.RenderTransform = shift;
        var begin = TimeSpan.FromMilliseconds(delayMs);
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) { BeginTime = begin, EasingFunction = Out });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(560)) { BeginTime = begin, EasingFunction = Out });
    }

    static IEnumerable<Border> FindTiles(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is Border b && b.Style == Application.Current.TryFindResource("TileBox") as Style)
            {
                yield return b;
                continue;
            }
            foreach (var t in FindTiles(child)) yield return t;
        }
    }

    void Window_Activated(object? sender, EventArgs e)
    {
        if (NavList.SelectedIndex == PageSms && ConvList.SelectedItem is Conversation c) MarkRead(c);
    }

    /// <summary>A popup is only worth showing when the user is not already looking at that page.</summary>
    bool Watching(int page) => IsActive && WindowState != WindowState.Minimized && NavList.SelectedIndex == page;

    void RefreshEmptyStates()
    {
        NotifEmpty.Visibility = _notifs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearNotifsBtn.Visibility = _notifs.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        MarkAllNotifsBtn.Visibility = _notifs.Any(n => n.CanRead) ? Visibility.Visible : Visibility.Collapsed;
        NotifSummary.Text = _notifs.Count switch
        {
            0 => "Nothing new",
            1 => "1 notification",
            _ => _notifs.Count + " notifications"
        };

        RefreshToday(); // "on your phone now" changes whenever the list does

        // the bell never counts more than is actually there (notifications leave when the phone clears them)
        if (_nav.Count > PageNotifications && _nav[PageNotifications].Count > _notifs.Count)
            _nav[PageNotifications].Count = _notifs.Count;

        // Overview: notifications (WhatsApp, Instagram, everything) on the left, unread texts on the right
        _unread.Clear();
        foreach (var n in _notifs.Where(n => !MessageRow.IsText(n)).Take(5)) _unread.Add(MessageRow.From(n));
        UnreadEmpty.Visibility = _unread.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        _recent.Clear();
        foreach (var c in _waConvs.Concat(_convs).Where(c => c.Unread > 0).OrderByDescending(c => c.LastTime).Take(4))
            _recent.Add(MessageRow.From(c));
        RecentEmpty.Visibility = _recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MarkAllReadTile.Visibility = _recent.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _nav[PageSms].Count = _convs.Sum(c => c.Unread) + _waConvs.Sum(c => c.Unread);

        var shown = ShowingWhatsApp ? _waConvs : _convs;
        ConvEmpty.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ConvEmpty.Text = ShowingWhatsApp
            ? _notifAccessOff
                ? "WhatsApp chats need notification access, which is off for Nyx on your phone. See the Notifications page for the steps."
                : "WhatsApp chats appear here as messages arrive on your phone. WhatsApp doesn't let other apps read older chats, " +
                  "so they start from when Nyx is running."
            : _smsHelp.Length > 0 ? _smsHelp : "Your text conversations load here once the phone is connected.";
        bool threadOpen = _composing || ConvList.SelectedItem != null;
        ThreadEmpty.Visibility = threadOpen ? Visibility.Collapsed : Visibility.Visible;
        Composer.Visibility = threadOpen ? Visibility.Visible : Visibility.Collapsed;
        ContactEmpty.Visibility = _contactView.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ContactEmpty.Text = _contactHelp != null && _contacts.Count == 0 ? _contactHelp :
            _contacts.Count > 0 && _contactView.Count == 0
            ? "No contact matches that search."
            : "Contacts from your phone appear here. Click one to fill the number, double-click to call.";
    }

    /// <summary>The Today tile: count since midnight and one dot column per hour for the last 12 hours.</summary>
    int _phoneToday;
    string _phoneTodayDay = "";

    void RefreshToday()
    {
        var now = DateTime.Now;
        // whole day (counted on the phone, so nothing is missed while the laptop was off), or
        // what's on the phone right now; Settings picks
        bool wholeDay = AppPrefs.Get("todayWholeDay", true);
        int seenHere = _arrivals.Count(t => t.Date == now.Date);
        int today = wholeDay
            ? (_phoneTodayDay == now.ToString("yyyy-MM-dd") ? Math.Max(_phoneToday, seenHere) : seenHere)
            : _notifs.Count;
        TodayCount.Text = today.ToString();
        TodayCap.Text = wholeDay ? "Today" : "On your phone";
        TodayUnit.Text = wholeDay ? (today == 1 ? "notification today" : "notifications today")
                                  : (today == 1 ? "notification now" : "notifications now");
        // the phone's own arrival times when it sent them (they include while the laptop was off)
        var bars = HourBars(_phoneTimes ?? _arrivals, now);
        if (!bars.SequenceEqual(ActivityBars.Values ?? Array.Empty<int>())) ActivityBars.Values = bars;
        ActivityBars.ToolTip = $"Notifications per hour, last 12 hours. This hour: {bars[11]}";
    }

    List<DateTime>? _phoneTimes;

    /// <summary>Arrivals per hour, oldest first: [11] is the last 60 minutes, [0] is 11 to 12 hours ago.</summary>
    internal static int[] HourBars(IEnumerable<DateTime> times, DateTime now)
    {
        var bars = new int[12];
        foreach (var t in times)
        {
            if (t > now) continue;
            int hoursAgo = (int)Math.Floor((now - t).TotalHours);
            if (hoursAgo < 12) bars[11 - hoursAgo]++;
        }
        return bars;
    }

    // ------------------------------------------------------------ theme

    // the sun/moon morphs the moment the switch starts, while the goo spreads
    void OnThemeSwitching(bool dark) => ThemeIcon.IsMoon = dark;

    void SyncThemeControls()
    {
        ThemeIcon.IsMoon = ThemeManager.IsDark;
        _syncingMode = true;
        (ThemeManager.Mode switch { ThemeMode.Light => ModeLight, ThemeMode.Dark => ModeDark, _ => ModeAuto }).IsChecked = true;
        _syncingMode = false;
        MoveSegThumb();
    }

    void MoveSegThumb()
    {
        int idx = ThemeManager.Mode switch { ThemeMode.Light => 0, ThemeMode.Dark => 1, _ => 2 };
        double x = idx * 76;
        if (!Theme.Motion || !IsLoaded) { SegThumbX.BeginAnimation(TranslateTransform.XProperty, null); SegThumbX.X = x; return; }
        SegThumbX.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(x, TimeSpan.FromMilliseconds(420)) { EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut } });
    }

    Point CentreOf(FrameworkElement el) =>
        el.TranslatePoint(new Point(el.ActualWidth / 2, el.ActualHeight / 2), (UIElement)Content);

    void ThemeBtn_Click(object sender, RoutedEventArgs e) =>
        ThemeManager.SetMode(ThemeManager.IsDark ? ThemeMode.Light : ThemeMode.Dark, this, CentreOf(ThemeBtn), ThemeBtn);

    void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncingMode || sender is not RadioButton rb) return;
        var mode = rb == ModeLight ? ThemeMode.Light : rb == ModeDark ? ThemeMode.Dark : ThemeMode.Auto;
        ThemeManager.SetMode(mode, this, CentreOf(rb));
        MoveSegThumb();
    }

    // ------------------------------------------------------------ live dots

    /// <summary>A soft ring pulses out of a red dot: the app's only looping motion, used for "live".</summary>
    static void Pulse(Ellipse? halo, UIElement? dot, bool on)
    {
        if (halo != null)
        {
            var st = (ScaleTransform)halo.RenderTransform;
            if (!on || !Theme.Motion)
            {
                halo.BeginAnimation(OpacityProperty, null); halo.Opacity = 0;
                st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                return;
            }
            var grow = new DoubleAnimation(1, 2.6, TimeSpan.FromSeconds(1.8)) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Out };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            halo.BeginAnimation(OpacityProperty, new DoubleAnimation(0.45, 0, TimeSpan.FromSeconds(1.8)) { RepeatBehavior = RepeatBehavior.Forever });
        }
        if (dot != null)
        {
            if (!on || !Theme.Motion) { dot.BeginAnimation(OpacityProperty, null); dot.Opacity = 1; return; }
            dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromSeconds(0.7)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        }
    }

    // ------------------------------------------------------------ connection

    async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        MovePuck();
        Enter(_pages[NavList.SelectedIndex]);
        if (!_demo) _lan.Start();
        SetUpTray();
        SetUpIphone();
        // heartbeat: the phone drops a link that goes quiet for 75 s, which is how it notices
        // this laptop went to sleep without closing the connection
        var heartbeat = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        heartbeat.Tick += async (_, _) =>
        {
            PageSub.Text = DateTime.Now.ToString("dddd, d MMMM"); // past midnight, the header follows
            if (_client.IsConnected) await _client.TrySendAsync(new JsonObject { ["t"] = "ping" });
        };
        heartbeat.Start();
        await RefreshDevicesAsync();
        if (_demo) LoadDemo();
        // files named on the command line go to the phone once it's connected
        if (App.StartupFiles.Count > 0) SendQueue.Add(App.StartupFiles);
        // opens the tray panel straight away (for screenshots and testing)
        if (_args.Contains("--traymenu"))
            _ = Dispatcher.InvokeAsync(async () => { await Task.Delay(800); TrayMenuNow().Show(); });
        if (_args.Contains("--flyout"))
            _ = Dispatcher.InvokeAsync(async () => { await Task.Delay(800); ToggleFlyout(); });
        // for testing: hide, then come back by itself while another app has focus (like a tray double-click)
        if (_args.Contains("--testfront"))
            _ = Dispatcher.InvokeAsync(async () => { await Task.Delay(1500); Hide(); await Task.Delay(4000); ShowFromTray(-1); });
        // for testing: plays a 60 fps H.264 file through the mirror window and logs how many pictures reach the screen
        int mt = Array.IndexOf(_args, "--mirrortest");
        if (mt >= 0 && mt + 1 < _args.Length) _ = MirrorTest(_args[mt + 1]).ContinueWith(t => { if (t.Exception != null) Diag.Log("mirrortest failed: " + t.Exception.GetBaseException()); });
        if (_args.Contains("--toast"))
            _ = Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(800);
                ToastWindow.Show("Weekend plans", "Sam: are we still on for Saturday? I can bring the speaker if you want", seconds: 30, from: "WhatsApp");
            });
    }

    // ------------------------------------------------------------------ files

    readonly Dictionary<string, string> _sendingNames = new();
    bool _sendingFiles;

    void SendFile_Click(object sender, RoutedEventArgs e) => PickAndSendFiles();

    /// <summary>Sends files that another launch handed over (or that waited for the phone).</summary>
    public async void SendQueuedFiles()
    {
        if (!_client.IsConnected || _sendingFiles) { Diag.Log("send queue: waiting (phone not connected or busy)"); return; }
        var files = SendQueue.Take();
        Diag.Log("send queue: " + files.Count + " file(s) to send");
        if (files.Count > 0) await SendFilesAsync(files);
    }

    /// <summary>Choose files and send them to the phone (also used by the tray panel).</summary>
    public async void PickAndSendFiles()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Send to your phone" };
        if (dlg.ShowDialog(this) == true) await SendFilesAsync(dlg.FileNames);
    }

    async Task SendFilesAsync(IEnumerable<string> paths)
    {
        var files = paths.Where(System.IO.File.Exists).Take(20).ToList();
        if (files.Count == 0) { ToastWindow.Show("Nothing to send", "Only files can be sent, not folders."); return; }
        if (!_client.IsConnected) { ToastWindow.Show("Phone not connected", "Connect your phone first, then send the files again."); return; }
        if (_sendingFiles) { ToastWindow.Show("Still sending", "Wait for the current files to finish."); return; }
        _sendingFiles = true;
        try
        {
            for (int i = 0; i < files.Count; i++)
            {
                string name = System.IO.Path.GetFileName(files[i]);
                string prefix = files.Count > 1 ? $"({i + 1} of {files.Count}) " : "";
                int lastPct = -1;
                string? problem = await FileTransfer.SendAsync(files[i], async msg =>
                {
                    if (msg["t"]?.ToString() == "file_start") _sendingNames[msg["id"]!.ToString()] = name;
                    return await _client.TrySendAsync(msg);
                }, p =>
                {
                    int pct = (int)(p * 100);
                    if (pct == lastPct) return;
                    lastPct = pct;
                    Dispatcher.InvokeAsync(() => StatusText.Text = $"Sending {prefix}{name} to your phone · {pct}%");
                });
                if (problem != null) { ToastWindow.Show("Couldn't send " + name, problem); break; }
            }
            StatusText.Text = files.Count == 1 ? "Sent to your phone." : $"Sent {files.Count} files to your phone.";
        }
        finally { _sendingFiles = false; }
    }

    void OnFileReceived(FileTransfer.Received f)
    {
        StatusText.Text = f.Ok ? "Received " + f.Name + " from your phone." : "Couldn't receive " + f.Name + ".";
        if (!f.Ok) { ToastWindow.Show("Couldn't receive " + f.Name, f.Message); return; }
        // click the notice to see the file in its folder (it is never opened by itself)
        ToastWindow.Show("Received " + f.Name, "From your phone · Downloads\\Nyx. Click to show it.", () =>
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select," + f.Path } }); } catch { }
        }, 10);
    }

    // drag files onto the window to send them
    static bool HasFiles(DragEventArgs e) => e.Data.GetDataPresent(DataFormats.FileDrop);

    void Window_DragEnter(object sender, DragEventArgs e)
    {
        if (!HasFiles(e)) return;
        DropHint.Text = _client.IsConnected ? "They go to your phone's Downloads/Nyx folder." : "Connect your phone first.";
        DropVeil.Visibility = Visibility.Visible;
        if (Theme.Motion) DropVeil.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 0.94, TimeSpan.FromMilliseconds(160)));
    }

    void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasFiles(e) && _client.IsConnected ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void Window_DragLeave(object sender, DragEventArgs e)
    {
        // leaving a child element also raises this: only hide when the pointer left the window
        var p = e.GetPosition(this);
        if (p.X <= 0 || p.Y <= 0 || p.X >= ActualWidth || p.Y >= ActualHeight) DropVeil.Visibility = Visibility.Collapsed;
    }

    async void Window_Drop(object sender, DragEventArgs e)
    {
        DropVeil.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) await SendFilesAsync(paths);
    }

    // ------------------------------------------------------------------ tray

    public const int PageSmsIndex = PageSms, PageCallIndex = PageCall, PageNotificationsIndex = PageNotifications, PageScreenIndex = PageScreen;
    System.Windows.Forms.NotifyIcon? _tray;
    TrayFlyout? _flyout;
    DateTime _flyoutClosedAt;
    bool _quitting, _btOn, _wifiOn;

    /// <summary>Nyx lives in the tray: click for the phone panel, right-click to open or quit.</summary>
    TrayMenu? _trayMenu;

    void SetUpTray()
    {
        if (_tray != null) return;
        _tray = new System.Windows.Forms.NotifyIcon { Text = "Nyx", Visible = true };
        try { _tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }

        // one click: the phone panel, straight away (waiting out the double-click time felt slow)
        _tray.MouseClick += (_, e) =>
        {
            // In Windows 11's hidden-icons menu the first click closes the menu, so the icon never
            // hears the second click of a double-click. Nyx listens for it itself for a moment.
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
                Dispatcher.Invoke(() =>
                {
                    if (!TrayClickIsInOverflow()) { ToggleFlyout(); return; }
                    // hidden-icons menu: wait out the double-click time before the panel, so it
                    // can't get in the way; a second click in that time opens Nyx instead
                    bool doubled = false;
                    bool armed = SecondClick.Arm(() => Dispatcher.BeginInvoke(() =>
                    {
                        doubled = true;
                        ShowFromTray(-1);
                        // When the hidden-icons menu finishes closing, Windows hands focus back to
                        // whatever was in front before it opened. Wait for it to go, then for a
                        // moment keep coming forward if something takes the focus back.
                        var started = DateTime.UtcNow;
                        int tries = 0;
                        var watch = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
                        watch.Tick += (_, _) =>
                        {
                            double ms = (DateTime.UtcNow - started).TotalMilliseconds;
                            if (ms > 2000 || tries >= 3) { watch.Stop(); return; }
                            if (TrayOverflowOpen()) return; // still closing
                            if (!IsActive) { tries++; ForceToFront(this); }
                        };
                        watch.Start();
                    }));
                    Diag.Log("tray: click in hidden icons" + (armed ? "" : " (couldn't watch for a second click)"));
                    var wait = new System.Windows.Threading.DispatcherTimer
                        { Interval = TimeSpan.FromMilliseconds(System.Windows.Forms.SystemInformation.DoubleClickTime + 40) };
                    wait.Tick += (_, _) => { wait.Stop(); if (!doubled && !SecondClick.Caught) ToggleFlyout(); };
                    wait.Start();
                });
            else if (e.Button == System.Windows.Forms.MouseButtons.Right) Dispatcher.Invoke(() => TrayMenuNow().Show());
        };
        // build the panel once while idle, so its code and layout are loaded before the first click
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
        {
            try { new TrayFlyout(this, FlyoutNow()).Close(); } catch { }
        });
        // double-click: the first click already opened the panel; swap it for Nyx itself
        _tray.MouseDoubleClick += (_, e) =>
        {
            if (e.Button != System.Windows.Forms.MouseButtons.Left) return;
            Dispatcher.Invoke(() => { _flyout?.Dismiss(); ShowFromTray(-1); });
        };
    }

    /// <summary>
    /// Puts a window in front even when Windows' focus rules would only flash it in the
    /// taskbar (after a tray click it can't see as Nyx's own): briefly joins the input of the
    /// window that has focus, which lets the switch through, then lets go again.
    /// </summary>
    public static void ForceToFront(Window w)
    {
        var me = new System.Windows.Interop.WindowInteropHelper(w).Handle;
        if (me == IntPtr.Zero) { w.Activate(); return; }
        var front = TrayNative.GetForegroundWindow();
        uint frontThread = TrayNative.GetWindowThreadProcessId(front, IntPtr.Zero);
        uint myThread = TrayNative.GetCurrentThreadId();
        bool joined = front != IntPtr.Zero && frontThread != myThread && TrayNative.AttachThreadInput(myThread, frontThread, true);
        try
        {
            // a key press from Nyx itself (an unassigned key, so nothing reacts to it) makes
            // Nyx "the app you used last", which Windows lets come to the front
            TrayNative.TapUnusedKey();
            TrayNative.BringWindowToTop(me);
            TrayNative.SetForegroundWindow(me);
            w.Activate();
        }
        finally
        {
            if (joined) TrayNative.AttachThreadInput(myThread, frontThread, false);
        }
        // last resort: on top for a moment, so it's at least visible
        bool ok = TrayNative.GetForegroundWindow() == me;
        if (!ok) { w.Topmost = true; w.Topmost = false; }
        Diag.Log("bring to front: " + (ok ? "done" : "Windows refused"));
    }

    /// <summary>Windows 11's hidden-icons menu is still on screen (it fades out after a click).</summary>
    static bool TrayOverflowOpen()
    {
        foreach (var cls in new[] { "TopLevelWindowForOverflowXamlIsland", "NotifyIconOverflowWindow" })
        {
            var h = TrayNative.FindWindowEx(IntPtr.Zero, IntPtr.Zero, cls, null);
            if (h != IntPtr.Zero && TrayNative.IsWindowVisible(h)) return true;
        }
        return false;
    }

    /// <summary>True when the pointer isn't on any taskbar: the click came from the hidden-icons menu.</summary>
    static bool TrayClickIsInOverflow()
    {
        if (!TrayNative.GetCursorPos(out var p)) return false;
        bool onBar = false;
        foreach (var cls in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
        {
            IntPtr h = IntPtr.Zero;
            while ((h = TrayNative.FindWindowEx(IntPtr.Zero, h, cls, null)) != IntPtr.Zero)
                if (TrayNative.GetWindowRect(h, out var r) && p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom)
                    onBar = true;
        }
        return !onBar;
    }

    static class TrayNative
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? title);
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

        /// <summary>Presses and releases key 0xE8, which no keyboard has and nothing listens for.</summary>
        public static void TapUnusedKey()
        {
            keybd_event(0xE8, 0, 0, UIntPtr.Zero);
            keybd_event(0xE8, 0, 2 /* KEYEVENTF_KEYUP */, UIntPtr.Zero);
        }
    }

    TrayMenu TrayMenuNow() => _trayMenu ??= new TrayMenu(new (string, Geometry?, bool, Action)?[]
    {
        ("Open Nyx", (Geometry)FindResource("IconGrid"), false, () => ShowFromTray(-1)),
        ("Send files…", (Geometry)FindResource("IconUpload"), false, PickAndSendFiles),
        ("Find phone", (Geometry)FindResource("IconBell"), false, RingFromTray),
        null,
        ("Quit Nyx", (Geometry)FindResource("IconPower"), true, QuitFromTray),
    });

    void ToggleFlyout()
    {
        // a click on the tray icon first takes focus away (closing the panel): don't reopen at once
        if (_flyout != null || (DateTime.UtcNow - _flyoutClosedAt).TotalMilliseconds < 300)
        {
            _flyout?.Dismiss();
            return;
        }
        var panel = _flyout = new TrayFlyout(this, FlyoutNow());
        _flyout.Closed += (_, _) => { _flyout = null; _flyoutClosedAt = DateTime.UtcNow; };
        // Shown at once, but it only takes focus after the double-click time: taking focus
        // closes Windows' hidden-icons menu, which would swallow the second click of a double-click.
        _flyout.ShowActivated = false;
        _flyout.Show();
        var focusLater = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(System.Windows.Forms.SystemInformation.DoubleClickTime) };
        focusLater.Tick += (_, _) => { focusLater.Stop(); if (ReferenceEquals(_flyout, panel)) panel.Activate(); };
        focusLater.Start();
    }

    FlyoutState FlyoutNow()
    {
        bool on = _client.IsConnected || _demo;
        return new FlyoutState(
            PhoneName.Text, on,
            on ? (_linkKind.Length > 0 ? "Connected over " + _linkKind : "Connected") : "Nyx connects by itself when your phone is on the same Wi-Fi.",
            PhoneWall.Source, PhoneDots.DotBrush,
            _btOn, _wifiOn, BatteryText.Text,
            _nav[PageNotifications].Count + _nav[PageSms].Count,
            RecentPeople(3));
    }

    /// <summary>Brings the window back, optionally on a page (and with a number ready to dial).</summary>
    public void ShowFromTray(int page, string? dial = null)
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        ForceToFront(this);
        if (page >= 0) Go(page);
        if (dial != null) DialBox.Text = dial;
    }

    public void QuitFromTray()
    {
        _quitting = true;
        Close();
    }

    public void RingFromTray()
    {
        if (!_ringing) FindPhone_Click(this, new RoutedEventArgs());
    }

    bool _toldAboutTray;

    /// <summary>Closing the window keeps Nyx in the tray, so the phone stays linked.</summary>
    void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_quitting || _tray == null) return;
        e.Cancel = true;
        Hide();
        if (!_toldAboutTray && !AppPrefs.Get("toldAboutTray", false))
        {
            _toldAboutTray = true;
            AppPrefs.Set("toldAboutTray", true);
            ToastWindow.Show("Nyx is still running", "It stays in the tray so your phone stays linked. Right-click the tray icon to quit.");
        }
    }

    void Window_Closed(object? sender, EventArgs e)
    {
        _closing = true;
        _flyout?.Close();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        _iphone.Dispose();
        _userDisconnected = true;
        ThemeManager.Changed -= SyncThemeControls;
        ThemeManager.Switching -= OnThemeSwitching;
        _lan.Dispose();
        _client.Close();
        CloseCallWindow();
    }

    async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshDevicesAsync();

    async Task RefreshDevicesAsync()
    {
        try
        {
            var devices = await BridgeClient.GetPairedDevicesAsync();
            DeviceList.ItemsSource = devices;
            if (devices.Count > 0) DeviceList.SelectedIndex = 0;
            DeviceEmpty.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            DeviceList.Visibility = devices.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception ex)
        {
            SetConn("off", "Bluetooth is off", "Turn Bluetooth on and refresh. (" + ex.Message + ")");
        }
    }

    string _androidState = "off", _androidTitle = "";

    /// <summary>The top-bar pill: the Android phone while it's linked, else a connected iPhone.</summary>
    void RefreshPill()
    {
        var (state, title, kind) = PhonePill.Pick(_androidState, _androidTitle, _linkKind, _iphone.Connected, _iphone.DeviceName);
        StatusDot.SetResourceReference(Shape.FillProperty, state switch { "on" => "Online", "busy" => "Ink3", _ => "LineStrong" });
        Pulse(LinkHalo, null, state == "on");
        Pulse(null, StatusDot, state == "busy");
        DeviceTitle.Text = title;
        LinkKind.Text = kind.Length > 0 ? "· " + kind : "";
    }

    /// <param name="state">off, busy or on</param>
    void SetConn(string state, string title, string detail)
    {
        _androidState = state;
        _androidTitle = title;
        PhoneName.Text = state == "off" ? "Not connected" : title;
        StatusText.Text = detail;
        DevicePanel.Visibility = state == "off" ? Visibility.Visible : Visibility.Collapsed;
        if (NearbyPanel != null) RefreshNearby();
        PhoneIcons.Visibility = state == "on" && _haveStatus ? Visibility.Visible : Visibility.Collapsed;
        ConnectedRow.Visibility = state == "off" ? Visibility.Collapsed : Visibility.Visible;
        ConnectedText.Text = state == "on" ? "Connected" : "Connecting";
        if (state == "off") _haveStatus = false;
        PhoneScreen.Opacity = state == "on" ? 1 : 0.4; // the phone picture dims while it's away
        if (state == "on" && _client.Kind.Length > 0) _linkKind = _client.Kind;
        RefreshPill();
        ConnectBtn.Content = state switch { "on" => "Disconnect", "busy" => "Cancel", _ => "Connect" };
        ConnectBtn.Style = Theme.StyleOf(state == "off" ? "BtnInk" : "Btn");
        ConnectBtn.Tag = "20";
    }

    async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_client.IsConnected || _connecting || !_userDisconnected)
        {
            _userDisconnected = true;
            _autoPaused = true;
            _client.Close();
            SetConn("off", "No phone connected", "Pick your phone below, then connect.");
            CloseCallWindow();
            return;
        }
        _autoPaused = false;
        if (DeviceList.SelectedItem is not DeviceEntry device)
        {
            StatusText.Text = "Pick a phone from the list first.";
            return;
        }
        _device = device;
        _userDisconnected = false;
        // a failed first attempt just reports why; only a dropped live link retries by itself
        if (!await TryConnectAsync()) _userDisconnected = true;
    }

    async Task<bool> TryConnectAsync()
    {
        if (_device == null || _connecting) return false;
        _connecting = true;
        SetConn("busy", _device.Name, "Connecting over Bluetooth...");
        try
        {
            await _client.ConnectAsync(_device.Id);
            if (_userDisconnected)
            {
                _client.Close();
                return false;
            }
            SetConn("on", _device.Name, "Connected. Waiting for the phone to say hello.");
            return true;
        }
        catch (Exception ex)
        {
            if (!_userDisconnected)
                SetConn("off", "Could not connect", ex.Message);
            return false;
        }
        finally
        {
            _connecting = false;
        }
    }

    /// <summary>
    /// A phone announced itself on the local network. If it is one we paired with and we
    /// are not already linked (and the user did not press Disconnect), join it at once.
    /// </summary>
    // ----------------------------------------------------------- phone card

    bool _haveStatus;

    /// <summary>Bluetooth, Wi-Fi, signal bars and battery, as the phone reports them.</summary>
    void ShowStatus(JsonObject m)
    {
        _haveStatus = true;
        PhoneIcons.Visibility = Visibility.Visible;
        _btOn = B(m, "bt"); _wifiOn = B(m, "wifi");
        IconBt.Opacity = _btOn ? 1 : 0.3;
        IconWifi.Opacity = _wifiOn ? 1 : 0.3;
        int signal = -1;
        try { signal = m["signal"]?.GetValue<int>() ?? -1; } catch { }
        SignalBars.Visibility = signal < 0 ? Visibility.Collapsed : Visibility.Visible;
        for (int i = 0; i < SignalBars.Children.Count; i++)
            if (SignalBars.Children[i] is Shape bar)
                bar.SetResourceReference(Shape.FillProperty, i < signal ? "Ink" : "DotOff");
        int pct = -1;
        try { pct = m["battery"]?.GetValue<int>() ?? -1; } catch { }
        pct = Math.Clamp(pct, -1, 100);
        bool charging = B(m, "charging");
        BatteryText.Text = pct < 0 ? "" : pct + "%";
        BatteryFill.Width = pct < 0 ? 0 : Math.Max(1.5, 13.5 * pct / 100.0);
        bool saver = B(m, "saver");
        // green while charging, red when low, yellow in battery saver, plain otherwise
        string tone = charging ? "Charge" : pct >= 0 && pct <= 15 ? "Red" : saver ? "Saver" : "Ink";
        BatteryFill.SetResourceReference(Border.BackgroundProperty, tone);
        BatteryText.SetResourceReference(TextBlock.ForegroundProperty, tone);
        BatteryText.ToolTip = charging ? "Charging" : saver ? "Battery saver is on" : pct >= 0 && pct <= 15 ? "Battery low" : null;
        if (_linkKind.Length > 0)
            LinkKind.Text = "· " + _linkKind + (pct >= 0 ? " · " + pct + "%" : "");
    }

    /// <summary>
    /// The phone's wallpaper (or the picture chosen in Nyx). Untrusted bytes, even from a
    /// paired phone: size-capped, decoded small, and any decoder failure just keeps the dots.
    /// </summary>
    void ShowWallpaper(string jpgBase64, string color)
    {
        try
        {
            if (InputCheck.IsHexColour(color))
                PhoneDots.DotBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        }
        catch { }

        if (jpgBase64.Length == 0) { PhoneWall.Source = null; return; }
        if (jpgBase64.Length > 2_000_000) return;
        try
        {
            var bytes = Convert.FromBase64String(jpgBase64);
            if (SetWallImage(bytes))
                try { System.IO.File.WriteAllBytes(WallCache, bytes); } catch { }
        }
        catch
        {
            PhoneWall.Source = null;
        }
    }

    static readonly string WallCache = System.IO.Path.Combine(Diag.Folder, "wallpaper.jpg");

    /// <summary>The last wallpaper the phone sent, so the card looks right before it connects.</summary>
    void LoadCachedWallpaper()
    {
        try
        {
            if (System.IO.File.Exists(WallCache) && new System.IO.FileInfo(WallCache).Length < 1_500_000)
                SetWallImage(System.IO.File.ReadAllBytes(WallCache));
        }
        catch { }
    }

    bool SetWallImage(byte[] bytes)
    {
        try
        {
            var img = new System.Windows.Media.Imaging.BitmapImage();
            img.BeginInit();
            img.StreamSource = new System.IO.MemoryStream(bytes);
            img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            img.DecodePixelHeight = 300;
            img.EndInit();
            img.Freeze();
            PhoneWall.Source = img;
            if (Theme.Motion)
                PhoneWall.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(400)) { EasingFunction = Out });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Says in words why the phone outline shows a colour instead of the wallpaper.</summary>
    void ShowWallHint(bool hasPicture, string why)
    {
        string text = hasPicture ? "" : why switch
        {
            "no_access" => "To show your wallpaper here: in Nyx on your phone, tap Show my wallpaper and switch on All files access.",
            "live" => "Your phone uses a live wallpaper, which Android can't share. In Nyx on your phone, tap Choose picture to show one here.",
            "" => "",
            _ => "Your phone wouldn't share its wallpaper. In Nyx on your phone, tap Choose picture to show one here.",
        };
        WallHint.Text = text;
        WallHint.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The Notifications page says plainly when the phone isn't allowed to share them.</summary>
    void ShowNotifAccess(bool on)
    {
        _notifAccessOff = !on;
        RefreshEmptyStates();
        NotifEmptyTitle.Text = on ? "Nothing here yet" : "Notification access is off on your phone";
        NotifEmptyBody.Text = on
            ? "Notifications show up here the moment they land on your phone. Dismiss one here and it clears on the phone too."
            : "Nyx on your phone isn't allowed to read notifications, so nothing can come through. On the phone: " +
              "Settings > Apps > Nyx > the ⋮ menu > Allow restricted settings. Then Settings > Notifications > Device & app notifications " +
              "(Notification access) > Nyx > on. Reconnect afterwards.";
        if (!on) StatusText.Text = "Notification access is off for Nyx on the phone, so notifications can't come through.";
    }

    bool _notifAccessOff;
    string _smsHelp = "";

    async void RefreshPhone_Click(object sender, RoutedEventArgs e)
    {
        if (!_client.IsConnected) return;
        await _client.SendAsync("get_status");
        await _client.SendAsync("get_contacts");
        if (WhatsAppEnabled) await _client.SendAsync("get_wa");
        await _client.SendAsync("get_notifs");
        await _client.SendAsync(new JsonObject { ["t"] = "get_calls", ["limit"] = 60 });
        await _client.SendAsync(new JsonObject { ["t"] = "get_sms", ["limit"] = 400 });
    }

    // ------------------------------------------------------ Wi-Fi pairing

    public sealed record NearbyPhone(string Id, string Name, System.Net.IPEndPoint At, DateTime Seen);
    readonly ObservableCollection<NearbyPhone> _nearby = new();
    WifiPairing? _pairing;
    System.Windows.Threading.DispatcherTimer? _nearbySweep;

    /// <summary>An unpaired Nyx phone is on this Wi-Fi: offer to pair with it.</summary>
    void NoteNearby(string id, System.Net.IPEndPoint at, string name)
    {
        if (NearbyList.ItemsSource == null) NearbyList.ItemsSource = _nearby;
        var entry = new NearbyPhone(id, name.Length > 0 ? name : "Phone " + id[..Math.Min(4, id.Length)], at, DateTime.UtcNow);
        int i = _nearby.ToList().FindIndex(n => n.Id == id);
        if (i >= 0) _nearby[i] = entry; else if (_nearby.Count < 6) _nearby.Add(entry);
        if (i < 0 && !_client.IsConnected && _pairing == null)
            StatusText.Text = entry.Name + " is on this Wi-Fi. Press Pair to link them, no Bluetooth needed.";
        RefreshNearby();
        // phones that stop announcing themselves drop off the list
        _nearbySweep ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromSeconds(3), System.Windows.Threading.DispatcherPriority.Background,
            (_, _) =>
            {
                for (int k = _nearby.Count - 1; k >= 0; k--)
                    if (DateTime.UtcNow - _nearby[k].Seen > TimeSpan.FromSeconds(10)) _nearby.RemoveAt(k);
                RefreshNearby();
            }, Dispatcher);
    }

    void RefreshNearby() =>
        NearbyPanel.Visibility = _nearby.Count > 0 && _pairing == null && !_client.IsConnected ? Visibility.Visible : Visibility.Collapsed;

    async void PairNearby_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not NearbyPhone phone || _pairing != null) return;
        PairPanel.Visibility = Visibility.Visible;
        PairButtons.Visibility = Visibility.Collapsed;
        PairTitle.Text = "Asking " + phone.Name + "...";
        PairCode.Text = "";
        PairHint.Text = "Keep Nyx open on the phone.";
        RefreshNearby();
        try
        {
            _pairing = await WifiPairing.StartAsync(phone.At, phone.Id);
            PairTitle.Text = "Pair with " + _pairing.PhoneName + "?";
            PairCode.Text = _pairing.Code[..3] + " " + _pairing.Code[3..];
            PairHint.Text = "Your phone shows a code too. If it's the same, press Codes match here and Allow on the phone.";
            PairButtons.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            EndPairing("Couldn't reach the phone: " + ex.Message);
        }
    }

    async void PairConfirm_Click(object sender, RoutedEventArgs e)
    {
        var p = _pairing;
        if (p == null) return;
        PairButtons.Visibility = Visibility.Collapsed;
        PairHint.Text = "Now tap Allow on your phone.";
        bool ok;
        try { ok = await p.ConfirmAsync(); } catch { ok = false; }
        string name = p.PhoneName;
        p.Dispose();
        _pairing = null;
        if (!ok)
        {
            EndPairing("The phone didn't allow it, so nothing was paired.");
            return;
        }
        PairPanel.Visibility = Visibility.Collapsed;
        _nearby.Clear();
        RefreshNearby();
        RefreshPaired();
        _autoPaused = false;
        StatusText.Text = "Paired with " + name + ". Linking over Wi-Fi...";
        ToastWindow.Show("Paired with " + name, "From now on they link by themselves on the same Wi-Fi.");
    }

    async void PairCancel_Click(object sender, RoutedEventArgs e)
    {
        var p = _pairing;
        _pairing = null;
        if (p != null) await p.CancelAsync();
        EndPairing("");
    }

    void EndPairing(string message)
    {
        _pairing?.Dispose();
        _pairing = null;
        if (message.Length > 0)
        {
            PairTitle.Text = message;
            PairCode.Text = "";
            PairHint.Text = "";
            PairButtons.Visibility = Visibility.Collapsed;
            var hide = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            hide.Tick += (_, _) => { hide.Stop(); if (_pairing == null) PairPanel.Visibility = Visibility.Collapsed; };
            hide.Start();
        }
        else PairPanel.Visibility = Visibility.Collapsed;
        RefreshNearby();
    }

    // seen-but-failed phones are left alone for a while, so a forged beacon can't keep the laptop busy
    readonly Dictionary<string, DateTime> _lanBackoff = new();

    async void OnPhoneSeen(string phoneId, System.Net.IPEndPoint at, string name)
    {
        if (PairStore.Find(phoneId) == null) { NoteNearby(phoneId, at, name); return; }
        if (_closing || _connecting || _autoPaused) return;
        if (_lanBackoff.TryGetValue(phoneId, out var until) && DateTime.UtcNow < until) return;
        if (_client.IsConnected && _client.Kind == "Wi-Fi") return;
        if (_client.Kind == "USB" && LinkPrefer == "usb") return; // you chose the cable
        var pair = PairStore.Find(phoneId);
        if (pair == null) return;
        // already on Bluetooth: move over to the faster Wi-Fi link
        _connecting = true;
        try
        {
            if (!_client.IsConnected) SetConn("busy", pair.Name, "Found your phone on Wi-Fi. Linking...");
            // the Bluetooth link (if any) stays up until the Wi-Fi link has proved itself
            await _client.ConnectLanAsync(at, pair);
            _linkKind = "Wi-Fi";
            _userDisconnected = false;
            _lanBackoff.Remove(phoneId);
            SetConn("on", pair.Name, "Connected over Wi-Fi, encrypted.");
        }
        catch (Exception ex)
        {
            _lanBackoff[phoneId] = DateTime.UtcNow.AddSeconds(15);
            _linkKind = _client.Kind;
            if (!_client.IsConnected)
                SetConn("off", "No phone connected", "Saw your phone on Wi-Fi but could not link: " + ex.Message);
        }
        finally
        {
            _connecting = false;
        }
    }

    async void OnDisconnected()
    {
        FileTransfer.AbortAll(); // half-received files from this link are thrown away
        _screen?.Stop();          // touches would have no way back
        _cam?.Stop();
        _mic?.Stop();
        _mirror?.Stop();
        _audio?.Stop();
        if (_closing) return;
        CloseCallWindow();
        SetCall("idle", "", "");
        if (_linkKind == "USB" && !_userDisconnected)
        {
            SetConn("off", "No phone connected", LinkPrefer == "usb"
                ? "The USB cable link dropped. Nyx relinks through the cable when it's plugged in, or over Wi-Fi."
                : "The USB cable link dropped. Nyx reconnects over Wi-Fi.");
            return;
        }
        if (_linkKind == "Wi-Fi" && !_userDisconnected)
        {
            // no polling needed: the phone's next Wi-Fi beacon brings the link straight back
            SetConn("off", "No phone connected", "Your phone left Wi-Fi or went to sleep. Nyx reconnects the moment it is back.");
            return;
        }
        if (_userDisconnected || AutoReconnect.IsChecked != true)
        {
            _userDisconnected = true;
            SetConn("off", "No phone connected", "The link dropped. Connect again when the phone is nearby.");
            return;
        }

        while (!_userDisconnected && !_closing && !_client.IsConnected)
        {
            SetConn("busy", _device?.Name ?? "Phone", "Link dropped. Trying again in a few seconds.");
            await Task.Delay(5000);
            if (_userDisconnected || _closing || _client.IsConnected) break;
            if (AutoReconnect.IsChecked != true)
            {
                _userDisconnected = true;
                SetConn("off", "No phone connected", "Pick your phone below, then connect.");
                break;
            }
            if (await TryConnectAsync()) break;
        }
    }

    // -------------------------------------------------------------- messages

    static string S(JsonObject o, string key) => o[key]?.ToString() ?? "";

    static DateTime TimeOf(JsonObject o)
    {
        try
        {
            long ms = o["time"]?.GetValue<long>() ?? 0;
            return ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime : DateTime.Now;
        }
        catch
        {
            return DateTime.Now;
        }
    }

    static bool B(JsonObject o, string key)
    {
        try { return o[key]?.GetValue<bool>() ?? false; } catch { return false; }
    }

    async void OnMessage(JsonObject m)
    {
        switch (S(m, "t"))
        {
            case "hello":
                Diag.Log("hello over " + _client.Kind + ": android " + S(m, "android") + ", notifAccess " + (m["notifAccess"]?.ToString() ?? "?") +
                         ", wallAccess " + (m["wallAccess"]?.ToString() ?? "?") + ", app " + S(m, "app"));
                _phoneId = S(m, "phone");
                SetConn("on", S(m, "device"), _client.Kind switch
                {
                    "Wi-Fi" => "Connected over Wi-Fi, encrypted · Android " + S(m, "android"),
                    "USB" => "Connected over the USB cable, encrypted · Android " + S(m, "android"),
                    _ => "Connected over Bluetooth · Android " + S(m, "android")
                });
                string phoneIp = InputCheck.IsIPv4(S(m, "ip")) ? S(m, "ip") : "";
                if (phoneIp.Length > 0) PhoneIpBox.Text = phoneIp;
                if (m["notifAccess"] != null) ShowNotifAccess(B(m, "notifAccess"));
                _phoneProto = m["proto"] is JsonNode pn && int.TryParse(pn.ToString(), out int pv) ? pv : 0;
                _phoneHasScreen = B(m, "screen");
                _phoneHasCamera = B(m, "camera");
                _phoneHasMic = B(m, "mic");
                _phoneHasMirror = B(m, "mirror");
                _phoneHasAudio = B(m, "audio");
                PayFixBtn.Visibility = B(m, "controlAuto") ? Visibility.Collapsed : Visibility.Visible; // already done
                // your app choices go to the phone first, then ask for its app list
                _ = SyncNotifAppsAsync();
                if (_phoneProto < PhoneProto)
                {
                    UpdateBanner.Visibility = Visibility.Visible;
                    ToastWindow.Show("Update Nyx on your phone",
                        "Your phone has an older Nyx, so notifications and Find phone won't work. Install the latest Nyx-phone.apk.", null, 12);
                }
                else UpdateBanner.Visibility = Visibility.Collapsed;

                // first Bluetooth link with this phone (or the phone lost its pairing):
                // hand it a fresh secret over the encrypted Bluetooth channel
                bool phoneKnowsUs = m["paired"] is JsonArray known && known.Any(n => n?.ToString() == PairStore.LaptopId);
                if (_client.Kind == "Bluetooth" && _phoneId.Length > 0 && (PairStore.Find(_phoneId) == null || !phoneKnowsUs))
                {
                    var pair = PairStore.Pair(_phoneId, S(m, "device"));
                    await _client.SendAsync(new JsonObject
                    {
                        ["t"] = "pair", ["laptop"] = PairStore.LaptopId,
                        ["name"] = Environment.MachineName, ["secret"] = pair.Secret
                    });
                    RefreshPaired();
                }
                var (macs, bcasts) = LanDiscovery.WakeInfo();
                await _client.SendAsync(new JsonObject
                {
                    ["t"] = "laptop_info", ["laptop"] = PairStore.LaptopId, ["name"] = Environment.MachineName,
                    ["macs"] = new JsonArray(macs.Select(x => (JsonNode)x).ToArray()),
                    ["bcast"] = new JsonArray(bcasts.Select(x => (JsonNode)x).ToArray()),
                    ["control"] = AllowControlBox.IsChecked == true
                });
                await _client.SendAsync("get_contacts");
                if (WhatsAppEnabled) await _client.SendAsync("get_wa");
                await _client.SendAsync("get_notifs"); // what's in the phone's shade right now
                await _client.SendAsync(new JsonObject { ["t"] = "get_calls", ["limit"] = 60 });
                await _client.SendAsync(new JsonObject { ["t"] = "get_sms", ["limit"] = 400 });
                SendQueuedFiles(); // files that waited for the phone
                break;

            case "paired":
                StatusText.Text = "Paired. From now on your phone links up by itself whenever both are on the same Wi-Fi.";
                ToastWindow.Show("Phone paired", "Nyx will now connect over Wi-Fi automatically.");
                break;

            case "pc":
                await OnPowerCommandAsync(S(m, "action"));
                break;
            case "link_prefer": // the phone's pill: Wi-Fi or the USB cable
                if (S(m, "kind") is "usb" or "wifi") await SwitchLink(S(m, "kind"), fromPhone: true);
                break;

            case "status":
                ShowStatus(m);
                break;

            case "control_auto_done":
                if (B(m, "ok")) PayFixBtn.Visibility = Visibility.Collapsed;
                break;

            case "calls":
                _calls.Clear();
                if (m["items"] is JsonArray calls)
                    foreach (var c in calls.OfType<JsonObject>())
                        _calls.Add((S(c, "number"), S(c, "name"), S(c, "kind"), TimeOf(c)));
                FilterContacts();
                break;

            case "wallpaper":
                Diag.Log("wallpaper: picture " + S(m, "jpg").Length + " chars, colour " + S(m, "color") + ", why '" + S(m, "why") + "'");
                ShowWallpaper(S(m, "jpg"), S(m, "color"));
                ShowWallHint(S(m, "jpg").Length > 0, S(m, "why"));
                break;

            case "get_power_log":
                var log = await Task.Run(() => PowerControl.ReadLog());
                await _client.SendAsync(new JsonObject { ["t"] = "power_log", ["items"] = log });
                break;

            case "contacts":
                _contactHelp = null;
                _contacts.Clear();
                if (m["items"] is JsonArray contacts)
                    foreach (var c in contacts.OfType<JsonObject>())
                        _contacts.Add(new ContactItem(S(c, "name"), S(c, "number")));
                FilterContacts();
                foreach (var conv in _convs) conv.Display = NameFor(conv.Address);
                break;

            case "sms_list":
            {
                _smsHelp = "";
                // a reload must not forget what's unread
                var wasUnread = _convs.ToDictionary(c => c.Key, c => c.Unread);
                _convs.Clear();
                ResetThread();
                if (m["items"] is JsonArray list)
                {
                    // the phone sends newest first; add oldest first so threads read top to bottom
                    foreach (var s in list.OfType<JsonObject>().Reverse())
                        AddSms(ToSms(s), false);
                }
                var now = DateTime.Now;
                foreach (var c in _convs)
                {
                    c.Unread = c.Items.Any(i => i.Read != null)
                        ? UnreadIn(c.Items, _readUpTo.GetValueOrDefault(c.Key), now)
                        : wasUnread.GetValueOrDefault(c.Key);
                }
                RefreshEmptyStates();
            }
                FilterContacts();
                break;

            case "sms":
                var sms = ToSms(m);
                var into = AddSms(sms, true);
                if (!(Watching(PageSms) && ReferenceEquals(ConvList.SelectedItem, into)))
                {
                    into.Unread++;
                    ToastWindow.Show(NameFor(sms.Address), sms.Body, from: "Messages");
                }
                RefreshEmptyStates();
                break;

            case "wa_chat":
                if (WhatsAppEnabled) OnWhatsAppChat(m);
                break;

            case "wa_gone":
                var gone = _waConvs.FirstOrDefault(c => c.Key == S(m, "chat"));
                if (gone != null)
                {
                    gone.CanReply = false;
                    if (ReferenceEquals(ConvList.SelectedItem, gone)) RefreshThreadHeader(gone);
                }
                break;

            case "wa_sent":
                if (!B(m, "ok")) ToastWindow.Show("WhatsApp message not sent", S(m, "msg"));
                break;

            case "sms_sent":
                if (!B(m, "ok"))
                {
                    StatusText.Text = "The phone could not send your text to " + S(m, "to") + ". " + S(m, "msg");
                    ToastWindow.Show("Text not sent", "To " + NameFor(S(m, "to")) + ". " + S(m, "msg"));
                }
                break;

            case "notif":
                var item = new NotifItem
                {
                    Id = S(m, "id"), App = S(m, "app"), Pkg = S(m, "pkg"), Title = S(m, "title"), Text = S(m, "text"), Time = TimeOf(m),
                    CanRead = B(m, "canRead")
                };
                if (NotifApps.IsMuted(item.Pkg)) break; // switched off in Settings (an older phone app may still send it)
                // "old": already in the phone's shade when the laptop connected. List it, but no pop-up
                AddNotification(item, B(m, "old"));
                break;

            case "notif_apps":
                if (m["items"] is JsonArray appItems) ShowNotifApps(appItems);
                break;

            case "notif_removed":
                RemoveNotif(S(m, "id"));
                RefreshEmptyStates();
                break;

            case "notif_today": // how many arrived on the phone today
                if (m["count"] is JsonNode tc && int.TryParse(tc.ToString(), out int todayCount))
                {
                    _phoneToday = Math.Clamp(todayCount, 0, 100000);
                    _phoneTodayDay = S(m, "day");
                    if (m["times"] is JsonArray ta)
                    {
                        // the phone's clock may be a little off from this one: line them up
                        var skew = long.TryParse(m["now"]?.ToString(), out long pnow)
                            ? DateTimeOffset.Now.ToUnixTimeMilliseconds() - pnow : 0;
                        if (Math.Abs(skew) > 3_600_000) skew = 0;
                        _phoneTimes = ta.Take(5000)
                            .Select(x => long.TryParse(x?.ToString(), out long ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms + skew).LocalDateTime : DateTime.MinValue)
                            .Where(t => t != DateTime.MinValue).ToList();
                    }
                    RefreshToday();
                }
                break;

            case "notif_sync":
            {
                // the phone's whole shade: anything else from the phone left it while we were away
                if (m["ids"] is not JsonArray ids) break;
                var inShade = ids.Select(x => x?.ToString() ?? "").ToHashSet();
                for (int i = _notifs.Count - 1; i >= 0; i--)
                    if (!_notifs[i].Id.StartsWith("ios:") && !inShade.Contains(_notifs[i].Id)) _notifs.RemoveAt(i);
                RefreshEmptyStates();
                RefreshToday();
                break;
            }

            case "call":
                SetCall(S(m, "state"), S(m, "number"), S(m, "name"));
                break;

            case "file_start":
            case "file_part":
            case "file_end":
            case "file_cancel":
            {
                if (S(m, "t") == "file_start") StatusText.Text = "Receiving " + FileTransfer.SafeName(S(m, "name")) + " from your phone…";
                var (reply, got) = FileTransfer.OnMessage(m);
                if (reply != null) await _client.SendAsync(reply);
                if (got != null) OnFileReceived(got);
                break;
            }

            case "file_done":
            {
                string id = S(m, "id");
                string name = _sendingNames.TryGetValue(id, out var nm) ? nm : "Your file";
                _sendingNames.Remove(id);
                ToastWindow.Show(B(m, "ok") ? "Sent " + name : "Couldn't send " + name, S(m, "msg"));
                break;
            }

            case "ring_done":
                _ringing = false;
                FindPhoneText.Text = "Find phone";
                break;

            // ---- phone audio on the laptop
            case "audio_request": // the phone's Play phone audio on laptop button
                if (_audio?.Running != true) await OfferAudio();
                break;
            case "audio_ready":
                if (m["port"] is JsonNode ap && int.TryParse(ap.ToString(), out int aport)) _ = StartAudio(aport);
                break;
            case "audio_stop":
                _audio?.Stop();
                RefreshAudio();
                break;
            case "audio_note": // something playing on the phone won't let Android share its sound
                bool blocked = B(m, "blocked");
                const string blockedText = "Android can't pass this app's sound along over Wi-Fi (Spotify plays its sound in a way only the phone hears), so it stays on the phone. For Spotify here, pick this laptop in Spotify's Connect to a device, or use Mirror over USB with sound.";
                _mirrorWin?.Hint(blocked ? blockedText : null);
                if (_audio?.Running == true) AudioShareState.Text = blocked ? blockedText : "Playing your phone's sound here.";
                break;

            // ---- mirroring over Wi-Fi
            case "mirror_request": // the phone's Show screen on laptop button
                if (_mirror?.Running != true && !await TryScrcpyWifi()) await OfferMirror();
                break;
            case "mirror_ready":
                if (_mirror != null) _mirror.Codec = m["codec"]?.ToString() == "h264" ? "h264" : "jpeg";
                if (m["port"] is JsonNode rp && int.TryParse(rp.ToString(), out int rport)) _ = StartMirror(rport, B(m, "control"));
                break;
            case "mirror_stop":
                _mirror?.Stop();
                if (_mirrorWin != null) _mirrorWin.Close();
                MirrorStatus.Text = m["why"]?.ToString() == "locked"
                    ? "Mirroring stopped because the phone locked (Android ends screen sharing then)."
                    : "Mirroring stopped.";
                break;

            // ---- microphone
            case "mic_request": // the phone's Use as microphone button
                if (_mic?.Running != true) await OfferMic();
                break;
            case "mic_ready":
                if (m["port"] is JsonNode mp && int.TryParse(mp.ToString(), out int mport)) _ = StartMic(mport);
                break;
            case "mic_stop":
                _mic?.Stop();
                RefreshMic();
                break;

            // ---- webcam
            case "cam_request": // the phone's Use as webcam button
                if (_cam?.Running != true) await OfferWebcam();
                break;
            case "cam_ready":
                if (m["port"] is JsonNode cp && int.TryParse(cp.ToString(), out int cport)) _ = StartWebcam(cport);
                break;
            case "cam_stop":
                _cam?.Stop();
                RefreshWebcam();
                break;

            // ---- second screen
            case "screen_request": // the phone asked (its Second screen button)
                if (_screen?.Running != true) await OfferSecondScreen();
                break;
            case "screen_ready":
                if (m["port"] is JsonNode pp && int.TryParse(pp.ToString(), out int sport)) _ = StartSecondScreen(sport);
                break;
            case "screen_touch":
                _screen?.Touch(S(m, "a"), D(m, "x"), D(m, "y"), D(m, "d"));
                break;
            case "screen_fingers": // real touch: every finger on the phone's glass
                if (_screen != null && m["p"] is JsonArray pts)
                {
                    var fingers = new List<(int, double, double)>();
                    foreach (var pt in pts.OfType<JsonArray>().Take(10))
                        try { fingers.Add((pt[0]!.GetValue<int>(), pt[1]!.GetValue<double>(), pt[2]!.GetValue<double>())); } catch { }
                    int changed = m["i"] is JsonNode ci && int.TryParse(ci.ToString(), out int cv) ? cv : -1;
                    _screen.Fingers(S(m, "a"), changed, fingers);
                }
                break;
            case "screen_windows": // the phone's Windows button: what can be brought over
                if (_screen?.Running == true && _screenTarget != null)
                {
                    var wins = SecondScreen.ListWindows(_screenTarget.Bounds);
                    _offeredWindows = wins.Select(w => w.Id).ToHashSet();
                    await _client.SendAsync(new JsonObject
                    {
                        ["t"] = "screen_windows",
                        ["items"] = new JsonArray(wins.Select(w => (JsonNode)new JsonObject
                            { ["id"] = w.Id, ["title"] = w.Title, ["here"] = w.OnPhone }).ToArray())
                    });
                }
                break;
            case "screen_move":
                // only a window from the list just sent: the phone can't name any other handle
                if (_screen?.Running == true && _screenTarget != null && m["id"] is JsonNode wid &&
                    long.TryParse(wid.ToString(), out long hwnd) && _offeredWindows.Contains(hwnd))
                    SecondScreen.MoveWindow(hwnd, _screenTarget.Bounds, toPhone: !B(m, "back"));
                break;
            case "screen_stop":
                _screen?.Stop();
                RefreshSecondScreen();
                break;

            case "error":
                Diag.Log("phone error: " + S(m, "code") + " " + S(m, "what") + " " + S(m, "msg"));
                if (S(m, "code") == "permission") ShowPermissionHelp(S(m, "what"), B(m, "restricted"));
                else ToastWindow.Show("Your phone reported a problem", S(m, "msg"));
                break;
        }
    }

    /// <summary>
    /// The phone asked to lock, sleep, restart or shut down this laptop. Only a paired phone
    /// may: on Wi-Fi the handshake proved it, on Bluetooth its id must be in the pair list.
    /// </summary>
    async Task OnPowerCommandAsync(string action)
    {
        async Task Reply(bool ok, string msg) =>
            await _client.SendAsync(new JsonObject { ["t"] = "pc_result", ["action"] = action, ["ok"] = ok, ["msg"] = msg });

        bool trusted = _client.IsVerified || (_client.Kind == "Bluetooth" && PairStore.Find(_phoneId) != null);
        if (!trusted) { await Reply(false, "This phone is not paired with the laptop."); return; }
        if (AllowControlBox.IsChecked != true) { await Reply(false, "Phone control is turned off in Nyx settings on the laptop."); return; }

        string? msg = PowerControl.Run(action);
        if (msg == null) { await Reply(false, "Unknown action."); return; }
        await Reply(true, msg);

        if (action is "restart" or "shutdown")
        {
            string what = action == "restart" ? "Restarting" : "Shutting down";
            ToastWindow.Show($"{what} in {PowerControl.GraceSeconds} seconds",
                "Your phone asked for this. Click here to cancel.",
                () => { PowerControl.Run("cancel"); ToastWindow.Show("Cancelled", "The laptop stays on.", always: true); },
                PowerControl.GraceSeconds, always: true); // even over a game: you must be able to cancel
        }
    }

    // ------------------------------------------------------------- settings

    /// <summary>Shows whether the phone's Wake can reach this laptop while it sleeps, with the fix if not.</summary>
    async Task RefreshWakeSetup()
    {
        var adapter = WakeSetup.WifiAdapter();
        if (adapter == null) { WakeState.Text = "No Wi-Fi card found, so the phone can't wake this laptop over Wi-Fi."; WakeSetupBtn.Visibility = Visibility.Collapsed; return; }
        var on = await WakeSetup.IsOnAsync(adapter);
        WakeState.Text = on switch
        {
            true => "On: the phone's Wake reaches this laptop's Wi-Fi while it sleeps. (On battery, Windows may switch the Wi-Fi off during long sleeps; plugged in, it stays reachable.)",
            false => "Off: Windows ignores the phone's Wake while this laptop sleeps. Turn it on below (Windows asks for permission).",
            _ => "Windows didn't say whether the Wi-Fi can wake this laptop."
        };
        WakeSetupBtn.Visibility = on == false ? Visibility.Visible : Visibility.Collapsed;
    }

    async void WakeSetup_Click(object sender, RoutedEventArgs e)
    {
        var adapter = WakeSetup.WifiAdapter();
        if (adapter == null) return;
        WakeSetupBtn.IsEnabled = false;
        WakeState.Text = "Approve Windows' question. The Wi-Fi drops for a few seconds while the card restarts…";
        var problem = await WakeSetup.TurnOnAsync(adapter);
        if (problem != null && await WakeSetup.IsOnAsync(adapter) == true) problem = null; // the part that matters worked
        Diag.Log("wake setup: " + (problem ?? "turned on"));
        WakeSetupBtn.IsEnabled = true;
        if (problem != null) { WakeState.Text = problem; return; }
        await Task.Delay(3000);
        await RefreshWakeSetup();
    }

    void RefreshPaired()
    {
        var phones = PairStore.Phones;
        PairedList.ItemsSource = phones;
        PairedEmpty.Visibility = phones.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void ForgetPhone_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PairedPhone p) return;
        PairStore.Forget(p.Id);
        if (_phoneId == p.Id && _client.Kind == "Wi-Fi") { _autoPaused = true; _client.Close(); }
        RefreshPaired();
    }

    void AllowControl_Click(object sender, RoutedEventArgs e) =>
        AppPrefs.Set("allowControl", AllowControlBox.IsChecked == true);

    void TodayWholeDay_Click(object sender, RoutedEventArgs e)
    {
        AppPrefs.Set("todayWholeDay", TodayWholeDayBox.IsChecked == true);
        RefreshToday();
    }

    void PopupsInGames_Click(object sender, RoutedEventArgs e) =>
        AppPrefs.Set("popupsInGames", PopupsInGamesBox.IsChecked == true);

    void StartWithWindows_Click(object sender, RoutedEventArgs e) =>
        AppPrefs.StartWithWindows = StartWithWindowsBox.IsChecked == true;

    readonly HashSet<string> _permissionToasted = new();
    string? _contactHelp;

    /// <summary>
    /// The phone is connected but one feature isn't allowed yet. Say exactly what to tap, on
    /// the page that feature lives on (and once as a notice), not as an error on the phone card.
    /// </summary>
    void ShowPermissionHelp(string what, bool restricted)
    {
        string feature = what switch { "sms" or "send_sms" => "texts", "contacts" => "contacts", _ => "calls" };
        string steps = restricted
            ? "On your phone: Settings > Apps > Nyx > ⋮ (top right) > Allow restricted settings. Then in Nyx tap Allow permissions again. " +
              "Android asks for this extra step for any app installed outside an app store."
            : "On your phone: open Nyx and tap Allow permissions, or Settings > Apps > Nyx > Permissions.";
        string text = "Nyx on your phone isn't allowed to read your " + feature + " yet. " + steps;
        switch (feature)
        {
            case "texts":
                _smsHelp = text;
                RefreshEmptyStates();
                break;
            case "contacts":
                _contactHelp = text;
                RefreshEmptyStates();
                break;
        }
        if (_permissionToasted.Add(feature))
            ToastWindow.Show("Allow " + feature + " on your phone", steps, null, 12);
    }

    /// <summary>A new row slides down into the list instead of popping in.</summary>
    void AnimateIn(ItemsControl list, object item)
    {
        if (!Theme.Motion) return;
        Dispatcher.InvokeAsync(() =>
        {
            if (list.ItemContainerGenerator.ContainerFromItem(item) is UIElement row) Rise(row, 0, -14);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // ---------------------------------------------------------- notifications

    bool RemoveNotif(string id)
    {
        bool any = false;
        for (int i = _notifs.Count - 1; i >= 0; i--)
            if (_notifs[i].Id == id) { _notifs.RemoveAt(i); any = true; }
        return any;
    }

    // ------------------------------------------------- notifications from apps (on / off)

    readonly System.Collections.ObjectModel.ObservableCollection<NotifAppRow> _appRows = new();
    System.ComponentModel.ICollectionView? _appView;

    void SetupNotifApps()
    {
        NotifAppList.ItemsSource = _appRows;
        _appView = System.Windows.Data.CollectionViewSource.GetDefaultView(_appRows);
        _appView.Filter = o => o is NotifAppRow r &&
            (NotifAppSearch.Text.Trim().Length == 0 || r.Name.Contains(NotifAppSearch.Text.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The phone's apps arrived: one switch each, off for the ones you muted.</summary>
    void ShowNotifApps(JsonArray items)
    {
        _appRows.Clear();
        foreach (var o in items.OfType<JsonObject>().Take(2000))
        {
            string pkg = S(o, "pkg"), name = S(o, "name");
            if (!NotifApps.Valid(pkg)) continue;
            _appRows.Add(new NotifAppRow { Pkg = pkg, Name = name.Length > 0 ? name : pkg, On = !NotifApps.IsMuted(pkg) });
        }
        RefreshNotifAppsText();
    }

    void RefreshNotifAppsText()
    {
        NotifAppsEmpty.Visibility = _appRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        int off = _appRows.Count(r => !r.On);
        NotifAppsSub.Text = "Pick which apps' notifications come to this laptop. Switched off, they stay on your phone only: no pop-up here and not counted in Today." +
                            (off > 0 ? $" {off} switched off." : "");
    }

    async Task SyncNotifAppsAsync()
    {
        await SendNotifMuteAsync();
        await _client.TrySendAsync(new JsonObject { ["t"] = "get_notif_apps" });
    }

    Task SendNotifMuteAsync() =>
        _client.TrySendAsync(new JsonObject { ["t"] = "notif_mute", ["pkgs"] = new JsonArray(NotifApps.Muted.Select(p => (JsonNode)p!).ToArray()) });

    async void NotifApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: NotifAppRow row } box) return;
        row.On = box.IsChecked == true;
        NotifApps.Set(row.Pkg, row.On);
        if (!row.On) DropNotifsFrom(row.Pkg);
        RefreshNotifAppsText();
        await SendNotifMuteAsync();
    }

    async void NotifAppsAll_Click(object sender, RoutedEventArgs e)
    {
        bool on = (sender as FrameworkElement)?.Tag as string == "on";
        foreach (var row in _appView?.Cast<NotifAppRow>().ToList() ?? new()) // only the ones the search shows
        {
            row.On = on;
            NotifApps.Set(row.Pkg, on);
            if (!on) DropNotifsFrom(row.Pkg);
        }
        RefreshNotifAppsText();
        await SendNotifMuteAsync();
    }

    void NotifAppSearch_TextChanged(object sender, TextChangedEventArgs e) => _appView?.Refresh();

    /// <summary>Takes a switched-off app's notifications out of the lists right away.</summary>
    void DropNotifsFrom(string pkg)
    {
        for (int i = _notifs.Count - 1; i >= 0; i--)
            if (_notifs[i].Pkg == pkg) _notifs.RemoveAt(i);
        RefreshEmptyStates();
        RefreshToday();
    }

    async void ChooseNotifApps_Click(object sender, RoutedEventArgs e)
    {
        NavList.SelectedIndex = PageSetup;
        await Dispatcher.InvokeAsync(() => NotifAppsCard.BringIntoView(new Rect(0, 0, 1, 1)), System.Windows.Threading.DispatcherPriority.Loaded); // its title at the top
        if (_client.IsConnected) await _client.TrySendAsync(new JsonObject { ["t"] = "get_notif_apps" }); // fresh list
    }

    async void ClearNotifs_Click(object sender, RoutedEventArgs e)
    {
        // the list mirrors the phone's shade, so clearing here clears them on the phone too
        foreach (var n in _notifs.ToList())
        {
            if (n.Id.StartsWith("ios:")) await _iphone.DismissAsync(n.Id);
            else await _client.SendAsync(new JsonObject { ["t"] = "dismiss_notif", ["id"] = n.Id });
        }
        _notifs.Clear();
        _nav[PageNotifications].Count = 0;
        RefreshEmptyStates();
    }

    /// <summary>Presses the notification's own "Mark as read" on the phone; the phone then clears it.</summary>
    async Task MarkNotifRead(NotifItem n)
    {
        if (!n.CanRead || n.Id.StartsWith("ios:")) return;
        RemoveNotif(n.Id); // the app clears it on the phone once it's read
        RefreshEmptyStates();
        await _client.TrySendAsync(new JsonObject { ["t"] = "notif_read", ["id"] = n.Id });
    }

    async void MarkNotifRead_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is NotifItem n) await MarkNotifRead(n);
    }

    async void MarkAllNotifsRead_Click(object sender, RoutedEventArgs e)
    {
        foreach (var n in _notifs.Where(n => n.CanRead).ToList()) await MarkNotifRead(n);
    }

    /// <summary>A text marked read here: also mark it read on the phone, through its Messages notification.</summary>
    async Task MarkTextReadOnPhone(Conversation c)
    {
        foreach (var n in _notifs.Where(n => n.CanRead && MessageRow.IsText(n) &&
                                             (n.Title == c.Display || n.Title == c.Address)).ToList())
            await MarkNotifRead(n);
    }

    async void DismissNotif_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is NotifItem n)
        {
            _notifs.Remove(n);
            RefreshEmptyStates();
            if (n.Id.StartsWith("ios:")) await _iphone.DismissAsync(n.Id);
            else await _client.SendAsync(new JsonObject { ["t"] = "dismiss_notif", ["id"] = n.Id });
        }
    }

    /// <summary>Adds one notification (from the Android phone or the iPhone) to the list.</summary>
    void AddNotification(NotifItem item, bool old)
    {
        if (!old) Diag.Log("notification from " + item.App); // the app only, never the text
        bool had = RemoveNotif(item.Id);
        _notifs.Insert(0, item);
        while (_notifs.Count > 200) _notifs.RemoveAt(_notifs.Count - 1);
        if (_notifAccessOff && !item.Id.StartsWith("ios:")) ShowNotifAccess(true);
        if (!old) _arrivals.Add(item.Time);
        if (!old && !had && !Watching(PageNotifications)) _nav[PageNotifications].Count++;
        RefreshEmptyStates();
        RefreshToday();
        if (!old) AnimateIn(NotifList, item);
        // no pop-up when that app is open in front of you (you're already reading it there)
        if (!old && !Watching(PageNotifications) && !Watching(PageHome) && !Quiet.AppInFront(item.App))
            ToastWindow.Show(item.Title, item.Text, from: item.App);
    }

    // ------------------------------------------------------------ iPhone (beta)

    readonly IphoneLink _iphone = new();

    void SetUpIphone()
    {
        _iphone.Posted += (n, old) => Dispatcher.BeginInvoke(() => AddNotification(n, old));
        _iphone.Removed += id => Dispatcher.BeginInvoke(() => { RemoveNotif(id); RefreshEmptyStates(); });
        _iphone.Status += s => Dispatcher.BeginInvoke(() => IphoneState.Text = s);
        _iphone.ConnectedChanged += on => Dispatcher.BeginInvoke(() =>
        {
            IphoneBtn.Content = on ? "Reconnect" : "Connect iPhone";
            RefreshPill();
        });
        // used before: try again quietly at start-up
        if (AppPrefs.Get("iphone", false) && !_demo) _ = _iphone.ConnectAsync();
    }

    async void IphoneConnect_Click(object sender, RoutedEventArgs e) => await ConnectIphoneAsync(quiet: true);

    /// <summary>Connects (or reconnects) the iPhone; from the pill, a failure says why in a toast.</summary>
    async Task ConnectIphoneAsync(bool quiet)
    {
        if (!IphoneBtn.IsEnabled) return; // already connecting
        IphoneBtn.IsEnabled = false;
        try
        {
            bool ok = await _iphone.ConnectAsync();
            Diag.Log("iphone: connect " + (ok ? "ok" : "failed"));
            if (ok) AppPrefs.Set("iphone", true);
            else if (!quiet) ToastWindow.Show("Couldn't link the iPhone", _iphone.LastStatus, always: true);
        }
        finally { IphoneBtn.IsEnabled = true; RefreshPill(); }
    }

    // -------------------------------------------------------------------- SMS

    static string KeyOf(string address)
    {
        string digits = new string(address.Where(char.IsDigit).ToArray());
        if (digits.Length >= 7) return digits.Length > 10 ? digits[^10..] : digits;
        return address.Trim().ToLowerInvariant();
    }

    string NameFor(string address)
    {
        string key = KeyOf(address);
        foreach (var c in _contacts)
            if (KeyOf(c.Number) == key) return c.Name;
        return address;
    }

    static SmsItem ToSms(JsonObject o) => new()
    {
        Address = S(o, "address"), Body = S(o, "body"), Time = TimeOf(o), Inbox = B(o, "inbox"),
        Read = o["read"] != null ? B(o, "read") : null
    };

    // when each conversation was last read here, so a reload from the phone doesn't bring it back
    readonly Dictionary<string, DateTime> _readUpTo = new();

    /// <summary>
    /// Unread texts in a conversation: unread on the phone, from the last two days (old unread
    /// bank texts aren't news), and newer than when you last opened it here.
    /// </summary>
    public static int UnreadIn(IEnumerable<SmsItem> items, DateTime readHereUpTo, DateTime now) =>
        items.Count(i => i.Inbox && i.Read == false && i.Time > now.AddDays(-2) && i.Time > readHereUpTo);

    Conversation AddSms(SmsItem sms, bool appendToOpenThread)
    {
        string key = KeyOf(sms.Address);
        var conv = _convs.FirstOrDefault(c => c.Key == key);
        if (conv == null)
        {
            conv = new Conversation { Key = key, Address = sms.Address, Display = NameFor(sms.Address) };
            _convs.Insert(0, conv);
        }
        else
        {
            int idx = _convs.IndexOf(conv);
            if (idx > 0) _convs.Move(idx, 0);
        }
        conv.Items.Add(sms);
        conv.Last = sms.Body.Replace('\n', ' ');
        conv.LastTime = sms.Time;

        if (appendToOpenThread && ReferenceEquals(ConvList.SelectedItem, conv))
        {
            _thread.Add(sms);
            ThreadList.ScrollIntoView(sms);
            AnimateIn(ThreadList, sms);
        }
        return conv;
    }

    /// <summary>
    /// Every text conversation read, here. (Only the phone's own texting app may change what's
    /// read on the phone itself, so the phone keeps its own unread marks.)
    /// </summary>
    void MarkAllRead_Click(object sender, RoutedEventArgs e)
    {
        foreach (var c in _convs.Concat(_waConvs).Where(c => c.Unread > 0).ToList()) MarkRead(c);
        RefreshEmptyStates();
    }

    /// <summary>The ✓ on one conversation (Messages list or the Overview's Texts tile).</summary>
    void MarkOneRead_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true; // don't open the conversation as well
        var conv = (sender as FrameworkElement)?.DataContext switch
        {
            Conversation c => c,
            MessageRow r => r.Conv,
            _ => null
        };
        if (conv != null) MarkRead(conv);
        RefreshEmptyStates();
    }

    void MarkRead(Conversation c)
    {
        if (!c.IsWhatsApp) _readUpTo[c.Key] = c.LastTime > DateTime.Now ? c.LastTime : DateTime.Now;
        _ = MarkTextReadOnPhone(c);
        if (c.Unread == 0) return;
        c.Unread = 0;
        RefreshEmptyStates();
    }

    // ------------------------------------------------------------- WhatsApp

    readonly ObservableCollection<Conversation> _waConvs = new();
    // what we sent, so WhatsApp's echo of our own reply isn't shown twice
    readonly List<(string Chat, string Text, DateTime At)> _waSent = new();

    /// <summary>WhatsApp is switched off for now: the Messages page shows texts only. Set true to bring it back.</summary>
    static readonly bool WhatsAppEnabled = false;

    bool ShowingWhatsApp => WhatsAppEnabled && ModeWhatsApp?.IsChecked == true;

    void MsgSource_Checked(object sender, RoutedEventArgs e)
    {
        if (ConvList == null) return;
        ConvList.ItemsSource = ShowingWhatsApp ? _waConvs : _convs;
        NewMessageBtn.Visibility = ShowingWhatsApp ? Visibility.Collapsed : Visibility.Visible;
        ConvList.SelectedItem = null;
        ResetThread();
        RefreshEmptyStates();
    }

    /// <summary>One WhatsApp chat from the phone: its recent messages and whether it can be answered.</summary>
    void OnWhatsAppChat(JsonObject m)
    {
        string id = S(m, "chat");
        if (id.Length == 0) return;
        var conv = _waConvs.FirstOrDefault(c => c.Key == id);
        if (conv == null)
        {
            conv = new Conversation { Key = id, IsWhatsApp = true };
            _waConvs.Insert(0, conv);
        }
        conv.Display = S(m, "name").Length > 0 ? S(m, "name") : "WhatsApp chat";
        conv.Address = "WhatsApp";
        conv.IsGroup = B(m, "group");
        conv.CanReply = B(m, "canReply");

        bool added = false, fromThem = false;
        if (m["messages"] is JsonArray msgs)
            foreach (var x in msgs.OfType<JsonObject>())
            {
                bool me = B(x, "me");
                string text = S(x, "text"), sender = S(x, "sender");
                var time = TimeOf(x);
                if (text.Length == 0) continue;
                string body = conv.IsGroup && !me && sender.Length > 0 ? sender + ": " + text : text;
                // already have it?
                if (conv.Items.Any(i => i.Inbox == !me && i.Body == body && Math.Abs((i.Time - time).TotalSeconds) < 90)) continue;
                // our own reply coming back from WhatsApp: already shown when we sent it
                if (me && _waSent.Any(w => w.Chat == id && w.Text == text && Math.Abs((w.At - time).TotalMinutes) < 5)) continue;
                var item = new SmsItem { Address = sender, Body = body, Time = time, Inbox = !me };
                int at = conv.Items.FindLastIndex(i => i.Time <= time) + 1;
                conv.Items.Insert(at, item);
                added = true;
                fromThem |= !me;
                if (ReferenceEquals(ConvList.SelectedItem, conv))
                {
                    _thread.Insert(Math.Min(at, _thread.Count), item);
                    ThreadList.ScrollIntoView(item);
                    AnimateIn(ThreadList, item);
                }
            }
        if (conv.Items.Count > 0)
        {
            var last = conv.Items[^1];
            conv.Last = (last.Inbox ? "" : "You: ") + last.Body.Replace('\n', ' ');
            conv.LastTime = last.Time;
        }
        // newest chat first
        int idx = _waConvs.IndexOf(conv);
        int target = _waConvs.Count(c => !ReferenceEquals(c, conv) && c.LastTime > conv.LastTime);
        if (idx != target) _waConvs.Move(idx, target);
        if (added && fromThem && !(Watching(PageSms) && ShowingWhatsApp && ReferenceEquals(ConvList.SelectedItem, conv)))
            conv.Unread++;
        if (ReferenceEquals(ConvList.SelectedItem, conv)) RefreshThreadHeader(conv);
        RefreshEmptyStates();
    }

    void RefreshThreadHeader(Conversation conv)
    {
        ThreadSub.Text = (conv.IsGroup ? "WhatsApp group" : "WhatsApp") + (conv.CanReply ? "" : " · open WhatsApp on your phone to reply");
        MsgBox.IsEnabled = conv.CanReply;
        MsgBox.Tag = conv.CanReply ? "WhatsApp message" : "Reply from your phone";
    }

    async Task SendWhatsAppAsync(Conversation conv)
    {
        string text = MsgBox.Text.Trim();
        if (text.Length == 0) return;
        if (!conv.CanReply)
        {
            ToastWindow.Show("Reply from your phone", "WhatsApp only lets Nyx reply while this chat's notification is on your phone.");
            return;
        }
        if (!_client.IsConnected)
        {
            ToastWindow.Show("Not sent", "Connect your phone first.");
            return;
        }
        await _client.SendAsync(new JsonObject { ["t"] = "wa_reply", ["chat"] = conv.Key, ["text"] = text });
        _waSent.Add((conv.Key, text, DateTime.Now));
        if (_waSent.Count > 50) _waSent.RemoveAt(0);
        MsgBox.Clear();
        var item = new SmsItem { Body = text, Time = DateTime.Now, Inbox = false };
        conv.Items.Add(item);
        conv.Last = "You: " + text;
        conv.LastTime = item.Time;
        _thread.Add(item);
        ThreadList.ScrollIntoView(item);
        AnimateIn(ThreadList, item);
        MsgBox.Focus();
    }

    /// <summary>Esc on Messages closes the open conversation and goes back to the empty view.</summary>
    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || NavList.SelectedIndex != PageSms) return;
        if (ConvList.SelectedItem == null && !_composing) return;
        ConvList.SelectedItem = null;
        ResetThread();
        RefreshEmptyStates();
        ConvList.Focus();
        e.Handled = true;
    }

    void ResetThread()
    {
        _composing = false;
        _thread.Clear();
        ThreadTitle.Text = "";
        ThreadSub.Text = "";
        ToRow.Visibility = Visibility.Collapsed;
    }

    void ConvList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ConvList.SelectedItem is not Conversation conv)
        {
            if (!_composing) ResetThread();
            RefreshEmptyStates();
            return;
        }
        _composing = false;
        ToRow.Visibility = Visibility.Collapsed;
        ThreadTitle.Text = conv.Display;
        ThreadSub.Text = conv.IsWhatsApp
            ? (conv.IsGroup ? "WhatsApp group" : "WhatsApp") + (conv.CanReply ? "" : " · open WhatsApp on your phone to reply")
            : conv.Display == conv.Address ? "" : conv.Address;
        MsgBox.IsEnabled = !conv.IsWhatsApp || conv.CanReply;
        MsgBox.Tag = MsgBox.IsEnabled ? (conv.IsWhatsApp ? "WhatsApp message" : "Text message") : "Reply from your phone";
        _thread.Clear();
        foreach (var s in conv.Items) _thread.Add(s);
        if (_thread.Count > 0) ThreadList.ScrollIntoView(_thread[^1]);
        if (NavList.SelectedIndex == PageSms) MarkRead(conv);
        RefreshEmptyStates();
    }

    void UnreadList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // a notification: read and dismiss it on the Notifications page
        if (UnreadList.SelectedItem is not MessageRow) return;
        UnreadList.SelectedItem = null;
        Go(PageNotifications);
    }

    void RecentList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // an unread text: open its conversation
        if (RecentList.SelectedItem is not MessageRow { Conv: { } conv }) return;
        RecentList.SelectedItem = null;
        ConvList.SelectedItem = conv;
        Go(PageSms);
    }

    void NewMessage_Click(object sender, RoutedEventArgs e)
    {
        _composing = true;
        ConvList.SelectedItem = null;
        _thread.Clear();
        ThreadTitle.Text = "New message";
        ThreadSub.Text = "";
        ToRow.Visibility = Visibility.Visible;
        ToBox.Clear();
        RefreshEmptyStates();
        ToBox.Focus();
    }

    void QuickText_Click(object sender, RoutedEventArgs e)
    {
        Go(PageSms);
        NewMessage_Click(sender, e);
    }

    void QuickCall_Click(object sender, RoutedEventArgs e)
    {
        Go(PageCall);
        DialBox.Focus();
    }

    void QuickMirror_Click(object sender, RoutedEventArgs e) => Go(PageScreen);

    /// <summary>Rings the phone loudly, even on silent, until it's found (or 30 seconds pass).</summary>
    async void FindPhone_Click(object sender, RoutedEventArgs e)
    {
        if (!_client.IsConnected) { ToastWindow.Show("Phone not connected", "Nyx can ring your phone once it's connected."); return; }
        if (_phoneProto < PhoneProto) { ToastWindow.Show("Update Nyx on your phone", "Find phone needs the latest Nyx-phone.apk."); return; }
        _ringing = !_ringing;
        await _client.SendAsync(_ringing ? "ring" : "ring_stop");
        FindPhoneText.Text = _ringing ? "Stop ringing" : "Find phone";
    }

    bool _ringing;

    async void ReloadSms_Click(object sender, RoutedEventArgs e)
    {
        if (ShowingWhatsApp) await _client.SendAsync("get_wa");
        else await _client.SendAsync(new JsonObject { ["t"] = "get_sms", ["limit"] = 400 });
    }

    async void MsgBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SendSmsAsync();
        }
    }

    async void SendSms_Click(object sender, RoutedEventArgs e) => await SendSmsAsync();

    async Task SendSmsAsync()
    {
        if (!_composing && ConvList.SelectedItem is Conversation wa && wa.IsWhatsApp)
        {
            await SendWhatsAppAsync(wa);
            return;
        }
        string to = _composing ? ToBox.Text.Trim() : (ConvList.SelectedItem as Conversation)?.Address ?? "";
        string body = MsgBox.Text;
        if (to.Length == 0)
        {
            if (_composing) ToBox.Focus();
            return;
        }
        if (body.Trim().Length == 0) return;
        if (!_client.IsConnected)
        {
            StatusText.Text = "Not connected, so the text was not sent.";
            ToastWindow.Show("Text not sent", "Connect your phone first.");
            return;
        }
        await _client.SendAsync(new JsonObject { ["t"] = "send_sms", ["to"] = to, ["body"] = body });
        MsgBox.Clear();

        var sms = new SmsItem { Address = to, Body = body, Time = DateTime.Now, Inbox = false };
        var conv = AddSms(sms, true);
        if (!ReferenceEquals(ConvList.SelectedItem, conv))
            ConvList.SelectedItem = conv; // loads the thread, including the new text
        MsgBox.Focus();
    }

    // ------------------------------------------------------------------ calls

    void SetCall(string state, string number, string name)
    {
        string who = name.Length > 0 ? name : (number.Length > 0 ? number : "Unknown number");
        _callState = state;
        switch (state)
        {
            case "ringing":
                ShowCallBanner();
                CallStatus.Text = who + " is calling";
                CallDetail.Text = name.Length > 0 ? number : "Incoming call";
                AnswerBtn.Visibility = Visibility.Visible;
                HangupBtn.Content = "Decline";
                Pulse(null, CallDot, true);
                // the caller's name or number can arrive in a second "ringing" message;
                // rebuild the panel so it never sticks on "Unknown number"
                if (_callWindow != null && (_callWindowWho != number + "|" + name))
                    CloseCallWindow();
                if (_callWindow == null)
                {
                    _callWindowWho = number + "|" + name;
                    var w = new CallWindow(name, number,
                        async () => await _client.SendAsync("answer"),
                        async () => await _client.SendAsync("hangup"));
                    w.Closed += (_, _) => { if (ReferenceEquals(_callWindow, w)) _callWindow = null; };
                    _callWindow = w;
                    w.Show();
                }
                break;

            case "offhook":
                CloseCallWindow();
                ShowCallBanner();
                CallStatus.Text = number.Length > 0 || name.Length > 0 ? "On a call with " + who : "On a call";
                CallDetail.Text = name.Length > 0 ? number : "Live on your phone";
                AnswerBtn.Visibility = Visibility.Collapsed;
                HangupBtn.Content = "Hang up";
                Pulse(null, CallDot, false);
                break;

            default:
                CloseCallWindow();
                Pulse(null, CallDot, false);
                CallBanner.Visibility = Visibility.Collapsed;
                break;
        }
    }

    void ShowCallBanner()
    {
        if (CallBanner.Visibility == Visibility.Visible) return;
        CallBanner.Visibility = Visibility.Visible;
        if (!Theme.Motion) return;
        var s = new ScaleTransform(0.9, 0.9);
        CallBanner.RenderTransformOrigin = new Point(1, 0.5);
        CallBanner.RenderTransform = s;
        var a = new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut } };
        s.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        s.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        CallBanner.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
    }

    void CloseCallWindow()
    {
        var w = _callWindow;
        _callWindow = null;
        try { w?.Dismiss(); } catch { }
    }

    void Key_Click(object sender, RoutedEventArgs e)
    {
        DialBox.Text += (sender as Button)?.Content as string;
        DialBox.CaretIndex = DialBox.Text.Length;
    }

    void Backspace_Click(object sender, RoutedEventArgs e)
    {
        if (DialBox.Text.Length > 0) DialBox.Text = DialBox.Text[..^1];
        DialBox.CaretIndex = DialBox.Text.Length;
    }

    async void DialBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await DialAsync();
        }
    }

    async void Dial_Click(object sender, RoutedEventArgs e) => await DialAsync();

    async Task DialAsync()
    {
        string number = DialBox.Text.Trim();
        if (number.Length == 0)
        {
            DialBox.Focus();
            return;
        }
        if (!_client.IsConnected)
        {
            StatusText.Text = "Connect your phone before placing a call.";
            ToastWindow.Show("Not connected", "Connect your phone before placing a call.");
            return;
        }
        await _client.SendAsync(new JsonObject { ["t"] = "dial", ["number"] = number });
        ShowCallBanner();
        CallStatus.Text = "Calling " + NameFor(number);
        CallDetail.Text = "Dialling from your phone";
        AnswerBtn.Visibility = Visibility.Collapsed;
        HangupBtn.Content = "Hang up";
    }

    async void Answer_Click(object sender, RoutedEventArgs e) => await _client.SendAsync("answer");

    async void Hangup_Click(object sender, RoutedEventArgs e)
    {
        await _client.SendAsync("hangup");
        if (_callState == "idle") CallBanner.Visibility = Visibility.Collapsed; // a dial that never connected
    }

    async void HandsFree_Click(object sender, RoutedEventArgs e)
    {
        AudioStatus.Text = "Asking Windows for the hands-free link...";
        // every name we know the phone by, and the Bluetooth ids of paired devices with those names
        var names = new List<string> { PhoneName.Text, DeviceTitle.Text };
        if (_device != null) names.Add(_device.Name);
        var btIds = new List<string>();
        if (_device != null) btIds.Add(_device.Id);
        if (DeviceList.ItemsSource is IEnumerable<DeviceEntry> paired)
            btIds.AddRange(paired.Where(d => names.Contains(d.Name)).Select(d => d.Id));
        AudioStatus.Text = await HandsFree.ConnectAsync(names, btIds);
    }

    void ContactSearch_TextChanged(object sender, TextChangedEventArgs e) => FilterContacts();

    readonly List<(string Number, string Name, string Kind, DateTime Time)> _calls = new();

    /// <summary>
    /// With the search box empty: the people you most recently called or texted, newest first.
    /// While searching: every contact whose name or number matches (spaces in numbers ignored).
    /// </summary>
    void FilterContacts()
    {
        string q = ContactSearch.Text.Trim();
        _contactView.Clear();
        if (q.Length == 0)
        {
            var recent = RecentPeople(30);
            ContactsTitle.Text = recent.Count > 0 ? "Recent" : "Contacts";
            foreach (var c in recent.Count > 0 ? recent : _contacts) _contactView.Add(c);
        }
        else
        {
            ContactsTitle.Text = "Contacts";
            string digits = new string(q.Where(char.IsDigit).ToArray());
            foreach (var c in _contacts)
                if (c.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    (digits.Length >= 3 && new string(c.Number.Where(char.IsDigit).ToArray()).Contains(digits)))
                    _contactView.Add(c);
        }
        RefreshEmptyStates();
    }

    List<ContactItem> RecentPeople(int max)
    {
        var latest = new Dictionary<string, (string Number, string Name, string What, DateTime Time)>();
        void Note(string number, string name, string what, DateTime time)
        {
            if (number.Length == 0) return;
            string key = KeyOf(number);
            if (!latest.TryGetValue(key, out var cur) || time > cur.Time) latest[key] = (number, name, what, time);
        }
        foreach (var c in _calls)
            Note(c.Number, c.Name, c.Kind switch { "missed" => "Missed call", "out" => "You called", _ => "Called you" }, c.Time);
        foreach (var conv in _convs)
            if (conv.Items.Count > 0)
            {
                var last = conv.Items[^1];
                if (InputCheck.IsDialable(conv.Address)) // not "AD-SBIUPI-S" and other text-only senders
                    Note(conv.Address, "", last.Inbox ? "Texted you" : "You texted", last.Time);
            }
        return latest.Values.OrderByDescending(v => v.Time).Take(max).Select(v =>
        {
            string name = NameFor(v.Number);
            if (name == v.Number && v.Name.Length > 0) name = v.Name;
            return new ContactItem(name, v.Number, v.What + " · " + Ago(v.Time));
        }).ToList();
    }

    static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalHours < 1) return (int)d.TotalMinutes + " min ago";
        if (d.TotalDays < 1 && t.Date == DateTime.Today) return (int)d.TotalHours + " h ago";
        if (t.Date == DateTime.Today.AddDays(-1)) return "yesterday " + t.ToString("HH:mm");
        return t.ToString("d MMM");
    }

    void ContactList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ContactList.SelectedItem is ContactItem c) DialBox.Text = c.Number;
    }

    async void ContactList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ContactList.SelectedItem is ContactItem c)
        {
            DialBox.Text = c.Number;
            await DialAsync();
        }
    }

    // ----------------------------------------------------------------- mirror

    void BrowseScrcpy_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "scrcpy.exe|scrcpy.exe|Programs|*.exe" };
        if (dlg.ShowDialog(this) == true) ScrcpyPath.Text = dlg.FileName;
        RefreshMirror();
    }

    void ScrcpyPath_LostFocus(object sender, RoutedEventArgs e) => RefreshMirror();

    /// <summary>Shows where scrcpy was found, or offers to install it.</summary>
    void RefreshMirror()
    {
        RefreshSecondScreen();
        RefreshWebcam();
        RefreshMic();
        RefreshAudio();
        var found = Mirror.FindScrcpy(ScrcpyPath.Text);
        if (found != null)
        {
            ScrcpyPath.Text = found;
            InstallScrcpyBtn.Visibility = Visibility.Collapsed;
            ScrcpyState.Text = "Found scrcpy. Pick USB or Wi-Fi below.";
        }
        else
        {
            InstallScrcpyBtn.Visibility = Visibility.Visible;
            ScrcpyState.Text = "scrcpy isn't installed on this laptop yet. It's free and open source: Install sets it up with winget, " +
                               "or Browse to a scrcpy.exe you already have.";
        }
    }

    // ----------------------------------------------------------- second screen

    SecondScreen? _screen;
    SecondScreen.Display? _screenTarget;
    HashSet<long> _offeredWindows = new();
    bool _phoneHasScreen;

    void RefreshSecondScreen()
    {
        if (_screen?.Running == true) { ExtendBtn.Content = "Stop"; GetDisplayBtn.Visibility = Visibility.Collapsed; return; }
        ExtendBtn.Content = "Extend to phone";
        var t = SecondScreen.FindTarget();
        GetDisplayBtn.Visibility = t == null ? Visibility.Visible : Visibility.Collapsed;
        SecondScreenState.Text = t == null
            ? "Windows needs an extra display to extend onto. Install the free, open-source Virtual Display Driver (Get the virtual display), " +
              "add a display in it, then come back here."
            : "Ready: " + t.Name + " (" + t.Bounds.Width + "×" + t.Bounds.Height + ") goes to your phone. Arrange displays sets where it sits next to this screen.";
    }

    void GetVirtualDisplay_Click(object sender, RoutedEventArgs e) =>
        OpenLink("https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/latest");

    void ArrangeDisplays_Click(object sender, RoutedEventArgs e) => OpenLink("ms-settings:display");

    static void OpenLink(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    async void Extend_Click(object sender, RoutedEventArgs e)
    {
        if (_screen?.Running == true) { await StopSecondScreen(tellPhone: true); return; }
        await OfferSecondScreen();
    }

    /// <summary>Sends the phone a fresh key; the phone opens its viewer and answers screen_ready.</summary>
    async Task OfferSecondScreen()
    {
        _screenTarget = await Task.Run(SecondScreen.PrepareTarget); // phone-friendly size, if it had an odd one
        string? problem =
            _screenTarget == null ? "There's no extra display yet. Get the virtual display, add a display in it, and try again."
            : !_client.IsConnected ? "Connect your phone first."
            : _client.Kind == "Bluetooth" ? "The second screen needs the Wi-Fi link; Bluetooth is too slow for video."
            : !_phoneHasScreen ? "Update Nyx on your phone: this version can't show a second screen yet."
            : !InputCheck.IsIPv4(PhoneIpBox.Text.Trim()) ? "The phone's Wi-Fi address isn't known yet. Reconnect the phone."
            : null;
        if (problem != null)
        {
            SecondScreenState.Text = problem;
            await _client.TrySendAsync(new JsonObject { ["t"] = "screen_error", ["msg"] = problem });
            return;
        }
        _screen?.Dispose();
        _screen = new SecondScreen();
        SecondScreenState.Text = "Starting on your phone… If it doesn't open by itself, tap Nyx's notification there.";
        await _client.SendAsync(new JsonObject
        {
            ["t"] = "screen_offer", ["key"] = Convert.ToBase64String(_screen.Key),
            ["w"] = _screenTarget!.Bounds.Width, ["h"] = _screenTarget.Bounds.Height
        });
    }

    async Task StartSecondScreen(int port)
    {
        var s = _screen;
        string ip = PhoneIpBox.Text.Trim();
        if (s == null || s.Running || _screenTarget == null || port is <= 0 or > 65535 || !InputCheck.IsIPv4(ip)) return;
        s.Status += text => Dispatcher.BeginInvoke(() => SecondScreenState.Text = text);
        var target = _screenTarget;
        s.Ended += () =>
        {
            // windows and pointer back on the laptop, virtual screen off: nothing lost out of sight
            SecondScreen.Tidy(target);
            Diag.Log("second screen: ended");
            Dispatcher.BeginInvoke(RefreshSecondScreenAfterEnd);
        };
        ExtendBtn.Content = "Stop";
        Diag.Log("second screen: started");
        var b = _screenTarget.Bounds; // relative to this screen, which starts at 0,0
        string edge = b.X > 0 ? "right" : b.Right <= 0 ? "left" : b.Y < 0 ? "top" : "bottom";
        ToastWindow.Show("Your phone is now a second screen",
            "It starts empty, like any new monitor. On the phone, tap Windows to bring one over. Or drag a window past the " + edge +
            " edge of this screen, or press Win+Shift+→ on the window you're using.",
            () => OpenLink("ms-settings:display"), 10);
        await s.RunAsync(ip, port, _screenTarget);
    }

    void RefreshSecondScreenAfterEnd()
    {
        string said = SecondScreenState.Text;
        RefreshSecondScreen();
        if (said.Length > 0 && !said.StartsWith("On your phone")) SecondScreenState.Text = said;
    }

    async Task StopSecondScreen(bool tellPhone)
    {
        _screen?.Stop();
        if (tellPhone) await _client.TrySendAsync(new JsonObject { ["t"] = "screen_stop" });
        RefreshSecondScreen();
    }

    // ----------------------------------------------------------- webcam

    PhoneCamera? _cam;
    bool _phoneHasCamera;

    void RefreshWebcam()
    {
        if (_cam?.Running == true) { WebcamBtn.Content = "Stop"; return; }
        WebcamBtn.Content = "Use phone camera";
        if (!VirtualCam.Installed)
            WebcamState.Text = "Meet, Zoom and Teams need a camera they can see: Nyx uses the free OBS Virtual Camera. Install OBS Studio (Get OBS), then come back.";
        else if (WebcamState.Text.Length == 0 || WebcamState.Text.StartsWith("Meet, Zoom"))
            WebcamState.Text = "Ready. Start it here or on the phone (Use as webcam), then pick \"OBS Virtual Camera\" in the call.";
        GetObsBtn.Visibility = VirtualCam.Installed ? Visibility.Collapsed : Visibility.Visible;
    }

    void WebcamFill_Click(object sender, RoutedEventArgs e)
    {
        VirtualCam.FillFrame = WebcamFillBox.IsChecked == true;
        AppPrefs.Set("webcamFill", VirtualCam.FillFrame);
    }

    void GetObs_Click(object sender, RoutedEventArgs e) => OpenLink("https://obsproject.com/download");

    async void Webcam_Click(object sender, RoutedEventArgs e)
    {
        if (_cam?.Running == true) { await StopWebcam(tellPhone: true); return; }
        await OfferWebcam();
    }

    async Task OfferWebcam()
    {
        string? problem =
            !VirtualCam.Installed ? "Install OBS Studio first: its free virtual camera is how the call sees your phone."
            : !_client.IsConnected ? "Connect your phone first."
            : _client.Kind == "Bluetooth" ? "The webcam needs the Wi-Fi link; Bluetooth is too slow for video."
            : !_phoneHasCamera ? "Update Nyx on your phone: this version can't be a webcam yet."
            : !InputCheck.IsIPv4(PhoneIpBox.Text.Trim()) ? "The phone's Wi-Fi address isn't known yet. Reconnect the phone."
            : null;
        if (problem != null)
        {
            WebcamState.Text = problem;
            await _client.TrySendAsync(new JsonObject { ["t"] = "cam_error", ["msg"] = problem });
            return;
        }
        _cam?.Dispose();
        _cam = new PhoneCamera();
        WebcamState.Text = "Starting on your phone… If it doesn't open by itself, tap Nyx's notification there.";
        await _client.SendAsync(new JsonObject { ["t"] = "cam_offer", ["key"] = Convert.ToBase64String(_cam.Key) });
    }

    async Task StartWebcam(int port)
    {
        var c = _cam;
        string ip = PhoneIpBox.Text.Trim();
        if (c == null || c.Running || port is <= 0 or > 65535 || !InputCheck.IsIPv4(ip)) return;
        c.Status += text => Dispatcher.BeginInvoke(() => WebcamState.Text = text);
        c.Ended += () => Dispatcher.BeginInvoke(() => { Diag.Log("webcam: ended"); RefreshWebcam(); });
        WebcamBtn.Content = "Stop";
        Diag.Log("webcam: started");
        await c.RunAsync(ip, port);
    }

    async Task StopWebcam(bool tellPhone)
    {
        _cam?.Stop();
        if (tellPhone) await _client.TrySendAsync(new JsonObject { ["t"] = "cam_stop" });
        RefreshWebcam();
    }

    // ----------------------------------------------------------- phone audio on the laptop

    PhoneAudio? _audio;
    bool _phoneHasAudio;

    void RefreshAudio()
    {
        AudioShareBtn.Content = _audio?.Running == true ? "Stop" : "Play phone audio";
        if (_audio?.Running != true && (AudioShareState.Text.Length == 0))
            AudioShareState.Text = "Start it here or on the phone (Play phone audio on laptop). The phone asks to share first.";
    }

    async void AudioShare_Click(object sender, RoutedEventArgs e)
    {
        if (_audio?.Running == true)
        {
            _audio.Stop();
            await _client.TrySendAsync(new JsonObject { ["t"] = "audio_stop" });
            RefreshAudio();
            return;
        }
        await OfferAudio();
    }

    async Task OfferAudio()
    {
        string? problem =
            !_client.IsConnected ? "Connect your phone first."
            : _client.Kind == "Bluetooth" ? "Phone audio needs the Wi-Fi link."
            : !_phoneHasAudio ? "Update Nyx on your phone (and it needs Android 10 or newer)."
            : !InputCheck.IsIPv4(PhoneIpBox.Text.Trim()) ? "The phone's Wi-Fi address isn't known yet. Reconnect the phone."
            : null;
        if (problem != null) { AudioShareState.Text = problem; return; }
        _audio?.Dispose();
        _audio = new PhoneAudio();
        AudioShareState.Text = "Tap Start on your phone.";
        await _client.SendAsync(new JsonObject { ["t"] = "audio_offer", ["key"] = Convert.ToBase64String(_audio.Key) });
    }

    async Task StartAudio(int port)
    {
        var a = _audio;
        string ip = PhoneIpBox.Text.Trim();
        if (a == null || a.Running || port is <= 0 or > 65535 || !InputCheck.IsIPv4(ip)) return;
        a.Status += text => Dispatcher.BeginInvoke(() => AudioShareState.Text = text);
        a.Ended += () => Dispatcher.BeginInvoke(() => { Diag.Log("phone audio: ended"); RefreshAudio(); _mirrorWin?.ShowSound(false); });
        AudioShareBtn.Content = "Stop";
        // mirroring with sound: the window's mute button controls it
        _mirrorWin?.ShowSound(true);
        Diag.Log("phone audio: started");
        await a.RunAsync(ip, port);
    }

    // ----------------------------------------------------------- microphone

    PhoneMic? _mic;
    PhoneMic.Cable? _cable;
    bool _phoneHasMic;

    void RefreshMic()
    {
        if (_mic?.Running == true) { MicBtn.Content = "Stop"; return; }
        MicBtn.Content = "Use phone mic";
        _cable = PhoneMic.FindCable();
        GetCableBtn.Visibility = _cable == null ? Visibility.Visible : Visibility.Collapsed;
        if (_cable == null)
            MicState.Text = "Calls need a microphone they can pick: Nyx plays the phone's voice into the free VB-Audio Virtual Cable. " +
                            "Install it (Get VB-Cable, then run VBCABLE_Setup_x64 as administrator and restart), then come back.";
        else if (MicState.Text.Length == 0 || MicState.Text.StartsWith("Calls need"))
            MicState.Text = "Ready. Start it here or on the phone, then pick \"" + _cable.PickInCall + "\" as the microphone in the call.";
    }

    void GetCable_Click(object sender, RoutedEventArgs e) => OpenLink("https://vb-audio.com/Cable/");

    async void Mic_Click(object sender, RoutedEventArgs e)
    {
        if (_mic?.Running == true) { await StopMic(tellPhone: true); return; }
        await OfferMic();
    }

    async Task OfferMic()
    {
        _cable = PhoneMic.FindCable();
        string? problem =
            _cable == null ? "Install VB-Audio Virtual Cable first: it's the microphone the call picks."
            : !_client.IsConnected ? "Connect your phone first."
            : _client.Kind == "Bluetooth" ? "The microphone needs the Wi-Fi link."
            : !_phoneHasMic ? "Update Nyx on your phone: this version can't be a microphone yet."
            : !InputCheck.IsIPv4(PhoneIpBox.Text.Trim()) ? "The phone's Wi-Fi address isn't known yet. Reconnect the phone."
            : null;
        if (problem != null)
        {
            MicState.Text = problem;
            await _client.TrySendAsync(new JsonObject { ["t"] = "mic_error", ["msg"] = problem });
            return;
        }
        _mic?.Dispose();
        _mic = new PhoneMic();
        MicState.Text = "Starting on your phone… If it doesn't start by itself, tap Nyx's notification there.";
        await _client.SendAsync(new JsonObject { ["t"] = "mic_offer", ["key"] = Convert.ToBase64String(_mic.Key) });
    }

    async Task StartMic(int port)
    {
        var m = _mic;
        var cable = _cable;
        string ip = PhoneIpBox.Text.Trim();
        if (m == null || cable == null || m.Running || port is <= 0 or > 65535 || !InputCheck.IsIPv4(ip)) return;
        m.Status += text => Dispatcher.BeginInvoke(() => MicState.Text = text);
        m.Ended += () => Dispatcher.BeginInvoke(() => { Diag.Log("microphone: ended"); RefreshMic(); });
        MicBtn.Content = "Stop";
        Diag.Log("microphone: started");
        await m.RunAsync(ip, port, cable);
    }

    async Task StopMic(bool tellPhone)
    {
        _mic?.Stop();
        if (tellPhone) await _client.TrySendAsync(new JsonObject { ["t"] = "mic_stop" });
        RefreshMic();
    }

    static double D(JsonObject m, string key)
    {
        try { return m[key]?.GetValue<double>() ?? double.NaN; } catch { return double.NaN; }
    }

    void InstallScrcpy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // a visible console, so you see winget's own progress and prompts
            var psi = new ProcessStartInfo("winget") { UseShellExecute = true };
            foreach (var a in new[] { "install", "--id", "Genymobile.scrcpy", "-e", "--source", "winget" }) psi.ArgumentList.Add(a);
            var p = Process.Start(psi);
            ScrcpyState.Text = "Installing scrcpy in the window that opened. This card updates when it finishes.";
            if (p != null)
                _ = p.WaitForExitAsync().ContinueWith(_ => Dispatcher.Invoke(RefreshMirror));
        }
        catch (Exception ex)
        {
            ScrcpyState.Text = "winget isn't available (" + ex.Message + "). Download scrcpy from github.com/Genymobile/scrcpy and use Browse.";
        }
    }

    string? ScrcpyOrSay()
    {
        var s = Mirror.FindScrcpy(ScrcpyPath.Text);
        if (s == null)
        {
            RefreshMirror();
            MirrorStatus.Text = "Install scrcpy first (top card).";
        }
        return s;
    }

    static async Task Busy(Button b, Func<Task> work)
    {
        b.IsEnabled = false;
        try { await work(); }
        finally { b.IsEnabled = true; }
    }

    async void MirrorUsb_Click(object sender, RoutedEventArgs e)
    {
        var scrcpy = ScrcpyOrSay();
        if (scrcpy == null) return;
        await Busy((Button)sender, async () =>
        {
            MirrorStatus.Text = "Opening the mirror over USB…";
            // scrcpy brings the sound along by itself (Android 11+); without sound, say so
            var args = new List<string> { "-d", "--window-title", "Nyx" };
            if (UsbSoundBox.IsChecked != true) args.Add("--no-audio");
            var r = await Mirror.RunAsync(scrcpy, TimeSpan.FromSeconds(6), args.ToArray());
            MirrorStatus.Text = !r.Started ? "Windows could not start scrcpy: " + r.Output
                : !r.Exited || r.Code == 0 ? "The mirror is open in its own window."
                : Mirror.Explain(r.Output);
        });
    }

    async void EnableTcpip_Click(object sender, RoutedEventArgs e)
    {
        var scrcpy = ScrcpyOrSay();
        if (scrcpy == null) return;
        var adb = Mirror.FindAdb(scrcpy);
        if (adb == null) { MirrorStatus.Text = "adb.exe wasn't found next to scrcpy. Reinstall scrcpy, or Browse to scrcpy.exe in its full folder."; return; }
        {
            MirrorStatus.Text = "Switching the phone to Wi-Fi mode…";
            var r = await Mirror.RunAsync(adb, TimeSpan.FromSeconds(15), "-d", "tcpip", "5555");
            MirrorStatus.Text = r.Started && r.Exited && r.Code == 0 && !r.Output.Contains("error", StringComparison.OrdinalIgnoreCase)
                ? "Wi-Fi mode is on until the phone restarts. Unplug the cable and press Mirror over Wi-Fi."
                : !r.Started ? "Windows could not start adb: " + r.Output
                : !r.Exited ? "The phone didn't answer over USB. Unlock it and allow USB debugging."
                : Mirror.Explain(r.Output);
        }
    }

    /// <summary>The phone's address from the box, or null (with the reason shown) when it isn't a plain IPv4 address.</summary>
    string? PhoneIpOrSay()
    {
        string ip = PhoneIpBox.Text.Trim();
        if (ip.Length == 0)
        {
            MirrorStatus.Text = "The phone's address is not known yet. Connect the phone first, or type it in.";
            PhoneIpBox.Focus();
            return null;
        }
        // the address can come from the phone, so it must be a plain IPv4 address and nothing
        // else: no spaces, quotes or extra flags can ride along into adb's or scrcpy's command line
        if (!InputCheck.IsIPv4(ip))
        {
            MirrorStatus.Text = "That isn't a valid phone address. It should look like 192.168.1.24.";
            PhoneIpBox.Focus();
            return null;
        }
        return ip;
    }

    /// <summary>One-time pairing with Android's Wireless debugging (Android 11 and newer).</summary>
    async void PairWireless_Click(object sender, RoutedEventArgs e)
    {
        string? ip = PhoneIpOrSay();
        if (ip == null) return;
        string port = PairPortBox.Text.Trim(), code = PairCodeBox.Text.Trim();
        if (!Mirror.ValidPairing(port, code))
        {
            MirrorStatus.Text = "Type the pairing port (just the number after the colon) and the 6-digit code from the phone's Pair device with pairing code screen.";
            return;
        }
        var scrcpy = ScrcpyOrSay();
        if (scrcpy == null) return;
        var adb = Mirror.FindAdb(scrcpy);
        if (adb == null) { MirrorStatus.Text = "adb.exe wasn't found next to scrcpy. Reinstall scrcpy."; return; }
        await Busy((Button)sender, async () =>
        {
            MirrorStatus.Text = "Pairing with the phone…";
            var r = await Mirror.RunAsync(adb, TimeSpan.FromSeconds(20), "pair", ip + ":" + port, code);
            bool ok = r.Output.Contains("Successfully paired", StringComparison.OrdinalIgnoreCase);
            PairCodeBox.Text = ""; // single use
            MirrorStatus.Text = ok ? "Paired. From now on, Mirror over Wi-Fi works whenever Wireless debugging is on."
                : !r.Started ? "Windows could not start adb: " + r.Output
                : !r.Exited ? "The phone didn't answer. Keep the pairing screen open on the phone and try again."
                : Mirror.Explain(r.Output);
        });
    }

    // ----------------------------------------------------------- Nyx's own mirroring over Wi-Fi

    PhoneMirror? _mirror;
    MirrorWindow? _mirrorWin;
    bool _phoneHasMirror;

    async void MirrorWifi_Click(object sender, RoutedEventArgs e)
    {
        if (!await TryScrcpyWifi()) await OfferMirror();
    }

    // ----------------------------------------------------------- USB quality over Wi-Fi (scrcpy)

    System.Windows.Threading.DispatcherTimer? _wifiAdbTimer;
    bool _arming;
    string? _armedSerial;

    void WifiAdb_Click(object sender, RoutedEventArgs e)
    {
        AppPrefs.Set("wifiAdb", WifiAdbBox.IsChecked == true);
        _armedSerial = null;
        if (WifiAdbBox.IsChecked == true) _ = ArmWifiAdb();
    }

    /// <summary>With the phone on USB: turns on adb over Wi-Fi (port 5555) if it's off. Lasts until the phone restarts.</summary>
    async Task ArmWifiAdb()
    {
        if (_arming || WifiAdbBox.IsChecked != true) return;
        var scrcpy = Mirror.FindScrcpy(ScrcpyPath.Text);
        var adb = scrcpy == null ? null : Mirror.FindAdb(scrcpy);
        if (adb == null) return;
        _arming = true;
        try
        {
            var list = await Mirror.RunAsync(adb, TimeSpan.FromSeconds(6), "devices");
            string? usb = Mirror.UsbSerial(list.Output);
            if (usb == null) { _armedSerial = null; return; }
            if (usb == _armedSerial) return; // already checked since it was plugged in
            var port = await Mirror.RunAsync(adb, TimeSpan.FromSeconds(6), "-s", usb, "shell", "getprop", "service.adb.tcp.port");
            if (port.Output.Trim() == "5555") { _armedSerial = usb; return; }
            // switching restarts the phone's debugging service, which would cut a mirror running over USB
            if (System.Diagnostics.Process.GetProcessesByName("scrcpy").Length > 0) return;
            var r = await Mirror.RunAsync(adb, TimeSpan.FromSeconds(15), "-s", usb, "tcpip", "5555");
            bool ok = r.Exited && r.Code == 0 && !r.Output.Contains("error", StringComparison.OrdinalIgnoreCase);
            if (ok) _armedSerial = usb;
            Diag.Log("wifi adb: " + (ok ? "on" : "failed: " + r.Output.Trim()));
        }
        catch (Exception ex) { Diag.Log("wifi adb: " + ex.Message); }
        finally { _arming = false; }
    }

    /// <summary>Opens scrcpy over Wi-Fi when the phone's Wi-Fi debugging is on; false = use Nyx's own mirror.</summary>
    async Task<bool> TryScrcpyWifi()
    {
        if (WifiAdbBox.IsChecked != true) return false;
        string ip = PhoneIpBox.Text.Trim();
        if (!InputCheck.IsIPv4(ip)) return false;
        var scrcpy = Mirror.FindScrcpy(ScrcpyPath.Text);
        var adb = scrcpy == null ? null : Mirror.FindAdb(scrcpy);
        if (scrcpy == null || adb == null) return false;
        if (System.Diagnostics.Process.GetProcessesByName("scrcpy").Length > 0)
        {
            MirrorStatus.Text = "The mirror is already open in its own window.";
            return true;
        }
        string target = ip + ":5555";
        MirrorStatus.Text = "Connecting over Wi-Fi…";
        var c = await Mirror.RunAsync(adb, TimeSpan.FromSeconds(6), "connect", target);
        if (!Mirror.Connected(c.Output))
        {
            Diag.Log("mirror: scrcpy wifi not available (" + c.Output.Trim() + ")");
            await Mirror.RunAsync(adb, TimeSpan.FromSeconds(4), "disconnect", target);
            return false;
        }
        var args = new List<string> { "-s", target, "--window-title", "Nyx", "--video-bit-rate=16M" };
        if (MirrorSoundBox.IsChecked != true) args.Add("--no-audio");
        var r = await Mirror.RunAsync(scrcpy, TimeSpan.FromSeconds(6), args.ToArray());
        bool open = r.Started && (!r.Exited || r.Code == 0);
        MirrorStatus.Text = open ? "The mirror is open in its own window (USB quality, over Wi-Fi)." : Mirror.Explain(r.Output);
        Diag.Log("mirror: scrcpy over wifi " + (open ? "open" : "failed"));
        return open;
    }

    async Task MirrorTest(string file)
    {
        await Task.Delay(1000);
        var data = await System.IO.File.ReadAllBytesAsync(file);
        var units = H264Decoder.SplitPictures(data);
        var win = new MirrorWindow(_ => Task.CompletedTask, canControl: false);
        win.Show();
        (byte[] px, int w, int h)? nextPx = null;
        object gate = new();
        long decodeTicks = 0; int decoded = 0;
        var run = Task.Factory.StartNew(() =>
        {
            using var d = new H264Decoder();
            var bufs = new byte[2][]; int which = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long t = 0;
            for (int n = 0; clock.Elapsed < TimeSpan.FromSeconds(12); n++)
            {
                var (start, len) = units[n % units.Count];
                if (n > 0 && n % units.Count == 0) { d.Dispose(); return; } // play once (keyframe only at the start)
                long before = System.Diagnostics.Stopwatch.GetTimestamp();
                var target = bufs[which]; bool made = false;
                void Got(byte[] px, int w, int h)
                {
                    bufs[which] = px; made = true; decoded++;
                    bool post;
                    lock (gate) { post = nextPx == null; nextPx = (px, w, h); }
                    if (post) Dispatcher.BeginInvoke(() =>
                    {
                        (byte[] px, int w, int h)? pic;
                        lock (gate) { pic = nextPx; nextPx = null; }
                        if (pic is { } p) win.ShowPixels(p.px, p.w, p.h);
                    });
                }
                if (target != null) lock (target) d.Push(data, start, len, t += 166_666, target, Got);
                else d.Push(data, start, len, t += 166_666, null, Got);
                if (made) which ^= 1;
                decodeTicks += System.Diagnostics.Stopwatch.GetTimestamp() - before;
                var due = TimeSpan.FromMilliseconds((n + 1) * 1000.0 / 60);
                var wait = due - clock.Elapsed;
                if (wait > TimeSpan.Zero) Thread.Sleep(wait);
            }
        }, TaskCreationOptions.LongRunning);
        var started = DateTime.UtcNow;
        await run;
        double secs = (DateTime.UtcNow - started).TotalSeconds;
        Diag.Log($"mirrortest: decoded {decoded / secs:N1} fps, painted {win.Painted / secs:N1} fps, decode+convert {(decoded > 0 ? decodeTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / decoded : 0):N1} ms each");
        win.Close();
    }

    /// <summary>
    /// Payment apps refuse to run while Nyx's laptop-control (accessibility) service is on. One
    /// adb grant lets the phone switch it on only while mirroring and off right after.
    /// </summary>
    async void PayFix_Click(object sender, RoutedEventArgs e)
    {
        var scrcpy = Mirror.FindScrcpy(ScrcpyPath.Text);
        var adb = scrcpy == null ? null : Mirror.FindAdb(scrcpy);
        if (adb == null) { MirrorStatus.Text = "This needs adb, which comes with scrcpy: install scrcpy first (top card)."; return; }
        MirrorStatus.Text = "Allowing it over USB…";
        var r = await Mirror.RunAsync(adb, TimeSpan.FromSeconds(10), "-d", "shell", "pm", "grant", "com.amak.phonebridge", "android.permission.WRITE_SECURE_SETTINGS");
        bool ok = r.Started && r.Exited && r.Code == 0 && r.Output.Trim().Length == 0;
        Diag.Log("payfix: " + (ok ? "granted" : "failed: " + r.Output.Trim()));
        if (!ok)
        {
            MirrorStatus.Text = r.Output.Contains("no devices", StringComparison.OrdinalIgnoreCase) || r.Output.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
                ? "Plug the phone in by USB with USB debugging on (and tap Allow on the phone), then press this again."
                : r.Output.Contains("Unknown permission", StringComparison.OrdinalIgnoreCase) || r.Output.Contains("not a changeable", StringComparison.OrdinalIgnoreCase)
                ? "Update Nyx on the phone first, then press this again."
                : Mirror.Explain(r.Output);
            return;
        }
        MirrorStatus.Text = "Done. Laptop control now switches on only while you mirror, so payment apps work the rest of the time.";
        await _client.TrySendAsync(new JsonObject { ["t"] = "control_auto" }); // turn it off right now
    }

    /// <summary>
    /// Android's own switch that lets Nyx share the screen without the "Start?" question
    /// (appops PROJECT_MEDIA), set over USB with adb. Pressing it again puts the question back.
    /// </summary>
    async void NoAsk_Click(object sender, RoutedEventArgs e)
    {
        var scrcpy = Mirror.FindScrcpy(ScrcpyPath.Text);
        var adb = scrcpy == null ? null : Mirror.FindAdb(scrcpy);
        if (adb == null) { MirrorStatus.Text = "This needs adb, which comes with scrcpy: install scrcpy first (top card)."; return; }
        const string pkg = "com.amak.phonebridge";
        MirrorStatus.Text = "Checking the phone over USB…";
        var now = await Mirror.RunAsync(adb, TimeSpan.FromSeconds(10), "-d", "shell", "appops", "get", pkg, "PROJECT_MEDIA");
        if (!now.Started || !now.Exited || now.Code != 0)
        {
            MirrorStatus.Text = "Plug the phone in by USB with USB debugging on (and tap Allow on the phone), then press this again.";
            return;
        }
        bool allowed = now.Output.Contains("allow", StringComparison.OrdinalIgnoreCase);
        var r = await Mirror.RunAsync(adb, TimeSpan.FromSeconds(10), "-d", "shell", "appops", "set", pkg, "PROJECT_MEDIA", allowed ? "default" : "allow");
        bool ok = r.Started && r.Exited && r.Code == 0;
        MirrorStatus.Text = !ok ? Mirror.Explain(r.Output)
            : allowed ? "Done: the phone asks Start? again each time."
            : "Done: Mirror over Wi-Fi now starts without a question on the phone. Press again to undo.";
        if (ok) NoAskBtn.Content = allowed ? "Stop asking on the phone" : "Ask on the phone again";
        Diag.Log("mirror: no-ask " + (ok ? (allowed ? "off" : "on") : "failed"));
    }

    void MirrorSound_Click(object sender, RoutedEventArgs e) => AppPrefs.Set("mirrorSound", MirrorSoundBox.IsChecked == true);
    void UsbSound_Click(object sender, RoutedEventArgs e) => AppPrefs.Set("usbSound", UsbSoundBox.IsChecked == true);

    async Task OfferMirror()
    {
        if (_mirrorWin != null) { _mirrorWin.Activate(); return; }
        string? problem =
            !_client.IsConnected ? "Connect your phone first."
            : _client.Kind == "Bluetooth" ? "Mirroring needs the Wi-Fi link (or use Mirror over USB)."
            : !_phoneHasMirror ? "Update Nyx on your phone: this version can't show its screen over Wi-Fi yet."
            : !InputCheck.IsIPv4(PhoneIpBox.Text.Trim()) ? "The phone's Wi-Fi address isn't known yet. Reconnect the phone."
            : null;
        if (problem != null) { MirrorStatus.Text = problem; return; }
        _mirror?.Dispose();
        _mirror = new PhoneMirror();
        var offer = new JsonObject { ["t"] = "mirror_offer", ["key"] = Convert.ToBase64String(_mirror.Key) };
        // smooth video when this PC can decode it (the phone falls back to pictures if it can't encode)
        if (H264Decoder.Available)
        {
            offer["h264"] = true;
            // about twice the window's height in pixels: sharp, and half the work of the phone's full size
            offer["size"] = 1600;
            offer["fps"] = 120; // the phone sends as many as its encoder manages (up to this)
        }
        // with sound: the same sharing session also brings the phone's audio here
        if (MirrorSoundBox.IsChecked == true && _phoneHasAudio && _audio?.Running != true)
        {
            _audio?.Dispose();
            _audio = new PhoneAudio();
            offer["audioKey"] = Convert.ToBase64String(_audio.Key);
        }
        MirrorStatus.Text = "Tap Start on your phone.";
        await _client.SendAsync(offer);
    }

    async Task StartMirror(int port, bool canControl)
    {
        var mirror = _mirror;
        string ip = PhoneIpBox.Text.Trim();
        if (mirror == null || mirror.Running || port is <= 0 or > 65535 || !InputCheck.IsIPv4(ip)) return;
        var win = _mirrorWin = new MirrorWindow(msg => _client.TrySendAsync(msg), canControl);
        win.Closed += async (_, _) =>
        {
            _mirrorWin = null;
            mirror.Stop();
            _audio?.Stop(); // its sound goes with it
            RefreshAudio();
            await _client.TrySendAsync(new JsonObject { ["t"] = "mirror_stop" });
        };
        // newest frame only: if the window falls behind, old frames are skipped, never queued up
        System.Windows.Media.Imaging.BitmapSource? next = null;
        mirror.Frame += f =>
        {
            if (Interlocked.Exchange(ref next, f) != null) return; // one is already on its way
            Dispatcher.BeginInvoke(() => { var latest = Interlocked.Exchange(ref next, null); if (latest != null) win.Show(latest); });
        };
        // video pictures: same idea, the newest one only
        (byte[] px, int w, int h)? nextPx = null;
        object pxGate = new();
        mirror.Picture += (px, w, h) =>
        {
            bool post;
            lock (pxGate) { post = nextPx == null; nextPx = (px, w, h); }
            if (!post) return;
            Dispatcher.BeginInvoke(() =>
            {
                (byte[] px, int w, int h)? pic;
                lock (pxGate) { pic = nextPx; nextPx = null; }
                if (pic is { } p) win.ShowPixels(p.px, p.w, p.h);
            });
        };
        Diag.Log("mirror: " + mirror.Codec);
        // muted here = the phone plays it itself again
        win.MuteChanged += muted =>
        {
            if (_audio != null) _audio.Muted = muted;
            _ = _client.TrySendAsync(new JsonObject { ["t"] = "audio_here", ["laptop"] = !muted });
        };
        if (_audio?.Running == true) win.ShowSound(true); // the sound got here first
        mirror.Ended += () => Dispatcher.BeginInvoke(() =>
        {
            Diag.Log("mirror: ended");
            MirrorStatus.Text = "Mirroring stopped.";
            if (_mirrorWin == win) win.Close();
        });
        win.Show();
        MirrorStatus.Text = canControl ? "Mirroring. Click to tap, drag to swipe, wheel to scroll, right-click for Back."
            : "Mirroring (view only). To control the phone from here: on the phone, Nyx > Allow laptop control.";
        Diag.Log("mirror: started");
        await mirror.RunAsync(ip, port);
    }

    /// <summary>scrcpy over Wi-Fi (Wireless debugging): sharper and full control, but needs developer options.</summary>
    async void ScrcpyWifi_Click(object sender, RoutedEventArgs e)
    {
        string? ip = PhoneIpOrSay();
        if (ip == null) return;
        var scrcpy = ScrcpyOrSay();
        if (scrcpy == null) return;
        var adb = Mirror.FindAdb(scrcpy);
        await Busy((Button)sender, async () =>
        {
            // Wireless debugging (Android 11+): its port changes every time, so ask the network for it
            string? target = null;
            if (adb != null)
            {
                MirrorStatus.Text = "Looking for the phone on Wi-Fi…";
                for (int i = 0; i < 3 && target == null; i++)
                {
                    var services = await Mirror.RunAsync(adb, TimeSpan.FromSeconds(8), "mdns", "services");
                    target = Mirror.FindWirelessPort(services.Output, ip);
                    if (target == null) await Task.Delay(1500);
                }
                if (target != null)
                {
                    var c = await Mirror.RunAsync(adb, TimeSpan.FromSeconds(10), "connect", target);
                    if (!c.Output.Contains("connected to", StringComparison.OrdinalIgnoreCase))
                    {
                        MirrorStatus.Text = c.Output.Contains("failed to authenticate", StringComparison.OrdinalIgnoreCase)
                            ? "This laptop isn't paired with the phone's Wireless debugging yet. Pair once below."
                            : Mirror.Explain(c.Output);
                        return;
                    }
                }
            }
            MirrorStatus.Text = "Opening the mirror over Wi-Fi…";
            var r = target != null
                ? await Mirror.RunAsync(scrcpy, TimeSpan.FromSeconds(10), "-s", target, "--window-title", "Nyx")
                // older phones: the classic port the cable switched on
                : await Mirror.RunAsync(scrcpy, TimeSpan.FromSeconds(10), "--tcpip=" + ip + ":5555", "--window-title", "Nyx");
            MirrorStatus.Text = !r.Started ? "Windows could not start scrcpy: " + r.Output
                : !r.Exited || r.Code == 0 ? "The mirror is open in its own window."
                : Mirror.KnownProblem(r.Output) is string known ? known
                : target == null ? "Couldn't find the phone on Wi-Fi. On the phone, turn on Developer options > Wireless debugging (and pair once below), then try again."
                : Mirror.Explain(r.Output);
        });
    }

    // ------------------------------------------------------------------- demo

    /// <summary>
    /// "dotnet run -- --demo" fills the app with made-up sample data, so the interface
    /// can be looked over before a phone is set up. Nothing is sent anywhere, and the
    /// phone tile says it is sample data.
    /// </summary>
    void LoadDemo()
    {
        long now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        long Min(int minutes) => now - minutes * 60_000L;

        JsonObject Contact(string name, string number) => new() { ["name"] = name, ["number"] = number };
        JsonObject Sms(string address, string body, int minutesAgo, bool inbox) => new()
        {
            ["address"] = address, ["body"] = body, ["time"] = Min(minutesAgo), ["inbox"] = inbox
        };
        JsonObject Notif(string id, string app, string title, string text, int minutesAgo) => new()
        {
            ["t"] = "notif", ["id"] = id, ["app"] = app, ["title"] = title, ["text"] = text, ["time"] = Min(minutesAgo)
        };

        OnMessage(new JsonObject
        {
            ["t"] = "contacts",
            ["items"] = new JsonArray(
                Contact("Meera Iyer", "+91 55501 00101"),
                Contact("Rohan Kulkarni", "+91 55501 00102"),
                Contact("Landlord", "+91 55501 00103"),
                Contact("Mum", "+91 55501 00104"),
                Contact("Pizza Corner", "+91 55501 00105"),
                Contact("Dr. Shah's clinic", "+91 55501 00106"))
        });
        OnMessage(new JsonObject
        {
            ["t"] = "sms_list",
            ["items"] = new JsonArray(
                Sms("+91 55501 00104", "Call when you are free, it is about Sunday", 6, true),
                Sms("+91 55501 00104", "Yes, Saturday evening. Train gets in at 7.", 50, false),
                Sms("+91 55501 00104", "Are you coming home this weekend?", 55, true),
                Sms("+91 55501 00102", "Works. Bring the breadboard.", 64, true),
                Sms("+91 55501 00102", "Lab at 3 instead of 2?", 69, false),
                Sms("+91 55501 00101", "Sent you 450 for lunch", 190, true),
                Sms("+91 55501 00103", "Paid, thanks.", 1500, false),
                Sms("+91 55501 00103", "Reminder: rent is due on the 5th.", 1560, true))
        });
        foreach (var c in _convs.Take(2))
            if (!ReferenceEquals(ConvList.SelectedItem, c)) c.Unread = 1;
        RefreshEmptyStates();

        var feed = new (string App, string Title, string Text, int Ago)[]
        {
            ("Gmail", "Timetable updated", "Thursday's lecture moves to room 204.", 655),
            ("Swiggy", "Order delivered", "Enjoy your meal.", 540),
            ("Calendar", "Standup in 10 minutes", "Library, group room 3.", 470),
            ("WhatsApp", "Lab group", "Rohan: who has the oscilloscope probes?", 300),
            ("Google Pay", "Rs 450 received", "From Meera Iyer.", 190),
            ("Instagram", "meera.iyer liked your photo", "", 150),
            ("Gmail", "Your parcel is out for delivery", "Expected today by 6 pm.", 95),
            ("WhatsApp", "Mum", "Call when you are free", 40),
            ("Calendar", "Project review in 30 minutes", "Seminar hall, second floor.", 12),
            ("WhatsApp", "Lab group", "Priya: I have two, bringing them.", 2),
        };
        for (int i = 0; i < feed.Length; i++)
            OnMessage(Notif("demo-" + i, feed[i].App, feed[i].Title, feed[i].Text, feed[i].Ago));

        JsonObject Wa(string chat, string name, bool group, params (string Sender, string Text, int Ago, bool Me)[] msgs) => new()
        {
            ["t"] = "wa_chat", ["chat"] = chat, ["name"] = name, ["group"] = group, ["canReply"] = true,
            ["messages"] = new JsonArray(msgs.Select(x => (JsonNode)new JsonObject
            {
                ["text"] = x.Text, ["time"] = Min(x.Ago), ["sender"] = x.Sender, ["me"] = x.Me
            }).ToArray())
        };
        OnMessage(Wa("wa-lab", "Lab group", true,
            ("Rohan", "who has the oscilloscope probes?", 18, false),
            ("Priya", "I have two, bringing them", 9, false),
            ("", "Great, see you at 3", 7, true),
            ("Rohan", "Room 204 right?", 2, false)));
        OnMessage(Wa("wa-mum", "Mum", false,
            ("Mum", "Did you eat?", 45, false),
            ("", "Yes, at the canteen", 40, true),
            ("Mum", "Call when you are free", 6, false)));
        SetConn("on", "Sample phone", "Demo mode: this is made-up sample data and nothing is sent.");
        PhoneIpBox.Text = "192.168.1.24";
        ShowStatus(new JsonObject { ["battery"] = 42, ["charging"] = false, ["signal"] = 3, ["wifi"] = true, ["bt"] = true });
        ShowWallpaper("", "#E8573A");
    }
}
