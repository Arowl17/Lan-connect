namespace LanConnect.Core.Clipboard;

/// <summary>The clipboard formats carried between computers (platform-neutral names).</summary>
public static class ClipboardFormats
{
    /// <summary>Plain text, UTF-8.</summary>
    public const string Text = "text";

    /// <summary>Windows "HTML Format" (CF_HTML) text, UTF-8: formatted text from browsers and Office.</summary>
    public const string Html = "html";

    /// <summary>Rich Text Format bytes (WordPad, Outlook, ...).</summary>
    public const string Rtf = "rtf";

    /// <summary>A PNG image (screenshots, "copy image" in browsers).</summary>
    public const string Png = "png";

    /// <summary>A Windows device-independent bitmap (CF_DIB), for apps that only offer that.</summary>
    public const string Dib = "dib";

    public static bool IsKnown(string name) => name is Text or Html or Rtf or Png or Dib;

    /// <summary>Formats worth compressing on the wire (PNG already is).</summary>
    public static bool IsCompressible(string name) => name is not Png;
}

public sealed class ClipboardContent
{
    public Dictionary<string, byte[]> Formats { get; } = new(StringComparer.Ordinal);

    /// <summary>Full local paths of the copied files and folders (top-level items only).</summary>
    public List<string> FilePaths { get; } = new();

    /// <summary>
    /// Marked private by the source app (e.g. a password manager). It is still shared with your
    /// own paired computer, but kept out of clipboard history there as well.
    /// </summary>
    public bool Sensitive { get; set; }

    public bool IsEmpty => Formats.Count == 0 && FilePaths.Count == 0;
}

/// <summary>Access to this computer's clipboard. Implemented by the Windows app.</summary>
public interface IClipboardPlatform
{
    /// <summary>Changes whenever anything puts new content on the clipboard.</summary>
    uint SequenceNumber { get; }

    /// <summary>Reads the clipboard; null when it is empty or holds nothing we can share.</summary>
    ClipboardContent? Read();

    /// <summary>Replaces the clipboard content.</summary>
    void Write(ClipboardContent content);
}

public sealed class ClipboardOptions
{
    /// <summary>Bring the clipboard along when control moves to another computer.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Also transfer copied files and folders.</summary>
    public bool IncludeFiles { get; set; } = true;

    /// <summary>Largest total transfer, in megabytes. Bigger file copies are skipped.</summary>
    public int MaxSizeMb { get; set; } = 1024;

    public long MaxBytes => Math.Max(1, MaxSizeMb) * 1024L * 1024L;
}

public enum ClipboardTransferKind
{
    /// <summary>Started receiving a large transfer (files or a big image).</summary>
    Receiving,

    /// <summary>A transfer arrived and is on the clipboard.</summary>
    Received,

    /// <summary>Copied files were too large to send (see <see cref="ClipboardOptions.MaxSizeMb"/>).</summary>
    TooLarge,

    /// <summary>A transfer failed or was rejected.</summary>
    Failed,
}

public sealed record ClipboardTransferEvent(ClipboardTransferKind Kind, string PeerName, int FileCount, long TotalBytes, string? Detail = null);
