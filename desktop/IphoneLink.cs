using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace PhoneBridge;

/// <summary>
/// iPhone notifications on the laptop, through Apple's Notification Center Service (ANCS) over
/// Bluetooth LE. No app on the iPhone: pair it with Windows, switch on "Share System
/// Notifications" for this laptop in the iPhone's Bluetooth settings, then connect here.
/// The byte format lives in Ancs.cs; this class only does the Bluetooth plumbing.
/// </summary>
public sealed class IphoneLink : IDisposable
{
    public event Action<NotifItem, bool>? Posted;      // the notification, and whether it was already there
    public event Action<string>? Removed;              // its id
    public event Action<string>? Status;               // a line for the Settings card
    public string LastStatus { get; private set; } = "";
    public event Action<bool>? ConnectedChanged;

    public bool Connected { get; private set; }
    public string DeviceName { get; private set; } = "";

    BluetoothLEDevice? _device;
    GattCharacteristic? _notifSource, _control, _data;
    readonly Ancs.Assembler _assembler = new();
    readonly Queue<byte[]> _requests = new();
    readonly SemaphoreSlim _writeLock = new(1, 1);
    readonly Dictionary<string, string> _appNames = new();          // bundle id -> "WhatsApp"
    readonly Dictionary<uint, Ancs.Event> _events = new();         // uid -> what it was
    readonly Dictionary<uint, Ancs.NotificationAnswer> _waitingForApp = new();
    bool _busy;   // one Control Point request at a time, as the spec asks
    int _sent;    // counts requests, so a late time-out can't free a newer one

    void Say(string line) { LastStatus = line; Status?.Invoke(line); }

    public static string IdOf(uint uid) => "ios:" + uid;

    /// <summary>Looks through paired Bluetooth devices for one that shares notifications (ANCS).</summary>
    public async Task<bool> ConnectAsync()
    {
        Close();
        Say("Looking for your iPhone…");
        try
        {
            var candidates = new List<BluetoothLEDevice>();
            // paired Bluetooth LE devices
            foreach (var d in await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)))
                if (await BluetoothLEDevice.FromIdAsync(d.Id) is { } le) candidates.Add(le);
            // classic-paired iPhones: the same address usually works for Bluetooth LE too
            foreach (var d in await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true)))
            {
                if (!d.Name.Contains("iPhone", StringComparison.OrdinalIgnoreCase) && !d.Name.Contains("iPad", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    using var classic = await BluetoothDevice.FromIdAsync(d.Id);
                    if (classic != null && candidates.All(c => c.BluetoothAddress != classic.BluetoothAddress)
                        && await BluetoothLEDevice.FromBluetoothAddressAsync(classic.BluetoothAddress) is { } le) candidates.Add(le);
                }
                catch { }
            }

            // ask every candidate at once, each with a time limit: an earbud or controller that
            // never answers mustn't keep the search hanging
            async Task<(BluetoothLEDevice Dev, GattDeviceService? Svc)> Probe(BluetoothLEDevice dev)
            {
                try
                {
                    var ask = dev.GetGattServicesForUuidAsync(Ancs.Service, BluetoothCacheMode.Uncached).AsTask();
                    if (await Task.WhenAny(ask, Task.Delay(8000)) != ask) return (dev, null);
                    var r = ask.Result;
                    return (dev, r.Status == GattCommunicationStatus.Success && r.Services.Count > 0 ? r.Services[0] : null);
                }
                catch { return (dev, null); }
            }
            var probes = candidates.Select(Probe).ToList();
            var all = Task.WhenAll(probes);
            await Task.WhenAny(all, Task.Delay(20000));
            var found = probes.Where(t => t.IsCompletedSuccessfully && t.Result.Svc != null).Select(t => t.Result).FirstOrDefault();
            if (found.Svc != null)
            {
                foreach (var other in candidates.Where(c => c != found.Dev)) other.Dispose();
                return await StartAsync(found.Dev, found.Svc);
            }
            bool anyApple = candidates.Any(c => c.Name.Contains("iPhone", StringComparison.OrdinalIgnoreCase) || c.Name.Contains("iPad", StringComparison.OrdinalIgnoreCase));
            Say(!anyApple
                ? "No paired iPhone found. Pair it in Windows Settings > Bluetooth & devices first."
                : "Found your iPhone, but it isn't sharing notifications. On the iPhone: Settings > Bluetooth > ⓘ next to this laptop > Share System Notifications.");
            foreach (var c in candidates) c.Dispose();
            return false;
        }
        catch (Exception ex)
        {
            Say("Bluetooth said: " + ex.Message);
            return false;
        }
    }

    async Task<bool> StartAsync(BluetoothLEDevice dev, GattDeviceService svc)
    {
        _device = dev;
        DeviceName = dev.Name;
        var chars = await svc.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
        if (chars.Status != GattCommunicationStatus.Success) { Say("Couldn't read the iPhone's notification service (" + chars.Status + ")."); return false; }
        _notifSource = chars.Characteristics.FirstOrDefault(c => c.Uuid == Ancs.NotificationSource);
        _control = chars.Characteristics.FirstOrDefault(c => c.Uuid == Ancs.ControlPoint);
        _data = chars.Characteristics.FirstOrDefault(c => c.Uuid == Ancs.DataSource);
        if (_notifSource == null || _control == null || _data == null) { Say("The iPhone's notification service is incomplete."); return false; }

        // the spec: subscribe to Data Source before Notification Source, or early answers are lost
        _data.ValueChanged += OnData;
        _notifSource.ValueChanged += OnEvent;
        var a = await _data.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);
        var b = await _notifSource.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);
        if (a != GattCommunicationStatus.Success || b != GattCommunicationStatus.Success)
        {
            Say("The iPhone didn't allow it. Check Share System Notifications is on for this laptop, then try again.");
            return false;
        }
        dev.ConnectionStatusChanged += OnConnectionChanged;
        SetConnected(true);
        Say("Getting notifications from " + DeviceName + ".");
        return true;
    }

    void OnConnectionChanged(BluetoothLEDevice d, object _) =>
        SetConnected(d.ConnectionStatus == BluetoothConnectionStatus.Connected);

    void SetConnected(bool on)
    {
        if (Connected == on) return;
        Connected = on;
        ConnectedChanged?.Invoke(on);
        if (!on) Say(DeviceName + " is out of reach. Notifications resume when it's back.");
    }

    static byte[] Bytes(IBuffer buf)
    {
        var b = new byte[buf.Length];
        using var r = DataReader.FromBuffer(buf);
        r.ReadBytes(b);
        return b;
    }

    void OnEvent(GattCharacteristic sender, GattValueChangedEventArgs e)
    {
        var ev = Ancs.ParseEvent(Bytes(e.CharacteristicValue));
        if (ev == null) return;
        var v = ev.Value;
        lock (_requests)
        {
            if (v.Id == Ancs.EventId.Removed)
            {
                _events.Remove(v.Uid);
                Removed?.Invoke(IdOf(v.Uid));
                return;
            }
            if (v.Flags.HasFlag(Ancs.EventFlags.Silent) && v.Flags.HasFlag(Ancs.EventFlags.PreExisting)) return;
            _events[v.Uid] = v;
            _requests.Enqueue(Ancs.GetNotificationAttributes(v.Uid));
        }
        _ = PumpAsync();
    }

    void OnData(GattCharacteristic sender, GattValueChangedEventArgs e)
    {
        var bytes = Bytes(e.CharacteristicValue);
        List<Ancs.Answer> answers;
        lock (_requests) answers = _assembler.Feed(bytes);
        foreach (var answer in answers)
        {
            lock (_requests)
            {
                _busy = false;
                if (answer is Ancs.NotificationAnswer n)
                {
                    string app = n.Values.GetValueOrDefault(Ancs.Attr.AppIdentifier, "");
                    if (app.Length > 0 && !_appNames.ContainsKey(app))
                    {
                        // first time we see this app: ask its name, publish when it comes
                        _waitingForApp[n.Uid] = n;
                        _requests.Enqueue(Ancs.GetAppAttributes(app));
                    }
                    else Publish(n);
                }
                else if (answer is Ancs.AppAnswer a)
                {
                    _appNames[a.AppId] = a.DisplayName.Length > 0 ? a.DisplayName : a.AppId;
                    foreach (var w in _waitingForApp.Values.Where(w => w.Values.GetValueOrDefault(Ancs.Attr.AppIdentifier) == a.AppId).ToList())
                    {
                        _waitingForApp.Remove(w.Uid);
                        Publish(w);
                    }
                }
            }
        }
        _ = PumpAsync();
    }

    void Publish(Ancs.NotificationAnswer n)
    {
        _events.TryGetValue(n.Uid, out var ev);
        string bundle = n.Values.GetValueOrDefault(Ancs.Attr.AppIdentifier, "");
        string sub = n.Values.GetValueOrDefault(Ancs.Attr.Subtitle, "");
        string msg = n.Values.GetValueOrDefault(Ancs.Attr.Message, "");
        var item = new NotifItem
        {
            Id = IdOf(n.Uid),
            App = _appNames.GetValueOrDefault(bundle, Ancs.CategoryName(ev.Category)),
            Title = n.Values.GetValueOrDefault(Ancs.Attr.Title, ""),
            Text = sub.Length > 0 ? sub + " · " + msg : msg,
            Time = Ancs.ParseDate(n.Values.GetValueOrDefault(Ancs.Attr.Date, "")) ?? DateTime.Now
        };
        if (item.Title.Length == 0 && item.Text.Length == 0) return;
        // already on the phone, or only changed: list it quietly, no pop-up
        Posted?.Invoke(item, ev.Flags.HasFlag(Ancs.EventFlags.PreExisting) || ev.Id == Ancs.EventId.Modified);
    }

    /// <summary>Sends the next queued Control Point request, one at a time.</summary>
    async Task PumpAsync()
    {
        byte[] next;
        int mine;
        lock (_requests)
        {
            if (_busy || _requests.Count == 0 || _control == null) return;
            next = _requests.Dequeue();
            _busy = true;
            mine = ++_sent;
        }
        await _writeLock.WaitAsync();
        try
        {
            var result = await _control.WriteValueAsync(next.AsBuffer(), GattWriteOption.WriteWithResponse);
            if (result != GattCommunicationStatus.Success) lock (_requests) _busy = false;
        }
        catch { lock (_requests) _busy = false; }
        finally { _writeLock.Release(); }
        // never stall: if no answer comes in 3 seconds, drop any half answer and move on
        _ = Task.Delay(3000).ContinueWith(_ =>
        {
            lock (_requests)
            {
                if (!_busy || _sent != mine) return Task.CompletedTask;
                _busy = false;
                _assembler.Reset();
            }
            return PumpAsync();
        });
    }

    /// <summary>Dismisses a notification on the iPhone (if the iPhone allows it for that one).</summary>
    public async Task DismissAsync(string id)
    {
        if (_control == null || !id.StartsWith("ios:") || !uint.TryParse(id[4..], out uint uid)) return;
        lock (_requests) if (_events.TryGetValue(uid, out var ev) && !ev.Flags.HasFlag(Ancs.EventFlags.NegativeAction)) return;
        await _writeLock.WaitAsync();
        try { await _control.WriteValueAsync(Ancs.PerformAction(uid, positive: false).AsBuffer(), GattWriteOption.WriteWithResponse); }
        catch { }
        finally { _writeLock.Release(); }
    }

    void Close()
    {
        if (_data != null) _data.ValueChanged -= OnData;
        if (_notifSource != null) _notifSource.ValueChanged -= OnEvent;
        if (_device != null) { _device.ConnectionStatusChanged -= OnConnectionChanged; _device.Dispose(); }
        _device = null; _notifSource = _control = _data = null;
        lock (_requests) { _requests.Clear(); _events.Clear(); _waitingForApp.Clear(); _busy = false; _assembler.Reset(); }
        if (Connected) SetConnected(false);
    }

    public void Dispose() => Close();
}

static class BufferExtensions
{
    public static IBuffer AsBuffer(this byte[] b)
    {
        using var w = new DataWriter();
        w.WriteBytes(b);
        return w.DetachBuffer();
    }
}
