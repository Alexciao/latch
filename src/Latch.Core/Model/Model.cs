using Latch.Core.Contract;

namespace Latch.Core.Model;

/// <summary>A locked app. Exe rules match by full path (or same signer and product); package rules by family name.</summary>
public sealed record LockRule(
    string Id,
    RuleKind Kind,
    string Name,
    string? Path,
    string? PackageFamilyName,
    string? Aumid,
    string? IconKey,
    string? Publisher,
    string? ProductName);

/// <summary>Preferences and rules, as stored in settings.json.</summary>
public sealed record LatchSettings
{
    public const int SchemaVersion = 1;
    public static readonly int[] GraceChoices = [0, 1, 5, 15, 60];
    public static readonly int[] IdleChoices = [0, 5, 10, 15, 30, 60];

    public IReadOnlyList<LockRule> Rules { get; init; } = [];
    public int GraceMinutes { get; init; } = 5;
    public bool RelockOnClose { get; init; } = true;
    public int IdleMinutes { get; init; } = 10;
    public bool StartWithWindows { get; init; } = true;
    public ThemeChoice Theme { get; init; } = ThemeChoice.System;
    public bool LockAllHotkey { get; init; } = true;
    public bool SetupDone { get; init; }
    public bool TrayHintShown { get; init; }
    public IReadOnlyList<string> Dismissed { get; init; } = [];

    public TimeSpan Grace => TimeSpan.FromMinutes(GraceMinutes);
    public TimeSpan? IdleTimeout => IdleMinutes > 0 ? TimeSpan.FromMinutes(IdleMinutes) : null;
}

/// <summary>secrets.json. The PIN is a salted PBKDF2-SHA256 hash; a passkey is stored as its id and public key.</summary>
public sealed record SecretsFile
{
    public int Schema { get; init; } = 1;
    public string? PinHash { get; init; }
    public string? PinSalt { get; init; }
    public int PinIterations { get; init; }
    /// <summary>Digits in the PIN (0: not known yet, for PINs set before 0.2). Lets the prompt submit by itself.</summary>
    public int PinLength { get; init; }
    public string? PasskeyId { get; init; }
    public string? PasskeyKey { get; init; }
    public uint PasskeyCount { get; init; }
    public bool HelloOn { get; init; }
    public int FailedCount { get; init; }
    public DateTimeOffset? LockoutUntil { get; init; }
}

/// <summary>The heartbeat file: last time Latch was known to be running, and whether it stopped on purpose.</summary>
public sealed record HeartbeatFile(DateTimeOffset Last, bool CleanExit);
