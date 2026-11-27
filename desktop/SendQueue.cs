using System.IO;
using System.IO.Pipes;
using System.Text;

namespace PhoneBridge;

/// <summary>
/// Files waiting to go to the phone: named at start-up ("Nyx.exe photo.jpg"), or handed over
/// by a second launch while Nyx is already running. The hand-over uses a named pipe that
/// only your own Windows account can open, not a file: an installed (packaged) Nyx can't see
/// files another process writes into AppData.
/// </summary>
public static class SendQueue
{
    const string PipeName = "Nyx.SendFiles";
    static readonly object Gate = new();
    static readonly List<string> _waiting = new();

    /// <summary>Raised (on a background thread) when files arrive from a second launch.</summary>
    public static event Action? Arrived;

    public static void Add(IEnumerable<string> paths)
    {
        lock (Gate)
            foreach (var p in paths)
                if (p.Length > 0 && !_waiting.Contains(p, StringComparer.OrdinalIgnoreCase) && _waiting.Count < 50) _waiting.Add(p);
    }

    /// <summary>Everything waiting that still exists, and empties the queue.</summary>
    public static List<string> Take()
    {
        lock (Gate)
        {
            var all = _waiting.Where(File.Exists).Take(20).ToList();
            _waiting.Clear();
            return all;
        }
    }

    /// <summary>The running Nyx listens for file lists from later launches.</summary>
    public static void Listen()
    {
        var t = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                        PipeOptions.CurrentUserOnly);
                    pipe.WaitForConnection();
                    using var reader = new StreamReader(pipe, Encoding.UTF8);
                    var files = new List<string>();
                    string? line;
                    while ((line = reader.ReadLine()) != null && files.Count < 20)
                        if (line.Length is > 0 and < 1024) files.Add(line);
                    if (files.Count > 0) { Add(files); Arrived?.Invoke(); }
                }
                catch { Thread.Sleep(500); }
            }
        }) { IsBackground = true, Name = "Nyx send-files pipe" };
        t.Start();
    }

    /// <summary>A second launch passes its files to the running Nyx. False if none was listening.</summary>
    public static bool HandOver(IReadOnlyList<string> files)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(2000);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false));
            foreach (var f in files) if (f.IndexOfAny(new[] { '\r', '\n' }) < 0) writer.WriteLine(f);
            return true;
        }
        catch { return false; }
    }
}
