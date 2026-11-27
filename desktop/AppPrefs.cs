using System.IO;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace PhoneBridge;

/// <summary>Small on/off preferences, kept in %APPDATA%\Nyx\prefs.json.</summary>
public static class AppPrefs
{
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nyx", "prefs.json");

    static JsonObject Load()
    {
        try { return JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject ?? new JsonObject(); }
        catch { return new JsonObject(); }
    }

    public static bool Get(string key, bool fallback)
    {
        try { return Load()[key]?.GetValue<bool>() ?? fallback; } catch { return fallback; }
    }

    public static string GetText(string key, string fallback)
    {
        try { return Load()[key]?.GetValue<string>() ?? fallback; } catch { return fallback; }
    }

    public static void SetText(string key, string value)
    {
        try
        {
            var o = Load();
            o[key] = value;
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, o.ToJsonString());
        }
        catch { }
    }

    public static void Set(string key, bool value)
    {
        try
        {
            var o = Load();
            o[key] = value;
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, o.ToJsonString());
        }
        catch { }
    }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string TaskId = "NyxStartup";

    static bool Packaged => HandsFree.IsInstalledApp;

    /// <summary>
    /// Whether Nyx starts (minimised) when you sign in to Windows. The installed app uses
    /// Windows' startup task, so it starts as the installed app (call audio needs that);
    /// a copy run from its build folder falls back to the Run key.
    /// </summary>
    public static bool StartWithWindows
    {
        get
        {
            try
            {
                if (Packaged)
                {
                    var state = Windows.ApplicationModel.StartupTask.GetAsync(TaskId).AsTask().GetAwaiter().GetResult().State;
                    return state is Windows.ApplicationModel.StartupTaskState.Enabled or Windows.ApplicationModel.StartupTaskState.EnabledByPolicy;
                }
                using var k = Registry.CurrentUser.OpenSubKey(RunKey);
                return k?.GetValue("Nyx") != null;
            }
            catch { return false; }
        }
        set
        {
            try
            {
                if (Packaged)
                {
                    var task = Windows.ApplicationModel.StartupTask.GetAsync(TaskId).AsTask().GetAwaiter().GetResult();
                    if (value) task.RequestEnableAsync().AsTask().GetAwaiter().GetResult();
                    else task.Disable();
                    // an old Run entry from before would start a second, unpackaged copy
                    using var old = Registry.CurrentUser.CreateSubKey(RunKey);
                    old.DeleteValue("Nyx", false);
                    return;
                }
                using var k = Registry.CurrentUser.CreateSubKey(RunKey);
                if (value) k.SetValue("Nyx", "\"" + Environment.ProcessPath + "\" --minimized");
                else k.DeleteValue("Nyx", false);
            }
            catch { }
        }
    }

    /// <summary>True when Windows started Nyx at sign-in: open to the tray, not on screen.</summary>
    public static bool StartedAtSignIn
    {
        get
        {
            try
            {
                return Packaged && Windows.ApplicationModel.AppInstance.GetActivatedEventArgs()?.Kind
                    == Windows.ApplicationModel.Activation.ActivationKind.StartupTask;
            }
            catch { return false; }
        }
    }
}
