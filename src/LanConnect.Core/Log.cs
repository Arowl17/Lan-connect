namespace LanConnect.Core;

/// <summary>Minimal logging hook; the app routes it to a log file.</summary>
public static class Log
{
    public static event Action<string, string>? Written;

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}: {exception.GetType().Name}: {exception.Message}");

    private static void Write(string level, string message)
    {
        try
        {
            Written?.Invoke(level, message);
        }
        catch
        {
            // logging must never break the caller
        }
    }
}
