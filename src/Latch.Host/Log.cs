namespace Latch.Host;

/// <summary>
/// A tiny local log (%LOCALAPPDATA%\Latch\latch.log, trimmed at 512 KB). It records versions, events and errors;
/// it never records PINs, codes, secrets or hashes, and callers only pass method names and exceptions.
/// </summary>
internal static class Log
{
    private static readonly Lock Gate = new();
    private static string? _path;

    public static void Init(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024) File.Move(path, path + ".old", overwrite: true);
        }
        catch (IOException)
        {
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string context, Exception e) => Write("ERROR", $"{context}: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");

    private static void Write(string level, string message)
    {
        if (_path is null) return;
        lock (Gate)
        {
            try
            {
                File.AppendAllText(_path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}\n");
            }
            catch (IOException)
            {
            }
        }
    }
}
