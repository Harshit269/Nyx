using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using PhoneBridge;
using Xunit;

namespace Nyx.Tests;

[Collection("files")]
public class FileTransferTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "nyx-files-" + Guid.NewGuid().ToString("N")[..8]);

    public FileTransferTests()
    {
        Directory.CreateDirectory(_dir);
        FileTransfer.FolderForTests = Path.Combine(_dir, "in");
    }

    public void Dispose()
    {
        FileTransfer.AbortAll();
        FileTransfer.FolderForTests = null;
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Theory]
    [InlineData("photo.jpg", "photo.jpg")]
    [InlineData("../../Windows/System32/evil.dll", "evil.dll")]
    [InlineData(@"C:\Users\x\notes.txt", "notes.txt")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("a<b>c:d|e?.txt", "abcde.txt")]
    [InlineData("..", "file")]
    [InlineData("trailing. . ", "trailing")]
    public void Names_are_made_safe(string raw, string expected) => Assert.Equal(expected, FileTransfer.SafeName(raw));

    [Fact]
    public void Long_names_are_shortened_keeping_the_extension()
    {
        var n = FileTransfer.SafeName(new string('x', 300) + ".pdf");
        Assert.True(n.Length <= 120);
        Assert.EndsWith(".pdf", n);
    }

    /// <summary>Sends a real file through the sender and straight into the receiver.</summary>
    async Task<(List<JsonObject> replies, FileTransfer.Received? got)> RoundTrip(byte[] content, Func<JsonObject, JsonObject>? tamper = null)
    {
        string src = Path.Combine(_dir, "source.bin");
        await File.WriteAllBytesAsync(src, content);
        var replies = new List<JsonObject>();
        FileTransfer.Received? got = null;
        string? problem = await FileTransfer.SendAsync(src, msg =>
        {
            var m = tamper != null ? tamper(msg) : msg;
            var (reply, done) = FileTransfer.OnMessage(m);
            if (reply != null) replies.Add(reply);
            if (done != null) got = done;
            return Task.FromResult(true);
        });
        Assert.Null(problem);
        return (replies, got);
    }

    [Fact]
    public async Task A_file_arrives_intact()
    {
        var content = RandomNumberGenerator.GetBytes(FileTransfer.Chunk * 3 + 1234); // several chunks
        var (replies, got) = await RoundTrip(content);
        Assert.NotNull(got);
        Assert.True(got!.Ok);
        Assert.Equal(content, await File.ReadAllBytesAsync(got.Path!));
        Assert.Single(replies);
        Assert.True(replies[0]["ok"]!.GetValue<bool>());
        Assert.Empty(Directory.GetFiles(FileTransfer.Folder, "*.part")); // no leftovers
    }

    [Fact]
    public async Task An_empty_file_works()
    {
        var (_, got) = await RoundTrip(Array.Empty<byte>());
        Assert.True(got!.Ok);
        Assert.Equal(0, new FileInfo(got.Path!).Length);
    }

    [Fact]
    public async Task A_tampered_part_is_thrown_away()
    {
        var content = RandomNumberGenerator.GetBytes(FileTransfer.Chunk * 2);
        var (replies, got) = await RoundTrip(content, m =>
        {
            if (m["t"]?.ToString() == "file_part" && m["seq"]!.GetValue<int>() == 1)
            {
                var bytes = Convert.FromBase64String(m["data"]!.ToString());
                bytes[10] ^= 0xFF;
                m["data"] = Convert.ToBase64String(bytes);
            }
            return m;
        });
        Assert.False(got!.Ok);
        Assert.False(replies[0]["ok"]!.GetValue<bool>());
        Assert.Empty(Directory.GetFiles(FileTransfer.Folder)); // nothing kept, not even a .part
    }

    [Fact]
    public async Task A_file_bigger_than_announced_is_refused()
    {
        var content = RandomNumberGenerator.GetBytes(5000);
        var (_, got) = await RoundTrip(content, m =>
        {
            if (m["t"]?.ToString() == "file_start") m["size"] = 100; // lies about its size
            return m;
        });
        Assert.False(got!.Ok);
        Assert.Empty(Directory.GetFiles(FileTransfer.Folder));
    }

    [Fact]
    public void Parts_out_of_order_are_refused()
    {
        FileTransfer.OnMessage(new JsonObject { ["t"] = "file_start", ["id"] = "abc", ["name"] = "x.bin", ["size"] = 10 });
        var (reply, _) = FileTransfer.OnMessage(new JsonObject { ["t"] = "file_part", ["id"] = "abc", ["seq"] = 3, ["data"] = "AAAA" });
        Assert.False(reply!["ok"]!.GetValue<bool>());
        Assert.Empty(Directory.GetFiles(FileTransfer.Folder));
    }

    [Fact]
    public async Task A_name_already_taken_gets_a_number()
    {
        Directory.CreateDirectory(FileTransfer.Folder);
        await File.WriteAllTextAsync(Path.Combine(FileTransfer.Folder, "source.bin"), "already here");
        var (_, got) = await RoundTrip(new byte[] { 1, 2, 3 });
        Assert.Equal("source (2).bin", got!.Name);
        Assert.Equal("already here", await File.ReadAllTextAsync(Path.Combine(FileTransfer.Folder, "source.bin")));
    }

    [Fact]
    public void Too_big_a_file_is_refused_up_front()
    {
        var (reply, _) = FileTransfer.OnMessage(new JsonObject { ["t"] = "file_start", ["id"] = "big", ["name"] = "huge.iso", ["size"] = FileTransfer.MaxSize + 1 });
        Assert.False(reply!["ok"]!.GetValue<bool>());
    }
}
