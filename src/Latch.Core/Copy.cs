using System.Text.Json;

namespace Latch.Core;

/// <summary>Reads strings from the one copy file (web/src/copy.json, embedded). Keys are dotted paths.</summary>
public static class Copy
{
    private static readonly Lazy<JsonDocument> Doc = new(() =>
    {
        using var stream = typeof(Copy).Assembly.GetManifestResourceStream("Latch.copy.json")
            ?? throw new InvalidOperationException("copy.json is not embedded.");
        return JsonDocument.Parse(stream);
    });

    public static string Get(string key)
    {
        var node = Doc.Value.RootElement;
        foreach (var part in key.Split('.'))
        {
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(part, out node)) return key;
        }
        return node.ValueKind == JsonValueKind.String ? node.GetString()! : key;
    }

    public static string Format(string key, params (string Name, string Value)[] args)
    {
        var text = Get(key);
        foreach (var (name, value) in args) text = text.Replace("{" + name + "}", value, StringComparison.Ordinal);
        return text;
    }
}
