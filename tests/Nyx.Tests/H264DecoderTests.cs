using PhoneBridge;
using Xunit;

namespace Nyx.Tests;

public class H264DecoderTests
{
    [Fact]
    public void Video_colours_become_the_right_pixels()
    {
        // 2x2 NV12: Y plane then one U,V pair; limited range BT.709
        byte[] Pixel(byte y)
        {
            var nv12 = new byte[] { y, y, y, y, 128, 128 };
            var bgra = new byte[16];
            H264Decoder.ToBgra(nv12, 2, 2, 2, bgra, bt601: false, fullRange: false);
            return bgra;
        }
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(235)[..4]); // white
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, Pixel(16)[..4]);        // black
        var grey = Pixel(126);
        Assert.InRange(grey[0], 126, 129);
        Assert.Equal(grey[0], grey[2]);
    }

    [Fact]
    public void Phone_sized_video_decodes_to_pictures()
    {
        // a 976x2160 clip, the size the phone sends (made with ffmpeg's test pattern)
        var data = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "phone-976x2160.h264"));
        using var d = new H264Decoder(); // throws with the reason if Windows' decoder can't start
        int pictures = 0, w = 0, h = 0;
        byte[]? last = null;
        long t = 0;
        foreach (var (start, len) in Units(data))
            d.Push(data, start, len, t += 333_333, last, (px, pw, ph) => { pictures++; w = pw; h = ph; last = px; });
        d.Flush(last, (px, pw, ph) => { pictures++; w = pw; h = ph; last = px; });
        Assert.True(pictures >= 4, "pictures: " + pictures);
        Assert.Equal(976, w);
        Assert.Equal(2160, h);
        Assert.Contains(last!, b => b > 40); // not a black picture
    }

    /// <summary>Splits Annex B video into whole pictures, the way the phone sends them (headers ride with the first).</summary>
    static IEnumerable<(int, int)> Units(byte[] d)
    {
        var starts = new List<int>();
        for (int i = 0; i + 3 < d.Length; i++)
            if (d[i] == 0 && d[i + 1] == 0 && d[i + 2] == 1)
            {
                int s = i > 0 && d[i - 1] == 0 ? i - 1 : i;
                int type = d[i + 3] & 0x1F;
                bool firstSlice = (type == 1 || type == 5) && (d[i + 4] & 0x80) != 0; // first_mb_in_slice == 0
                if (firstSlice) starts.Add(s);
                i += 2;
            }
        // headers (SPS/PPS/SEI) go with the first picture
        for (int k = 0; k < starts.Count; k++)
        {
            int from = k == 0 ? 0 : starts[k], to = k + 1 < starts.Count ? starts[k + 1] : d.Length;
            yield return (from, to - from);
        }
    }
}

public class WifiAdbTests
{
    [Fact]
    public void Finds_the_phone_plugged_in_by_usb_not_wifi_ones()
    {
        var output = "List of devices attached\r\n10.1.28.213:5555\tdevice\r\nR5CT10ABCDE\tdevice\r\n";
        Assert.Equal("R5CT10ABCDE", Mirror.UsbSerial(output));
        Assert.Null(Mirror.UsbSerial("List of devices attached\r\nR5CT10ABCDE\tunauthorized\r\n"));
        Assert.Null(Mirror.UsbSerial("List of devices attached\r\n10.1.28.213:5555\tdevice\r\n"));
        Assert.Null(Mirror.UsbSerial("List of devices attached\r\nbad;rm -rf\tdevice\r\n"));
    }

    [Fact]
    public void Knows_when_adb_connect_worked()
    {
        Assert.True(Mirror.Connected("connected to 10.1.28.213:5555"));
        Assert.True(Mirror.Connected("already connected to 10.1.28.213:5555"));
        Assert.False(Mirror.Connected("cannot connect to 10.1.28.213:5555: No connection could be made"));
        Assert.False(Mirror.Connected("failed to connect to '10.1.28.213:5555': Connection refused"));
    }
}

public class WakeSetupTests
{
    [Fact]
    public void Only_plain_adapter_names_go_into_the_command()
    {
        Assert.True(WakeSetup.SafeName("Wi-Fi"));
        Assert.True(WakeSetup.SafeName("Wi-Fi 2 (USB)"));
        Assert.False(WakeSetup.SafeName("Wi-Fi'; Remove-Item C: -Recurse; '"));
        Assert.False(WakeSetup.SafeName("Wi-Fi\"$(calc)"));
        Assert.False(WakeSetup.SafeName(""));
    }
}
