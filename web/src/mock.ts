// Mock backend implementing the same LatchApi as the host, for building the UI in a browser.
// URL flags: ?state=fresh (first run) | ?state=attention | ?state=empty | ?state=long (worst-case data)
// Prompt: prompt.html?mock=app|hello|code|lockout|latch
import { emit, LatchError } from "./bridge";
import { copy } from "./copy";
import type {
  ApiError,
  AppRow,
  AppSnapshot,
  HistoryEntry,
  InstalledApp,
  LatchApi,
  PromptView,
} from "./contract.gen";

const params = new URLSearchParams(location.search);
const scenario = params.get("state") ?? "normal";
const now = Date.now();
const iso = (msAgo: number) => new Date(now - msAgo).toISOString();
const MIN = 60_000;
const HOUR = 60 * MIN;
const DAY = 24 * HOUR;

function icon(letter: string, hue: number): string {
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48"><rect width="48" height="48" rx="11" fill="hsl(${hue} 55% 48%)"/><text x="24" y="31" font-family="Segoe UI" font-size="22" font-weight="600" fill="white" text-anchor="middle">${letter}</text></svg>`;
  return `data:image/svg+xml,${encodeURIComponent(svg)}`;
}

const catalog: InstalledApp[] = [
  ["Calculator", "package", 210],
  ["Discord", "exe", 235],
  ["Notepad", "package", 200],
  ["Photos", "package", 190],
  ["Signal", "exe", 220],
  ["Spotify", "exe", 140],
  ["Steam", "exe", 215],
  ["Telegram", "exe", 200],
  ["Visual Studio Code", "exe", 205],
  ["VLC media player", "exe", 25],
  ["WhatsApp", "package", 135],
  ["Zen Browser", "exe", 260],
  ...(scenario === "long"
    ? ([
        ["Microsoft Visual Studio Installer and Extremely Long Product Name Edition 2026", "exe", 300],
        ["微信", "exe", 120],
        ["Ö", "exe", 50],
      ] as const)
    : []),
].map(([name, kind, hue]) => ({
  key: String(name).toLowerCase().replace(/\W+/g, "-"),
  name: String(name),
  kind: kind as InstalledApp["kind"],
  path: kind === "exe" ? `C:\\Program Files\\${name}\\${name}.exe` : null,
  packageFamilyName: kind === "package" ? `Microsoft.${String(name).replace(/\s/g, "")}_8wekyb3d8bbwe` : null,
  aumid: null,
  iconUrl: icon(String(name)[0], Number(hue)),
  locked: false,
}));

const fresh = scenario === "fresh";
let locked = new Set(fresh || scenario === "empty" ? [] : ["notepad", "spotify", "vlc-media-player", "signal", "calculator"]);
if (scenario === "long") ["microsoft-visual-studio-installer-and-extremely-long-product-name-edition-2026", "微信"].forEach((k) => locked.add(k));
let missing = scenario === "attention" ? new Set(["signal"]) : new Set<string>();
let dismissed = new Set<string>();

const state: AppSnapshot = {
  setupDone: !fresh,
  apps: [],
  attention: [],
  settings: { graceMinutes: 5, relockOnClose: true, idleMinutes: 10, startWithWindows: true, theme: "system", lockAllHotkey: true, lockAllHotkeyTaken: false },
  methods: { pinSet: !fresh, helloAvailable: true, helloOn: !fresh, passkeyAvailable: true, passkeyOn: !fresh },
  historyRevision: 1,
  version: "0.1.0",
};

const history: HistoryEntry[] = [
  { at: iso(4 * MIN), kind: "unlocked", app: "Spotify", method: "pin", from: null },
  { at: iso(5 * MIN), kind: "failed", app: "Spotify", method: "pin", from: null },
  { at: iso(2 * HOUR), kind: "latchStarted", app: null, method: null, from: null },
  { at: iso(2 * HOUR + 1000), kind: "latchNotRunning", app: null, method: null, from: iso(4 * HOUR) },
  { at: iso(DAY + 3 * HOUR), kind: "lockoutStarted", app: "Signal", method: "pin", from: null },
  { at: iso(DAY + 3 * HOUR + 2000), kind: "failed", app: "Signal", method: "pin", from: null },
  { at: iso(DAY + 5 * HOUR), kind: "unlocked", app: "Notepad", method: "hello", from: null },
  { at: iso(3 * DAY), kind: "promptCancelled", app: "Calculator", method: null, from: null },
  { at: iso(9 * DAY), kind: "unlocked", app: "VLC media player", method: "passkey", from: null },
  { at: iso(9 * DAY + HOUR), kind: "latchQuit", app: null, method: null, from: null },
];

const grace: Record<string, number> = { spotify: now + 3 * MIN };
const hidden = new Set(scenario === "attention" ? ["notepad"] : []);
const unlocked = new Set(["vlc-media-player"]);

function rebuild() {
  state.apps = catalog
    .filter((a) => locked.has(a.key))
    .map<AppRow>((a) => ({
      id: a.key,
      name: a.name,
      iconUrl: a.iconUrl,
      status: hidden.has(a.key) ? "hidden" : grace[a.key] ? "grace" : unlocked.has(a.key) ? "unlocked" : "locked",
      relocksAt: grace[a.key] ? new Date(grace[a.key]).toISOString() : null,
    }))
    .sort((a, b) => a.name.localeCompare(b.name));
  const attention: AppSnapshot["attention"] = [];
  if (scenario === "attention" || scenario === "long") {
    attention.push({ id: "notRunning:1", kind: "notRunning", appId: null, appName: null, at: iso(2 * HOUR), from: iso(4 * HOUR) });
    attention.push({ id: "lockout:1", kind: "lockout", appId: null, appName: "Signal", at: iso(DAY + 3 * HOUR), from: null });
  }
  for (const id of missing) {
    const app = catalog.find((a) => a.key === id);
    if (app && locked.has(id)) attention.push({ id: "missing:" + id, kind: "appMissing", appId: id, appName: app.name, at: iso(0), from: null });
  }
  state.attention = attention.filter((a) => !dismissed.has(a.id));
  emit("state", structuredClone(state));
}

function fail(code: ApiError["code"]): never {
  const key = code as keyof typeof copy.errors;
  throw new LatchError({ code, message: copy.errors[key], fix: { kind: "tryAgain", label: copy.common.tryAgain } });
}

const delay = (ms: number) => new Promise((r) => setTimeout(r, ms));

const impl: LatchApi = {
  async getState() {
    rebuild();
    return structuredClone(state);
  },
  async windowReady() {
    return {};
  },
  async setPin({ pin }) {
    if (!/^\d{6,12}$/.test(pin)) fail("pinFormat");
    state.methods.pinSet = true;
    rebuild();
    return {};
  },
  async turnOnHello() {
    await delay(600);
    state.methods.helloOn = true;
    rebuild();
    return true;
  },
  async turnOffHello() {
    state.methods.helloOn = false;
    rebuild();
    return {};
  },
  async addPasskey() {
    await delay(900);
    state.methods.passkeyOn = true;
    rebuild();
    return true;
  },
  async removePasskey() {
    state.methods.passkeyOn = false;
    rebuild();
    return {};
  },
  async finishSetup() {
    state.setupDone = true;
    rebuild();
    return {};
  },
  async listInstalledApps() {
    await delay(700);
    return catalog.map((a) => ({ ...a, locked: locked.has(a.key) }));
  },
  async browseForProgram() {
    await delay(300);
    return { key: "obs", name: "OBS Studio", kind: "exe", path: "C:\\Program Files\\obs-studio\\bin\\64bit\\obs64.exe", packageFamilyName: null, aumid: null, iconUrl: icon("O", 0), locked: false };
  },
  async setLockedApps({ apps }) {
    for (const a of apps) if (!catalog.some((c) => c.key === a.key)) catalog.push(a);
    locked = new Set(apps.map((a) => a.key));
    rebuild();
    return {};
  },
  async removeApp({ id }) {
    locked.delete(id);
    missing.delete(id);
    rebuild();
    return {};
  },
  async showApp({ id }) {
    hidden.delete(id);
    unlocked.add(id);
    rebuild();
    return {};
  },
  async getHistory() {
    await delay(150);
    return scenario === "empty" || fresh ? [] : structuredClone(history);
  },
  async dismissAttention({ id }) {
    dismissed.add(id);
    rebuild();
    return {};
  },
  async updateSettings(patch) {
    for (const [k, v] of Object.entries(patch)) if (v !== null) (state.settings as unknown as Record<string, unknown>)[k] = v;
    rebuild();
    return {};
  },
  async requestUnlock() {
    await delay(400);
    return true;
  },
  async submitPin({ pin }) {
    await delay(250);
    return pin === "123456" ? { outcome: "ok", lockoutUntil: null } : { outcome: "wrong", lockoutUntil: null };
  },
  async usePasskey() {
    await delay(1500);
    return { outcome: "notVerified", lockoutUntil: null };
  },
  async useHello() {
    await delay(params.get("mock") === "hello" ? 600_000 : 1500);
    return { outcome: "notVerified", lockoutUntil: null };
  },
  async cancelPrompt() {
    return {};
  },
  async promptPainted() {
    return {};
  },
};

export async function mockCall(method: string, args: unknown): Promise<unknown> {
  const fn = (impl as unknown as Record<string, (a: unknown) => Promise<unknown>>)[method];
  if (!fn) throw new Error("Unknown method " + method);
  return fn(args);
}

/** Sample prompts for prompt.html in a browser. */
export function mockPrompt(): PromptView {
  const kind = params.get("mock") ?? "app";
  return {
    promptId: "p1",
    purpose: kind === "latch" ? "openLatch" : "app",
    appName: params.get("app") ?? "Spotify",
    icon: kind === "latch" ? null : icon("S", 140),
    helloOn: kind === "hello",
    passkeyOn: kind !== "plain",
    lockoutUntil: kind === "lockout" ? new Date(Date.now() + 28_000).toISOString() : null,
    theme: (params.get("theme") as PromptView["theme"]) ?? "system",
  };
}
