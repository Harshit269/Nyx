using PhoneBridge;
using Xunit;

namespace Nyx.Tests;

public class HandsFreeTests
{
    [Theory]
    [InlineData(@"Bluetooth#Bluetooth70:a6:cc:11:22:33-a4:c3:f0:85:ac:2b", "a4c3f085ac2b")]
    [InlineData(@"BTHENUM#{0000111f-0000-1000-8000-00805f9b34fb}_LOCALMFG&001d#7&1c3f&0&A4C3F085AC2B_C00000000", "a4c3f085ac2b")]
    // the full id Windows really hands out ends in an interface GUID, whose last part looks like an address
    [InlineData(@"\\?\BTHENUM#{0000111f-0000-1000-8000-00805f9b34fb}_VID&0001001d_PID&0000#9&7e9c5b0&0&3CB0EDDD16DA_C00000000#{bd41df2d-addd-4fc9-a194-b9881d2a2efa}\service", "3cb0eddd16da")]
    [InlineData("no address here", null)]
    public void Bluetooth_addresses_are_found_in_windows_device_ids(string id, string? mac) =>
        Assert.Equal(mac, HandsFree.MacOf(id));
}
