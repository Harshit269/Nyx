using System.Drawing;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace PhoneBridge;

/// <summary>
/// The phone as a webcam. The phone films, compresses each frame to JPEG, seals it with
/// AES-256-GCM (a fresh key per session, sent inside Nyx's encrypted link) and streams it over
/// Wi-Fi; Nyx turns it the right way up and shows it in OBS Virtual Camera.
///
/// Per frame: 4-byte big-endian length, 12-byte nonce, then ciphertext + 16-byte tag of
/// [rotation in quarter turns: 1 byte][JPEG].
/// </summary>
public sealed class PhoneCamera : IDisposable
{
    const int MaxFrame = 4 * 1024 * 1024;

    readonly CancellationTokenSource _stop = new();
    TcpClient? _tcp;

    public byte[] Key { get; } = RandomNumberGenerator.GetBytes(32);
    public bool Running { get; private set; }
    public event Action<string>? Status;
    public event Action? Ended;

    public async Task RunAsync(string phoneIp, int port)
    {
        Running = true;
        VirtualCam? cam = null;
        try
        {
            cam = new VirtualCam();
            using var tcp = new TcpClient { NoDelay = true, ReceiveBufferSize = 1 << 20 };
            _tcp = tcp;
            using (var t = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
                await tcp.ConnectAsync(phoneIp, port, t.Token);
            tcp.ReceiveTimeout = 10_000;
            var net = tcp.GetStream();
            Status?.Invoke("Live: in Meet, Zoom or Teams pick \"OBS Virtual Camera\".");
            var vc = cam;
            await Task.Factory.StartNew(() => Pump(net, vc), _stop.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            Status?.Invoke(_stop.IsCancellationRequested ? "Camera stopped." : "The phone stopped the camera.");
        }
        catch (Exception ex)
        {
            Status?.Invoke("Camera stopped: " + ex.Message);
        }
        finally
        {
            Running = false;
            cam?.Dispose(); // the call sees a black frame, not a frozen one
            Ended?.Invoke();
        }
    }

    void Pump(NetworkStream net, VirtualCam cam)
    {
        using var gcm = new AesGcm(Key, 16);
        var lenBytes = new byte[4];
        long last = 0;
        while (!_stop.IsCancellationRequested)
        {
            net.ReadExactly(lenBytes);
            int len = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(lenBytes);
            if (len < 12 + 16 + 2 || len > MaxFrame) throw new IOException("bad frame");
            var buf = new byte[len];
            net.ReadExactly(buf);
            byte[] plain = Open(gcm, buf, ref last);
            if (plain.Length < 2) continue; // replayed or reordered: skip
            using var ms = new MemoryStream(plain, 1, plain.Length - 1);
            using var bmp = new Bitmap(ms);
            Turn(bmp, plain[0]);
            cam.Show(bmp);
        }
    }

    /// <summary>Opens one sealed frame; an old counter (replay) gives an empty result, a bad tag throws.</summary>
    public static byte[] Open(AesGcm gcm, byte[] buf, ref long lastCounter)
    {
        long counter = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(buf.AsSpan(4, 8));
        if (counter <= lastCounter) return Array.Empty<byte>();
        var plain = new byte[buf.Length - 28];
        gcm.Decrypt(buf.AsSpan(0, 12), buf.AsSpan(12, plain.Length), buf.AsSpan(buf.Length - 16, 16), plain);
        lastCounter = counter;
        return plain;
    }

    /// <summary>Turns the picture upright: the phone says how many quarter turns clockwise.</summary>
    public static void Turn(Bitmap bmp, byte quarters)
    {
        switch (quarters & 3)
        {
            case 1: bmp.RotateFlip(RotateFlipType.Rotate90FlipNone); break;
            case 2: bmp.RotateFlip(RotateFlipType.Rotate180FlipNone); break;
            case 3: bmp.RotateFlip(RotateFlipType.Rotate270FlipNone); break;
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
