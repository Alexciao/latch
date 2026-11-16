using Latch.Core.Locking;

namespace Latch.Core.Tests;

public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

public sealed class FakeInspector : IWindowInspector
{
    public Dictionary<nint, WindowFacts> Windows { get; } = [];
    public Dictionary<string, (string? Product, string? Publisher)> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int PublisherReads { get; private set; }

    public WindowFacts? Inspect(nint hwnd) => Windows.GetValueOrDefault(hwnd);
    public string? ProductName(string path) => Files.TryGetValue(path, out var f) ? f.Product : null;

    public string? Publisher(string path)
    {
        PublisherReads++;
        return Files.TryGetValue(path, out var f) ? f.Publisher : null;
    }
}

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "latch-tests-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, true);
        }
        catch (IOException)
        {
        }
    }
}
