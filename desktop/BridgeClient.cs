using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Networking.Sockets;

namespace PhoneBridge;

/// <summary>
/// The link to the Nyx phone app, over Bluetooth RFCOMM or over Wi-Fi (TCP).
/// Wire format: one JSON object per line, UTF-8. On Wi-Fi every line after the
/// handshake is AES-GCM sealed (see LinkCipher). Events are raised on a background thread.
/// </summary>
public sealed class BridgeClient
{
    // Must match LinkService.APP_UUID in the Android app.
    public static readonly Guid ServiceUuid = Guid.Parse("8f2b6c1e-5d3a-4e7b-9c41-2a6d0b7e9f13");

    public event Action<JsonObject>? MessageReceived;
    public event Action? Disconnected;

    sealed class Link
    {
        public required Stream In;
        public required Stream Out;
        public required IDisposable Owner;
        public LinkCipher? Cipher;
        public required string Kind;
    }

    volatile Link? _link;
    readonly SemaphoreSlim _sendLock = new(1, 1);

    public bool IsConnected => _link != null;

    /// <summary>"Wi-Fi", "USB" or "Bluetooth" while connected, otherwise empty.</summary>
    public string Kind => _link?.Kind ?? "";

    /// <summary>True for a Wi-Fi link that passed the pairing handshake.</summary>
    public bool IsVerified => _link?.Cipher != null;

    public static async Task<List<DeviceEntry>> GetPairedDevicesAsync()
    {
        // only phones: controllers and earbuds are paired too, and can never run Nyx
        var phones = new List<DeviceEntry>();
        var unknown = new List<DeviceEntry>();
        string selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
        var infos = await DeviceInformation.FindAllAsync(selector);
        foreach (var info in infos)
        {
            var entry = new DeviceEntry(info.Id, string.IsNullOrEmpty(info.Name) ? "(unnamed)" : info.Name);
            BluetoothMajorClass? major = null;
            try
            {
                using var bt = await BluetoothDevice.FromIdAsync(info.Id);
                major = bt?.ClassOfDevice.MajorClass;
            }
            catch { }
            if (major == BluetoothMajorClass.Phone) phones.Add(entry);
            else if (major == null || major == BluetoothMajorClass.Miscellaneous) unknown.Add(entry);
        }
        // Windows couldn't classify anything as a phone: show what we can't rule out
        return phones.Count > 0 ? phones : unknown;
    }

    // ------------------------------------------------------------ Bluetooth

    public async Task ConnectAsync(string deviceId)
    {
        Close();

        using var device = await BluetoothDevice.FromIdAsync(deviceId)
            ?? throw new InvalidOperationException("Could not open the Bluetooth device.");

        var result = await device.GetRfcommServicesForIdAsync(
            RfcommServiceId.FromUuid(ServiceUuid), BluetoothCacheMode.Uncached);

        if (result.Services.Count == 0)
        {
            // tell "can't reach the phone at all" apart from "phone is there, Nyx isn't listening"
            var any = await device.GetRfcommServicesAsync(BluetoothCacheMode.Uncached);
            if (any.Services.Count == 0)
                throw new InvalidOperationException(
                    "The laptop can't reach your phone over Bluetooth. Check Bluetooth is on and the phone is nearby, and that this laptop is still listed under paired devices on the phone; if not, remove the phone in Windows Bluetooth settings and pair them again.");
            throw new InvalidOperationException(
                "Your phone is reachable, but Nyx isn't listening on it. In Nyx on the phone, check Bridge says Running and that Nearby devices is allowed (Settings > Apps > Nyx > Permissions).");
        }

        var service = result.Services[0];
        var socket = new StreamSocket();
        try
        {
            await socket.ConnectAsync(
                service.ConnectionHostName,
                service.ConnectionServiceName,
                SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication);
        }
        catch
        {
            socket.Dispose(); // a failed attempt must not leak the socket (auto-reconnect retries every 5 s)
            throw;
        }

        Start(new Link
        {
            In = socket.InputStream.AsStreamForRead(0),
            Out = socket.OutputStream.AsStreamForWrite(0),
            Owner = socket,
            Kind = "Bluetooth"
        });
    }

    // ------------------------------------------------------------ Wi-Fi

    /// <summary>
    /// Connects to a paired phone that announced itself on the local network, then runs
    /// the mutual handshake. Throws if the phone cannot prove it holds the pairing secret.
    /// </summary>
    public async Task ConnectLanAsync(IPEndPoint phone, PairedPhone pair, CancellationToken ct = default, string kind = "Wi-Fi")
    {
        // no Close() here: an existing (Bluetooth) link keeps working until this one is verified
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await tcp.ConnectAsync(phone, timeout.Token);
                var stream = tcp.GetStream();
                var reader = new LineReader(stream);
                var secret = Convert.FromBase64String(pair.Secret);

                // 1. phone: who it is, and its nonce
                var hello = JsonNode.Parse(await reader.ReadLineAsync(timeout.Token) ?? "")?.AsObject()
                            ?? throw new InvalidDataException("no hello");
                if (hello["t"]?.ToString() != "nyx_hello" || hello["phone"]?.ToString() != pair.Id)
                    throw new InvalidDataException("This is not the paired phone.");
                var nPhone = Convert.FromBase64String(hello["n"]!.ToString());

                // 2. laptop: its nonce and proof
                var nLaptop = RandomNumberGenerator.GetBytes(16);
                await WriteLineAsync(stream, new JsonObject
                {
                    ["t"] = "nyx_auth",
                    ["laptop"] = PairStore.LaptopId,
                    ["n"] = Convert.ToBase64String(nLaptop),
                    ["mac"] = Convert.ToBase64String(LinkCipher.Mac(secret, "L", nPhone, nLaptop))
                }.ToJsonString(), timeout.Token);

                // 3. phone: its proof
                var ok = JsonNode.Parse(await reader.ReadLineAsync(timeout.Token) ?? "")?.AsObject();
                var theirs = Convert.FromBase64String(ok?["mac"]?.ToString() ?? "");
                if (ok?["t"]?.ToString() != "nyx_auth_ok" ||
                    !CryptographicOperations.FixedTimeEquals(theirs, LinkCipher.Mac(secret, "P", nLaptop, nPhone)))
                    throw new InvalidDataException("The phone could not prove it is paired with this laptop.");

                var key = LinkCipher.Mac(secret, "K", nPhone, nLaptop);
                Start(new Link
                {
                    In = new PrefixedStream(reader.Leftover(), stream),
                    Out = stream,
                    Owner = tcp,
                    Cipher = new LinkCipher(key, LinkCipher.ToPhone, LinkCipher.ToLaptop),
                    Kind = kind
                });
            }
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    static async Task WriteLineAsync(Stream s, string line, CancellationToken ct)
    {
        var data = Encoding.UTF8.GetBytes(line + "\n");
        await s.WriteAsync(data, ct);
        await s.FlushAsync(ct);
    }

    // ------------------------------------------------------------ shared

    void Start(Link link)
    {
        var old = _link;
        _link = link;
        // the old link's read loop ends quietly: it is no longer the current link
        if (old != null && !ReferenceEquals(old, link)) try { old.Owner.Dispose(); } catch { }
        _ = Task.Run(() => ReadLoopAsync(link));
    }

    async Task ReadLoopAsync(Link link)
    {
        try
        {
            var reader = new LineReader(link.In);
            while (true)
            {
                string? line = await reader.ReadLineAsync(CancellationToken.None);
                if (line == null) break;
                if (line.Length == 0) continue;
                // a line that fails to decrypt means tampering or a broken link: drop it
                Dispatch(link.Cipher != null ? link.Cipher.Open(line) : line);
            }
        }
        catch
        {
            // socket dropped, or a forged frame
        }

        if (ReferenceEquals(_link, link))
        {
            Close();
            Disconnected?.Invoke();
        }
    }

    void Dispatch(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            if (JsonNode.Parse(text) is JsonObject obj)
                MessageReceived?.Invoke(obj);
        }
        catch
        {
            // ignore malformed lines
        }
    }

    public async Task SendAsync(JsonObject message)
    {
        var link = _link;
        if (link == null) return;
        await _sendLock.WaitAsync();
        try
        {
            string text = message.ToJsonString();
            string line = link.Cipher != null ? link.Cipher.Seal(text) : text;
            await WriteLineAsync(link.Out, line, CancellationToken.None);
        }
        catch
        {
            // the read loop will notice the drop
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public Task SendAsync(string type) => SendAsync(new JsonObject { ["t"] = type });

    /// <summary>Like SendAsync, but says whether the message went out (used to stream files).</summary>
    public async Task<bool> TrySendAsync(JsonObject message)
    {
        var link = _link;
        if (link == null) return false;
        await _sendLock.WaitAsync();
        try
        {
            if (!ReferenceEquals(link, _link)) return false;
            string text = message.ToJsonString();
            await WriteLineAsync(link.Out, link.Cipher != null ? link.Cipher.Seal(text) : text, CancellationToken.None);
            return true;
        }
        catch { return false; }
        finally { _sendLock.Release(); }
    }

    public void Close()
    {
        var l = _link;
        _link = null;
        try { l?.Owner.Dispose(); } catch { }
    }

    // ------------------------------------------------------------ helpers

    /// <summary>Reads UTF-8 lines from a stream without over-reading past what it returns.</summary>
    internal sealed class LineReader
    {
        readonly Stream _s;
        readonly byte[] _buf = new byte[8192];
        int _start, _end;
        readonly MemoryStream _line = new();

        public LineReader(Stream s) => _s = s;

        public async Task<string?> ReadLineAsync(CancellationToken ct)
        {
            while (true)
            {
                for (int i = _start; i < _end; i++)
                {
                    if (_buf[i] != (byte)'\n') continue;
                    _line.Write(_buf, _start, i - _start);
                    _start = i + 1;
                    var text = Encoding.UTF8.GetString(_line.GetBuffer(), 0, (int)_line.Length).TrimEnd('\r');
                    _line.SetLength(0);
                    return text;
                }
                _line.Write(_buf, _start, _end - _start);
                _start = _end = 0;
                int n = await _s.ReadAsync(_buf, ct);
                if (n == 0) return null;
                if (_line.Length > 4 * 1024 * 1024) throw new InvalidDataException("line too long");
                _end = n;
            }
        }

        /// <summary>Bytes already read from the stream but not yet returned as a line.</summary>
        public byte[] Leftover()
        {
            var rest = new byte[_line.Length + (_end - _start)];
            _line.ToArray().CopyTo(rest, 0);
            Array.Copy(_buf, _start, rest, _line.Length, _end - _start);
            return rest;
        }
    }

    /// <summary>A read stream that first returns some buffered bytes, then the real stream.</summary>
    sealed class PrefixedStream : Stream
    {
        readonly byte[] _prefix;
        int _pos;
        readonly Stream _inner;
        public PrefixedStream(byte[] prefix, Stream inner) { _prefix = prefix; _inner = inner; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_pos < _prefix.Length)
            {
                int n = Math.Min(buffer.Length, _prefix.Length - _pos);
                _prefix.AsMemory(_pos, n).CopyTo(buffer);
                _pos += n;
                return n;
            }
            return await _inner.ReadAsync(buffer, ct);
        }
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
}
