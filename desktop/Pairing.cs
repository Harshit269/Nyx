using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PhoneBridge;

public sealed record PairedPhone(string Id, string Name, string Secret);

/// <summary>
/// Phones this laptop has paired with, and this laptop's own id. A pairing is made once,
/// over the (already encrypted) Bluetooth link: the laptop makes a random 32-byte secret
/// and hands it to the phone. From then on Wi-Fi links must prove they hold that secret.
/// Stored per Windows user in %APPDATA%\Nyx\pairs.json.
/// </summary>
public static class PairStore
{
    static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nyx", "pairs.json");
    static string FilePath => _testPath ?? DefaultPath;
    static readonly object Gate = new();
    static string _laptopId = "";
    static List<PairedPhone> _phones = new();
    static bool _loaded;

    public static string LaptopId { get { Load(); return _laptopId; } }

    public static IReadOnlyList<PairedPhone> Phones { get { Load(); lock (Gate) return _phones.ToList(); } }

    public static PairedPhone? Find(string phoneId)
    {
        Load();
        lock (Gate) return _phones.FirstOrDefault(p => p.Id == phoneId);
    }

    /// <summary>Makes (or replaces) the pairing for a phone and returns it.</summary>
    public static PairedPhone Pair(string phoneId, string name)
    {
        Load();
        var p = new PairedPhone(phoneId, name, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        lock (Gate)
        {
            _phones.RemoveAll(x => x.Id == phoneId);
            _phones.Add(p);
        }
        Save();
        return p;
    }

    /// <summary>Saves a pairing whose secret was agreed elsewhere (Wi-Fi pairing).</summary>
    public static PairedPhone Add(string phoneId, string name, string secretBase64)
    {
        Load();
        var p = new PairedPhone(phoneId, name, secretBase64);
        lock (Gate)
        {
            _phones.RemoveAll(x => x.Id == phoneId);
            _phones.Add(p);
        }
        Save();
        return p;
    }

    public static void Forget(string phoneId)
    {
        Load();
        lock (Gate) _phones.RemoveAll(x => x.Id == phoneId);
        Save();
    }

    static void Load()
    {
        lock (Gate)
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (File.Exists(FilePath))
                {
                    var root = JsonNode.Parse(File.ReadAllText(FilePath))!.AsObject();
                    _laptopId = root["laptop"]?.ToString() ?? "";
                    bool plainOnDisk = false;
                    if (root["phones"] is JsonArray arr)
                        foreach (var n in arr.OfType<JsonObject>())
                        {
                            plainOnDisk |= n["secret"] != null;
                            string? secret = n["sealed"] is JsonNode s ? Unseal(s.ToString()) : n["secret"]?.ToString();
                            if (secret != null)
                                _phones.Add(new PairedPhone(n["id"]!.ToString(), n["name"]?.ToString() ?? "", secret));
                        }
                    // a file from before secrets were sealed: rewrite it sealed straight away
                    if (plainOnDisk && _laptopId.Length > 0) SaveLocked();
                }
            }
            catch { _phones.Clear(); }
            if (_laptopId.Length == 0)
            {
                _laptopId = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
                SaveLocked();
            }
        }
    }

    static void Save() { lock (Gate) SaveLocked(); }

    static void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var root = new JsonObject
            {
                ["laptop"] = _laptopId,
                ["phones"] = new JsonArray(_phones.Select(p => (JsonNode)new JsonObject
                {
                    ["id"] = p.Id, ["name"] = p.Name, ["sealed"] = Seal(p.Secret)
                }).ToArray())
            };
            File.WriteAllText(FilePath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    // The secrets on disk are sealed with Windows DPAPI for the current user: a copied
    // pairs.json, or another account on the same laptop, cannot use them.
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Nyx pairing secret v1");

    internal static string Seal(string secretBase64) =>
        Convert.ToBase64String(ProtectedData.Protect(Convert.FromBase64String(secretBase64), Entropy, DataProtectionScope.CurrentUser));

    internal static string? Unseal(string sealedBase64)
    {
        try
        {
            return Convert.ToBase64String(ProtectedData.Unprotect(Convert.FromBase64String(sealedBase64), Entropy, DataProtectionScope.CurrentUser));
        }
        catch
        {
            return null; // made by another user or machine: treat the phone as unpaired
        }
    }

    /// <summary>For tests: point the store at another file and start fresh.</summary>
    internal static void UseFileForTests(string path)
    {
        lock (Gate)
        {
            _testPath = path;
            _loaded = false;
            _laptopId = "";
            _phones = new();
        }
    }

    static string? _testPath;
}

/// <summary>
/// The Wi-Fi handshake and the encryption that follows it. Both sides hold the pairing
/// secret; each sends a random nonce and proves knowledge of the secret with an HMAC
/// over both nonces, so a replayed or forged handshake fails. The session key is
/// HMAC(secret, "K" | phoneNonce | laptopNonce), and every line after that is
/// AES-256-GCM with a per-direction counter nonce. The phone app mirrors this exactly.
/// </summary>
public sealed class LinkCipher
{
    public const uint ToPhone = 1, ToLaptop = 2;

    readonly AesGcm _aes;
    ulong _sendCounter, _recvCounter;
    readonly uint _sendDir, _recvDir;

    public LinkCipher(byte[] key, uint sendDir, uint recvDir)
    {
        _aes = new AesGcm(key, 16);
        _sendDir = sendDir;
        _recvDir = recvDir;
    }

    static byte[] Nonce(uint dir, ulong counter)
    {
        var n = new byte[12];
        n[0] = (byte)(dir >> 24); n[1] = (byte)(dir >> 16); n[2] = (byte)(dir >> 8); n[3] = (byte)dir;
        for (int i = 0; i < 8; i++) n[4 + i] = (byte)(counter >> (56 - 8 * i));
        return n;
    }

    /// <summary>Plain JSON text in, one base64 line (ciphertext + tag) out.</summary>
    public string Seal(string plain)
    {
        var pt = Encoding.UTF8.GetBytes(plain);
        var ct = new byte[pt.Length];
        var tag = new byte[16];
        lock (this) _aes.Encrypt(Nonce(_sendDir, _sendCounter++), pt, ct, tag);
        var line = new byte[ct.Length + 16];
        ct.CopyTo(line, 0);
        tag.CopyTo(line, ct.Length);
        return Convert.ToBase64String(line);
    }

    /// <summary>Throws when a line was tampered with, replayed or reordered.</summary>
    public string Open(string line)
    {
        var data = Convert.FromBase64String(line);
        if (data.Length < 16) throw new CryptographicException("short frame");
        var ct = data.AsSpan(0, data.Length - 16);
        var tag = data.AsSpan(data.Length - 16);
        var pt = new byte[ct.Length];
        _aes.Decrypt(Nonce(_recvDir, _recvCounter++), ct, tag, pt);
        return Encoding.UTF8.GetString(pt);
    }

    public static byte[] Mac(byte[] secret, string label, byte[] a, byte[] b)
    {
        using var h = new HMACSHA256(secret);
        var msg = new byte[1 + a.Length + b.Length];
        msg[0] = (byte)label[0];
        a.CopyTo(msg, 1);
        b.CopyTo(msg, 1 + a.Length);
        return h.ComputeHash(msg);
    }
}
