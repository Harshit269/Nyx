using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace PhoneBridge;

/// <summary>
/// The phone as a microphone. The phone records (with Android's echo and noise cancelling),
/// seals 20 ms of sound at a time with AES-256-GCM under a fresh key sent inside Nyx's link,
/// and streams it over Wi-Fi. Nyx plays it into a virtual audio cable: a playback device whose
/// sound comes out of a matching recording device, which calls can pick as their microphone.
/// Steam's "Steam Streaming Microphone" is one; VB-Audio's free "CABLE" is another.
///
/// Audio: 48 kHz, mono, 16-bit. Per packet: 4-byte length, 12-byte nonce, ciphertext + tag.
/// </summary>
public sealed class PhoneMic : IDisposable
{
    public const int Rate = 48000;
    const int MaxPacket = 64 * 1024;

    /// <summary>A virtual cable: where Nyx plays, and what to pick as the microphone in a call.</summary>
    public sealed record Cable(int Device, string PlayInto, string PickInCall);

    readonly CancellationTokenSource _stop = new();
    TcpClient? _tcp;

    public byte[] Key { get; } = RandomNumberGenerator.GetBytes(32);
    public bool Running { get; private set; }
    public event Action<string>? Status;
    public event Action? Ended;

    /// <summary>The first virtual cable Windows has, by its playback side's name.</summary>
    public static Cable? FindCable()
    {
        int n = waveOutGetNumDevs();
        for (int i = 0; i < n; i++)
        {
            var caps = new WAVEOUTCAPS();
            if (waveOutGetDevCaps((IntPtr)i, ref caps, Marshal.SizeOf<WAVEOUTCAPS>()) != 0) continue;
            string name = caps.szPname ?? "";
            var c = Match(i, name);
            if (c != null) return c;
        }
        return null;
    }

    /// <summary>Which playback names are a virtual cable (Windows cuts device names at 31 characters).</summary>
    public static Cable? Match(int device, string name)
    {
        // (Steam's "Streaming Microphone" looks like one, but only passes sound while Steam streams)
        if (name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase))
            return new Cable(device, name, "CABLE Output (VB-Audio Virtual Cable)");
        if (name.Contains("Voicemeeter Input", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("VoiceMeeter Input", StringComparison.OrdinalIgnoreCase))
            return new Cable(device, name, "Voicemeeter Out B1 (VB-Audio Voicemeeter VAIO)");
        return null;
    }

    public async Task RunAsync(string phoneIp, int port, Cable cable)
    {
        Running = true;
        Player? player = null;
        try
        {
            player = new Player(cable.Device);
            using var tcp = new TcpClient { NoDelay = true };
            _tcp = tcp;
            using (var t = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
                await tcp.ConnectAsync(phoneIp, port, t.Token);
            tcp.ReceiveTimeout = 10_000;
            var net = tcp.GetStream();
            Status?.Invoke("Live: in Meet, Zoom or Teams pick \"" + cable.PickInCall + "\" as the microphone.");
            var p = player;
            await Task.Factory.StartNew(() => Pump(net, p), _stop.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            Status?.Invoke(_stop.IsCancellationRequested ? "Microphone stopped." : "The phone stopped the microphone.");
        }
        catch (Exception ex)
        {
            Status?.Invoke("Microphone stopped: " + ex.Message);
        }
        finally
        {
            Running = false;
            player?.Dispose();
            Ended?.Invoke();
        }
    }

    void Pump(NetworkStream net, Player player)
    {
        using var gcm = new AesGcm(Key, 16);
        var lenBytes = new byte[4];
        long last = 0;
        while (!_stop.IsCancellationRequested)
        {
            net.ReadExactly(lenBytes);
            int len = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(lenBytes);
            if (len < 12 + 16 + 2 || len > MaxPacket) throw new IOException("bad packet");
            var buf = new byte[len];
            net.ReadExactly(buf);
            var pcm = PhoneCamera.Open(gcm, buf, ref last);
            if (pcm.Length >= 2) player.Play(pcm);
        }
    }

    /// <summary>For testing the cable: a 1 kHz tone, played the way phone audio is.</summary>
    internal static void ProbeTone(int device, TimeSpan length)
    {
        using var p = new Player(device);
        var until = DateTime.UtcNow + length;
        int t = 0;
        while (DateTime.UtcNow < until)
        {
            var pcm = new byte[960 * 2];
            for (int i = 0; i < 960; i++, t++)
            {
                short s = (short)(Math.Sin(2 * Math.PI * 1000 * t / Rate) * 12000);
                pcm[2 * i] = (byte)s; pcm[2 * i + 1] = (byte)(s >> 8);
            }
            p.Play(pcm);
            Thread.Sleep(20);
        }
    }

    public void Stop()
    {
        if (!_stop.IsCancellationRequested) _stop.Cancel();
        try { _tcp?.Close(); } catch { }
        Running = false;
    }

    public void Dispose() => Stop();

    // ------------------------------------------------------------ playback (winmm)

    /// <summary>The laptop's normal speakers or headphones (whatever Windows plays sound through).</summary>
    internal const int DefaultOutput = -1; // WAVE_MAPPER

    /// <summary>Plays PCM into one output device, keeping only a short queue so the sound stays live.</summary>
    internal sealed class Player : IDisposable
    {
        const int Slots = 12;
        readonly IntPtr _out;
        readonly IntPtr[] _hdr = new IntPtr[Slots];
        readonly IntPtr[] _data = new IntPtr[Slots];
        readonly int[] _size = new int[Slots];
        int _next;

        public Player(int device, int channels = 1)
        {
            var fmt = new WAVEFORMATEX
            {
                wFormatTag = 1, nChannels = (ushort)channels, nSamplesPerSec = Rate, wBitsPerSample = 16,
                nBlockAlign = (ushort)(2 * channels), nAvgBytesPerSec = Rate * 2 * channels
            };
            int r = waveOutOpen(out _out, (IntPtr)device, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
            if (r != 0) throw new IOException("Windows couldn't open the sound device (" + r + ").");
        }

        public void Play(byte[] pcm)
        {
            int slot = _next;
            // a slot still playing means we're over 240 ms behind: drop this packet to catch up
            if (_hdr[slot] != IntPtr.Zero && (Marshal.ReadInt32(_hdr[slot], HdrFlagsOffset) & WHDR_DONE) == 0) return;
            Release(slot);
            _data[slot] = Marshal.AllocHGlobal(pcm.Length);
            Marshal.Copy(pcm, 0, _data[slot], pcm.Length);
            _size[slot] = pcm.Length;
            var h = new WAVEHDR { lpData = _data[slot], dwBufferLength = (uint)pcm.Length };
            _hdr[slot] = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEHDR>());
            Marshal.StructureToPtr(h, _hdr[slot], false);
            waveOutPrepareHeader(_out, _hdr[slot], Marshal.SizeOf<WAVEHDR>());
            waveOutWrite(_out, _hdr[slot], Marshal.SizeOf<WAVEHDR>());
            _next = (slot + 1) % Slots;
        }

        void Release(int slot)
        {
            if (_hdr[slot] != IntPtr.Zero)
            {
                waveOutUnprepareHeader(_out, _hdr[slot], Marshal.SizeOf<WAVEHDR>());
                Marshal.FreeHGlobal(_hdr[slot]);
                _hdr[slot] = IntPtr.Zero;
            }
            if (_data[slot] != IntPtr.Zero) { Marshal.FreeHGlobal(_data[slot]); _data[slot] = IntPtr.Zero; }
        }

        public void Dispose()
        {
            waveOutReset(_out);
            for (int i = 0; i < Slots; i++) Release(i);
            waveOutClose(_out);
        }
    }

    const int WHDR_DONE = 1;
    static readonly int HdrFlagsOffset = (int)Marshal.OffsetOf<WAVEHDR>(nameof(WAVEHDR.dwFlags));

    [StructLayout(LayoutKind.Sequential)]
    struct WAVEFORMATEX { public ushort wFormatTag, nChannels; public int nSamplesPerSec, nAvgBytesPerSec; public ushort nBlockAlign, wBitsPerSample, cbSize; }
    [StructLayout(LayoutKind.Sequential)]
    struct WAVEHDR { public IntPtr lpData; public uint dwBufferLength, dwBytesRecorded; public IntPtr dwUser; public uint dwFlags, dwLoops; public IntPtr lpNext, reserved; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WAVEOUTCAPS { public ushort wMid, wPid; public uint vDriverVersion; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname; public uint dwFormats; public ushort wChannels, wReserved1; public uint dwSupport; }

    [DllImport("winmm.dll")] static extern int waveOutGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern int waveOutGetDevCaps(IntPtr id, ref WAVEOUTCAPS caps, int size);
    [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr h, IntPtr device, ref WAVEFORMATEX fmt, IntPtr cb, IntPtr inst, int flags);
    [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr h);
    [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr h);
}
