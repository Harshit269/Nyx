using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using PhoneBridge;
using Xunit;

namespace Nyx.Tests;

[Collection("store")]
public class PairStoreTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "nyx-tests-" + Guid.NewGuid().ToString("N"));
    string FilePath => Path.Combine(_dir, "pairs.json");

    public PairStoreTests()
    {
        Directory.CreateDirectory(_dir);
        PairStore.UseFileForTests(FilePath);
    }

    public void Dispose()
    {
        PairStore.UseFileForTests(Path.Combine(_dir, "unused.json"));
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Secrets_are_random_256_bit_and_never_written_in_plain_text()
    {
        var a = PairStore.Pair("p1", "Phone A");
        var b = PairStore.Pair("p2", "Phone B");
        Assert.Equal(32, Convert.FromBase64String(a.Secret).Length);
        Assert.NotEqual(a.Secret, b.Secret);
        string onDisk = File.ReadAllText(FilePath);
        Assert.DoesNotContain(a.Secret, onDisk);
        Assert.DoesNotContain(b.Secret, onDisk);
        Assert.DoesNotContain("\"secret\"", onDisk);
    }

    [Fact]
    public void Pairings_survive_a_restart_and_the_laptop_id_stays_put()
    {
        var a = PairStore.Pair("p1", "Phone A");
        string id = PairStore.LaptopId;
        PairStore.UseFileForTests(FilePath); // simulate a fresh start reading the same file
        Assert.Equal(id, PairStore.LaptopId);
        Assert.Equal(a.Secret, PairStore.Find("p1")!.Secret);
    }

    [Fact]
    public void Repairing_replaces_the_old_secret_and_forget_removes_it()
    {
        var first = PairStore.Pair("p1", "Phone A");
        var second = PairStore.Pair("p1", "Phone A");
        Assert.NotEqual(first.Secret, second.Secret);
        Assert.Single(PairStore.Phones);
        PairStore.Forget("p1");
        Assert.Null(PairStore.Find("p1"));
        PairStore.UseFileForTests(FilePath);
        Assert.Null(PairStore.Find("p1"));
    }

    [Fact]
    public void An_old_plain_text_file_is_sealed_on_first_load()
    {
        string secret = Convert.ToBase64String(new byte[32].Select((_, i) => (byte)i).ToArray());
        File.WriteAllText(FilePath, "{\"laptop\":\"lap1\",\"phones\":[{\"id\":\"p1\",\"name\":\"Old\",\"secret\":\"" + secret + "\"}]}");
        PairStore.UseFileForTests(FilePath);
        Assert.Equal(secret, PairStore.Find("p1")!.Secret);
        string onDisk = File.ReadAllText(FilePath);
        Assert.DoesNotContain(secret, onDisk);
        Assert.Contains("\"sealed\"", onDisk);
        Assert.Equal("lap1", PairStore.LaptopId);
    }

    [Fact]
    public void A_corrupt_file_means_no_pairings_not_a_crash()
    {
        File.WriteAllText(FilePath, "{ this is : not json");
        PairStore.UseFileForTests(FilePath);
        Assert.Empty(PairStore.Phones);
        Assert.NotEmpty(PairStore.LaptopId);
    }

    [Fact]
    public void A_secret_sealed_elsewhere_cannot_be_used_here()
    {
        // a sealed blob that this Windows user did not make (random bytes) must not unseal
        Assert.Null(PairStore.Unseal(Convert.ToBase64String(new byte[200])));
        Assert.Null(PairStore.Unseal("not base64"));
        var s = PairStore.Seal(Convert.ToBase64String(new byte[32]));
        Assert.Equal(Convert.ToBase64String(new byte[32]), PairStore.Unseal(s));
    }
}

public class InputCheckTests
{
    [Theory]
    [InlineData("192.168.1.24", true)]
    [InlineData("10.0.0.1", true)]
    [InlineData("1.2.3.4 --record=C:\\x.mp4", false)]   // extra scrcpy flag smuggled in
    [InlineData("1.2.3.4\" --push-target=/x", false)]
    [InlineData("1.2.3.4:5555", false)]
    [InlineData("999.1.1.1", false)]
    [InlineData("::1", false)]
    [InlineData("1", false)]                              // IPAddress.TryParse would accept this one
    [InlineData("", false)]
    [InlineData("192.168.1.24\n", false)]
    [InlineData("localhost", false)]
    public void Only_plain_IPv4_addresses_pass(string ip, bool ok) => Assert.Equal(ok, InputCheck.IsIPv4(ip));

    [Theory]
    [InlineData("+91 98765 43210", true)]
    [InlineData("(022) 6953-9639", true)]
    [InlineData("121", true)]
    [InlineData("AD-SBIUPI-S", false)]   // business sender: texts only
    [InlineData("JM-ZOMATO-S", false)]
    [InlineData("VK-650022", false)]
    [InlineData("12", false)]
    [InlineData("", false)]
    public void Only_callable_numbers_count_as_dialable(string n, bool ok) => Assert.Equal(ok, InputCheck.IsDialable(n));

    [Theory]
    [InlineData("com.whatsapp", true)]
    [InlineData("com.google.android.apps.messaging", true)]
    [InlineData("in.org.npci.upiapp", true)]
    [InlineData("com.x,com.y", false)]   // would split the stored list
    [InlineData("a b", false)]
    [InlineData("com.x\"", false)]
    [InlineData("", false)]
    public void Only_package_names_can_be_switched_off(string pkg, bool ok) => Assert.Equal(ok, NotifApps.Valid(pkg));

    [Fact]
    public void The_switched_off_list_reads_back_and_skips_junk() =>
        Assert.Equal(new HashSet<string> { "com.a", "com.b" }, NotifApps.Parse(" com.a, ,com.b,bad name,"));

    [Fact]
    public void Today_dots_put_each_arrival_in_its_hour_newest_last()
    {
        var now = new DateTime(2026, 11, 27, 9, 30, 0);
        var times = new[] { now.AddMinutes(-5), now.AddMinutes(-59), now.AddMinutes(-61), now.AddHours(-11.9), now.AddHours(-12.1), now.AddMinutes(3) };
        Assert.Equal(new[] { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2 }, MainWindow.HourBars(times, now));
    }

    [Theory]
    [InlineData("#E8573A", true)]
    [InlineData("#e8573a", true)]
    [InlineData("E8573A", false)]
    [InlineData("#E8573", false)]
    [InlineData("#E8573AFF", false)]
    [InlineData("red", false)]
    public void Only_six_digit_hex_colours_pass(string c, bool ok) => Assert.Equal(ok, InputCheck.IsHexColour(c));
}

public class AppTagTests
{
    [Theory]
    [InlineData("WhatsApp", "WA")]
    [InlineData("Gmail", "GM")]
    [InlineData("Google Pay", "GPay")]
    [InlineData("Meera Iyer", "MI")]
    [InlineData("Uber", "UB")]           // hand-set code
    [InlineData("+91 98200 11223", "#")]
    [InlineData("", "#")]
    [InlineData("   ", "#")]
    [InlineData("😀 Party", "Pa")]        // emoji skipped, one word gives two letters
    [InlineData("Swiggy Instamart", "SI")]
    [InlineData("Zepto", "Ze")]
    public void Codes(string name, string code) => Assert.Equal(code, AppTag.Code(name));

    [Fact]
    public void Hue_is_stable_and_in_range()
    {
        foreach (var n in new[] { "", "a", "Some App", "नमस्ते", new string('z', 10_000) })
        {
            int h = AppTag.Hue(n);
            Assert.InRange(h, 0, AppTag.Hues - 1);
            Assert.Equal(h, AppTag.Hue(n));
        }
    }
}

[Collection("store")]
public class DiscoveryTests
{
    static void Beacon(string payload)
    {
        using var u = new UdpClient();
        var b = Encoding.UTF8.GetBytes(payload);
        u.Send(b, b.Length, new IPEndPoint(IPAddress.Loopback, LanDiscovery.BeaconPort));
    }

    [Fact]
    public async Task Valid_beacons_are_heard_and_junk_is_ignored()
    {
        using var lan = new LanDiscovery();
        lan.Start();
        Assert.True(lan.Running);
        var seen = new List<(string, IPEndPoint)>();
        // only beacons this test sends count; other Nyx phones on the network may be talking too
        string tag = Guid.NewGuid().ToString("N")[..8];
        lan.PhoneSeen += (id, ep, name) => { if (id.StartsWith(tag) || id == "x") lock (seen) seen.Add((id, ep)); };

        Beacon("garbage");
        Beacon("{\"nyx\":2,\"phone\":\"x\",\"port\":1}");          // wrong version
        Beacon("{\"nyx\":1,\"phone\":\"\",\"port\":1}");           // no id
        Beacon("{\"nyx\":1,\"phone\":\"x\",\"port\":70000}");      // bad port
        Beacon("{\"nyx\":1,\"phone\":\"x\",\"port\":\"47823\"}");  // port as text
        Beacon(new string('{', 600));                             // oversized
        Beacon("{\"nyx\":1,\"phone\":\"" + tag + "-phone\",\"port\":47823}");
        await Task.Delay(800);

        lock (seen)
        {
            Assert.True(seen.Count == 1, "heard: " + string.Join(" | ", seen.Select(s => s.Item1 + "@" + s.Item2)));
            Assert.Equal(tag + "-phone", seen[0].Item1);
            Assert.Equal(47823, seen[0].Item2.Port);
            Assert.Equal(IPAddress.Loopback, seen[0].Item2.Address); // always the sender, never an address from the packet
        }
    }
}

public class PowerTests
{
    [Fact]
    public void Unknown_actions_do_nothing()
    {
        Assert.Null(PowerControl.Run("format-c"));
        Assert.Null(PowerControl.Run(""));
        Assert.Null(PowerControl.Run("SHUTDOWN")); // exact names only
    }

    [Fact]
    public void Power_log_reads_real_events_newest_first_with_known_kinds()
    {
        var log = PowerControl.ReadLog(30);
        Assert.InRange(log.Count, 0, 30);
        long previous = long.MaxValue;
        foreach (var item in log.OfType<JsonObject>())
        {
            Assert.Contains(item["kind"]!.ToString(), new[] { "on", "off", "sleep", "wake", "crash" });
            long t = (long)item["time"]!;
            Assert.True(t <= previous);
            previous = t;
        }
    }
}
