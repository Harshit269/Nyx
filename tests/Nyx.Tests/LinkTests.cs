using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using PhoneBridge;
using Xunit;

namespace Nyx.Tests;

/// <summary>Shared state (the pair store file, UDP port) means these run one at a time.</summary>
[CollectionDefinition("store", DisableParallelization = true)]
public class StoreCollection { }

[Collection("store")]
public class LinkTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "nyx-tests-" + Guid.NewGuid().ToString("N"));
    readonly byte[] _secret = RandomNumberGenerator.GetBytes(32);
    readonly PairedPhone _pair;

    public LinkTests()
    {
        Directory.CreateDirectory(_dir);
        PairStore.UseFileForTests(Path.Combine(_dir, "pairs.json"));
        _pair = new PairedPhone("phone-1", "Test phone", Convert.ToBase64String(_secret));
    }

    public void Dispose()
    {
        PairStore.UseFileForTests(Path.Combine(_dir, "unused.json"));
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public async Task Honest_phone_links_and_messages_flow_encrypted_both_ways()
    {
        using var phone = new FakePhone("phone-1", _secret);
        var client = new BridgeClient();
        var got = new List<JsonObject>();
        var hello = new TaskCompletionSource<JsonObject>();
        client.MessageReceived += m => { lock (got) got.Add(m); if (m["t"]?.ToString() == "status") hello.TrySetResult(m); };

        await client.ConnectLanAsync(phone.EndPoint, _pair);
        Assert.True(client.IsConnected);
        Assert.True(client.IsVerified);
        Assert.Equal("Wi-Fi", client.Kind);

        var status = await hello.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(42, (int)status["battery"]!);
        Assert.Contains(got, m => m["t"]?.ToString() == "hello");

        await client.SendAsync("get_contacts");
        await client.SendAsync(new JsonObject { ["t"] = "get_sms", ["limit"] = 400 });
        await client.SendAsync(new JsonObject { ["t"] = "send_sms", ["to"] = "+91 55501 00101", ["body"] = "हाँ, ₹450 😀" });
        var inbox = await phone.Received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("get_contacts", inbox[0]["t"]!.ToString());
        Assert.Equal("हाँ, ₹450 😀", inbox[2]["body"]!.ToString());
        client.Close();
    }

    [Theory]
    [InlineData(FakePhone.Mode.WrongSecret)]
    [InlineData(FakePhone.Mode.WrongPhoneId)]
    [InlineData(FakePhone.Mode.BadProofAfterHello)]
    [InlineData(FakePhone.Mode.Garbage)]
    public async Task Impostors_are_refused_before_any_message_is_accepted(FakePhone.Mode mode)
    {
        using var phone = new FakePhone("phone-1", _secret, mode);
        var client = new BridgeClient();
        int messages = 0;
        client.MessageReceived += _ => Interlocked.Increment(ref messages);
        await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectLanAsync(phone.EndPoint, _pair));
        Assert.False(client.IsConnected);
        Assert.Equal(0, messages);
    }

    [Fact]
    public async Task A_phone_that_never_answers_times_out_within_seconds()
    {
        using var phone = new FakePhone("phone-1", _secret, FakePhone.Mode.Silent);
        var client = new BridgeClient();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectLanAsync(phone.EndPoint, _pair));
        Assert.InRange(sw.Elapsed.TotalSeconds, 0, 8);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task Nothing_listening_fails_fast()
    {
        var client = new BridgeClient();
        await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectLanAsync(new IPEndPoint(IPAddress.Loopback, 1), _pair));
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task A_tampered_frame_on_a_live_link_drops_the_link()
    {
        using var phone = new FakePhone("phone-1", _secret, FakePhone.Mode.TamperAfterLink);
        var client = new BridgeClient();
        var dropped = new TaskCompletionSource();
        var types = new List<string>();
        client.MessageReceived += m => { lock (types) types.Add(m["t"]!.ToString()); };
        client.Disconnected += () => dropped.TrySetResult();
        await client.ConnectLanAsync(phone.EndPoint, _pair);
        await dropped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(client.IsConnected);
        Assert.DoesNotContain("status", types); // the tampered message was never delivered
    }

    [Fact]
    public async Task An_oversized_line_drops_the_link_instead_of_eating_memory()
    {
        using var phone = new FakePhone("phone-1", _secret, FakePhone.Mode.HugeLine);
        var client = new BridgeClient();
        var dropped = new TaskCompletionSource();
        client.Disconnected += () => dropped.TrySetResult();
        await client.ConnectLanAsync(phone.EndPoint, _pair);
        await dropped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task A_failed_Wi_Fi_attempt_keeps_the_link_that_already_works()
    {
        using var good = new FakePhone("phone-1", _secret);
        using var bad = new FakePhone("phone-1", _secret, FakePhone.Mode.WrongSecret);
        var client = new BridgeClient();
        int drops = 0;
        client.Disconnected += () => Interlocked.Increment(ref drops);
        await client.ConnectLanAsync(good.EndPoint, _pair);
        await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectLanAsync(bad.EndPoint, _pair));
        Assert.True(client.IsConnected);
        await client.SendAsync("ping");
        await Task.Delay(300);
        Assert.Equal(0, drops);
        client.Close();
    }

    [Fact]
    public async Task Sending_while_disconnected_is_a_harmless_no_op()
    {
        var client = new BridgeClient();
        await client.SendAsync("ping");
        client.Close();
        client.Close();
        Assert.False(client.IsConnected);
    }
}
