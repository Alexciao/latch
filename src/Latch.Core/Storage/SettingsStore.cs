using System.Text.Json;
using Latch.Core.Contract;
using Latch.Core.Model;

namespace Latch.Core.Storage;

/// <summary>
/// settings.json: loaded field by field over defaults so one bad value drops only that field, written atomically
/// with a `.bak`. Writes are serialized so two quick changes can't overwrite each other.
/// </summary>
public sealed class SettingsStore(string path)
{
    private readonly Lock _gate = new();

    public LatchSettings Load()
    {
        var parsed = AtomicFile.Read(path, bytes => Parse(bytes));
        return parsed ?? new LatchSettings();
    }

    public void Save(LatchSettings settings)
    {
        lock (_gate) AtomicFile.Write(path, Serialize(settings));
    }

    /// <summary>Returns null only when the bytes aren't a JSON object at all (then `.bak` is tried).</summary>
    internal static LatchSettings? Parse(byte[] bytes)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            return null;
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var d = new LatchSettings();
            return new LatchSettings
            {
                Rules = ReadRules(root),
                GraceMinutes = Int(root, "graceMinutes", LatchSettings.GraceChoices) ?? d.GraceMinutes,
                RelockOnClose = Bool(root, "relockOnClose") ?? d.RelockOnClose,
                IdleMinutes = Int(root, "idleMinutes", LatchSettings.IdleChoices) ?? d.IdleMinutes,
                StartWithWindows = Bool(root, "startWithWindows") ?? d.StartWithWindows,
                Theme = Enum<ThemeChoice>(root, "theme") ?? d.Theme,
                LockAllHotkey = Bool(root, "lockAllHotkey") ?? d.LockAllHotkey,
                SetupDone = Bool(root, "setupDone") ?? d.SetupDone,
                TrayHintShown = Bool(root, "trayHintShown") ?? d.TrayHintShown,
                Dismissed = Strings(root, "dismissed") ?? d.Dismissed,
            };
        }
    }

    internal static byte[] Serialize(LatchSettings s)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", LatchSettings.SchemaVersion);
            w.WritePropertyName("rules");
            JsonSerializer.Serialize(w, s.Rules.ToList(), ContractJson.Default.ListLockRule);
            w.WriteNumber("graceMinutes", s.GraceMinutes);
            w.WriteBoolean("relockOnClose", s.RelockOnClose);
            w.WriteNumber("idleMinutes", s.IdleMinutes);
            w.WriteBoolean("startWithWindows", s.StartWithWindows);
            w.WriteString("theme", JsonNamingPolicy.CamelCase.ConvertName(s.Theme.ToString()));
            w.WriteBoolean("lockAllHotkey", s.LockAllHotkey);
            w.WriteBoolean("setupDone", s.SetupDone);
            w.WriteBoolean("trayHintShown", s.TrayHintShown);
            w.WritePropertyName("dismissed");
            JsonSerializer.Serialize(w, s.Dismissed.ToList(), ContractJson.Default.ListString);
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    private static List<LockRule> ReadRules(JsonElement root)
    {
        var rules = new List<LockRule>();
        if (!root.TryGetProperty("rules", out var arr) || arr.ValueKind != JsonValueKind.Array) return rules;
        foreach (var item in arr.EnumerateArray())
        {
            try
            {
                var rule = item.Deserialize(ContractJson.Default.LockRule);
                if (rule is null || string.IsNullOrWhiteSpace(rule.Id) || string.IsNullOrWhiteSpace(rule.Name)) continue;
                if (rule.Kind == RuleKind.Exe && string.IsNullOrWhiteSpace(rule.Path)) continue;
                if (rule.Kind == RuleKind.Package && string.IsNullOrWhiteSpace(rule.PackageFamilyName)) continue;
                rules.Add(rule);
            }
            catch (JsonException)
            {
                // One broken rule is dropped; the others still load.
            }
        }
        return rules;
    }

    private static int? Int(JsonElement root, string name, int[] allowed) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) && allowed.Contains(i) ? i : null;

    private static bool? Bool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static T? Enum<T>(JsonElement root, string name) where T : struct, System.Enum =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && System.Enum.TryParse<T>(v.GetString(), ignoreCase: true, out var e) && System.Enum.IsDefined(e) ? e : null;

    private static List<string>? Strings(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return null;
        return v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList();
    }
}
