using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using LanConnect.Core.Net;

namespace LanConnect.Core.Clipboard;

/// <summary>
/// Brings the clipboard along when control moves between computers: the computer losing control
/// sends its clipboard (if it changed since the last sync) to the computer gaining it.
///
/// Wire format: frames of type <see cref="FrameType.Clipboard"/> on the encrypted TLS connection,
/// each <c>[u8 message][u32 transfer id][body]</c>:
/// <list type="bullet">
/// <item>Begin: <c>[u8 flags][u64 total bytes][u32 entry count]</c></item>
/// <item>Entry: <c>[u8 kind][u64 size][u16 name length][UTF-8 name]</c> (format id or relative path)</item>
/// <item>Data: raw bytes of the current entry, at most <see cref="ChunkSize"/> per frame</item>
/// <item>End / Cancel: no body</item>
/// </list>
/// Everything the other side sends is validated: sizes must add up, names must be known formats
/// or safe relative paths, and files can only be written inside this transfer's own folder.
/// </summary>
public sealed class ClipboardSync
{
    public const int ChunkSize = 256 * 1024;
    private const int HeaderSize = 5;
    private const int MaxEntries = 100_000;
    private const int MaxFormatBytes = 256 * 1024 * 1024;
    private const int MaxPathLength = 1024;
    private const int MaxPathDepth = 64;
    private const long NotifyAboveBytes = 5 * 1024 * 1024;

    private const byte MsgBegin = 1, MsgEntry = 2, MsgData = 3, MsgEnd = 4, MsgCancel = 5;
    private const byte KindFormat = 1, KindFile = 2, KindDirectory = 3, KindCompressed = 0x80;
    private const byte FlagSensitive = 1;

    private readonly IClipboardPlatform _platform;
    private readonly Func<ClipboardOptions> _options;
    private readonly string _receiveRoot;
    private readonly object _lock = new();
    private readonly Dictionary<PeerLink, Incoming> _incoming = new();
    private bool _hasSynced;
    private uint _lastSyncedSequence;
    private string? _lastFingerprint;
    private CancellationTokenSource? _sending;
    private int _nextTransferId;

    public ClipboardSync(IClipboardPlatform platform, Func<ClipboardOptions> options, string receiveRoot)
    {
        _platform = platform;
        _options = options;
        _receiveRoot = Path.GetFullPath(receiveRoot);
        CleanUpOldTransfers(TimeSpan.FromDays(1));
    }

    /// <summary>Raised on a background thread for noteworthy transfers (e.g. to show a notification).</summary>
    public event Action<ClipboardTransferEvent>? TransferEvent;

    /// <summary>Starts listening for clipboard transfers from a newly connected computer.</summary>
    public void Attach(PeerLink link)
    {
        link.ClipboardFrameReceived += OnFrame;
        link.Closed += OnLinkClosed;
    }

    /// <summary>
    /// Control is moving from this computer to <paramref name="target"/>: send the clipboard along
    /// if it changed since it was last synced (in either direction). Returns immediately.
    /// </summary>
    public void OnControlLeaving(PeerLink target)
    {
        var options = _options();
        if (!options.Enabled)
        {
            return;
        }

        uint sequence;
        try
        {
            sequence = _platform.SequenceNumber;
        }
        catch (Exception e)
        {
            Log.Warn($"Reading the clipboard state failed: {e.Message}");
            return;
        }

        CancellationTokenSource cts;
        lock (_lock)
        {
            if (_hasSynced && sequence == _lastSyncedSequence)
            {
                return; // nothing new since the last copy went either way
            }
            _hasSynced = true;
            _lastSyncedSequence = sequence;
            _sending?.Cancel();
            _sending = cts = new CancellationTokenSource();
        }
        _ = Task.Run(() => SendAsync(target, options, sequence, cts.Token));
    }

    /// <summary>Sends the current clipboard to <paramref name="target"/> (test entry point).</summary>
    internal Task SendNowAsync(PeerLink target) => SendAsync(target, _options(), sequence: null, CancellationToken.None);

    // ------------------------------------------------------------------ sending

    private readonly record struct OutgoingEntry(byte Kind, string Name, long Size, byte[]? Data, string? FilePath);

    private async Task SendAsync(PeerLink target, ClipboardOptions options, uint? sequence, CancellationToken token)
    {
        uint id = 0;
        var begun = false;
        try
        {
            var content = _platform.Read();
            if (content is null || content.IsEmpty)
            {
                return;
            }
            var fingerprint = Fingerprint(content);
            lock (_lock)
            {
                if (fingerprint == _lastFingerprint)
                {
                    return; // same content as the last transfer (e.g. re-set by a clipboard manager)
                }
            }

            var entries = BuildEntries(content, options, target.PeerName);
            if (entries.Count == 0)
            {
                return;
            }
            var total = entries.Sum(e => e.Size);
            var fileCount = entries.Count(e => e.Kind == KindFile);

            id = unchecked((uint)Interlocked.Increment(ref _nextTransferId));
            var buffer = new byte[HeaderSize + ChunkSize];

            var begin = Frame(buffer, MsgBegin, id);
            begin[HeaderSize] = content.Sensitive ? FlagSensitive : (byte)0;
            BinaryPrimitives.WriteInt64BigEndian(begin.AsSpan(HeaderSize + 1), total);
            BinaryPrimitives.WriteInt32BigEndian(begin.AsSpan(HeaderSize + 9), entries.Count);
            await target.SendClipboardFrameAsync(begin.AsMemory(0, HeaderSize + 13), token).ConfigureAwait(false);
            begun = true;

            foreach (var entry in entries)
            {
                var name = Encoding.UTF8.GetBytes(entry.Name);
                var header = Frame(buffer, MsgEntry, id);
                header[HeaderSize] = entry.Kind;
                BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(HeaderSize + 1), entry.Size);
                BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(HeaderSize + 9), (ushort)name.Length);
                name.CopyTo(header, HeaderSize + 11);
                await target.SendClipboardFrameAsync(header.AsMemory(0, HeaderSize + 11 + name.Length), token).ConfigureAwait(false);

                if (entry.Data is { } data)
                {
                    for (var offset = 0; offset < data.Length; offset += ChunkSize)
                    {
                        var count = Math.Min(ChunkSize, data.Length - offset);
                        Frame(buffer, MsgData, id);
                        data.AsSpan(offset, count).CopyTo(buffer.AsSpan(HeaderSize));
                        await target.SendClipboardFrameAsync(buffer.AsMemory(0, HeaderSize + count), token).ConfigureAwait(false);
                    }
                }
                else if (entry.FilePath is { } path && entry.Size > 0)
                {
                    await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, ChunkSize, useAsync: true);
                    var remaining = entry.Size;
                    while (remaining > 0)
                    {
                        var read = await file.ReadAsync(buffer.AsMemory(HeaderSize, (int)Math.Min(ChunkSize, remaining)), token).ConfigureAwait(false);
                        if (read == 0)
                        {
                            throw new IOException($"{Path.GetFileName(path)} got shorter while it was being sent.");
                        }
                        Frame(buffer, MsgData, id);
                        await target.SendClipboardFrameAsync(buffer.AsMemory(0, HeaderSize + read), token).ConfigureAwait(false);
                        remaining -= read;
                    }
                }
            }

            await target.SendClipboardFrameAsync(Frame(buffer, MsgEnd, id).AsMemory(0, HeaderSize), token).ConfigureAwait(false);
            lock (_lock)
            {
                _lastFingerprint = fingerprint;
            }
            Log.Info($"Clipboard sent to {target.PeerName} ({DescribeSize(total)}{(fileCount > 0 ? $", {fileCount} files" : "")})");
        }
        catch (OperationCanceledException)
        {
            await TryCancelAsync(target, id, begun).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Sending the clipboard to {target.PeerName} failed: {e.Message}");
            ForgetFailedSync(sequence);
            await TryCancelAsync(target, id, begun).ConfigureAwait(false);
            Raise(new ClipboardTransferEvent(ClipboardTransferKind.Failed, target.PeerName, 0, 0, e.Message));
        }
        catch (Exception e)
        {
            Log.Error($"Sending the clipboard to {target.PeerName} failed", e);
            ForgetFailedSync(sequence);
            await TryCancelAsync(target, id, begun).ConfigureAwait(false);
        }
    }

    /// <summary>A failed send must not count as synced, so the next switch tries again.</summary>
    private void ForgetFailedSync(uint? sequence)
    {
        lock (_lock)
        {
            if (sequence is { } s && _lastSyncedSequence == s)
            {
                _hasSynced = false;
            }
        }
    }

    private List<OutgoingEntry> BuildEntries(ClipboardContent content, ClipboardOptions options, string peerName)
    {
        var entries = new List<OutgoingEntry>();
        long total = 0;
        foreach (var (name, data) in content.Formats)
        {
            if (!ClipboardFormats.IsKnown(name) || data.Length == 0 || data.Length > MaxFormatBytes)
            {
                continue;
            }
            var bytes = data;
            byte kind = KindFormat;
            if (ClipboardFormats.IsCompressible(name) && data.Length > 64 * 1024)
            {
                var compressed = Compress(data);
                if (compressed.Length < data.Length * 9 / 10)
                {
                    bytes = compressed;
                    kind |= KindCompressed;
                }
            }
            entries.Add(new OutgoingEntry(kind, name, bytes.Length, bytes, null));
            total += bytes.Length;
        }

        if (content.FilePaths.Count > 0 && options.IncludeFiles)
        {
            var files = new List<OutgoingEntry>();
            long fileBytes = 0;
            var tooLarge = false;
            foreach (var root in content.FilePaths)
            {
                if (!CollectFiles(root, files, ref fileBytes, options.MaxBytes - total))
                {
                    tooLarge = true;
                    break;
                }
            }
            if (tooLarge)
            {
                Log.Info($"Copied files are larger than {options.MaxSizeMb} MB; not sending them to {peerName}");
                Raise(new ClipboardTransferEvent(ClipboardTransferKind.TooLarge, peerName, 0, 0, $"{options.MaxSizeMb} MB"));
            }
            else
            {
                entries.AddRange(files);
                total += fileBytes;
            }
        }
        return entries;
    }

    /// <summary>Adds a file or a folder tree; false when the size budget is exceeded.</summary>
    private static bool CollectFiles(string root, List<OutgoingEntry> entries, ref long bytes, long budget)
    {
        var rootName = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
        if (string.IsNullOrEmpty(rootName))
        {
            return true; // e.g. a whole drive: not supported
        }
        if (File.Exists(root))
        {
            var length = new FileInfo(root).Length;
            bytes += length;
            entries.Add(new OutgoingEntry(KindFile, rootName, length, null, root));
            return bytes <= budget && entries.Count <= MaxEntries;
        }
        if (!Directory.Exists(root))
        {
            return true;
        }

        entries.Add(new OutgoingEntry(KindDirectory, rootName, 0, null, null));
        var pending = new Stack<(string Path, string Relative)>();
        pending.Push((root, rootName));
        while (pending.Count > 0)
        {
            var (dir, relative) = pending.Pop();
            var info = new DirectoryInfo(dir);
            foreach (var item in info.EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                var itemRelative = relative + "/" + item.Name;
                if (item is DirectoryInfo)
                {
                    entries.Add(new OutgoingEntry(KindDirectory, itemRelative, 0, null, null));
                    pending.Push((item.FullName, itemRelative));
                }
                else if (item is FileInfo file)
                {
                    bytes += file.Length;
                    entries.Add(new OutgoingEntry(KindFile, itemRelative, file.Length, null, file.FullName));
                }
                if (bytes > budget || entries.Count > MaxEntries)
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static byte[] Frame(byte[] buffer, byte message, uint id)
    {
        buffer[0] = message;
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(1), id);
        return buffer;
    }

    private static async Task TryCancelAsync(PeerLink target, uint id, bool begun)
    {
        if (!begun)
        {
            return;
        }
        try
        {
            var frame = new byte[HeaderSize];
            Frame(frame, MsgCancel, id);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await target.SendClipboardFrameAsync(frame, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // the connection is gone; the other side drops the partial transfer on disconnect
        }
    }

    // ------------------------------------------------------------------ receiving

    private sealed class Incoming
    {
        public uint Id;
        public bool Sensitive;
        public long DeclaredTotal;
        public int DeclaredEntries;
        public int EntriesSeen;
        public long BytesReceived;
        public string? Folder;
        public readonly List<string> Roots = new();
        public readonly HashSet<string> RootNames = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, byte[]> Formats = new(StringComparer.Ordinal);
        public int FileCount;

        // current entry
        public bool InEntry;
        public byte Kind;
        public string EntryName = "";
        public long Remaining;
        public MemoryStream? Buffer;
        public FileStream? File;
        public bool Skip;
    }

    private void OnFrame(PeerLink link, byte[] frame)
    {
        if (frame.Length < HeaderSize)
        {
            return;
        }
        var message = frame[0];
        var id = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(1));
        var body = frame.AsSpan(HeaderSize);

        Incoming? state;
        lock (_lock)
        {
            _incoming.TryGetValue(link, out state);
        }

        try
        {
            switch (message)
            {
                case MsgBegin:
                    Abort(link, state, reason: null);
                    Begin(link, id, body);
                    break;
                case MsgEntry when state is not null && state.Id == id:
                    StartEntry(state, body);
                    break;
                case MsgData when state is not null && state.Id == id:
                    AppendData(state, body);
                    break;
                case MsgEnd when state is not null && state.Id == id:
                    Finish(link, state);
                    break;
                case MsgCancel when state is not null && state.Id == id:
                    Abort(link, state, reason: null);
                    break;
                default:
                    break; // frames of a transfer we dropped or ignore
            }
        }
        catch (InvalidDataException e)
        {
            Log.Warn($"Rejected clipboard data from {link.PeerName}: {e.Message}");
            Abort(link, state, e.Message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Receiving the clipboard from {link.PeerName} failed: {e.Message}");
            Abort(link, state, e.Message);
        }
    }

    private void Begin(PeerLink link, uint id, ReadOnlySpan<byte> body)
    {
        var options = _options();
        if (!options.Enabled)
        {
            return;
        }
        if (body.Length < 13)
        {
            throw new InvalidDataException("Truncated start of transfer.");
        }
        var state = new Incoming
        {
            Id = id,
            Sensitive = (body[0] & FlagSensitive) != 0,
            DeclaredTotal = BinaryPrimitives.ReadInt64BigEndian(body[1..]),
            DeclaredEntries = BinaryPrimitives.ReadInt32BigEndian(body[9..]),
        };
        if (state.DeclaredTotal < 0 || state.DeclaredEntries is < 0 or > MaxEntries)
        {
            throw new InvalidDataException("Invalid transfer size.");
        }
        if (state.DeclaredTotal > options.MaxBytes + MaxFormatBytes)
        {
            throw new InvalidDataException($"Transfer of {DescribeSize(state.DeclaredTotal)} is over this computer's limit.");
        }
        lock (_lock)
        {
            _incoming[link] = state;
        }
        if (state.DeclaredTotal >= NotifyAboveBytes)
        {
            Raise(new ClipboardTransferEvent(ClipboardTransferKind.Receiving, link.PeerName, 0, state.DeclaredTotal));
        }
    }

    private void StartEntry(Incoming state, ReadOnlySpan<byte> body)
    {
        if (state.InEntry)
        {
            throw new InvalidDataException("New item before the previous one was complete.");
        }
        if (body.Length < 11)
        {
            throw new InvalidDataException("Truncated item header.");
        }
        var kindByte = body[0];
        var kind = (byte)(kindByte & ~KindCompressed);
        var size = BinaryPrimitives.ReadInt64BigEndian(body[1..]);
        var nameLength = BinaryPrimitives.ReadUInt16BigEndian(body[9..]);
        if (body.Length != 11 + nameLength || nameLength > MaxPathLength * 4)
        {
            throw new InvalidDataException("Bad item name.");
        }
        var name = Encoding.UTF8.GetString(body.Slice(11, nameLength));
        if (++state.EntriesSeen > state.DeclaredEntries || size < 0 || state.BytesReceived + size > state.DeclaredTotal)
        {
            throw new InvalidDataException("Items don't add up to the announced size.");
        }

        state.Kind = kind;
        state.EntryName = name;
        state.Remaining = size;
        state.Skip = false;
        state.Buffer = null;
        state.File = null;
        var options = _options();

        switch (kind)
        {
            case KindFormat:
                if (!ClipboardFormats.IsKnown(name) || size > MaxFormatBytes)
                {
                    state.Skip = true; // a format from a newer version: ignore its bytes
                }
                else
                {
                    // Grows as data arrives: don't trust the announced size for an upfront allocation.
                    state.Buffer = new MemoryStream((int)Math.Min(size, 4 * 1024 * 1024));
                    if ((kindByte & KindCompressed) != 0)
                    {
                        state.Kind = KindFormat | KindCompressed;
                    }
                }
                break;

            case KindDirectory:
            case KindFile:
                if (!options.IncludeFiles)
                {
                    state.Skip = true;
                    break;
                }
                var relative = SanitizeRelativePath(name) ?? throw new InvalidDataException($"Unsafe file name \"{name}\".");
                if (kind == KindDirectory && size != 0)
                {
                    throw new InvalidDataException("A folder can't have content.");
                }
                var folder = state.Folder ??= CreateTransferFolder(state.Id);
                var full = Path.GetFullPath(Path.Combine(folder, relative));
                if (!full.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Unsafe file name \"{name}\".");
                }
                var rootName = relative.Split(Path.DirectorySeparatorChar)[0];
                if (state.RootNames.Add(rootName))
                {
                    state.Roots.Add(Path.Combine(folder, rootName));
                }
                if (kind == KindDirectory)
                {
                    Directory.CreateDirectory(full);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    state.File = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None, ChunkSize);
                    state.FileCount++;
                }
                break;

            default:
                throw new InvalidDataException($"Unknown item type {kind}.");
        }

        state.InEntry = true;
        if (size == 0)
        {
            CompleteEntry(state);
        }
    }

    private static void AppendData(Incoming state, ReadOnlySpan<byte> data)
    {
        if (!state.InEntry || data.Length == 0 || data.Length > state.Remaining)
        {
            throw new InvalidDataException("More data than announced.");
        }
        if (!state.Skip)
        {
            if (state.File is { } file)
            {
                file.Write(data);
            }
            else
            {
                state.Buffer?.Write(data);
            }
        }
        state.Remaining -= data.Length;
        state.BytesReceived += data.Length;
        if (state.Remaining == 0)
        {
            CompleteEntry(state);
        }
    }

    private static void CompleteEntry(Incoming state)
    {
        state.InEntry = false;
        if (state.File is { } file)
        {
            file.Dispose();
            state.File = null;
        }
        if (state.Buffer is { } buffer && !state.Skip)
        {
            var bytes = buffer.ToArray();
            if ((state.Kind & KindCompressed) != 0)
            {
                bytes = Decompress(bytes, MaxFormatBytes);
            }
            state.Formats[state.EntryName] = bytes;
            state.Buffer = null;
        }
    }

    private void Finish(PeerLink link, Incoming state)
    {
        if (state.InEntry || state.EntriesSeen != state.DeclaredEntries || state.BytesReceived != state.DeclaredTotal)
        {
            throw new InvalidDataException("Transfer ended early.");
        }
        lock (_lock)
        {
            _incoming.Remove(link);
        }

        var content = new ClipboardContent { Sensitive = state.Sensitive };
        foreach (var (name, data) in state.Formats)
        {
            content.Formats[name] = data;
        }
        content.FilePaths.AddRange(state.Roots);
        if (content.IsEmpty)
        {
            DeleteFolder(state.Folder);
            return;
        }

        _platform.Write(content);
        lock (_lock)
        {
            _hasSynced = true;
            _lastSyncedSequence = _platform.SequenceNumber;
            _lastFingerprint = Fingerprint(content);
        }
        CleanUpOldTransfers(TimeSpan.Zero, keep: state.Folder);
        Log.Info($"Clipboard received from {link.PeerName} ({DescribeSize(state.BytesReceived)}{(state.FileCount > 0 ? $", {state.FileCount} files" : "")})");
        if (state.FileCount > 0 || state.BytesReceived >= NotifyAboveBytes)
        {
            Raise(new ClipboardTransferEvent(ClipboardTransferKind.Received, link.PeerName, state.FileCount, state.BytesReceived));
        }
    }

    private void Abort(PeerLink link, Incoming? state, string? reason)
    {
        if (state is null)
        {
            return;
        }
        lock (_lock)
        {
            if (_incoming.TryGetValue(link, out var current) && current == state)
            {
                _incoming.Remove(link);
            }
        }
        state.File?.Dispose();
        state.File = null;
        DeleteFolder(state.Folder);
        if (reason is not null)
        {
            Raise(new ClipboardTransferEvent(ClipboardTransferKind.Failed, link.PeerName, 0, 0, reason));
        }
    }

    private void OnLinkClosed(PeerLink link)
    {
        link.ClipboardFrameReceived -= OnFrame;
        link.Closed -= OnLinkClosed;
        Incoming? state;
        lock (_lock)
        {
            _incoming.TryGetValue(link, out state);
        }
        Abort(link, state, reason: null);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Turns a received "folder/sub/file.txt" into a safe relative path, or null if it could
    /// escape the transfer folder or isn't a valid Windows file name.
    /// </summary>
    internal static string? SanitizeRelativePath(string name)
    {
        if (name.Length == 0 || name.Length > MaxPathLength)
        {
            return null;
        }
        var segments = name.Split('/');
        if (segments.Length > MaxPathDepth)
        {
            return null;
        }
        foreach (var segment in segments)
        {
            if (!IsSafeFileName(segment))
            {
                return null;
            }
        }
        return string.Join(Path.DirectorySeparatorChar, segments);
    }

    private static readonly char[] InvalidNameChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static bool IsSafeFileName(string segment)
    {
        if (segment.Length == 0 || segment.Length > 255 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' '))
        {
            return false;
        }
        foreach (var c in segment)
        {
            if (c < 32 || Array.IndexOf(InvalidNameChars, c) >= 0)
            {
                return false;
            }
        }
        var stem = segment.Split('.')[0].ToUpperInvariant();
        return stem is not ("CON" or "PRN" or "AUX" or "NUL")
            && !(stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && char.IsDigit(stem[3]));
    }

    private string CreateTransferFolder(uint id)
    {
        var folder = Path.Combine(_receiveRoot, $"{DateTime.Now:yyyyMMdd-HHmmss}-{id}");
        Directory.CreateDirectory(folder);
        return Path.GetFullPath(folder);
    }

    /// <summary>Deletes received files that are no longer on the clipboard.</summary>
    private void CleanUpOldTransfers(TimeSpan olderThan, string? keep = null)
    {
        try
        {
            if (!Directory.Exists(_receiveRoot))
            {
                return;
            }
            var cutoff = DateTime.Now - olderThan;
            foreach (var folder in Directory.EnumerateDirectories(_receiveRoot))
            {
                if (keep is not null && string.Equals(Path.GetFullPath(folder), keep, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                lock (_lock)
                {
                    if (_incoming.Values.Any(s => s.Folder is { } f && string.Equals(f, Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase)))
                    {
                        continue; // a transfer in progress
                    }
                }
                if (Directory.GetLastWriteTime(folder) <= cutoff)
                {
                    DeleteFolder(folder);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Cleaning up received clipboard files failed: {e.Message}");
        }
    }

    private static void DeleteFolder(string? folder)
    {
        if (folder is null)
        {
            return;
        }
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Still in use (e.g. being pasted); cleaned up next time.
        }
    }

    internal static string Fingerprint(ClipboardContent content)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        foreach (var (name, data) in content.Formats.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(name));
            BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
            hash.AppendData(length);
            hash.AppendData(data);
        }
        foreach (var path in content.FilePaths)
        {
            hash.AppendData(Encoding.UTF8.GetBytes("file:" + path));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream(data.Length / 2);
        using (var zlib = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(data);
        }
        return output.ToArray();
    }

    private static byte[] Decompress(byte[] data, int maxBytes)
    {
        try
        {
            using var input = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = input.Read(buffer)) > 0)
            {
                if (output.Length + read > maxBytes)
                {
                    throw new InvalidDataException("Decompressed data is too large.");
                }
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        catch (Exception e) when (e is not InvalidDataException)
        {
            throw new InvalidDataException("Corrupt compressed data.");
        }
    }

    private static string DescribeSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };

    private void Raise(ClipboardTransferEvent e)
    {
        try
        {
            TransferEvent?.Invoke(e);
        }
        catch (Exception ex)
        {
            Log.Error("Clipboard notification failed", ex);
        }
    }
}
