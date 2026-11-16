using Latch.Core.Contract;
using Latch.Core.Locking;
using Latch.Core.Model;
using Latch.Core.Security;

namespace Latch.Core;

/// <summary>Everything the core needs from Windows. The host implements it; the core holds the decisions.</summary>
public interface IPlatform
{
    IWindowInspector Inspector { get; }

    string Version { get; }

    /// <summary>Carries out engine effects. May be called from any thread; the host marshals as needed.</summary>
    void Apply(IReadOnlyList<Effect> effects);

    Task<IReadOnlyList<InstalledApp>> ListInstalledAppsAsync();

    Task<InstalledApp?> BrowseForProgramAsync();

    Task<bool> IsHelloAvailableAsync();

    /// <summary>Runs Windows Hello over the prompt (or the main window when <paramref name="overPrompt"/> is false).</summary>
    Task<bool> VerifyHelloAsync(bool overPrompt, string reason);

    /// <summary>Whether the Windows passkey API (webauthn.dll) is present.</summary>
    bool PasskeysAvailable { get; }

    /// <summary>Creates a passkey on a phone or security key (never this PC). Null when cancelled.</summary>
    Task<PasskeyCreation?> CreatePasskeyAsync(byte[] clientData, string rpId, string rpName);

    /// <summary>Asks the passkey to sign; shown over the prompt. Null when cancelled.</summary>
    Task<PasskeyAssertion?> GetPasskeyAssertionAsync(byte[] clientData, string rpId, byte[] credentialId);

    void SetRunAtStartup(bool on);

    /// <summary>Registers or removes the lock-all shortcut. False when another app already owns it.</summary>
    bool SetLockAllHotkey(bool on);

    /// <summary>Whether a rule's program or package is still installed. May do I/O; called off the hot path.</summary>
    bool TargetExists(LockRule rule);

    string? IconUrl(string? iconKey);

    /// <summary>A small data URL for the prompt, from an in-memory cache, so the icon shows at frame 1.</summary>
    string? IconDataUrl(string? iconKey);

    void WindowReady();

    void PromptPainted(string promptId, int height);
}
