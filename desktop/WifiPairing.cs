using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace PhoneBridge;

/// <summary>
/// Pairing over Wi-Fi with no Bluetooth needed. Both sides make a fresh P-256 key pair.
/// The laptop sends only a hash of its key first, sees the phone's key, then reveals its
/// own; that order stops anyone in the middle from trying keys until the 6-digit codes
/// happen to match. Both screens show the code, and the pairing is saved only when the
/// person confirms on the laptop AND allows it on the phone. Mirrors PairCrypto on Android.
/// </summary>
public sealed class WifiPairing : IDisposable
{
    readonly TcpClient _tcp;
    readonly NetworkStream _stream;
    readonly BridgeClient.LineReader _reader;
    Task<string?>? _phoneAnswer;
    byte[] _secret = Array.Empty<byte>();

    public string PhoneId { get; private set; } = "";
    public string PhoneName { get; private set; } = "";
    /// <summary>The six digits to compare, e.g. "042917".</summary>
    public string Code { get; private set; } = "";

    WifiPairing(TcpClient tcp)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        _reader = new BridgeClient.LineReader(_stream);
    }

    public static async Task<WifiPairing> StartAsync(IPEndPoint phone, string expectedPhoneId, CancellationToken ct = default)
    {
        var tcp = new TcpClient { NoDelay = true };
        using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
        t.CancelAfter(TimeSpan.FromSeconds(8));
        try { await tcp.ConnectAsync(phone, t.Token); }
        catch { tcp.Dispose(); throw; }
        var p = new WifiPairing(tcp); // the stream only exists once connected
        try
        {

            var hello = await p.ReadAsync(t.Token);
            if (hello["t"]?.ToString() != "nyx_hello" || hello["phone"]?.ToString() != expectedPhoneId)
                throw new InvalidDataException("That isn't the phone that announced itself.");

            using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            byte[] pubLaptop = PublicBytes(ecdh);
            await p.WriteAsync(new JsonObject
            {
                ["t"] = "nyx_pair", ["laptop"] = PairStore.LaptopId, ["name"] = Environment.MachineName,
                ["commit"] = Convert.ToBase64String(SHA256.HashData(pubLaptop))
            }, t.Token);

            var pub = await p.ReadAsync(t.Token);
            if (pub["t"]?.ToString() != "nyx_pair_pub" || pub["phone"]?.ToString() != expectedPhoneId)
                throw new InvalidDataException("The phone did not answer the pairing request.");
            byte[] pubPhone = Convert.FromBase64String(pub["pub"]!.ToString());
            p.PhoneId = expectedPhoneId;
            p.PhoneName = Clean(pub["name"]?.ToString() ?? "Phone");

            await p.WriteAsync(new JsonObject { ["t"] = "nyx_pair_reveal", ["pub"] = Convert.ToBase64String(pubLaptop) }, t.Token);

            byte[] z = Agree(ecdh, pubPhone);
            p._secret = Secret(z, pubPhone, pubLaptop);
            p.Code = Code6(z, pubPhone, pubLaptop);
            // the phone answers whenever its owner taps Allow or Deny; start listening now
            p._phoneAnswer = p._reader.ReadLineAsync(CancellationToken.None);
            return p;
        }
        catch
        {
            p.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The person confirmed the codes match. Waits (up to two minutes) for the phone's own
    /// Allow, then saves the pairing. Returns false if the phone said no or never answered.
    /// </summary>
    public async Task<bool> ConfirmAsync()
    {
        await WriteAsync(new JsonObject { ["t"] = "nyx_pair_ok" }, CancellationToken.None);
        var answer = _phoneAnswer ?? Task.FromResult<string?>(null);
        if (await Task.WhenAny(answer, Task.Delay(TimeSpan.FromMinutes(2))) != answer) return false;
        string? line = await answer;
        if (line == null) return false;
        var o = JsonNode.Parse(line) as JsonObject;
        if (o?["t"]?.ToString() != "nyx_pair_ok") return false;
        PairStore.Add(PhoneId, PhoneName, Convert.ToBase64String(_secret));
        return true;
    }

    public async Task CancelAsync()
    {
        try { await WriteAsync(new JsonObject { ["t"] = "nyx_pair_no" }, CancellationToken.None); } catch { }
        Dispose();
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_secret);
        try { _tcp.Dispose(); } catch { }
    }

    async Task<JsonObject> ReadAsync(CancellationToken ct)
    {
        string line = await _reader.ReadLineAsync(ct) ?? throw new IOException("The phone closed the connection.");
        if (line.Length > 16 * 1024) throw new InvalidDataException("line too long");
        return JsonNode.Parse(line) as JsonObject ?? throw new InvalidDataException("not a message");
    }

    async Task WriteAsync(JsonObject o, CancellationToken ct)
    {
        var data = Encoding.UTF8.GetBytes(o.ToJsonString() + "\n");
        await _stream.WriteAsync(data, ct);
        await _stream.FlushAsync(ct);
    }

    static string Clean(string s)
    {
        s = new string(s.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return s.Length == 0 ? "Phone" : s.Length > 64 ? s[..64] : s;
    }

    // ------------------------------------------------------------ crypto (shared with tests)

    internal static byte[] PublicBytes(ECDiffieHellman ecdh)
    {
        var q = ecdh.ExportParameters(false).Q;
        return new byte[] { 4 }.Concat(q.X!).Concat(q.Y!).ToArray();
    }

    /// <summary>Raw shared X coordinate. Throws for anything that isn't a valid P-256 point.</summary>
    internal static byte[] Agree(ECDiffieHellman mine, byte[] peer)
    {
        if (peer.Length != 65 || peer[0] != 4) throw new CryptographicException("bad public key");
        try
        {
            using var other = ECDiffieHellman.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = peer[1..33], Y = peer[33..65] }
            });
            return mine.DeriveRawSecretAgreement(other.PublicKey);
        }
        catch (Exception e) when (e is not CryptographicException)
        {
            // Windows rejects off-curve points with PlatformNotSupportedException; same meaning
            throw new CryptographicException("bad public key", e);
        }
    }

    static byte[] Hash(string label, byte[] z, byte[] pubPhone, byte[] pubLaptop) =>
        SHA256.HashData(Encoding.ASCII.GetBytes(label).Concat(z).Concat(pubPhone).Concat(pubLaptop).ToArray());

    internal static byte[] Secret(byte[] z, byte[] pubPhone, byte[] pubLaptop) => Hash("nyx pair v1", z, pubPhone, pubLaptop);

    internal static string Code6(byte[] z, byte[] pubPhone, byte[] pubLaptop)
    {
        var h = Hash("nyx code v1", z, pubPhone, pubLaptop);
        uint n = (uint)(h[0] << 24 | h[1] << 16 | h[2] << 8 | h[3]);
        return (n % 1_000_000).ToString("D6");
    }
}
