using System.Drawing;
using System.Security.Cryptography;
using PhoneBridge;
using Xunit;

namespace Nyx.Tests;

public class SecondScreenTests
{
    [Fact]
    public void Frames_decrypt_with_the_session_key_and_nonces_never_repeat()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var jpeg = RandomNumberGenerator.GetBytes(5000);
        var a = SecondScreen.Seal(key, 1, jpeg);
        var b = SecondScreen.Seal(key, 2, jpeg);
        Assert.Equal(12 + jpeg.Length + 16, a.Length);
        Assert.False(a.AsSpan(0, 12).SequenceEqual(b.AsSpan(0, 12)));

        // the same split the phone does: nonce | ciphertext | tag
        var plain = new byte[jpeg.Length];
        using var gcm = new AesGcm(key, 16);
        gcm.Decrypt(a.AsSpan(0, 12), a.AsSpan(12, jpeg.Length), a.AsSpan(12 + jpeg.Length, 16), plain);
        Assert.Equal(jpeg, plain);

        // a flipped bit is refused, not shown
        a[20] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() =>
            gcm.Decrypt(a.AsSpan(0, 12), a.AsSpan(12, jpeg.Length), a.AsSpan(12 + jpeg.Length, 16), plain));
    }

    [Fact]
    public async Task A_real_capture_streams_as_sealed_jpeg_frames()
    {
        // stands in for the phone: listen, read one frame, decrypt it like the phone does
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        using var screen = new SecondScreen();
        var area = new SecondScreen.Display("test", new Rectangle(0, 0, 320, 200)); // a corner of the main screen
        var run = screen.RunAsync("127.0.0.1", port, area);

        using var phone = await listener.AcceptTcpClientAsync();
        var input = phone.GetStream();
        var lenBytes = new byte[4];
        await input.ReadExactlyAsync(lenBytes);
        int len = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(lenBytes);
        Assert.InRange(len, 12 + 16 + 100, 8 * 1024 * 1024);
        var frame = new byte[len];
        await input.ReadExactlyAsync(frame);

        var jpeg = new byte[len - 28];
        using (var gcm = new AesGcm(screen.Key, 16))
            gcm.Decrypt(frame.AsSpan(0, 12), frame.AsSpan(12, len - 28), frame.AsSpan(len - 16, 16), jpeg);
        Assert.Equal(0xFF, jpeg[0]); // JPEG start-of-image
        Assert.Equal(0xD8, jpeg[1]);
        using (var img = Image.FromStream(new MemoryStream(jpeg)))
            Assert.Equal(new Size(320, 200), img.Size);

        screen.Stop();
        await run;
        Assert.False(screen.Running);
        listener.Stop();
    }

    [Fact]
    public void An_odd_virtual_display_gets_a_phone_friendly_size()
    {
        var offered = new[] { (800, 600), (1366, 768), (1920, 1080), (2560, 1440), (3840, 2160) };
        Assert.Equal((1366, 768), SecondScreen.PickSize(800, 600, offered));   // the driver's first size
        Assert.Null(SecondScreen.PickSize(1920, 1080, offered));               // already sensible: leave the person's choice
        Assert.Null(SecondScreen.PickSize(1366, 768, offered));
        Assert.Equal((1920, 1080), SecondScreen.PickSize(800, 600, new[] { (800, 600), (1920, 1080) }));
        Assert.Null(SecondScreen.PickSize(800, 600, new[] { (800, 600), (1024, 768) })); // nothing wide on offer
    }

    [Fact]
    public void Touches_land_inside_the_extra_display_only()
    {
        var display = new Rectangle(1920, 0, 2400, 1080); // right of the main screen
        Assert.Equal(new Point(1920, 0), SecondScreen.Map(display, 0, 0));
        Assert.Equal(new Point(1920 + 2399, 1079), SecondScreen.Map(display, 1, 1));
        Assert.Equal(new Point(1920 + 1200, 540), SecondScreen.Map(display, 0.5, 0.5));
        // out of range or garbage from the phone is clamped onto the display
        Assert.Equal(new Point(1920, 1079), SecondScreen.Map(display, -3, 9));
        Assert.True(display.Contains(SecondScreen.Map(display, double.NaN, double.PositiveInfinity)));
    }
}
