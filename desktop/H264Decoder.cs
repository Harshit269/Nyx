using System.Runtime.InteropServices;

namespace PhoneBridge;

/// <summary>
/// Windows' own H.264 decoder (Media Foundation, part of Windows), in low-latency mode, for the
/// phone's screen over Wi-Fi: each piece of video goes in, finished pictures (NV12) come out and
/// are turned into ordinary 32-bit pixels for the window. Nothing to install.
/// </summary>
public sealed class H264Decoder : IDisposable
{
    readonly IMFTransform _mft;
    IMFSample? _outSample;          // reused for every picture when the decoder wants us to supply one
    IntPtr _outSamplePtr;
    IMFMediaBuffer? _outBuf;        // its buffer, emptied before each picture
    bool _decoderProvides;
    int _outSize;
    byte[] _nv12 = Array.Empty<byte>();
    bool _bt601, _fullRange;

    public int Width { get; private set; }
    public int Height { get; private set; }

    static bool? _available;
    /// <summary>Whether this PC can decode H.264 (Windows N editions may lack it).</summary>
    public static bool Available
    {
        get
        {
            if (_available is bool b) return b;
            try { using var d = new H264Decoder(); _available = true; }
            catch (Exception ex) { Diag.Log("h264: not available: " + ex.Message); _available = false; }
            return _available.Value;
        }
    }

    public H264Decoder()
    {
        Check(MFStartup(0x00020070, 0), "MFStartup");
        var type = Type.GetTypeFromCLSID(new Guid("62CE7E72-4C71-4d20-B15D-452831A87D9D"), throwOnError: true)!;
        _mft = (IMFTransform)Activator.CreateInstance(type)!;
        // low latency: a picture comes out as soon as its data is in, no frames held back
        if (_mft.GetAttributes(out var attrs) >= 0 && attrs != null)
        {
            var low = LowLatency;
            attrs.SetUINT32(ref low, 1);
            Marshal.ReleaseComObject(attrs);
        }
        Check(MFCreateMediaType(out var input), "MFCreateMediaType");
        var major = MT_MAJOR; var video = MediaVideo; var sub = MT_SUBTYPE; var h264 = FormatH264; var il = MT_INTERLACE;
        input.SetGUID(ref major, ref video);
        input.SetGUID(ref sub, ref h264);
        input.SetUINT32(ref il, 2); // progressive
        Check(_mft.SetInputType(0, input, 0), "SetInputType");
        Marshal.ReleaseComObject(input);
        try { PickOutput(); } catch { } // some versions only offer pictures once the first video arrives
        _mft.ProcessMessage(0x10000000, IntPtr.Zero); // begin streaming
        _mft.ProcessMessage(0x10000003, IntPtr.Zero); // start of stream
    }

    /// <summary>Asks for NV12 pictures and notes their size, colours and how they're handed out.</summary>
    void PickOutput()
    {
        for (uint i = 0; ; i++)
        {
            if (_mft.GetOutputAvailableType(0, i, out var t) < 0 || t == null) throw new InvalidOperationException("no NV12 output");
            var sub = MT_SUBTYPE;
            bool nv12 = t.GetGUID(ref sub, out var g) >= 0 && g == FormatNV12;
            if (nv12)
            {
                Check(_mft.SetOutputType(0, t, 0), "SetOutputType");
                var fs = MT_FRAME_SIZE;
                if (t.GetUINT64(ref fs, out ulong size) >= 0) { Width = (int)(size >> 32); Height = (int)(size & 0xFFFFFFFF); }
                var mx = MT_YUV_MATRIX; var nr = MT_NOMINAL_RANGE;
                _bt601 = t.GetUINT32(ref mx, out uint matrix) >= 0 && matrix == 2;
                _fullRange = t.GetUINT32(ref nr, out uint range) >= 0 && range == 1;
                Marshal.ReleaseComObject(t);
                break;
            }
            Marshal.ReleaseComObject(t);
        }
        Check(_mft.GetOutputStreamInfo(0, out var info), "GetOutputStreamInfo");
        _decoderProvides = (info.Flags & 0x100) != 0;
        _outSize = (int)info.Size;
        ReleaseOutSample();
        if (!_decoderProvides)
        {
            Check(MFCreateSample(out var s), "MFCreateSample");
            Check(MFCreateMemoryBuffer((uint)Math.Max(_outSize, Width * Height * 3 / 2), out var buf), "MFCreateMemoryBuffer");
            s.AddBuffer(buf);
            _outBuf = buf;
            _outSample = s;
            var unk = Marshal.GetIUnknownForObject(s);
            try { var iid = typeof(IMFSample).GUID; Check(Marshal.QueryInterface(unk, in iid, out _outSamplePtr), "QueryInterface"); }
            finally { Marshal.Release(unk); }
        }
    }

    void ReleaseOutSample()
    {
        if (_outSamplePtr != IntPtr.Zero) { Marshal.Release(_outSamplePtr); _outSamplePtr = IntPtr.Zero; }
        if (_outBuf != null) { Marshal.ReleaseComObject(_outBuf); _outBuf = null; }
        if (_outSample != null) { Marshal.ReleaseComObject(_outSample); _outSample = null; }
    }

    /// <summary>Decodes one piece of video; <paramref name="picture"/> gets each finished picture as 32-bit pixels.</summary>
    public void Push(byte[] data, int offset, int count, long time100ns, byte[]? into, Action<byte[], int, int> picture)
    {
        Check(MFCreateMemoryBuffer((uint)count, out var buf), "MFCreateMemoryBuffer");
        try
        {
            Check(buf.Lock(out var p, out _, out _), "Lock");
            Marshal.Copy(data, offset, p, count);
            buf.Unlock();
            buf.SetCurrentLength((uint)count);
            Check(MFCreateSample(out var sample), "MFCreateSample");
            try
            {
                sample.AddBuffer(buf);
                sample.SetSampleTime(time100ns);
                int hr = _mft.ProcessInput(0, sample, 0);
                if (hr == MF_E_NOTACCEPTING) { Drain(into, picture); hr = _mft.ProcessInput(0, sample, 0); }
                Check(hr, "ProcessInput");
            }
            finally { Marshal.ReleaseComObject(sample); }
        }
        finally { Marshal.ReleaseComObject(buf); }
        Drain(into, picture);
    }

    /// <summary>Hands out every picture still inside the decoder.</summary>
    public void Flush(byte[]? into, Action<byte[], int, int> picture)
    {
        _mft.ProcessMessage(0x00000001, IntPtr.Zero); // drain
        Drain(into, picture);
    }

    void Drain(byte[]? into, Action<byte[], int, int> picture)
    {
        while (true)
        {
            if (!_decoderProvides && _outSamplePtr == IntPtr.Zero)
            {
                try { PickOutput(); } catch { return; }
            }
            _outBuf?.SetCurrentLength(0); // reused: the decoder wants it empty
            var ob = new OutBuf { StreamId = 0, Sample = _decoderProvides ? IntPtr.Zero : _outSamplePtr };
            int hr = _mft.ProcessOutput(0, 1, ref ob, out _);
            if (ob.Events != IntPtr.Zero) Marshal.Release(ob.Events);
            if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT) return;
            if (hr == MF_E_TRANSFORM_STREAM_CHANGE) // new picture size (first picture, or the phone turned)
            {
                PickOutput();
                _mft.ProcessMessage(0x10000000, IntPtr.Zero); // begin streaming again with the new type
                _mft.ProcessMessage(0x10000003, IntPtr.Zero);
                continue;
            }
            Check(hr, $"ProcessOutput ({Width}x{Height})");
            IMFSample s = _decoderProvides ? (IMFSample)Marshal.GetObjectForIUnknown(ob.Sample) : _outSample!;
            try
            {
                Check(s.ConvertToContiguousBuffer(out var cb), "ConvertToContiguousBuffer");
                try
                {
                    Check(cb.Lock(out var p, out _, out uint len), "Lock");
                    try
                    {
                        if (_nv12.Length < len) _nv12 = new byte[len];
                        Marshal.Copy(p, _nv12, 0, (int)len);
                    }
                    finally { cb.Unlock(); }
                    int w = Width, h = Height;
                    if (w <= 0 || h <= 0) continue;
                    int stride = len >= (uint)(w * h * 3 / 2) ? (int)(len * 2 / 3 / h) : w;
                    if (stride < w) continue;
                    var bgra = into != null && into.Length == w * h * 4 ? into : new byte[w * h * 4];
                    ToBgra(_nv12, stride, w, h, bgra, _bt601, _fullRange);
                    picture(bgra, w, h);
                }
                finally { Marshal.ReleaseComObject(cb); }
            }
            finally
            {
                if (_decoderProvides) { Marshal.ReleaseComObject(s); Marshal.Release(ob.Sample); }
            }
        }
    }

    /// <summary>NV12 (video's usual layout) to 32-bit BGRA, split across the CPU's cores.</summary>
    public static void ToBgra(byte[] nv12, int stride, int w, int h, byte[] bgra, bool bt601, bool fullRange)
    {
        // fixed-point (x1024) coefficients for the colour standard the video says it uses
        int ys, rv, gu, gv, bu;
        if (bt601) { if (fullRange) { ys = 1024; rv = 1436; gu = 352; gv = 731; bu = 1815; } else { ys = 1192; rv = 1634; gu = 401; gv = 832; bu = 2066; } }
        else { if (fullRange) { ys = 1024; rv = 1613; gu = 192; gv = 479; bu = 1900; } else { ys = 1192; rv = 1836; gu = 218; gv = 546; bu = 2163; } }
        int yOff = fullRange ? 0 : 16;
        int uvBase = stride * h;
        Parallel.For(0, h / 2, pair =>
        {
            for (int row = pair * 2; row < pair * 2 + 2; row++)
            {
                int yi = row * stride, ci = uvBase + pair * stride, o = row * w * 4;
                for (int x = 0; x < w; x++)
                {
                    int c = ci + (x & ~1);
                    int u = nv12[c] - 128, v = nv12[c + 1] - 128;
                    int y = (nv12[yi + x] - yOff) * ys + 512; // +512: round, not truncate
                    int r = (y + rv * v) >> 10, g = (y - gu * u - gv * v) >> 10, b = (y + bu * u) >> 10;
                    bgra[o] = (byte)(b < 0 ? 0 : b > 255 ? 255 : b);
                    bgra[o + 1] = (byte)(g < 0 ? 0 : g > 255 ? 255 : g);
                    bgra[o + 2] = (byte)(r < 0 ? 0 : r > 255 ? 255 : r);
                    bgra[o + 3] = 255;
                    o += 4;
                }
            }
        });
    }

    /// <summary>Splits Annex B video into whole pictures (headers ride with the first), as the phone sends them.</summary>
    public static List<(int Start, int Length)> SplitPictures(byte[] d)
    {
        var starts = new List<int>();
        for (int i = 0; i + 4 < d.Length; i++)
            if (d[i] == 0 && d[i + 1] == 0 && d[i + 2] == 1)
            {
                int s = i > 0 && d[i - 1] == 0 ? i - 1 : i;
                int type = d[i + 3] & 0x1F;
                if ((type == 1 || type == 5) && (d[i + 4] & 0x80) != 0) starts.Add(s); // first slice of a picture
                i += 2;
            }
        var list = new List<(int, int)>();
        for (int k = 0; k < starts.Count; k++)
        {
            int from = k == 0 ? 0 : starts[k], to = k + 1 < starts.Count ? starts[k + 1] : d.Length;
            list.Add((from, to - from));
        }
        return list;
    }

    public void Dispose()
    {
        ReleaseOutSample();
        try { Marshal.ReleaseComObject(_mft); } catch { }
    }

    static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"{what} failed (0x{hr:X8})");
    }

    const int MF_E_NOTACCEPTING = unchecked((int)0xC00D36B5);
    const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);

    static readonly Guid MT_MAJOR = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    static readonly Guid MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    static readonly Guid MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    static readonly Guid MT_INTERLACE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    static readonly Guid MT_YUV_MATRIX = new("3e23d450-2c75-4d25-a00e-b91670d12327");
    static readonly Guid MT_NOMINAL_RANGE = new("c21b8ee5-b956-4071-8daf-325edf5cab11");
    static readonly Guid MediaVideo = new("73646976-0000-0010-8000-00AA00389B71");
    static readonly Guid FormatH264 = new("34363248-0000-0010-8000-00AA00389B71");
    static readonly Guid FormatNV12 = new("3231564E-0000-0010-8000-00AA00389B71");
    static readonly Guid LowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");

    [StructLayout(LayoutKind.Sequential)] struct OutInfo { public uint Flags, Size, Alignment; }
    [StructLayout(LayoutKind.Sequential)] struct OutBuf { public uint StreamId; public IntPtr Sample; public uint Status; public IntPtr Events; }

    [DllImport("mfplat.dll")] static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] static extern int MFCreateMediaType(out IMFMediaType type);
    [DllImport("mfplat.dll")] static extern int MFCreateSample(out IMFSample sample);
    [DllImport("mfplat.dll")] static extern int MFCreateMemoryBuffer(uint length, out IMFMediaBuffer buffer);

    // Only the methods used are typed; the rest hold their place in the table (order matters).
    [ComImport, Guid("bf94c121-5b05-4e6f-8000-ba598961414d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFTransform
    {
        void GetStreamLimits(); void GetStreamCount(); void GetStreamIDs(); void GetInputStreamInfo();
        [PreserveSig] int GetOutputStreamInfo(uint id, out OutInfo info);
        [PreserveSig] int GetAttributes(out IMFAttributes attrs);
        void GetInputStreamAttributes(); void GetOutputStreamAttributes(); void DeleteInputStream(); void AddInputStreams();
        void GetInputAvailableType();
        [PreserveSig] int GetOutputAvailableType(uint id, uint index, out IMFMediaType type);
        [PreserveSig] int SetInputType(uint id, IMFMediaType type, uint flags);
        [PreserveSig] int SetOutputType(uint id, IMFMediaType type, uint flags);
        void GetInputCurrentType(); void GetOutputCurrentType(); void GetInputStatus(); void GetOutputStatus();
        void SetOutputBounds(); void ProcessEvent();
        [PreserveSig] int ProcessMessage(uint message, IntPtr param);
        [PreserveSig] int ProcessInput(uint id, IMFSample sample, uint flags);
        [PreserveSig] int ProcessOutput(uint flags, uint count, ref OutBuf buffer, out uint status);
    }

    [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFAttributes
    {
        void GetItem(); void GetItemType(); void CompareItem(); void Compare(); void GetUINT32(); void GetUINT64(); void GetDouble();
        void GetGUID(); void GetStringLength(); void GetString(); void GetAllocatedString(); void GetBlobSize(); void GetBlob();
        void GetAllocatedBlob(); void GetUnknown(); void SetItem(); void DeleteItem(); void DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, uint value);
    }

    [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFMediaType
    {
        void GetItem(); void GetItemType(); void CompareItem(); void Compare();
        [PreserveSig] int GetUINT32(ref Guid key, out uint value);
        [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
        void GetDouble();
        [PreserveSig] int GetGUID(ref Guid key, out Guid value);
        void GetStringLength(); void GetString(); void GetAllocatedString(); void GetBlobSize(); void GetBlob();
        void GetAllocatedBlob(); void GetUnknown(); void SetItem(); void DeleteItem(); void DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, uint value);
        [PreserveSig] int SetUINT64(ref Guid key, ulong value);
        void SetDouble();
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
    }

    [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFSample
    {
        void GetItem(); void GetItemType(); void CompareItem(); void Compare(); void GetUINT32(); void GetUINT64(); void GetDouble();
        void GetGUID(); void GetStringLength(); void GetString(); void GetAllocatedString(); void GetBlobSize(); void GetBlob();
        void GetAllocatedBlob(); void GetUnknown(); void SetItem(); void DeleteItem(); void DeleteAllItems(); void SetUINT32();
        void SetUINT64(); void SetDouble(); void SetGUID(); void SetString(); void SetBlob(); void SetUnknown(); void LockStore();
        void UnlockStore(); void GetCount(); void GetItemByIndex(); void CopyAllItems();
        void GetSampleFlags(); void SetSampleFlags(); void GetSampleTime();
        [PreserveSig] int SetSampleTime(long time);
        void GetSampleDuration(); void SetSampleDuration(); void GetBufferCount(); void GetBufferByIndex();
        [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
        [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
    }

    [ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr data, out uint maxLength, out uint currentLength);
        [PreserveSig] int Unlock();
        void GetCurrentLength();
        [PreserveSig] int SetCurrentLength(uint length);
    }
}
