# Tech philosophy

These rules decide how the app is built: where logic lives, how the parts talk, how data stays safe and how
correctness is proven. A reference stack comes last. Use it when the app is a desktop app and it fits. The principles
apply whatever stack you choose. UI and copy rules live in `UI-PHILOSOPHY.md`.

The core rule is **thin host, fat core, one contract**. Every piece of logic lives in a plain library with no UI and
no platform dependency. The native host only does platform glue. The UI only renders. One typed contract sits between
them.

## 1. Architecture

- **Put all logic in a UI-free core library** that builds and tests on any OS. The host (window, tray, OS
  integration) and the UI call into it and hold no decisions of their own. When the host needs an OS feature (registry,
  shell links, image decoding), the core defines an interface and the host implements it.
- **Keep one contract as the single source of truth.** Write the API (methods, events, data records, errors) once in
  the backend language and generate the frontend types from it with a small tool you own. CI fails when the generated
  file is stale. Change the contract only at its source.
- **Make the wire format boring and exact.** Enums are readable strings. Nullable fields are always present as
  `T | null`, never optional. Serialization is source-generated, not reflection-based, so startup stays fast.
- **Treat errors as data.** Each error carries a code, a human message and a fix action the UI can run in one click.
  All user-facing error copy lives in one file. Every API method runs through one wrapper that turns exceptions into
  those errors and logs anything unexpected.
- **Put the shape of a quirk behind one seam.** If one item needs special handling, it goes in a single profiles
  table keyed by ID, and nothing else in the code branches on that item.

## 2. State and data flow

- **Push state; the UI doesn't poll.** The backend sends one small snapshot of app state (a few KB) whenever it
  changes, coalesced to about 30 per second. Replacing the whole snapshot removes any risk of patches drifting.
- **Split by frequency and size.** High-frequency data (progress, live metrics) goes on its own channel, throttled to
  about 10 Hz, and never re-renders whole pages. Large collections arrive once in full and then as revisioned deltas.
  When the UI sees a gap in revisions, it asks for the full set again.
- **Use structural sharing in the UI store,** so unchanged branches keep their identity and selectors fire only on
  real changes.
- **Building a snapshot reads memory only.** It never does I/O or starts processes.
- **Be optimistic only where the UI can't lie.** Cheap, reversible actions (pause, dismiss, toggles) update right
  away and roll back on error. Expensive ones (installs, launches) show a pending state until the backend confirms.
- **Serialize writes that replace a whole object,** so two quick toggles can't overwrite each other.

## 3. Reliability

- **Make every write atomic:** write to a temp file, flush, then replace, retrying for a few seconds (antivirus and
  indexers briefly lock files). Keep the previous version as `.bak` and fall back to it if the main file is corrupt.
- **Load settings field by field over defaults,** so one bad or unknown value drops only that field, not the whole
  file.
- **Version the data and migrate forward.** Migrations run before any store loads, back up the original bytes first,
  and write a schema marker last, so a crash mid-migration just reruns it. Old files are left in place so a downgrade
  still works.
- **Keep user data apart from the install folder,** so uninstalling or updating the app never touches it. Caches live
  in their own folder and are safe to delete.
- **Make long work a persisted, crash-resumable queue.** Jobs are idempotent per (target, kind), so a double click
  doesn't queue twice. Choosing a job and marking it running happen atomically. Nothing writes to the queue before it
  has been restored from disk.
- **Treat external processes as untrusted.** Run each one in a kill-on-close process group, close its stdin, and set
  its working directory to somewhere harmless. Judge success by parsed output markers, not exit codes. Add a stall
  watchdog. Pin each bundled binary by SHA-256 and fail the build on a mismatch.
- **Make every slow call async and cancellable.** The UI thread only renders.

## 4. Performance

- **First paint comes from cache.** Publish the cached state before any network call, process start or slow file
  parse, then warm everything else up in the background, best-effort and logged.
- **Prefer events to polling.** Use file watchers and OS notifications, poll only as a fallback or only while
  something is active, and keep timers coarse.
- **Cost nothing when hidden.** Lower memory targets, suspend the web view, and trim the working set when the window
  is hidden or idle. Start in the tray without creating the browser at all.
- **Keep heavy features off the critical path.** Lazy-load them after first paint, animate only transform and
  opacity, and draw high-frequency visuals outside the render cycle.
- **Measure and record the number.** Performance and size claims come with a measurement and its date.

## 5. Dependencies and packaging

- **The user installs nothing.** Ship self-contained, including runtimes and bundled tools. Reduce size by cutting
  unused pieces, and report the real installer size.
- **Keep dependencies few.** If a dependency would replace about 150 lines of your own code, write the code instead.
  Choose actively maintained packages and check their latest version and release date at the time you add them.
- **Pin everything exactly:** SDK (with feature roll-forward), package manager version (one source shared by local
  builds and CI), packages, CI action majors and build tools.
- **Make builds strict:** warnings as errors, the recommended analyzer level, nullable on, deterministic output.
- **Respect the platform's native behaviour.** A custom title bar keeps native snapping, resize borders, shadows and
  system fallbacks. The window never flashes white: keep it hidden until the UI reports it has painted.
- **Ship self-update** from your release host. Check shortly after start and then on a long interval, and apply on
  exit, never with a forced restart.

## 6. Security and privacy

- Redact tokens, codes and passwords from logs and diagnostics. Logs carry versions and settings, never account
  secrets.
- Open external URLs only for http(s). Open local files only inside the app's own data folder.
- Serve local UI and assets from private virtual hosts, with a strict navigation allowlist for any embedded web
  content.

## 7. Testing

Prove each layer against the most realistic input you can make hermetic:

- **Core unit tests use real captured data:** real API responses, real tool output and real old-version files,
  stored as fixtures.
- **External tools get a scriptable fake** (a stand-in executable) for end-to-end tests of the whole core runtime.
- **A separate real-integration suite runs the actual bundled tool:** temp data folders, synthetic credentials, and
  network cut by default via a dead proxy. It's opt-in and runs non-parallel.
- **Before tagging a release, run the online paths once against the real service.** Offline, hermetic CI can't catch
  a crash that only happens online.
- **The frontend runs against a mock backend that implements the same contract,** with a time scale (long jobs finish
  in seconds) and a freeze flag for stable screenshots.
- **Screenshot every UI state** (Playwright), including worst-case data. Wait on a ready attribute, never on sleeps.
  Also run accessibility (axe) and focus-order tests.
- **Ship a bounded self-test mode** in the real app for things only a real OS can show (hit-testing, rendering,
  window behaviour).
- **Reproduce owner-reported bugs read-only on the owner's real data** before claiming a fix.
- The release gate is a strict build plus every suite green.

## 8. Docs and decisions

- **Keep `DECISIONS.md`:** one line of reasoning per decision, newest at the bottom of each section. Record each
  decision when you make it.
- **Keep `ARCHITECTURE.md`** mapping the design onto the code: modules, contract, lanes or threads, and data
  locations.
- **Label research by evidence:** [E] verified by experiment, [D] read in docs or source, [K] known but not
  re-checked. Base risky choices on [E]: build a throwaway spike, measure, then decide.
- **Keep a glossary** so code, UI copy and docs use the same words.

## Reference stack (Windows desktop)

The stack Glider ships with. For another platform or app type, keep the roles and swap the parts. Check current
versions before you pin them.

| Role | Choice | Why |
|---|---|---|
| Runtime | .NET (current LTS), C# latest, `.slnx` | One SDK for app, tools and build scripts |
| Host | WPF in code only (no XAML) + raw WebView2 controller | Fast start, no airspace plumbing, native window semantics |
| Core | Plain `netX.0` class library, System.Text.Json source generation | UI-free, testable anywhere, no reflection warm-up |
| Contract | Records in C# + a small reflection-based TS generator with a `--check` mode in CI | One source of truth, no third-party generator |
| UI | React, TypeScript, Vite, Tailwind, Radix primitives, Motion (lazy-loaded), Zustand, Lucide icons, bundled variable font | Accessible primitives, small store, no CDN at runtime |
| Packages | pnpm, version pinned in `packageManager` | Same version locally and in CI |
| Backend tests | xUnit v3 on Microsoft.Testing.Platform | Current .NET test runner |
| Frontend tests | Vitest + Testing Library + user-event + jest-dom; Playwright + axe-core | Real keyboard sequences; screenshots and a11y |
| Packaging | Velopack, self-contained + ReadyToRun, WebView2 bootstrap; `vpk` pinned as a local dotnet tool | No prerequisites, delta-ready self-update |
| CI/CD | GitHub Actions; release via `gh release create` from a tag | Reproducible, prereleases from `-` tags |

## Done checklist

- [ ] All logic is in the core library; the host and UI hold no decisions.
- [ ] The contract changed only at its source, and the generated types are current.
- [ ] Every new error has a message and a fix action.
- [ ] State reaches the UI by push; high-frequency data has its own channel.
- [ ] Every new write is atomic; every new persisted format is versioned with a migration.
- [ ] Every slow call is async and cancellable; nothing slow runs before first paint.
- [ ] Each new dependency is justified, pinned exactly and actively maintained.
- [ ] Tests use real captured data; new UI states have screenshots.
- [ ] Each new decision has its line in `DECISIONS.md`.
