using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Nyx.Tests;

/// <summary>
/// A phone written from the protocol description with raw HMAC and AES-GCM, deliberately
/// not reusing the app's LinkCipher, so the laptop is tested against a second implementation.
/// Each "behaviour" makes it misbehave in a particular way.
/// </summary>
public sealed class FakePhone : IDisposable
{
    public enum Mode { Honest, WrongSecret, WrongPhoneId, Silent, Garbage, BadProofAfterHello, TamperAfterLink, HugeLine }

    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    readonly byte[] _secret;
    readonly string _phoneId;
    readonly Mode _mode;
    public IPEndPoint EndPoint => (IPEndPoint)_listener.LocalEndpoint;
    public readonly TaskCompletionSource<List<JsonObject>> Received = new();
    public readonly List<JsonObject> Inbox = new();
    TcpClient? _client;

    public FakePhone(string phoneId, byte[] secret, Mode mode = Mode.Honest)
    {
        _phoneId = phoneId;
        _secret = secret;
        _mode = mode;
        _listener.Start();
        _ = Task.Run(RunAsync);
    }

    static byte[] Mac(byte[] secret, string label, byte[] a, byte[] b)
    {
        using var h = new HMACSHA256(secret);
        return h.ComputeHash(new[] { (byte)label[0] }.Concat(a).Concat(b).ToArray());
    }

    static byte[] Nonce(uint dir, ulong n)
    {
        var x = new byte[12];
        x[0] = (byte)(dir >> 24); x[1] = (byte)(dir >> 16); x[2] = (byte)(dir >> 8); x[3] = (byte)dir;
        for (int i = 0; i < 8; i++) x[4 + i] = (byte)(n >> (56 - 8 * i));
        return x;
    }

    async Task RunAsync()
    {
        try
        {
            _client = await _listener.AcceptTcpClientAsync();
            var s = _client.GetStream();
            var r = new StreamReader(s, Encoding.UTF8);
            async Task Line(string text) { var b = Encoding.UTF8.GetBytes(text + "\n"); await s.WriteAsync(b); await s.FlushAsync(); }

            if (_mode == Mode.Silent) { await Task.Delay(20_000); return; }
            if (_mode == Mode.Garbage) { await Line("this is not json at all"); await Task.Delay(3000); return; }

            var nPhone = RandomNumberGenerator.GetBytes(16);
            string id = _mode == Mode.WrongPhoneId ? "someone-else" : _phoneId;
            await Line(new JsonObject { ["t"] = "nyx_hello", ["phone"] = id, ["n"] = Convert.ToBase64String(nPhone) }.ToJsonString());

            var auth = JsonNode.Parse(await r.ReadLineAsync() ?? "")!.AsObject();
            var nLaptop = Convert.FromBase64String(auth["n"]!.ToString());
            byte[] secret = _mode == Mode.WrongSecret ? RandomNumberGenerator.GetBytes(32) : _secret;
            byte[] proof = Mac(secret, "P", nLaptop, nPhone);
            if (_mode == Mode.BadProofAfterHello) proof[0] ^= 1;
            await Line(new JsonObject { ["t"] = "nyx_auth_ok", ["mac"] = Convert.ToBase64String(proof) }.ToJsonString());

            using var aes = new AesGcm(Mac(secret, "K", nPhone, nLaptop), 16);
            ulong sent = 0, recv = 0;
            async Task Send(JsonObject o)
            {
                var pt = Encoding.UTF8.GetBytes(o.ToJsonString());
                var ct = new byte[pt.Length]; var tag = new byte[16];
                aes.Encrypt(Nonce(2, sent++), pt, ct, tag);
                var frame = ct.Concat(tag).ToArray();
                if (_mode == Mode.TamperAfterLink && sent == 2) frame[0] ^= 0x40;
                await Line(Convert.ToBase64String(frame));
            }

            await Send(new JsonObject { ["t"] = "hello", ["phone"] = _phoneId, ["device"] = "Fake phone", ["android"] = "15", ["ip"] = "1.2.3.4 --record=C:\\x.mp4" });
            if (_mode == Mode.HugeLine)
            {
                await Line(new string('A', 5 * 1024 * 1024)); // over the 4 MB line limit
            }
            await Send(new JsonObject { ["t"] = "status", ["battery"] = 42, ["signal"] = 3, ["wifi"] = true, ["bt"] = true });

            // read what the laptop says back
            while (true)
            {
                var line = await r.ReadLineAsync();
                if (line == null) break;
                var data = Convert.FromBase64String(line);
                var pt = new byte[data.Length - 16];
                aes.Decrypt(Nonce(1, recv++), data.AsSpan(0, data.Length - 16), data.AsSpan(data.Length - 16), pt);
                lock (Inbox) Inbox.Add(JsonNode.Parse(Encoding.UTF8.GetString(pt))!.AsObject());
                if (Inbox.Count >= 3) Received.TrySetResult(Inbox.ToList());
            }
        }
        catch (Exception e)
        {
            Received.TrySetException(e);
        }
    }

    public void Dispose()
    {
        try { _client?.Dispose(); } catch { }
        _listener.Stop();
    }
}
