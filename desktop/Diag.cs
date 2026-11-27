using System.IO;

namespace PhoneBridge;

/// <summary>
/// A short local activity log (%LOCALAPPDATA%\Nyx\nyx.log) for "why didn't X work?" questions.
/// Never holds message contents, keys or secrets: only events and reasons. Kept under 256 KB.
/// </summary>
public static class Diag
{
    public static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nyx");

    static readonly string LogPath = Path.Combine(Folder, "nyx.log");
    static readonly object Gate = new();

    public static void Log(string line)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > 256 * 1024)
                {
                    var keep = File.ReadAllLines(LogPath);
                    File.WriteAllLines(LogPath, keep.Skip(keep.Length / 2));
                }
                File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line.Replace('\n', ' ') + Environment.NewLine);
            }
        }
        catch { }
    }

    public static string Read()
    {
        try { lock (Gate) return File.Exists(LogPath) ? File.ReadAllText(LogPath) : ""; }
        catch { return ""; }
    }
}
