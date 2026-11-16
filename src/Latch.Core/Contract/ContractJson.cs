using System.Text.Json;
using System.Text.Json.Serialization;
using Latch.Core.Model;

namespace Latch.Core.Contract;

/// <summary>Source-generated serialization for the wire and for files on disk. No reflection at runtime.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    WriteIndented = false)]
[JsonSerializable(typeof(Empty))]
[JsonSerializable(typeof(ApiError))]
[JsonSerializable(typeof(AppSnapshot))]
[JsonSerializable(typeof(PromptView))]
[JsonSerializable(typeof(InstalledApp))]
[JsonSerializable(typeof(IReadOnlyList<InstalledApp>))]
[JsonSerializable(typeof(HistoryEntry))]
[JsonSerializable(typeof(IReadOnlyList<HistoryEntry>))]
[JsonSerializable(typeof(PinArgs))]
[JsonSerializable(typeof(IdArgs))]
[JsonSerializable(typeof(PurposeArgs))]
[JsonSerializable(typeof(SetLockedAppsArgs))]
[JsonSerializable(typeof(SettingsPatch))]
[JsonSerializable(typeof(PromptPinArgs))]
[JsonSerializable(typeof(PromptIdArgs))]
[JsonSerializable(typeof(PromptPaintedArgs))]
[JsonSerializable(typeof(UnlockResult))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(LockRule))]
[JsonSerializable(typeof(List<LockRule>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(SecretsFile))]
[JsonSerializable(typeof(HeartbeatFile))]
[JsonSerializable(typeof(ThemeChoice))]
[JsonSerializable(typeof(JsonElement))]
public sealed partial class ContractJson : JsonSerializerContext;
