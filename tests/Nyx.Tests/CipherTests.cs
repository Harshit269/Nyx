using System.Security.Cryptography;
using PhoneBridge;
using Xunit;

namespace Nyx.Tests;

/// <summary>
/// Known answers were produced independently with Python's "cryptography" package; the
/// Android tests check the same numbers, so all three implementations agree byte for byte.
/// </summary>
public class CipherTests
{
    static readonly byte[] Secret = Convert.FromBase64String("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=");
    static readonly byte[] NPhone = Convert.FromBase64String("oKGio6SlpqeoqaqrrK2urw==");
    static readonly byte[] NLaptop = Convert.FromBase64String("UFFSU1RVVldYWVpbXF1eXw==");
    const string Key = "sSb0CLdzQLjlR/ZdKLaRKkVlgBTqQ2w8Og/MW5a6E6Y=";

    [Fact]
    public void Handshake_macs_and_session_key_match_known_answers()
    {
        Assert.Equal("J7aVjZQSbaEMSC8a5qeqzIbx3pec63n7GcAYVU+1f44=", Convert.ToBase64String(LinkCipher.Mac(Secret, "L", NPhone, NLaptop)));
        Assert.Equal("4uqfaFkAFqzbNblOXk2/nYxxQU0Vll2NT6Pg03ek204=", Convert.ToBase64String(LinkCipher.Mac(Secret, "P", NLaptop, NPhone)));
        Assert.Equal(Key, Convert.ToBase64String(LinkCipher.Mac(Secret, "K", NPhone, NLaptop)));
    }

    [Fact]
    public void Laptop_seals_exactly_like_the_reference()
    {
        var c = new LinkCipher(Convert.FromBase64String(Key), LinkCipher.ToPhone, LinkCipher.ToLaptop);
        Assert.Equal("6iJptetzGc5g1ne9j3s0+ruY/rtOsfbH8PCllw==", c.Seal("{\"t\":\"ping\"}"));
        Assert.Equal("j+RykuK9CZ69Yz7re9sS/44a+l57W7lCD0wLciFFDmHptn2ipzk+grKzOQ==", c.Seal("{\"t\":\"get_sms\",\"limit\":400}"));
    }

    [Fact]
    public void Laptop_opens_a_reference_phone_frame()
    {
        var c = new LinkCipher(Convert.FromBase64String(Key), LinkCipher.ToPhone, LinkCipher.ToLaptop);
        Assert.Equal("{\"t\":\"hello\",\"phone\":\"p1\"}", c.Open("5xtl24K9N/iqaoG9L7W+M8CSQfamm4tFHl1DkwnoqS4fjEDRuwnivwCC"));
    }

    static (LinkCipher laptop, LinkCipher phone) Pair()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        return (new LinkCipher(key, LinkCipher.ToPhone, LinkCipher.ToLaptop),
                new LinkCipher(key, LinkCipher.ToLaptop, LinkCipher.ToPhone));
    }

    [Fact]
    public void Round_trip_both_ways_including_unicode_and_large_text()
    {
        var (laptop, phone) = Pair();
        string big = new string('x', 200_000) + " ₹450 · नमस्ते · 😀";
        Assert.Equal(big, phone.Open(laptop.Seal(big)));
        Assert.Equal("{}", laptop.Open(phone.Seal("{}")));
        Assert.Equal("", laptop.Open(phone.Seal("")));
    }

    [Fact]
    public void Tampering_replay_reordering_and_wrong_key_all_fail()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var laptop = new LinkCipher(key, LinkCipher.ToPhone, LinkCipher.ToLaptop);
        string a = laptop.Seal("first"), b = laptop.Seal("second");

        // tamper
        var bytes = Convert.FromBase64String(a);
        bytes[3] ^= 0x80;
        Assert.ThrowsAny<CryptographicException>(() => new LinkCipher(key, LinkCipher.ToLaptop, LinkCipher.ToPhone).Open(Convert.ToBase64String(bytes)));

        // reorder: the second frame cannot be read first
        Assert.ThrowsAny<CryptographicException>(() => new LinkCipher(key, LinkCipher.ToLaptop, LinkCipher.ToPhone).Open(b));

        // replay: the same frame twice
        var phone = new LinkCipher(key, LinkCipher.ToLaptop, LinkCipher.ToPhone);
        Assert.Equal("first", phone.Open(a));
        Assert.ThrowsAny<CryptographicException>(() => phone.Open(a));

        // reflection: a frame the laptop sent cannot be bounced back to the laptop
        var laptop2 = new LinkCipher(key, LinkCipher.ToPhone, LinkCipher.ToLaptop);
        Assert.ThrowsAny<CryptographicException>(() => laptop2.Open(new LinkCipher(key, LinkCipher.ToPhone, LinkCipher.ToLaptop).Seal("x")));

        // wrong key
        Assert.ThrowsAny<CryptographicException>(() => new LinkCipher(RandomNumberGenerator.GetBytes(32), LinkCipher.ToLaptop, LinkCipher.ToPhone).Open(a));

        // truncated and junk frames
        Assert.ThrowsAny<Exception>(() => new LinkCipher(key, LinkCipher.ToLaptop, LinkCipher.ToPhone).Open("AAAA"));
        Assert.ThrowsAny<Exception>(() => new LinkCipher(key, LinkCipher.ToLaptop, LinkCipher.ToPhone).Open("not base64 !!"));
    }

    [Fact]
    public void Every_bit_flip_in_a_frame_is_caught()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var frame = Convert.FromBase64String(new LinkCipher(key, LinkCipher.ToPhone, LinkCipher.ToLaptop).Seal("{\"t\":\"pc\",\"action\":\"shutdown\"}"));
        for (int i = 0; i < frame.Length * 8; i++)
        {
            var bad = (byte[])frame.Clone();
            bad[i / 8] ^= (byte)(1 << (i % 8));
            var phone = new LinkCipher(key, LinkCipher.ToLaptop, LinkCipher.ToPhone);
            Assert.ThrowsAny<CryptographicException>(() => phone.Open(Convert.ToBase64String(bad)));
        }
    }

    [Fact]
    public void Proofs_use_different_labels_so_one_cannot_stand_in_for_another()
    {
        // an attacker who gets the laptop to prove itself ("L") must not be able to reuse that as the phone's proof ("P")
        Assert.NotEqual(LinkCipher.Mac(Secret, "L", NPhone, NLaptop), LinkCipher.Mac(Secret, "P", NPhone, NLaptop));
        Assert.NotEqual(LinkCipher.Mac(Secret, "L", NPhone, NLaptop), LinkCipher.Mac(Secret, "P", NLaptop, NPhone));
        Assert.NotEqual(LinkCipher.Mac(Secret, "K", NPhone, NLaptop), LinkCipher.Mac(Secret, "L", NPhone, NLaptop));
    }
}
