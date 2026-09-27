using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LanConnect.Core.Clipboard;

namespace LanConnect.App;

/// <summary>
/// The Windows clipboard, accessed on a dedicated STA thread (OLE clipboard calls require one,
/// and this keeps the UI thread free).
/// </summary>
internal sealed partial class WpfClipboard : IClipboardPlatform, IDisposable
{
    private const string PngFormat = "PNG";
    private const string PreferredDropEffect = "Preferred DropEffect";
    private const string ExcludeFromMonitoring = "ExcludeClipboardContentFromMonitorProcessing";
    private const string CanIncludeInHistory = "CanIncludeInClipboardHistory";
    private const string CanUploadToCloud = "CanUploadToCloudClipboard";
    private const int DropEffectCopy = 1;

    private readonly Dispatcher _dispatcher;

    public WpfClipboard()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "LanConnect clipboard",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        _dispatcher = dispatcher!;
    }

    public uint SequenceNumber => GetClipboardSequenceNumber();

    public ClipboardContent? Read() => _dispatcher.Invoke(ReadCore);

    public void Write(ClipboardContent content) => _dispatcher.Invoke(() => WriteCore(content));

    private static ClipboardContent? ReadCore()
    {
        var data = WithRetry(Clipboard.GetDataObject);
        if (data is null)
        {
            return null;
        }

        var content = new ClipboardContent
        {
            Sensitive = data.GetDataPresent(ExcludeFromMonitoring, false)
                || IsZeroFlag(data, CanIncludeInHistory)
                || IsZeroFlag(data, CanUploadToCloud),
        };

        if (data.GetDataPresent(DataFormats.FileDrop, false) && data.GetData(DataFormats.FileDrop, false) is string[] files)
        {
            content.FilePaths.AddRange(files);
        }
        AddText(data, DataFormats.UnicodeText, ClipboardFormats.Text, content);
        AddText(data, DataFormats.Html, ClipboardFormats.Html, content);
        AddText(data, DataFormats.Rtf, ClipboardFormats.Rtf, content);
        AddBytes(data, PngFormat, ClipboardFormats.Png, content);
        AddBytes(data, DataFormats.Dib, ClipboardFormats.Dib, content);
        return content.IsEmpty ? null : content;
    }

    private static void AddText(IDataObject data, string windowsFormat, string format, ClipboardContent content)
    {
        if (data.GetDataPresent(windowsFormat, false) && data.GetData(windowsFormat, false) is string text && text.Length > 0)
        {
            content.Formats[format] = Encoding.UTF8.GetBytes(text);
        }
    }

    private static void AddBytes(IDataObject data, string windowsFormat, string format, ClipboardContent content)
    {
        if (data.GetDataPresent(windowsFormat, false) && data.GetData(windowsFormat, false) is MemoryStream stream && stream.Length > 0)
        {
            content.Formats[format] = stream.ToArray();
        }
    }

    private static bool IsZeroFlag(IDataObject data, string format) =>
        data.GetDataPresent(format, false)
        && data.GetData(format, false) is MemoryStream stream
        && stream.Length >= 4
        && BitConverter.ToInt32(stream.ToArray(), 0) == 0;

    private static void WriteCore(ClipboardContent content)
    {
        var data = new DataObject();
        if (content.Formats.TryGetValue(ClipboardFormats.Text, out var text))
        {
            data.SetData(DataFormats.UnicodeText, Encoding.UTF8.GetString(text));
        }
        if (content.Formats.TryGetValue(ClipboardFormats.Html, out var html))
        {
            data.SetData(DataFormats.Html, Encoding.UTF8.GetString(html));
        }
        if (content.Formats.TryGetValue(ClipboardFormats.Rtf, out var rtf))
        {
            data.SetData(DataFormats.Rtf, Encoding.UTF8.GetString(rtf));
        }
        if (content.Formats.TryGetValue(ClipboardFormats.Png, out var png))
        {
            data.SetData(PngFormat, new MemoryStream(png));
        }
        if (content.Formats.TryGetValue(ClipboardFormats.Dib, out var dib))
        {
            data.SetData(DataFormats.Dib, new MemoryStream(dib));
        }
        else if (png is not null && DecodePng(png) is { } image)
        {
            data.SetImage(image); // for apps that only paste plain bitmaps
        }
        if (content.FilePaths.Count > 0)
        {
            var list = new StringCollection();
            list.AddRange(content.FilePaths.ToArray());
            data.SetFileDropList(list);
            data.SetData(PreferredDropEffect, new MemoryStream(BitConverter.GetBytes(DropEffectCopy)));
        }
        if (content.Sensitive)
        {
            // Keep passwords out of clipboard history and cloud sync on this computer too.
            data.SetData(ExcludeFromMonitoring, new MemoryStream(BitConverter.GetBytes(1)));
            data.SetData(CanIncludeInHistory, new MemoryStream(BitConverter.GetBytes(0)));
            data.SetData(CanUploadToCloud, new MemoryStream(BitConverter.GetBytes(0)));
        }
        WithRetry(() =>
        {
            Clipboard.SetDataObject(data, copy: true);
            return true;
        });
    }

    private static BitmapSource? DecodePng(byte[] png)
    {
        try
        {
            var decoder = new PngBitmapDecoder(new MemoryStream(png), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze();
            return frame;
        }
        catch (Exception e) when (e is FileFormatException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The clipboard is briefly locked while another program uses it: retry a few times.</summary>
    private static T WithRetry<T>(Func<T> action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (Exception e) when (e is COMException or ExternalException)
            {
                if (attempt >= 10)
                {
                    throw new IOException("The clipboard is in use by another program.", e);
                }
                Thread.Sleep(25 * attempt);
            }
        }
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetClipboardSequenceNumber();

    public void Dispose() => _dispatcher.InvokeShutdown();
}
