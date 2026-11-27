using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace PhoneBridge;

public partial class App : Application
{
    static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nyx", "crash.log");

    // one Nyx per user: two copies would both link to the phone and knock each other off
    static Mutex? _single;
    static EventWaitHandle? _showSignal;
    static RegisteredWaitHandle? _showWait;
    const string SingleName = @"Local\Nyx.SingleInstance";
    const string ShowName = @"Local\Nyx.ShowWindow";

    protected override void OnStartup(StartupEventArgs e)
    {
        _single = new Mutex(true, SingleName, out bool first);
        if (!first)
        {
            // already running: hand over any files to send, and bring that window forward
            // instead of starting a second link
            var files = FilesIn(e.Args);
            if (files.Count > 0) SendQueue.HandOver(files);
            else try { EventWaitHandle.OpenExisting(ShowName).Set(); } catch { }
            Shutdown(0);
            return;
        }
        StartupFiles = FilesIn(e.Args);
        SendQueue.Listen();
        SendQueue.Arrived += () => Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.SendQueuedFiles());
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowName);
        // keep the registration alive: if it were garbage-collected, the signal would go unheard
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showSignal, (_, _) => Dispatcher.BeginInvoke(BringToFront), null, -1, false);

        // anything that would otherwise kill the app silently is written down first
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log("unhandled", a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { Log("unobserved task", a.Exception); a.SetObserved(); };
        DispatcherUnhandledException += OnUiException;

        ThemeManager.Initialize(); // palette must exist before the first window loads
        base.OnStartup(e);
        new MainWindow().Show();
    }

    /// <summary>Files named on the command line ("Nyx.exe photo.jpg"): sent to the phone.</summary>
    public static List<string> StartupFiles { get; private set; } = new();

    static List<string> FilesIn(string[] args) =>
        args.Where(a => !a.StartsWith("--") && File.Exists(a)).Select(Path.GetFullPath).Take(20).ToList();

    void BringToFront()
    {
        var w = MainWindow;
        if (w == null) return;
        w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        PhoneBridge.MainWindow.ForceToFront(w);
    }

    /// <summary>
    /// An error on the UI thread (a malformed message, a bad picture) is logged and the app
    /// keeps running; a phone link should never be able to close Nyx.
    /// </summary>
    void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log("ui", e.Exception);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // a normal close isn't a crash: only odd exits get written down
        if (e.ApplicationExitCode != 0) Log("exit code " + e.ApplicationExitCode, null);
        base.OnExit(e);
    }

    internal static void Log(string what, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 512 * 1024) File.Delete(LogPath);
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {what}: {ex}\n{Environment.StackTrace}\n\n");
        }
        catch { }
    }
}
