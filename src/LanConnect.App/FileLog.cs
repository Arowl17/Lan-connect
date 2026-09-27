using System.IO;
using System.Text;
using LanConnect.Core;
using LanConnect.Windows;

namespace LanConnect.App;

/// <summary>Writes <see cref="Log"/> output to %LOCALAPPDATA%\LanConnect\logs\lanconnect.log.</summary>
internal static class FileLog
{
    private const long MaxSize = 2 * 1024 * 1024;
    private static readonly object Gate = new();
    private static string? s_path;

    public static string Path => s_path ?? System.IO.Path.Combine(AppPaths.LogDirectory, "lanconnect.log");

    public static void Start()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDirectory);
            s_path = System.IO.Path.Combine(AppPaths.LogDirectory, "lanconnect.log");
            Log.Written += Write;
        }
        catch (IOException)
        {
        }
    }

    private static void Write(string level, string message)
    {
        if (s_path is null)
        {
            return;
        }
        lock (Gate)
        {
            try
            {
                var info = new FileInfo(s_path);
                if (info.Exists && info.Length > MaxSize)
                {
                    File.Move(s_path, s_path + ".old", overwrite: true);
                }
                File.AppendAllText(s_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level,-5} {message}{Environment.NewLine}", Encoding.UTF8);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
