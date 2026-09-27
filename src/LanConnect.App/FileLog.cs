using System.IO;
using System.Text;
using System.Threading.Channels;
using LanConnect.Core;
using LanConnect.Windows;

namespace LanConnect.App;

/// <summary>
/// Writes <see cref="Log"/> output to %LOCALAPPDATA%\LanConnect\logs\lanconnect.log.
/// Messages are queued and written on a background task: logging can happen on the input hook
/// thread, which must never wait for the disk.
/// </summary>
internal static class FileLog
{
    private const long MaxSize = 2 * 1024 * 1024;
    private static readonly Channel<string> Queue = Channel.CreateBounded<string>(
        new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private static string? s_path;

    public static string Path => s_path ?? System.IO.Path.Combine(AppPaths.LogDirectory, "lanconnect.log");

    public static void Start()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDirectory);
            s_path = System.IO.Path.Combine(AppPaths.LogDirectory, "lanconnect.log");
            Log.Written += (level, message) =>
                Queue.Writer.TryWrite($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level,-5} {message}{Environment.NewLine}");
            _ = Task.Run(WriteLoopAsync);
        }
        catch (IOException)
        {
        }
    }

    private static async Task WriteLoopAsync()
    {
        var batch = new StringBuilder();
        while (await Queue.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            batch.Clear();
            while (Queue.Reader.TryRead(out var line))
            {
                batch.Append(line);
            }
            try
            {
                var info = new FileInfo(s_path!);
                if (info.Exists && info.Length > MaxSize)
                {
                    File.Move(s_path!, s_path + ".old", overwrite: true);
                }
                await File.AppendAllTextAsync(s_path!, batch.ToString(), Encoding.UTF8).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
