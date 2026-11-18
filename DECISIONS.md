# Decisions

One line of reasoning per decision, newest at the bottom of each section. Evidence labels: [E] measured by
experiment, [D] read in docs or source, [K] known but not re-checked.

## Scope and threat model

- Latch stops a non-technical person using the owner's signed-in Windows account; it does not stop anyone who can
  end processes with intent, edit the registry or boot another OS. It is a normal per-user app: visible tray icon,
  normal uninstall, no service, no admin rights, no self-protection.
- Getting around Latch by ending it is detected, not prevented: a 60 s heartbeat plus a clean-quit marker produce a
  "Latch wasn't running" history entry on the next start.

## Spike (2026-11-16, Windows 11 26300, WebView2 runtime 157, .NET 10.0.404)

- [E] An out-of-context `SetWinEventHook` (`EVENT_OBJECT_CREATE..SHOW`, `EVENT_SYSTEM_FOREGROUND`,
  `EVENT_OBJECT_CLOAKED..UNCLOAKED`) delivered the first top-level `SHOW` of Notepad (packaged Win32), VLC (classic
  Win32) and Calculator (UWP) with 0 ms event lag (`GetTickCount` minus event time).
- [E] Hiding at that `SHOW` completed 18 ms (VLC) and 41 ms (Notepad) later, because a cross-process `ShowWindow`
  waits for the target thread; the window is gone within about 1–3 frames. The host therefore uses `ShowWindowAsync`
  so a busy or hung app never blocks the hook thread.
- [E] A UWP frame (`ApplicationFrameHost.exe`, class `ApplicationFrameWindow`) is shown while still DWM-cloaked and
  without an AppUserModelID; the AUMID appears about 130 ms later (at `FOREGROUND`), still cloaked. Hiding it then
  means it is never painted. So frames without an AUMID are re-checked on `FOREGROUND`/`UNCLOAKED`/`SHOW`.
- [E] The UWP app's own `Windows.UI.Core.CoreWindow` is briefly top-level in the app process (which has the package
  family name). It is tracked (for the process id) but never hidden, because it gets re-parented into the frame.
- [E] `ApplicationFrameHost` re-shows the frame once after uncloaking (about 270 ms after launch); the hook catches
  that `SHOW` and hides again within 22 ms. Accepted: at most one frame of the Calculator frame can flash.
- [E] Win11 Notepad is a packaged Win32 app: `GetPackageFamilyName` on its process returns
  `Microsoft.WindowsNotepad_8wekyb3d8bbwe`, so package rules match it directly without `ApplicationFrameHost`.
- [E] Publisher lookup is cheap: `FileVersionInfo` 0.4–3 ms, embedded Authenticode subject 2–15 ms (cold). Both are
  cached per path and the signer is read only when a file's product name matches a rule. Catalog-signed system files
  (System32 notepad.exe) have no embedded signer, so they match by path only.
- [E] Cold WebView2 prompt (environment + controller + navigate + painted): 1240 ms first, 664 ms with a warm disk
  cache. Fails the 250 ms target.
- [E] Pre-created, hidden and `TrySuspendAsync`-suspended WebView2: resume + show + post state + painted (double
  `requestAnimationFrame`) took 22–34 ms over 7 runs. Chosen.
- [E] Cost of the pre-warmed prompt while idle: 6 `msedgewebview2` processes, about 130 MB private / 270 MB working
  set (shared pages counted per process). `MemoryUsageTargetLevel.Low` before suspending changed nothing measurable.
- [E] Mica (`DWMWA_SYSTEMBACKDROP_TYPE = 2`) shows through a WebView2 with `DefaultBackgroundColor` transparent when
  the WPF window background and `CompositionTarget.BackgroundColor` are transparent and the frame is extended into
  the client area via `WindowChrome`. No white flash. Used on Windows 11; solid neutrals elsewhere.

## Stack

- .NET 10 (current LTS; SDK 10.0.404 pinned in `global.json` with feature roll-forward); host TFM
  `net10.0-windows10.0.19041.0` for WinRT (Hello, PackageManager) without extra packages.
- Code-only WPF host + raw `CoreWebView2Controller`; a UI-free `Latch.Core` on plain `net10.0`.
- Font: system Segoe UI Variable with Segoe UI fallback; no bundled font. (Replaced by Inter on 2026-11-18, below.)

## Changes after the owner's first use (2026-11-16)

- Prompts for Latch itself centre on Latch's window or the monitor under the cursor; the old anchor could be a
  destroyed window, which made the monitor lookup fail and put the prompt at the top-left corner.
- While Windows Hello or the passkey dialog is up, the prompt becomes a compact card ("<App> is locked / Confirm it's
  you with Windows Hello") that the host keeps centred 16 px above the system dialog, found as a visible window owned
  by the prompt or by CredentialUIBroker.exe. It polls every 60 ms only while that dialog is up. [E] Placement verified
  with the real Hello dialog; Hello ignores synthetic input, so cancelling it was not automated.
- Passkeys replace the authenticator code (owner's request). Optional, off by default. Only cross-platform
  authenticators (phone by QR code, security key): a passkey stored on this PC would open with the Windows PIN.
- Latch is its own WebAuthn relying party ("latch.local"): it builds the client data, stores the credential's COSE
  public key, and verifies each assertion's signature, rpIdHash, user-presence flag and signature counter. Tests use a
  software P-256 authenticator. System.Formats.Cbor (first-party) parses COSE keys; QRCoder and DPAPI were removed.
- A passkey works during a PIN lockout and clears it (it can't be guessed, and it is how a forgotten PIN is reset).
- UI moved to shadcn/ui components (Radix via `radix-ui`, cva, tailwind-merge, tw-animate-css) with Latch's tokens.
- `LATCH_DATA_DIR` runs a separate dev instance (own data, own single-instance name, never touches the Run key).

## Second round (2026-11-16)

- Hello and passkey dialogs get an invisible owner window centred like the full prompt. [E] With the prompt as
  owner, Windows re-placed the dialog relative to the moving card and both walked to the bottom of the screen.
- The prompt never moves on screen: it stays cloaked until it is in its final place (above the Hello dialog, or
  centred again after Hello), following the dialog only once its rectangle holds still for two 60 ms samples and is
  dialog-sized. [E] Sampled every 30 ms: one reveal per placement, no visible jumps.
- Starting Latch (or launching it again) no longer opens the PIN-guarded window; a notification card says it's
  running. The card is native WPF (no web view, nothing while idle), slides up 12 px and fades in over 200 ms, fades
  out on hover or after 3.5 s, and skips animation when Windows animations are off. The unlock prompt and its
  compact card are not animated: they must be instant (the 250 ms target) and Mica windows can't fade as a whole.
- Tray apps (WhatsApp, Discord): an unlocked app whose last window hides or cloaks, but whose process lives on,
  counts as closed after 1.5 s (to ride over splash screens); grace starts and the next window asks again. `Not now`
  never ends such an app.
- Lock-all shortcut Win+Shift+X, on by default, through RegisterHotKey (no keyboard hook; Windows delivers only that
  combination). Off unregisters it. Win+L-style letters were avoided (Win+L is reserved by Windows), and Ctrl+Alt
  combinations were avoided because AltGr equals Ctrl+Alt on many European layouts.
- Not adopted: covering locked windows with an overlay instead of hiding them (see the owner's question): the content
  would still leak through Alt+Tab, taskbar and Task View thumbnails and screen sharing, and keystrokes would still
  reach the app.
- While Hello or the passkey dialog is up, neither it nor the card is always-on-top: the card is owned by the same
  invisible owner window as the dialog, so Windows raises, covers and uncovers them as one group; clicking the card
  hands focus to the dialog. [E] Probed the z-order with another window opened on top and with the card activated.
- Hello opens in front: Latch takes the foreground first (card still cloaked), allows any process to take it
  (AllowSetForegroundWindow), and raises the dialog once when it appears. [E] Started while another app had focus:
  the Hello window ended up as the foreground window.
- [E] Cancelling the Hello async operation does not close Hello's window, and other processes can't close it, so
  Latch never "cancels" Hello; the dialog follows only a broker process started for the current request.

## Lock latency (2026-11-16)

- [E] The flash of a locked app is the app's own thread taking 31–171 ms to act on Latch's asynchronous hide (the
  hook itself sees the window with no measurable lag). Latch can't make another app's thread faster.
- Not done: stopping the window before it exists. That needs code inside every app (an injected WH_CBT hook DLL,
  excluded by the brief and the behaviour of malware), or admin rights (Image File Execution Options "Debugger"
  redirection, kernel ETW process events, a driver) — all against "no admin, no injection".
- Done instead: an instant cover. The hook thread keeps two hidden native windows ready; when it hides a locked
  app's window it moves one over the exact spot, topmost, and paints the Latch mark at once. [E] On screen 5–10 ms
  after the lock decision, within about one frame of the app's window appearing. Removed when the prompt shows
  (1.5 s fallback). Skipped for Store frames, which are still cloaked and never painted.
- Prompt: the content is posted before the window work, and the page reports "painted" after one frame instead
  of two. [E] Debug build: 109–140 ms from the app's window to the painted prompt, first one after start ~220 ms.
- `--disable-features=CalculateNativeWinOcclusion` for the web views, so a cloaked prompt keeps rendering at full
  speed; [E] no measurable change, kept because cloaked windows are exactly what Chromium may throttle.
- The cover is the locked app's stand-in, not a Latch splash: a neutral surface with a title strip (the app's icon
  and name, a lock and "Locked") over the app's spot. One per app; it lasts as long as that app's prompt (on screen
  or queued), goes 150 ms after an unlock so the app's windows are back first, and at once otherwise. The prompt
  centres on it when it fits (else on the monitor), Hello's owner sits where the prompt was, covers drop under the
  Hello group while it's up, and clicking a cover hands focus to the prompt (or Hello). Painted with GDI on the
  watcher thread from the cached icon, so it still appears within about a frame.
- The cover keeps a title bar behind the prompt, drawn to Windows 11 caption metrics: 32 px, 16 px icon, the name in
  12 px Segoe UI Variable (regular), one surface colour with no divider, a lock and "Locked" where the window buttons
  would be, rounded corners and a drop shadow (CS_DROPSHADOW; extending the DWM frame let the background bleed
  through). Text is laid out by WPF into a cached opaque bitmap: GDI can't render Segoe UI Variable's optical sizes.
- Tray apps that re-show their window (WhatsApp): the cover stays always-on-top until every window it covers is
  really hidden, and goes back on top whenever one of them shows again; it drops under the Hello group only once
  Hello's window exists. [E] With a test app that stalls its UI thread 400 ms after showing: the app's window was never
  stacked above the cover (20 ms sampling).
- [E] Some apps show their window again without any WinEvent (the test app did, after its stall). For 3 s after a
  hide, Latch re-checks the window every 25 ms and hides it again; nothing runs once that window is settled.
- [E] WPF text layout can stall for seconds the first time in a process; on the event thread that delayed a window
  event by 9 s. Title bar pieces (left: rounded 16 px icon and name; right: lock and "Locked") are rendered on their
  own thread, pre-rendered for every locked app at start, and the cover paints its surface at once and the pieces
  when ready. [E] Launch 3 s after start: event to cover 32 ms.
- The unlock prompt is a plain popup now: solid popover surface, rounded corners and the light popup shadow
  (CS_DROPSHADOW), like the Windows Hello dialog and the cover. The extended DWM frame it had for Mica brought the
  heavy full-window shadow, which looked wrong next to Hello. Mica stays on the main window only.
- After Hello is cancelled the prompt is placed at its last full size at once and revealed on the page's next paint;
  the page reports "painted" from a timer too (a cloaked window may get no animation frames), and the fallback is
  150 ms instead of 800 ms. A trace line records how long Windows took to return Hello's result after its window
  closed.
- The cover's title bar has a Windows 11 close button (46 × 32, ChromeClose glyph, red on hover, acts on release).
  It does what `Not now` does for that app, whether its prompt is on screen or waiting. [E] Clicked: prompt
  dismissed, history entry written, the just-launched app closed.
- While Latch waits for Windows Hello, a recreated Windows 11 ProgressRing (32 px, 4 px round-capped stroke, accent
  fill colour, grow/shrink/rotate) shows on the cover after 100 ms, owned by the cover and animated only while shown.
  WinUI's real ProgressRing can't be hosted without XAML Islands.

## Third round (2026-11-18)

- The cover's background is a heavily blurred picture of the app (about 40 DIPs), mixed with the window colour (62 %
  dark, 72 % light) so the title bar and prompt stand out. It can't be taken when the cover appears: the app's window
  is hidden the moment it shows, before it has drawn anything. It is taken earlier instead, while the app is
  unlocked: a second after its windows come back or it comes to the front, every 10 s while it stays in front and
  the owner is active, and as it loses the front; never more than once per 2 s per app. PrintWindow with
  PW_RENDERFULLCONTENT (GPU-drawn content included). [E] 33–45 ms for a 3408 × 1456 window, 90–100 ms including
  the reduction in a Debug build, all on a below-normal thread of its own; the sharp capture is reduced to one pixel
  per 12 DIPs at once and freed. Only the blurred picture is kept: in memory (about 5 MB per app at 3440 px wide)
  and, reduced, in `cache\covers` (about 100 KB per app, at most one write a minute), so the first cover after
  Latch starts has a picture too. Keeping it is harmless: it shows nothing more than the cover shows anyone. Files of
  apps no longer locked are deleted at start. An app never unlocked since 0.2.0 gets the plain cover.
- [E] A hidden window can't be captured: after SW_HIDE, PrintWindow returns all black for Notepad and Paint, with
  and without PW_RENDERFULLCONTENT. And a freshly launched app is hidden before it has drawn anything anyway.
- The cover paints the picture with a plain StretchBlt from half size (the picture is already smooth, with ±½ level
  of noise against banding). [E] 2.3 ms; cover on screen 8–17 ms after the lock decision, as before.
- PINs may be 4 to 12 digits (was 6 to 12). The PIN's length is stored with its hash (learned at the next correct
  PIN for PINs set before), and the prompt submits as soon as that many digits are typed. The length is no secret
  worth keeping: the masked field shows it anyway. Nothing is checked before the last digit, so a wrong PIN of the
  right length counts as one wrong try, as before.
- The UI font is Inter (owner's choice after side-by-side screenshots): the variable font with optical sizes
  (@fontsource-variable/inter, bundled), and static Regular and SemiBold instances of the same font for the native
  cover title bar and notification card (WPF can't select variable-font axes). The prompt loads the font while it's
  still hidden, so the first prompt never paints in the fallback face.
- Automatic updates through Velopack from the GitHub releases (the repository is public, so no token): a check a
  minute after start and every 6 h, a quiet download, and installation at the next start (Velopack applies pending
  updates at startup, and Latch starts with Windows). A toast says once that it's ready, and the tray menu offers
  "Restart to update", which asks for an unlock like Quit. Latch never restarts itself unasked: a restart gives
  hidden windows back and starts over.
