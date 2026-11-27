using System.Drawing;
using System.Security.Cryptography;
using PhoneBridge;
using Xunit;

namespace Nyx.Tests;

public class WebcamTests
{
    [Fact]
    public void Frame_slots_are_laid_out_like_OBS()
    {
        int frame = 1280 * 720 * 3 / 2;
        var o = VirtualCam.Layout(frame, out int total);
        Assert.Equal(96, o[0]);                               // 80-byte header, 32-aligned
        Assert.Equal(0, o[1] % 32);
        Assert.Equal(0, o[2] % 32);
        Assert.True(o[1] >= o[0] + frame + 32 && o[2] >= o[1] + frame + 32);
        Assert.True(total >= o[2] + frame + 32);
    }

    [Fact]
    public void A_portrait_picture_fills_the_16_9_frame_from_its_middle()
    {
        var r = VirtualCam.Fill(720, 1280);
        Assert.Equal(1280, r.Width);                          // as wide as the frame
        Assert.True(r.Height > 720 && r.Y < 0);               // top and bottom cropped evenly
        Assert.Equal(VirtualCam.Fill(1280, 720), new Rectangle(0, 0, 1280, 720));
    }

    [Fact]
    public void Sideways_fills_upright_shows_whole()
    {
        Assert.Equal(new Rectangle(0, -120, 1280, 960), VirtualCam.Place(1280, 960, fill: false));  // 4:3 sideways: full width
        var upright = VirtualCam.Place(960, 1280, fill: false);
        Assert.Equal(720, upright.Height);                                                         // whole picture
        Assert.True(upright.X > 0);                                                                // bars at the sides
        Assert.Equal(1280, VirtualCam.Place(960, 1280, fill: true).Width);                         // asked to fill
    }

    [Fact]
    public void Colours_come_out_right_in_NV12()
    {
        using var bmp = new Bitmap(4, 2, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        for (int x = 0; x < 4; x++) for (int y = 0; y < 2; y++) bmp.SetPixel(x, y, x < 2 ? Color.White : Color.Black);
        var nv12 = new byte[4 * 2 * 3 / 2];
        VirtualCam.ToNv12(bmp, nv12);
        Assert.InRange(nv12[0], 230, 240);                    // white: Y about 235
        Assert.InRange(nv12[3], 15, 18);                      // black: Y 16
        Assert.InRange(nv12[8], 126, 130);                    // no colour: U and V about 128
        Assert.InRange(nv12[9], 126, 130);
    }

    [Fact]
    public void Camera_frames_open_only_with_the_key_and_never_twice()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var frame = SecondScreen.Seal(key, 5, new byte[] { 1, 0xFF, 0xD8 });
        using var gcm = new AesGcm(key, 16);
        long last = 0;
        Assert.Equal(new byte[] { 1, 0xFF, 0xD8 }, PhoneCamera.Open(gcm, frame, ref last));
        Assert.Equal(5, last);
        Assert.Empty(PhoneCamera.Open(gcm, frame, ref last));           // replayed: dropped
        using var wrong = new AesGcm(RandomNumberGenerator.GetBytes(32), 16);
        long l2 = 0;
        Assert.ThrowsAny<CryptographicException>(() => PhoneCamera.Open(wrong, frame, ref l2));
    }
}
