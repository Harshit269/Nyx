using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace PhoneBridge;

/// <summary>
/// The phone's sound through the laptop's speakers or headphones. The phone captures what its
/// apps are playing (Android asks first), seals 20 ms of 48 kHz stereo at a time with AES-256-GCM
/// under a fresh key sent inside Nyx's link, and streams it over Wi-Fi.
/// </summary>
public sealed class PhoneAudio : IDisposable
{
    const int MaxPacket = 64 * 1024;
    readonly CancellationTokenSource _stop = new();
    TcpClient? _tcp;

    public byte[] Key { get; } = RandomNumberGenerator.GetBytes(32);
    public bool Running { get; private set; }
    /// <summary>Silent on the laptop for now (the stream keeps going, so unmuting is instant).</summary>
    public bool Muted { get; set; }
    public event Action<string>? Status;
    public event Action? Ended;

    public async Task RunAsync(string phoneIp, int port)
    {
        Running = true;
        PhoneMic.Player? player = null;
        try
        {
            player = new PhoneMic.Player(PhoneMic.DefaultOutput, channels: 2);
            using var tcp = new TcpClient { NoDelay = true };
            _tcp = tcp;
            using (var t = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
                await tcp.ConnectAsync(phoneIp, port, t.Token);
            tcp.ReceiveTimeout = 0; // silence on the phone sends nothing for a while; that's fine
            var net = tcp.GetStream();
            Status?.Invoke("Playing your phone's sound here. Some apps (calls, some streaming apps) don't allow it.");
            var p = player;
            await Task.Factory.StartNew(() => Pump(net, p), _stop.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            Status?.Invoke(_stop.IsCancellationRequested ? "Phone audio stopped." : "The phone stopped sending its sound.");
        }
        catch (Exception ex)
        {
            Status?.Invoke("Phone audio stopped: " + ex.Message);
        }
        finally
        {
            Running = false;
            player?.Dispose();
            Ended?.Invoke();
        }
    }

    void Pump(NetworkStream net, PhoneMic.Player player)
    {
        using var gcm = new AesGcm(Key, 16);
        var lenBytes = new byte[4];
        long last = 0;
        while (!_stop.IsCancellationRequested)
        {
            net.ReadExactly(lenBytes);
            int len = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(lenBytes);
            if (len < 12 + 16 + 4 || len > MaxPacket) throw new IOException("bad packet");
            var buf = new byte[len];
            net.ReadExactly(buf);
            var pcm = PhoneCamera.Open(gcm, buf, ref last);
            if (pcm.Length >= 4 && !Muted) player.Play(pcm);
        }
    }

    public void Stop()
    {
        if (!_stop.IsCancellationRequested) _stop.Cancel();
        try { _tcp?.Close(); } catch { }
        Running = false;
    }

    public void Dispose() => Stop();
}
