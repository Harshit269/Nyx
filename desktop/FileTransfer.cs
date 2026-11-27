using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace PhoneBridge;

/// <summary>
/// Files between laptop and phone over the encrypted link:
///   file_start {id, name, size} -> file_part {id, seq, data} ... -> file_end {id, size, sha256}
/// answered by file_done {id, ok, msg}. A received file is kept only when its size and SHA-256
/// match; it goes to Downloads\Nyx and is never opened by itself.
/// </summary>
public static class FileTransfer
{
    public const int Chunk = 96 * 1024;
    public const long MaxSize = 4L * 1024 * 1024 * 1024; // 4 GB

    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>A name that's safe to create on Windows: no folders, no reserved names or characters.</summary>
    public static string SafeName(string raw)
    {
        string n = raw ?? "";
        int cut = Math.Max(n.LastIndexOf('/'), n.LastIndexOf('\\'));
        if (cut >= 0) n = n[(cut + 1)..];
        var bad = Path.GetInvalidFileNameChars();
        n = new string(n.Where(c => c >= ' ' && !bad.Contains(c)).ToArray()).Trim().TrimEnd('.', ' ');
        if (n.Length == 0 || n == "." || n == "..") n = "file";
        string stem = Path.GetFileNameWithoutExtension(n);
        if (Reserved.Contains(stem)) n = "_" + n;
        if (n.Length > 120)
        {
            string ext = Path.GetExtension(n);
            if (ext.Length > 12) ext = "";
            n = n[..(120 - ext.Length)] + ext;
        }
        return n;
    }

    /// <summary>Downloads\Nyx, created when needed.</summary>
    /// <summary>Tests point this at a temporary folder.</summary>
    internal static string? FolderForTests;

    public static string Folder
    {
        get
        {
            string dir = FolderForTests ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Nyx");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>"photo.jpg", or "photo (2).jpg" if that's taken.</summary>
    public static string UniquePath(string dir, string name)
    {
        string path = Path.Combine(dir, name);
        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        for (int i = 2; File.Exists(path) || File.Exists(path + ".part"); i++) path = Path.Combine(dir, $"{stem} ({i}){ext}");
        return path;
    }

    // ------------------------------------------------------------------ receiving

    sealed class Incoming
    {
        public string Name = "", Final = "", Temp = "";
        public long Size, Got;
        public int Next;
        public FileStream? Out;
        public IncrementalHash Sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    }

    static readonly Dictionary<string, Incoming> _incoming = new();

    /// <summary>A file the phone has finished sending (or failed to).</summary>
    public record Received(string Name, string? Path, bool Ok, string Message);

    /// <summary>Handles file_start/part/end/cancel. Returns the reply to send (if any) and a finished file (if any).</summary>
    public static (JsonObject? Reply, Received? Done) OnMessage(JsonObject m)
    {
        string id = (m["id"]?.ToString() ?? "");
        if (id.Length == 0 || id.Length > 40) return (null, null);
        string t = m["t"]?.ToString() ?? "";
        switch (t)
        {
            case "file_start":
            {
                Abort(id);
                long size = Num(m, "size");
                string name = SafeName(m["name"]?.ToString() ?? "");
                if (size > MaxSize) return (Done(id, false, "The laptop takes files up to 4 GB."), null);
                if (_incoming.Count >= 4) return (Done(id, false, "Too many files at once."), null);
                try
                {
                    var dir = Folder;
                    if (size > 0 && new DriveInfo(Path.GetPathRoot(dir)!).AvailableFreeSpace < size + 50_000_000)
                        return (Done(id, false, "The laptop is out of disk space."), null);
                    var f = new Incoming { Name = name, Size = size, Final = UniquePath(dir, name) };
                    f.Temp = f.Final + ".part";
                    f.Out = new FileStream(f.Temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024);
                    _incoming[id] = f;
                    return (null, null);
                }
                catch (Exception ex) { return (Done(id, false, "The laptop couldn't save it: " + ex.Message), null); }
            }
            case "file_part":
            {
                if (!_incoming.TryGetValue(id, out var f)) return (null, null);
                if ((int)Num(m, "seq") != f.Next) { Abort(id); return (Done(id, false, "Parts arrived out of order."), new Received(f.Name, null, false, "It didn't arrive in one piece.")); }
                byte[] data;
                try { data = Convert.FromBase64String(m["data"]?.ToString() ?? ""); }
                catch { Abort(id); return (Done(id, false, "A part was damaged."), new Received(f.Name, null, false, "A part was damaged.")); }
                f.Got += data.Length;
                if (f.Got > MaxSize || (f.Size >= 0 && f.Got > f.Size))
                {
                    Abort(id);
                    return (Done(id, false, "The file was bigger than announced."), new Received(f.Name, null, false, "It was bigger than announced."));
                }
                f.Sha.AppendData(data);
                f.Out!.Write(data);
                f.Next++;
                return (null, null);
            }
            case "file_end":
            {
                if (!_incoming.Remove(id, out var f)) return (null, null);
                f.Out?.Dispose();
                bool sizeOk = f.Got == Num(m, "size") && (f.Size < 0 || f.Got == f.Size);
                bool shaOk = Convert.ToHexString(f.Sha.GetHashAndReset()).Equals(m["sha256"]?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
                if (!sizeOk || !shaOk)
                {
                    TryDelete(f.Temp);
                    return (Done(id, false, "The file didn't arrive intact, so the laptop threw it away. Try again."),
                            new Received(f.Name, null, false, "It didn't arrive intact, so it was thrown away."));
                }
                try
                {
                    string final = File.Exists(f.Final) ? UniquePath(Path.GetDirectoryName(f.Final)!, f.Name) : f.Final;
                    File.Move(f.Temp, final);
                    return (Done(id, true, "Saved on the laptop in Downloads\\Nyx."), new Received(Path.GetFileName(final), final, true, "Saved in Downloads\\Nyx."));
                }
                catch (Exception ex)
                {
                    TryDelete(f.Temp);
                    return (Done(id, false, "The laptop couldn't save it."), new Received(f.Name, null, false, ex.Message));
                }
            }
            case "file_cancel":
                Abort(id);
                return (null, null);
        }
        return (null, null);
    }

    /// <summary>Throws away every half-received file (the link ended).</summary>
    public static void AbortAll() { foreach (var id in _incoming.Keys.ToList()) Abort(id); }

    static void Abort(string id)
    {
        if (!_incoming.Remove(id, out var f)) return;
        f.Out?.Dispose();
        TryDelete(f.Temp);
    }

    static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    static long Num(JsonObject m, string key)
    {
        try { return m[key]?.GetValue<long>() ?? -1; } catch { return long.TryParse(m[key]?.ToString(), out var v) ? v : -1; }
    }

    static JsonObject Done(string id, bool ok, string msg) => new() { ["t"] = "file_done", ["id"] = id, ["ok"] = ok, ["msg"] = msg };

    // ------------------------------------------------------------------ sending

    /// <summary>Streams one file to the phone. <paramref name="progress"/> gets 0..1.</summary>
    public static async Task<string?> SendAsync(string path, Func<JsonObject, Task<bool>> send, Action<double>? progress = null, CancellationToken ct = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return "That file doesn't exist any more.";
        if ((info.Attributes & FileAttributes.Directory) != 0) return "Folders can't be sent, only files.";
        if (info.Length > MaxSize) return "That file is bigger than 4 GB.";
        string id = Guid.NewGuid().ToString("N")[..12];
        try
        {
            if (!await send(new JsonObject { ["t"] = "file_start", ["id"] = id, ["name"] = SafeName(info.Name), ["size"] = info.Length }))
                return "The phone isn't connected.";
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, Chunk, useAsync: true);
            var buf = new byte[Chunk];
            long sent = 0; int seq = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int n = await ReadFullyAsync(input, buf, ct);
                if (n <= 0) break;
                sha.AppendData(buf, 0, n);
                if (!await send(new JsonObject { ["t"] = "file_part", ["id"] = id, ["seq"] = seq++, ["data"] = Convert.ToBase64String(buf, 0, n) }))
                    return "The link dropped while sending.";
                sent += n;
                progress?.Invoke(info.Length > 0 ? (double)sent / info.Length : 1);
            }
            await send(new JsonObject { ["t"] = "file_end", ["id"] = id, ["size"] = sent, ["sha256"] = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant() });
            return null;
        }
        catch (OperationCanceledException)
        {
            await send(new JsonObject { ["t"] = "file_cancel", ["id"] = id });
            return "Cancelled.";
        }
        catch (Exception ex)
        {
            await send(new JsonObject { ["t"] = "file_cancel", ["id"] = id });
            return "Couldn't read that file: " + ex.Message;
        }
    }

    static async Task<int> ReadFullyAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        int total = 0;
        while (total < buf.Length)
        {
            int r = await s.ReadAsync(buf.AsMemory(total), ct);
            if (r == 0) break;
            total += r;
        }
        return total;
    }
}
