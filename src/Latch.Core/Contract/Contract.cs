// The one contract between the host and the UI. TypeScript types in web/src/contract.gen.ts are generated from this
// file by tools/Latch.ContractGen; change the contract only here. Enums travel as camelCase strings, nullable
// members are always present as `T | null`, dates are ISO 8601 strings.

using System.Text.Json.Serialization;

namespace Latch.Core.Contract;

[JsonConverter(typeof(CamelEnum<RuleKind>))]
public enum RuleKind { Exe, Package }

[JsonConverter(typeof(CamelEnum<AppStatus>))]
public enum AppStatus { Locked, Unlocked, Grace, Hidden }

[JsonConverter(typeof(CamelEnum<ThemeChoice>))]
public enum ThemeChoice { System, Light, Dark }

[JsonConverter(typeof(CamelEnum<UnlockMethod>))]
public enum UnlockMethod { Pin, Hello, Passkey }

[JsonConverter(typeof(CamelEnum<HistoryKind>))]
public enum HistoryKind { Unlocked, Failed, LockoutStarted, PromptCancelled, LatchStarted, LatchQuit, LatchNotRunning }

[JsonConverter(typeof(CamelEnum<AttentionKind>))]
public enum AttentionKind { NotRunning, Lockout, AppMissing }

/// <summary>Why a prompt is on screen. <see cref="App"/> guards a locked app; the rest guard Latch itself.</summary>
[JsonConverter(typeof(CamelEnum<PromptPurpose>))]
public enum PromptPurpose { App, OpenLatch, Quit, ChangePin, TurnOffHello, RemovePasskey, TurnOffStartup }

/// <summary><see cref="NotVerified"/>: Windows Hello or the passkey dialog was cancelled or failed.</summary>
[JsonConverter(typeof(CamelEnum<UnlockOutcome>))]
public enum UnlockOutcome { Ok, Wrong, LockedOut, NotVerified, Gone }

[JsonConverter(typeof(CamelEnum<ErrorCode>))]
public enum ErrorCode
{
    Unexpected,
    PinFormat,
    NeedsUnlock,
    PasskeyFailed,
    PasskeyUnavailable,
    HelloUnavailable,
    HelloFailed,
    NotAProgram,
    NotFound,
}

[JsonConverter(typeof(CamelEnum<FixKind>))]
public enum FixKind { TryAgain, Unlock, Remove }

public sealed record Empty
{
    public static readonly Empty Value = new();
}

public sealed record FixAction(FixKind Kind, string Label);

public sealed record ApiError(ErrorCode Code, string Message, FixAction? Fix);

// ---- Pushed state -------------------------------------------------------------------------------------------------

/// <summary>One locked app as the Apps page shows it.</summary>
public sealed record AppRow(string Id, string Name, string? IconUrl, AppStatus Status, DateTimeOffset? RelocksAt);

/// <summary>Something the owner should look at. Gone only when fixed or dismissed.</summary>
public sealed record AttentionItem(
    string Id,
    AttentionKind Kind,
    string? AppId,
    string? AppName,
    DateTimeOffset At,
    DateTimeOffset? From);

/// <param name="LockAllHotkeyTaken">The shortcut is on but another app already owns it.</param>
public sealed record SettingsView(int GraceMinutes, bool RelockOnClose, int IdleMinutes, bool StartWithWindows, ThemeChoice Theme, bool LockAllHotkey, bool LockAllHotkeyTaken);

public sealed record MethodsView(bool PinSet, bool HelloAvailable, bool HelloOn, bool PasskeyAvailable, bool PasskeyOn);

/// <summary>The whole main-window state. Small, rebuilt from memory only, pushed on every change.</summary>
public sealed record AppSnapshot(
    bool SetupDone,
    IReadOnlyList<AppRow> Apps,
    IReadOnlyList<AttentionItem> Attention,
    SettingsView Settings,
    MethodsView Methods,
    long HistoryRevision,
    string Version);

/// <summary>What the unlock prompt shows. Null when no prompt is active.</summary>
public sealed record PromptView(
    string PromptId,
    PromptPurpose Purpose,
    string AppName,
    string? Icon,
    bool HelloOn,
    bool PasskeyOn,
    DateTimeOffset? LockoutUntil,
    ThemeChoice Theme,
    int? PinLength);

// ---- Collections fetched on demand --------------------------------------------------------------------------------

public sealed record InstalledApp(
    string Key,
    string Name,
    RuleKind Kind,
    string? Path,
    string? PackageFamilyName,
    string? Aumid,
    string? IconUrl,
    bool Locked);

public sealed record HistoryEntry(
    DateTimeOffset At,
    HistoryKind Kind,
    string? App,
    UnlockMethod? Method,
    DateTimeOffset? From);

// ---- Method arguments and results ---------------------------------------------------------------------------------

public sealed record PinArgs(string Pin);

public sealed record IdArgs(string Id);

public sealed record PurposeArgs(PromptPurpose Purpose);

public sealed record SetLockedAppsArgs(IReadOnlyList<InstalledApp> Apps);

public sealed record SettingsPatch(int? GraceMinutes, bool? RelockOnClose, int? IdleMinutes, bool? StartWithWindows, ThemeChoice? Theme, bool? LockAllHotkey);

public sealed record PromptPinArgs(string PromptId, string Pin);

public sealed record PromptIdArgs(string PromptId);

/// <summary>The prompt painted its content; the host sizes the window to <see cref="Height"/> (CSS px) and reveals it.</summary>
public sealed record PromptPaintedArgs(string PromptId, int Height);

public sealed record UnlockResult(UnlockOutcome Outcome, DateTimeOffset? LockoutUntil);

/// <summary>
/// Every method the UI can call. The host serves them through one wrapper that turns exceptions into
/// <see cref="ApiError"/>. Main-window methods first, then the prompt's.
/// </summary>
public interface ILatchApi
{
    Task<AppSnapshot> GetState(Empty args);
    Task<Empty> WindowReady(Empty args);

    Task<Empty> SetPin(PinArgs args);
    Task<bool> TurnOnHello(Empty args);
    Task<Empty> TurnOffHello(Empty args);
    Task<bool> AddPasskey(Empty args);
    Task<Empty> RemovePasskey(Empty args);
    Task<Empty> FinishSetup(Empty args);

    Task<IReadOnlyList<InstalledApp>> ListInstalledApps(Empty args);
    Task<InstalledApp?> BrowseForProgram(Empty args);
    Task<Empty> SetLockedApps(SetLockedAppsArgs args);
    Task<Empty> RemoveApp(IdArgs args);
    Task<Empty> ShowApp(IdArgs args);

    Task<IReadOnlyList<HistoryEntry>> GetHistory(Empty args);
    Task<Empty> DismissAttention(IdArgs args);
    Task<Empty> UpdateSettings(SettingsPatch args);
    Task<bool> RequestUnlock(PurposeArgs args);

    Task<UnlockResult> SubmitPin(PromptPinArgs args);
    Task<UnlockResult> UsePasskey(PromptIdArgs args);
    Task<UnlockResult> UseHello(PromptIdArgs args);
    Task<Empty> CancelPrompt(PromptIdArgs args);
    Task<Empty> PromptPainted(PromptPaintedArgs args);
}

/// <summary>Events pushed from the host. Names are the `event` field of the envelope.</summary>
public static class ServerEvents
{
    public const string State = "state";
    public const string Prompt = "prompt";
}
