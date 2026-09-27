using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using LanConnect.Core.Clipboard;
using LanConnect.Core.Engine;
using LanConnect.Core.Layout;
using LanConnect.Core.Net;

namespace LanConnect.Core.Tests;

/// <summary>An in-memory clipboard with Windows-like sequence numbers.</summary>
internal sealed class FakeClipboard : IClipboardPlatform
{
    private readonly object _lock = new();
    private ClipboardContent? _content;
    private uint _sequence = 1;

    public int Writes { get; private set; }

    public uint SequenceNumber
    {
        get { lock (_lock) return _sequence; }
    }

    public ClipboardContent? Content
    {
        get { lock (_lock) return _content; }
    }

    public ClipboardContent? Read()
    {
        lock (_lock)
        {
            return _content is null ? null : Clone(_content);
        }
    }

    /// <summary>Content arriving from the other computer.</summary>
    public void Write(ClipboardContent content)
    {
        lock (_lock)
        {
            _content = Clone(content);
            _sequence++;
            Writes++;
        }
    }

    /// <summary>The user copies something on this computer.</summary>
    public void Copy(ClipboardContent content)
    {
        lock (_lock)
        {
            _content = Clone(content);
            _sequence++;
        }
    }

    public void CopyText(string text) => Copy(TextContent(text));

    public string? Text => Content?.Formats.TryGetValue(ClipboardFormats.Text, out var t) == true ? Encoding.UTF8.GetString(t) : null;

    public static ClipboardContent TextContent(string text)
    {
        var c = new ClipboardContent();
        c.Formats[ClipboardFormats.Text] = Encoding.UTF8.GetBytes(text);
        return c;
    }

    private static ClipboardContent Clone(ClipboardContent content)
    {
        var copy = new ClipboardContent { Sensitive = content.Sensitive };
        foreach (var (k, v) in content.Formats)
        {
            copy.Formats[k] = v.ToArray();
        }
        copy.FilePaths.AddRange(content.FilePaths);
        return copy;
    }
}

public class ClipboardSyncTests
{
    private static Task WaitUntilAsync(Func<bool> condition) => EngineTests.WaitUntilAsync(condition);

    private static async Task SwitchToLaptopAsync(TestComputer pc, TestComputer laptop)
    {
        pc.Platform.Cursor = new PointI(3800, 540);
        Assert.True(pc.Move(150, 0));
        await WaitUntilAsync(() => laptop.Engine.Status.State == ControlState.ControlledBy);
    }

    private static async Task SwitchBackAsync(TestComputer pc, TestComputer laptop)
    {
        for (var i = 0; i < 20 && pc.Engine.Status.State == ControlState.Controlling; i++)
        {
            pc.Move(-400, 0);
            await Task.Delay(30);
        }
        await WaitUntilAsync(() => pc.Engine.Status.State == ControlState.Local && laptop.Engine.Status.State == ControlState.Local);
    }

    [Fact]
    public async Task CopyOnThePcPasteOnTheLaptop()
    {
        var (pc, laptop) = await EngineTests.SetUpAsync();
        await using var _ = pc;
        await using var __ = laptop;

        var content = FakeClipboard.TextContent("Hello from the gaming PC ✓");
        content.Formats[ClipboardFormats.Html] = Encoding.UTF8.GetBytes("Version:0.9\r\n<b>Hello</b>");
        pc.Clipboard.Copy(content);

        await SwitchToLaptopAsync(pc, laptop);
        await WaitUntilAsync(() => laptop.Clipboard.Text == "Hello from the gaming PC ✓");
        Assert.Equal(content.Formats[ClipboardFormats.Html], laptop.Clipboard.Content!.Formats[ClipboardFormats.Html]);
    }

    [Fact]
    public async Task CopyOnTheLaptopPasteOnThePcAndNothingEchoes()
    {
        var (pc, laptop) = await EngineTests.SetUpAsync();
        await using var _ = pc;
        await using var __ = laptop;

        pc.Clipboard.CopyText("first");
        await SwitchToLaptopAsync(pc, laptop);
        await WaitUntilAsync(() => laptop.Clipboard.Text == "first");

        // Copy on the laptop (while controlling it), then come back: it arrives on the PC.
        laptop.Clipboard.CopyText("from the laptop");
        await SwitchBackAsync(pc, laptop);
        await WaitUntilAsync(() => pc.Clipboard.Text == "from the laptop");

        // Switching again without copying anything moves nothing in either direction.
        var laptopWrites = laptop.Clipboard.Writes;
        var pcWrites = pc.Clipboard.Writes;
        await SwitchToLaptopAsync(pc, laptop);
        await SwitchBackAsync(pc, laptop);
        await Task.Delay(500);
        Assert.Equal(laptopWrites, laptop.Clipboard.Writes);
        Assert.Equal(pcWrites, pc.Clipboard.Writes);
    }

    [Fact]
    public async Task LargeImagesAndCompressibleDataArriveIntact()
    {
        var (pc, laptop) = await EngineTests.SetUpAsync();
        await using var _ = pc;
        await using var __ = laptop;

        var png = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 123); // several chunks, incompressible
        var dib = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("pixel data ", 400_000))); // compressible
        var content = new ClipboardContent();
        content.Formats[ClipboardFormats.Png] = png;
        content.Formats[ClipboardFormats.Dib] = dib;
        pc.Clipboard.Copy(content);

        await SwitchToLaptopAsync(pc, laptop);
        await WaitUntilAsync(() => laptop.Clipboard.Content?.Formats.ContainsKey(ClipboardFormats.Png) == true);
        Assert.Equal(png, laptop.Clipboard.Content!.Formats[ClipboardFormats.Png]);
        Assert.Equal(dib, laptop.Clipboard.Content.Formats[ClipboardFormats.Dib]);
    }

    [Fact]
    public async Task CopiedFilesAndFoldersArriveWithTheirStructure()
    {
        var (pc, laptop) = await EngineTests.SetUpAsync();
        await using var _ = pc;
        await using var __ = laptop;

        var source = Path.Combine(pc.Directory, "files");
        var project = Path.Combine(source, "Project");
        Directory.CreateDirectory(Path.Combine(project, "src", "empty"));
        File.WriteAllText(Path.Combine(project, "readme.txt"), "read me");
        var big = RandomNumberGenerator.GetBytes(1_500_000);
        File.WriteAllBytes(Path.Combine(project, "src", "data.bin"), big);
        File.WriteAllText(Path.Combine(source, "notes.txt"), "loose file");

        var content = new ClipboardContent();
        content.FilePaths.Add(project);
        content.FilePaths.Add(Path.Combine(source, "notes.txt"));
        pc.Clipboard.Copy(content);

        var events = new ConcurrentQueue<ClipboardTransferEvent>();
        laptop.ClipboardSync.TransferEvent += events.Enqueue;

        await SwitchToLaptopAsync(pc, laptop);
        await WaitUntilAsync(() => laptop.Clipboard.Content?.FilePaths.Count == 2);

        var roots = laptop.Clipboard.Content!.FilePaths;
        var receivedProject = roots.Single(p => Path.GetFileName(p) == "Project");
        var receivedNotes = roots.Single(p => Path.GetFileName(p) == "notes.txt");
        Assert.Equal("read me", File.ReadAllText(Path.Combine(receivedProject, "readme.txt")));
        Assert.Equal(big, File.ReadAllBytes(Path.Combine(receivedProject, "src", "data.bin")));
        Assert.True(Directory.Exists(Path.Combine(receivedProject, "src", "empty")));
        Assert.Equal("loose file", File.ReadAllText(receivedNotes));
        Assert.StartsWith(laptop.Directory, receivedProject);
        // The notification is raised just after the clipboard is set.
        await WaitUntilAsync(() => events.Any(e => e.Kind == ClipboardTransferKind.Received && e.FileCount == 3));
    }

    [Fact]
    public async Task FilesOverTheLimitAreSkippedButTextStillArrives()
    {
        var (pc, laptop) = await EngineTests.SetUpAsync();
        await using var _ = pc;
        await using var __ = laptop;
        pc.Settings.Update(s => s.Clipboard.MaxSizeMb = 1);

        var file = Path.Combine(pc.Directory, "huge.bin");
        File.WriteAllBytes(file, new byte[2 * 1024 * 1024]);
        var content = FakeClipboard.TextContent("huge.bin");
        content.FilePaths.Add(file);
        pc.Clipboard.Copy(content);
        var events = new ConcurrentQueue<ClipboardTransferEvent>();
        pc.ClipboardSync.TransferEvent += events.Enqueue;

        await SwitchToLaptopAsync(pc, laptop);
        await WaitUntilAsync(() => laptop.Clipboard.Text == "huge.bin");
        Assert.Empty(laptop.Clipboard.Content!.FilePaths);
        Assert.Contains(events, e => e.Kind == ClipboardTransferKind.TooLarge);
    }

    [Fact]
    public async Task SharingCanBeTurnedOff()
    {
        var (pc, laptop) = await EngineTests.SetUpAsync();
        await using var _ = pc;
        await using var __ = laptop;
        pc.Settings.Update(s => s.Clipboard.Enabled = false);

        pc.Clipboard.CopyText("private");
        await SwitchToLaptopAsync(pc, laptop);
        await Task.Delay(500);
        Assert.Null(laptop.Clipboard.Content);
    }

    [Fact]
    public async Task SensitiveContentKeepsItsMarker()
    {
        var (pc, laptop) = await EngineTests.SetUpAsync();
        await using var _ = pc;
        await using var __ = laptop;

        var password = FakeClipboard.TextContent("hunter2");
        password.Sensitive = true;
        pc.Clipboard.Copy(password);
        await SwitchToLaptopAsync(pc, laptop);
        await WaitUntilAsync(() => laptop.Clipboard.Text == "hunter2");
        Assert.True(laptop.Clipboard.Content!.Sensitive);
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("folder/../../evil.txt")]
    [InlineData("C:/Windows/evil.exe")]
    [InlineData("..\\evil.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("CON")]
    [InlineData("folder/NUL.txt")]
    [InlineData("trailing. ")]
    public async Task AMaliciousPeerCannotWriteOutsideTheTransferFolder(string name)
    {
        var (pc, laptop) = await EngineTests.SetUpAsync();
        await using var _ = pc;
        await using var __ = laptop;
        var events = new ConcurrentQueue<ClipboardTransferEvent>();
        laptop.ClipboardSync.TransferEvent += events.Enqueue;

        var link = pc.Host.GetLink(laptop.Id)!;
        var payload = Encoding.UTF8.GetBytes("pwned");
        await link.SendClipboardFrameAsync(Frame(1, 7, [0, .. Int64(payload.Length), .. Int32(1)]), default);
        await link.SendClipboardFrameAsync(Frame(2, 7, [2, .. Int64(payload.Length), .. UInt16(Encoding.UTF8.GetByteCount(name)), .. Encoding.UTF8.GetBytes(name)]), default);
        await link.SendClipboardFrameAsync(Frame(3, 7, payload), default);
        await link.SendClipboardFrameAsync(Frame(4, 7, []), default);

        await WaitUntilAsync(() => events.Any(e => e.Kind == ClipboardTransferKind.Failed));
        Assert.Null(laptop.Clipboard.Content);
        var escaped = Directory.EnumerateFiles(laptop.Directory, "*", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f) == "pwned");
        Assert.Empty(escaped);
    }

    [Fact]
    public async Task InconsistentSizesAreRejected()
    {
        var (pc, laptop) = await EngineTests.SetUpAsync();
        await using var _ = pc;
        await using var __ = laptop;
        var events = new ConcurrentQueue<ClipboardTransferEvent>();
        laptop.ClipboardSync.TransferEvent += events.Enqueue;

        var link = pc.Host.GetLink(laptop.Id)!;
        await link.SendClipboardFrameAsync(Frame(1, 9, [0, .. Int64(3), .. Int32(1)]), default);
        await link.SendClipboardFrameAsync(Frame(2, 9, [1, .. Int64(3), .. UInt16(4), .. "text"u8.ToArray()]), default);
        await link.SendClipboardFrameAsync(Frame(3, 9, "too much data"u8.ToArray()), default);

        await WaitUntilAsync(() => events.Any(e => e.Kind == ClipboardTransferKind.Failed));
        Assert.Null(laptop.Clipboard.Content);
    }

    [Theory]
    [InlineData("file.txt", true)]
    [InlineData("Folder/Sub folder/file (1).txt", true)]
    [InlineData("héllo/日本語.txt", true)]
    [InlineData("..", false)]
    [InlineData("a/./b", false)]
    [InlineData("", false)]
    [InlineData("a//b", false)]
    [InlineData("a:b", false)]
    [InlineData("LPT1.txt", false)]
    [InlineData("com9", false)]
    [InlineData("COMPUTER.txt", true)]
    public void RelativePathsAreValidated(string name, bool valid)
    {
        Assert.Equal(valid, ClipboardSync.SanitizeRelativePath(name) is not null);
    }

    private static byte[] Frame(byte message, uint id, byte[] body)
    {
        var frame = new byte[5 + body.Length];
        frame[0] = message;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1), id);
        body.CopyTo(frame, 5);
        return frame;
    }

    private static byte[] Int64(long v)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(b, v);
        return b;
    }

    private static byte[] Int32(int v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, v);
        return b;
    }

    private static byte[] UInt16(int v)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v);
        return b;
    }
}
