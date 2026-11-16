namespace Latch.Core.Storage;

/// <summary>
/// Atomic file writes: write a temp file, flush to disk, then replace the target, keeping the previous version as
/// `.bak`. Retries for a few seconds because antivirus and indexers briefly lock files.
/// </summary>
public static class AtomicFile
{
    public static void Write(string path, ReadOnlySpan<byte> bytes)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var temp = path + ".tmp";
        using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(bytes);
            fs.Flush(flushToDisk: true);
        }
        var backup = path + ".bak";
        Retry(() =>
        {
            if (File.Exists(path)) File.Replace(temp, path, backup, ignoreMetadataErrors: true);
            else File.Move(temp, path);
        });
    }

    /// <summary>Reads the file, or its `.bak` when the main file is missing or rejected by <paramref name="accept"/>.</summary>
    public static T? Read<T>(string path, Func<byte[], T?> parse) where T : class
    {
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                var result = parse(File.ReadAllBytes(candidate));
                if (result is not null) return result;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (System.Text.Json.JsonException) { }
        }
        return null;
    }

    private static void Retry(Action action)
    {
        var deadline = Environment.TickCount64 + 3000;
        while (true)
        {
            try
            {
                action();
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && Environment.TickCount64 < deadline)
            {
                Thread.Sleep(50);
            }
        }
    }
}
