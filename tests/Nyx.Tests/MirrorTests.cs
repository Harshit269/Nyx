using PhoneBridge;
using Xunit;

namespace Nyx.Tests;

public class MirrorTests
{
    const string Mdns =
        "List of discovered mdns services\n" +
        "adb-R5CT10ABCDE-AbCdEf\t_adb-tls-pairing._tcp\t192.168.29.35:41111\n" +
        "adb-R5CT10ABCDE-AbCdEf\t_adb-tls-connect._tcp\t192.168.29.35:37425\n" +
        "adb-OTHERPHONE-xyz\t_adb-tls-connect._tcp\t192.168.29.80:40000\n";

    [Fact]
    public void Wireless_debugging_port_is_found_for_this_phone_only()
    {
        Assert.Equal("192.168.29.35:37425", Mirror.FindWirelessPort(Mdns, "192.168.29.35"));
        Assert.Equal("192.168.29.80:40000", Mirror.FindWirelessPort(Mdns, "192.168.29.80"));
        Assert.Null(Mirror.FindWirelessPort(Mdns, "192.168.29.99"));     // not on the network
        Assert.Null(Mirror.FindWirelessPort("", "192.168.29.35"));
        Assert.Null(Mirror.FindWirelessPort("x\t_adb-tls-connect._tcp\t192.168.29.35:99999", "192.168.29.35")); // bad port
    }

    [Theory]
    [InlineData("37425", "123456", true)]
    [InlineData("37425", "12345", false)]
    [InlineData("0", "123456", false)]
    [InlineData("70000", "123456", false)]
    [InlineData("37425 --x", "123456", false)]
    [InlineData("37425", "12345a", false)]
    public void Pairing_input_is_digits_only(string port, string code, bool ok) =>
        Assert.Equal(ok, Mirror.ValidPairing(port, code));

    [Theory]
    [InlineData("adb: device unauthorized.", "allowed this laptop")]
    [InlineData("INFO: Connected to 192.168.29.35:5555\nadb: error: failed to get feature set: device unauthorized.", "Connected over Wi-Fi, but")]
    [InlineData("ERROR: Could not find any ADB device", "No phone found")]
    [InlineData("adb: more than one device/emulator", "More than one")]
    [InlineData("failed to connect to '10.0.0.5:5555': Connection refused", "refused")]
    public void Known_failures_get_plain_words(string output, string expected) =>
        Assert.Contains(expected, Mirror.Explain(output));

    [Fact]
    public void Unknown_failure_shows_the_error_line()
    {
        string said = Mirror.Explain("INFO: starting\nERROR: something odd happened\n");
        Assert.Equal("ERROR: something odd happened", said);
    }

    [Fact]
    public void Missing_typed_path_is_not_trusted()
    {
        // a path that doesn't exist must never come back as "found"
        var found = Mirror.FindScrcpy(@"C:\definitely\not\here\scrcpy.exe");
        Assert.NotEqual(@"C:\definitely\not\here\scrcpy.exe", found);
    }

    [Fact]
    public async Task A_missing_program_reports_not_started()
    {
        var r = await Mirror.RunAsync(@"C:\definitely\not\here\nothing.exe", TimeSpan.FromSeconds(1));
        Assert.False(r.Started);
    }
}
