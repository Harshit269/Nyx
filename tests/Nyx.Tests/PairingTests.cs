using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using PhoneBridge;
using Xunit;

namespace Nyx.Tests;

/// <summary>
/// Wi-Fi pairing. Known answers were made with Python's "cryptography" package; the Android
/// tests check the same values, so both apps derive the same secret and the same 6 digits.
/// </summary>
[Collection("store")]
public class PairingTests : IDisposable
{
    const string DPhone = "CTjq8nlFexnyP4p7VUA/1DJ72EYm7UcOYOXIHye29B4=";
    const string DLaptop = "PuEFDYaXt8bOd5Nc+776XH7gPHC3g1d39RU3lLm04Qo=";
    const string PubPhone = "BHQJW89MN0HtxsmNKaCkh1Hl7IDwuf2L0QcHbvkREp+qqhWIlfytCdHt7YR6JBmkyZ5miPOkGV20jlHHI7qvn2k=";
    const string PubLaptop = "BFKBEjGRoZnabJNfZFU+wloDfTKap0uXWZk0zVFzOSV326PdHDiNb1X3zegV0+GX/aZSe6iP1qWsUJ0gLDpmi3E=";
    const string Z = "2nSyhcOA/VIZOgMNCdsrGYx5IzYdXgV/Sb56UFQvZ9U=";

    readonly string _dir = Path.Combine(Path.GetTempPath(), "nyx-tests-" + Guid.NewGuid().ToString("N"));

    public PairingTests()
    {
        Directory.CreateDirectory(_dir);
        PairStore.UseFileForTests(Path.Combine(_dir, "pairs.json"));
    }

    public void Dispose()
    {
        PairStore.UseFileForTests(Path.Combine(_dir, "unused.json"));
        try { Directory.Delete(_dir, true); } catch { }
    }

    static ECDiffieHellman Load(string d, string pub)
    {
        var q = Convert.FromBase64String(pub);
        return ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = Convert.FromBase64String(d),
            Q = new ECPoint { X = q[1..33], Y = q[33..65] }
        });
    }

    [Fact]
    public void Agreement_secret_and_code_match_the_reference()
    {
        using var laptop = Load(DLaptop, PubLaptop);
        var pubP = Convert.FromBase64String(PubPhone);
        var pubL = Convert.FromBase64String(PubLaptop);
        Assert.Equal(PubLaptop, Convert.ToBase64String(WifiPairing.PublicBytes(laptop)));
        var z = WifiPairing.Agree(laptop, pubP);
        Assert.Equal(Z, Convert.ToBase64String(z));
        Assert.Equal("lGw+Bv709sEX+RCyp/S/iuwavE3Oy0pCvQZ2ymkIS6A=", Convert.ToBase64String(WifiPairing.Secret(z, pubP, pubL)));
        Assert.Equal("055367", WifiPairing.Code6(z, pubP, pubL));
        Assert.Equal("AxnIZJi8eRfYCP9Cz8igQcwfNg4SfOJ+51Tf8aDT8Mg=", Convert.ToBase64String(SHA256.HashData(pubL)));
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 4, 1, 2, 3 })]
    public void Malformed_public_keys_are_refused(byte[] key)
    {
        using var mine = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        Assert.ThrowsAny<CryptographicException>(() => WifiPairing.Agree(mine, key));
    }

    [Fact]
    public void A_point_that_is_not_on_the_curve_is_refused()
    {
        using var mine = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var bad = new byte[65];
        bad[0] = 4;
        bad[1] = 1; // (1, 0) is not a P-256 point: an invalid-curve attack would try this
        Assert.ThrowsAny<CryptographicException>(() => WifiPairing.Agree(mine, bad));
    }

    // ------------------------------------------------------------ the whole exchange

    /// <summary>A phone that pairs (written from the protocol, not reusing app code).</summary>
    sealed class PairingPhone : IDisposable
    {
        public readonly TcpListener Listener = new(IPAddress.Loopback, 0);
        public string? Code;
        public byte[]? Secret;
        public bool CommitOk;
        public string Answer = "nyx_pair_ok";
        public bool SendWrongId;
        public IPEndPoint EndPoint => (IPEndPoint)Listener.LocalEndpoint;

        public PairingPhone() { Listener.Start(); }

        public async Task RunAsync()
        {
            using var c = await Listener.AcceptTcpClientAsync();
            var s = c.GetStream();
            var r = new StreamReader(s, Encoding.UTF8);
            async Task W(JsonObject o) { var b = Encoding.UTF8.GetBytes(o.ToJsonString() + "\n"); await s.WriteAsync(b); }
            await W(new JsonObject { ["t"] = "nyx_hello", ["phone"] = "pp-1", ["n"] = Convert.ToBase64String(new byte[16]) });
            var pair = JsonNode.Parse((await r.ReadLineAsync())!)!.AsObject();
            var commit = Convert.FromBase64String(pair["commit"]!.ToString());
            using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var q = ecdh.ExportParameters(false).Q;
            var pubP = new byte[] { 4 }.Concat(q.X!).Concat(q.Y!).ToArray();
            await W(new JsonObject { ["t"] = "nyx_pair_pub", ["phone"] = SendWrongId ? "other" : "pp-1", ["name"] = "Pairing\u0007 Phone", ["pub"] = Convert.ToBase64String(pubP) });
            if (SendWrongId) return;
            var reveal = JsonNode.Parse((await r.ReadLineAsync())!)!.AsObject();
            var pubL = Convert.FromBase64String(reveal["pub"]!.ToString());
            CommitOk = SHA256.HashData(pubL).SequenceEqual(commit);
            using var other = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = pubL[1..33], Y = pubL[33..65] } });
            var z = ecdh.DeriveRawSecretAgreement(other.PublicKey);
            Secret = SHA256.HashData(Encoding.ASCII.GetBytes("nyx pair v1").Concat(z).Concat(pubP).Concat(pubL).ToArray());
            var h = SHA256.HashData(Encoding.ASCII.GetBytes("nyx code v1").Concat(z).Concat(pubP).Concat(pubL).ToArray());
            Code = ((uint)(h[0] << 24 | h[1] << 16 | h[2] << 8 | h[3]) % 1_000_000).ToString("D6");
            await W(new JsonObject { ["t"] = Answer });
            await r.ReadLineAsync(); // the laptop's ok / no
        }

        public void Dispose() => Listener.Stop();
    }

    [Fact]
    public async Task Both_sides_show_the_same_code_and_the_pairing_is_saved_with_the_same_secret()
    {
        using var phone = new PairingPhone();
        var run = phone.RunAsync();
        using var pairing = await WifiPairing.StartAsync(phone.EndPoint, "pp-1");
        Assert.Matches("^[0-9]{6}$", pairing.Code);
        Assert.Equal("Pairing Phone", pairing.PhoneName); // control characters stripped
        Assert.True(await pairing.ConfirmAsync());
        await run;
        Assert.True(phone.CommitOk);
        Assert.Equal(phone.Code, pairing.Code);
        Assert.Equal(Convert.ToBase64String(phone.Secret!), PairStore.Find("pp-1")!.Secret);
    }

    [Fact]
    public async Task If_the_phone_says_no_nothing_is_saved()
    {
        using var phone = new PairingPhone { Answer = "nyx_pair_no" };
        var run = phone.RunAsync();
        using var pairing = await WifiPairing.StartAsync(phone.EndPoint, "pp-1");
        Assert.False(await pairing.ConfirmAsync());
        await run;
        Assert.Null(PairStore.Find("pp-1"));
    }

    [Fact]
    public async Task A_different_phone_answering_is_refused()
    {
        using var phone = new PairingPhone { SendWrongId = true };
        var run = phone.RunAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => WifiPairing.StartAsync(phone.EndPoint, "pp-1"));
        Assert.Null(PairStore.Find("pp-1"));
    }

    [Fact]
    public async Task Cancelling_saves_nothing()
    {
        using var phone = new PairingPhone();
        var run = phone.RunAsync();
        var pairing = await WifiPairing.StartAsync(phone.EndPoint, "pp-1");
        await pairing.CancelAsync();
        Assert.Null(PairStore.Find("pp-1"));
    }
}
