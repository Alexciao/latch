using System.Text;
using System.Text.Json;
using Latch.Core.Contract;

namespace Latch.Core.Storage;

/// <summary>
/// Append-only unlock history as JSON Lines. The current file rotates at about 1 MB; the last 5 files are kept
/// (history.jsonl, history.1.jsonl … history.4.jsonl).
/// </summary>
public sealed class HistoryLog
{
    public const long RotateBytes = 1024 * 1024;
    public const int KeepFiles = 5;

    private readonly string _dir;
    private readonly Lock _gate = new();
    private List<HistoryEntry>? _cache;
    private long _revision;

    public HistoryLog(string dir) => _dir = dir;

    public long Revision => Interlocked.Read(ref _revision);

    private string FileAt(int index) => Path.Combine(_dir, index == 0 ? "history.jsonl" : $"history.{index}.jsonl");

    public void Append(HistoryEntry entry)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_dir);
            var current = FileAt(0);
            if (File.Exists(current) && new FileInfo(current).Length >= RotateBytes) Rotate();
            var line = JsonSerializer.Serialize(entry, ContractJson.Default.HistoryEntry) + "\n";
            File.AppendAllText(current, line, Encoding.UTF8);
            _cache?.Add(entry);
            Interlocked.Increment(ref _revision);
        }
    }

    private void Rotate()
    {
        var oldest = FileAt(KeepFiles - 1);
        if (File.Exists(oldest)) File.Delete(oldest);
        for (var i = KeepFiles - 2; i >= 0; i--)
        {
            var from = FileAt(i);
            if (File.Exists(from)) File.Move(from, FileAt(i + 1));
        }
    }

    /// <summary>All entries, oldest first. Cached after the first read.</summary>
    public IReadOnlyList<HistoryEntry> ReadAll()
    {
        lock (_gate)
        {
            if (_cache is null)
            {
                var list = new List<HistoryEntry>();
                for (var i = KeepFiles - 1; i >= 0; i--)
                {
                    var file = FileAt(i);
                    if (!File.Exists(file)) continue;
                    foreach (var line in File.ReadLines(file))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        try
                        {
                            if (JsonSerializer.Deserialize(line, ContractJson.Default.HistoryEntry) is { } e) list.Add(e);
                        }
                        catch (JsonException)
                        {
                            // A torn last line after a crash is skipped.
                        }
                    }
                }
                _cache = list;
            }
            return _cache.ToList();
        }
    }
}
