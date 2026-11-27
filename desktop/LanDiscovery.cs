using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace PhoneBridge;

/// <summary>
/// Listens for the phone's "I'm here" beacon on the local network. While the phone is on
/// Wi-Fi it broadcasts a tiny UDP packet every two seconds:
///   {"nyx":1,"phone":"&lt;id&gt;","port":47823,"name":"Sam's Nothing Phone (3)"}
/// When a paired phone is heard, PhoneSeen fires (on a background thread) with the
/// address to connect to. Windows asks once to allow Nyx through the firewall.
/// </summary>
public sealed class LanDiscovery : IDisposable
{
    public const int BeaconPort = 47824;

    /// <summary>(phone id, where to connect, the phone's name)</summary>
    public event Action<string, IPEndPoint, string>? PhoneSeen;

    UdpClient? _udp;
    CancellationTokenSource? _cts;

    public bool Running => _udp != null;

    public void Start()
    {
        if (_udp != null) return;
        try
        {
            var udp = new UdpClient();
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, BeaconPort));
            _udp = udp;
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => LoopAsync(udp, _cts.Token));
        }
        catch
        {
            _udp = null; // port busy or blocked: Wi-Fi discovery stays off, Bluetooth still works
        }
    }

    async Task LoopAsync(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var r = await udp.ReceiveAsync(ct);
                if (r.Buffer.Length > 512) continue;
                if (JsonNode.Parse(Encoding.UTF8.GetString(r.Buffer)) is not JsonObject o) continue;
                if (o["nyx"]?.ToString() != "1") continue;
                string phone = o["phone"]?.ToString() ?? "";
                int port = (int?)o["port"] ?? 0;
                if (phone.Length == 0 || port <= 0 || port > 65535) continue;
                string name = o["name"]?.ToString() ?? "";
                name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
                if (name.Length > 48) name = name[..48];
                PhoneSeen?.Invoke(phone.Length > 64 ? phone[..64] : phone, new IPEndPoint(r.RemoteEndPoint.Address, port), name);
            }
            catch (OperationCanceledException) { break; }
            catch { /* a stray packet; keep listening */ }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _udp?.Dispose(); } catch { }
        _udp = null;
    }

    /// <summary>
    /// This laptop's Wi-Fi and Ethernet MAC addresses, and where the phone should send the wake
    /// signal: the network's broadcast address and this laptop's own address. Routers often hold
    /// back broadcasts from a sleeping laptop's Wi-Fi; a packet to its own address gets through
    /// (the Wi-Fi card answers for it while asleep).
    /// </summary>
    public static (List<string> Macs, List<string> Broadcasts) WakeInfo()
    {
        var macs = new List<string>();
        var bcasts = new List<string>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.NetworkInterfaceType is not (NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet)) continue;
            var mac = ni.GetPhysicalAddress().GetAddressBytes();
            if (mac.Length != 6) continue;
            macs.Add(string.Join(":", mac.Select(b => b.ToString("X2"))));
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                var ip = ua.Address.GetAddressBytes();
                var mask = ua.IPv4Mask.GetAddressBytes();
                var b = new byte[4];
                for (int i = 0; i < 4; i++) b[i] = (byte)(ip[i] | ~mask[i]);
                bcasts.Add(new IPAddress(b).ToString());
                if (ni.OperationalStatus == OperationalStatus.Up) bcasts.Add(ua.Address.ToString());
            }
        }
        return (macs.Distinct().ToList(), bcasts.Distinct().ToList());
    }
}
