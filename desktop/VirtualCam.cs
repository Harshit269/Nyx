using System.Drawing;
using System.Drawing.Imaging;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace PhoneBridge;

/// <summary>
/// Puts pictures into "OBS Virtual Camera", the free camera OBS Studio adds to Windows, so
/// Meet, Zoom, Teams and the rest can use the phone's camera. OBS doesn't need to be running:
/// its camera reads frames from a shared-memory queue, and Nyx writes that queue the same way
/// OBS does (three NV12 frame slots behind a small header).
/// </summary>
public sealed class VirtualCam : IDisposable
{
    public const int Width = 1280, Height = 720, Fps = 30;
    const string QueueName = "OBSVirtualCamVideo";
    const int HeaderSize = 80, FrameHeaderSize = 32;
    const uint StateStarting = 1, StateReady = 2, StateStopping = 3;

    readonly MemoryMappedFile _map;
    readonly MemoryMappedViewAccessor _view;
    readonly int[] _offsets = new int[3];
    readonly int _frameSize = Width * Height * 3 / 2;
    readonly byte[] _nv12;
    uint _writeIdx;
    bool _closed;

    /// <summary>Whether OBS's virtual camera is installed (registered with Windows).</summary>
    public static bool Installed
    {
        get
        {
            try
            {
                using var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(@"CLSID\{860BB310-5D01-11d0-BD3B-00A0C911CE86}\Instance");
                if (key == null) return false;
                foreach (var name in key.GetSubKeyNames())
                    using (var sub = key.OpenSubKey(name))
                        if ((sub?.GetValue("FriendlyName") as string)?.Contains("OBS Virtual Camera") == true) return true;
            }
            catch { }
            return false;
        }
    }

    /// <summary>Byte offsets of the three frame slots, laid out as OBS does (32-byte aligned).</summary>
    public static int[] Layout(int frameSize, out int total)
    {
        static int Align(int n) => (n + 31) & ~31;
        var o = new int[3];
        int size = Align(HeaderSize);
        for (int i = 0; i < 3; i++)
        {
            o[i] = size;
            size = Align(size + frameSize + FrameHeaderSize);
        }
        total = size;
        return o;
    }

    public VirtualCam()
    {
        var offs = Layout(_frameSize, out int total);
        offs.CopyTo(_offsets, 0);
        _nv12 = new byte[_frameSize];
        _map = MemoryMappedFile.CreateOrOpen(QueueName, total, MemoryMappedFileAccess.ReadWrite);
        _view = _map.CreateViewAccessor(0, total);
        // header: write_idx, read_idx, state, offsets[3], type (video = 0), cx, cy, interval (100 ns)
        _view.Write(0, 0u);
        _view.Write(4, 0u);
        _view.Write(8, StateStarting);
        for (int i = 0; i < 3; i++) _view.Write(12 + i * 4, (uint)_offsets[i]);
        _view.Write(24, 0u);
        _view.Write(28, (uint)Width);
        _view.Write(32, (uint)Height);
        _view.Write(40, 10_000_000UL / Fps);
        Black();
    }

    /// <summary>Fill the 16:9 frame (crops the edges) instead of showing the whole picture with bars.</summary>
    public static bool FillFrame { get; set; }

    /// <summary>Shows a picture: all of it, centred (or, with FillFrame, filling the frame).</summary>
    public void Show(Bitmap picture)
    {
        if (_closed) return;
        using (var frame = new Bitmap(Width, Height, PixelFormat.Format24bppRgb))
        {
            using (var g = Graphics.FromImage(frame))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                g.Clear(Color.Black);
                g.DrawImage(picture, Place(picture.Width, picture.Height, FillFrame));
            }
            ToNv12(frame, _nv12);
        }
        Write(_nv12);
    }

    /// <summary>Where a picture goes to fill 1280×720 with its middle (like a phone's camera app).</summary>
    public static Rectangle Fill(int w, int h)
    {
        double scale = Math.Max((double)Width / w, (double)Height / h);
        int dw = (int)Math.Round(w * scale), dh = (int)Math.Round(h * scale);
        return new Rectangle((Width - dw) / 2, (Height - dh) / 2, dw, dh);
    }

    /// <summary>
    /// A sideways phone fills the frame (only a sliver off the top and bottom, the full width
    /// stays, like any webcam). An upright one is shown whole with bars at the sides, since
    /// filling would cut most of it away, unless fill is asked for.
    /// </summary>
    public static Rectangle Place(int w, int h, bool fill) => fill || w >= h ? Fill(w, h) : Fit(w, h);

    /// <summary>Where a picture goes to show all of it inside 1280×720 (black bars where it doesn't reach).</summary>
    public static Rectangle Fit(int w, int h)
    {
        double scale = Math.Min((double)Width / w, (double)Height / h);
        int dw = (int)Math.Round(w * scale), dh = (int)Math.Round(h * scale);
        return new Rectangle((Width - dw) / 2, (Height - dh) / 2, dw, dh);
    }

    void Black()
    {
        Array.Fill(_nv12, (byte)16, 0, Width * Height);          // Y: black
        Array.Fill(_nv12, (byte)128, Width * Height, Width * Height / 2); // UV: no colour
        Write(_nv12);
    }

    void Write(byte[] nv12)
    {
        uint next = _writeIdx + 1;
        int slot = _offsets[next % 3];
        _view.Write(slot, (ulong)DateTime.UtcNow.Ticks);                // the slot's timestamp
        _view.WriteArray(slot + FrameHeaderSize, nv12, 0, nv12.Length);
        _writeIdx = next;
        _view.Write(0, next);   // write_idx
        _view.Write(4, next);   // read_idx
        _view.Write(8, StateReady);
    }

    /// <summary>RGB to NV12 (BT.601, studio range), the format OBS's camera reads.</summary>
    public static void ToNv12(Bitmap rgb, byte[] nv12)
    {
        int w = rgb.Width, h = rgb.Height;
        var data = rgb.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            int stride = data.Stride;
            var px = new byte[stride * h];
            Marshal.Copy(data.Scan0, px, 0, px.Length);
            int uv = w * h;
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x * 3;
                    int b = px[i], g = px[i + 1], r = px[i + 2];
                    nv12[y * w + x] = (byte)(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
                    if ((y & 1) == 0 && (x & 1) == 0)
                    {
                        int o = uv + (y / 2) * w + x;
                        nv12[o] = (byte)(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
                        nv12[o + 1] = (byte)(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
                    }
                }
            }
        }
        finally { rgb.UnlockBits(data); }
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        try { Black(); _view.Write(8, StateStopping); } catch { }
        _view.Dispose();
        _map.Dispose();
    }
}
