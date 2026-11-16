namespace Latch.Core.Storage;

/// <summary>Where Latch keeps user data: %LOCALAPPDATA%\Latch, apart from the install folder.</summary>
public sealed record DataPaths(string Root)
{
    /// <summary>LATCH_DATA_DIR (for development and testing) or %LOCALAPPDATA%\Latch.</summary>
    public static DataPaths Default() =>
        new(Environment.GetEnvironmentVariable("LATCH_DATA_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Latch"));

    public string Settings => Path.Combine(Root, "settings.json");
    public string Secrets => Path.Combine(Root, "secrets.json");
    public string History => Path.Combine(Root, "history");
    public string Heartbeat => Path.Combine(Root, "heartbeat");
    public string Cache => Path.Combine(Root, "cache");
    public string Icons => Path.Combine(Root, "cache", "icons");
    public string WebView => Path.Combine(Root, "cache", "webview2");
    public string Log => Path.Combine(Root, "latch.log");
}
