using System.Diagnostics;
using System.IO;

namespace PhoneBridge;

/// <summary>
/// Finds scrcpy (and the adb that ships with it) and runs them, reporting what really
/// happened instead of assuming a started process means success.
/// </summary>
public static class Mirror
{
    /// <summary>The scrcpy.exe to use: the typed path if it exists, else PATH, else where winget puts it.</summary>
    public static string? FindScrcpy(string typed)
    {
        typed = typed.Trim().Trim('"');
        if (typed.Length > 0 && Path.IsPathRooted(typed) && File.Exists(typed)) return typed;

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try
            {
                if (dir.Length == 0) continue;
                var p = Path.Combine(dir.Trim('"'), "scrcpy.exe");
                if (File.Exists(p)) return p;
            }
            catch { }
        }

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var link = Path.Combine(local, "Microsoft", "WinGet", "Links", "scrcpy.exe");
        if (File.Exists(link)) return link;
        try
        {
            var pkgs = Path.Combine(local, "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(pkgs))
                foreach (var d in Directory.EnumerateDirectories(pkgs, "Genymobile.scrcpy*"))
                {
                    var hit = Directory.EnumerateFiles(d, "scrcpy.exe", SearchOption.AllDirectories).FirstOrDefault();
                    if (hit != null) return hit;
                }
        }
        catch { }
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"C:\scrcpy" })
        {
            var p = Path.Combine(root, "scrcpy", "scrcpy.exe");
            if (File.Exists(p)) return p;
            p = Path.Combine(root, "scrcpy.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>adb.exe next to scrcpy (the scrcpy download includes it), else the real one behind a winget link.</summary>
    public static string? FindAdb(string scrcpy)
    {
        var dir = Path.GetDirectoryName(scrcpy);
        if (!string.IsNullOrEmpty(dir))
        {
            var p = Path.Combine(dir, "adb.exe");
            if (File.Exists(p)) return p;
        }
        try
        {
            // a winget link is a symlink: look next to the file it points at
            var target = new FileInfo(scrcpy).ResolveLinkTarget(true)?.FullName;
            var tdir = target == null ? null : Path.GetDirectoryName(target);
            if (tdir != null && File.Exists(Path.Combine(tdir, "adb.exe"))) return Path.Combine(tdir, "adb.exe");
        }
        catch { }
        return null;
    }

    public record Result(bool Started, bool Exited, int Code, string Output);

    /// <summary>The first phone attached by USB in `adb devices` output (Wi-Fi ones have ip:port), or null.</summary>
    public static string? UsbSerial(string devicesOutput)
    {
        foreach (var raw in devicesOutput.Split('\n'))
        {
            var parts = raw.Trim().Split('\t', ' ');
            if (parts.Length >= 2 && parts[^1] == "device" && !parts[0].Contains(':') && parts[0].Length > 0
                && parts[0].All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.'))
                return parts[0];
        }
        return null;
    }

    /// <summary>Whether `adb connect` reached the phone.</summary>
    public static bool Connected(string connectOutput) =>
        connectOutput.Contains("connected to", StringComparison.OrdinalIgnoreCase)
        && !connectOutput.Contains("cannot", StringComparison.OrdinalIgnoreCase)
        && !connectOutput.Contains("failed", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Starts a tool with each argument passed separately (never one command string) and waits
    /// up to <paramref name="wait"/>. A mirror window that is still open after the wait is a success.
    /// </summary>
    public static async Task<Result> RunAsync(string file, TimeSpan wait, params string[] args)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process p;
        try { p = Process.Start(psi)!; }
        catch (Exception ex) { return new Result(false, true, -1, ex.Message); }

        var output = new System.Text.StringBuilder();
        void Add(string? line) { if (line != null) lock (output) { if (output.Length < 8000) output.AppendLine(line); } }
        p.OutputDataReceived += (_, e) => Add(e.Data);
        p.ErrorDataReceived += (_, e) => Add(e.Data);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using var cts = new CancellationTokenSource(wait);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { }
        bool exited = p.HasExited;
        if (exited) p.WaitForExit(); // flush the output readers
        string text;
        lock (output) text = output.ToString();
        return new Result(true, exited, exited ? p.ExitCode : 0, text);
    }

    /// <summary>
    /// The "ip:port" Wireless debugging is listening on, from "adb mdns services" output.
    /// Android picks a new port every time Wireless debugging is switched on, so it is looked
    /// up each time instead of remembered. Only an entry for <paramref name="ip"/> counts.
    /// </summary>
    public static string? FindWirelessPort(string mdnsOutput, string ip)
    {
        foreach (var raw in mdnsOutput.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.Contains("_adb-tls-connect._tcp")) continue;
            var m = System.Text.RegularExpressions.Regex.Match(line, @"(\d{1,3}(?:\.\d{1,3}){3}):(\d{1,5})\s*$");
            if (!m.Success || m.Groups[1].Value != ip) continue;
            if (int.TryParse(m.Groups[2].Value, out int port) && port is > 0 and < 65536) return ip + ":" + port;
        }
        return null;
    }

    /// <summary>A pairing port and code as shown on the phone: digits only, so nothing extra can reach adb.</summary>
    public static bool ValidPairing(string port, string code) =>
        int.TryParse(port, out int p) && p is > 0 and < 65536 && port.All(char.IsAsciiDigit) &&
        code.Length == 6 && code.All(char.IsAsciiDigit);

    /// <summary>The line of scrcpy/adb output that best explains a failure, in plain words where we know it.</summary>
    public static string Explain(string output) => KnownProblem(output) ?? LastLine(output);

    /// <summary>A plain-words explanation when the output shows a problem we recognise; null otherwise.</summary>
    public static string? KnownProblem(string output)
    {
        string o = output.ToLowerInvariant();
        if (o.Contains("unauthorized") && o.Contains("connected to"))
            return "Connected over Wi-Fi, but the phone hasn't allowed this laptop yet. Unlock the phone, tap Allow on the debugging question, then press Mirror over Wi-Fi again.";
        if (o.Contains("unauthorized")) return "The phone hasn't allowed this laptop yet. Unlock the phone and tap Allow on the USB debugging question.";
        if (o.Contains("no devices") || o.Contains("could not find any adb device") || o.Contains("device not found"))
            return "No phone found over USB. Plug it in, turn on USB debugging in Developer options, and allow this laptop on the phone.";
        if (o.Contains("more than one")) return "More than one Android device is connected. Unplug the others and try again.";
        if (o.Contains("failed: unable to start pairing") || o.Contains("wrong password") || o.Contains("protocol fault"))
            return "Pairing failed. The code and port change each time: on the phone, tap Pair device with pairing code again and type the new ones.";
        if (o.Contains("connection refused") || o.Contains("failed to connect") || o.Contains("cannot connect"))
            return "The phone refused the Wi-Fi connection. Check Wireless debugging is still on (it switches off when the phone changes Wi-Fi network).";
        if (o.Contains("timed out") || o.Contains("timeout")) return "The phone didn't answer. Check that both are on the same Wi-Fi.";
        return null;
    }

    static string LastLine(string output)
    {
        var last = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0 && (l.StartsWith("ERROR") || l.Contains("error", StringComparison.OrdinalIgnoreCase)))
                   ?? output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
        return last.Length > 220 ? last[..220] + "…" : last;
    }
}
