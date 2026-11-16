// The UI's side of the contract. Calls go to the host over WebView2 messages; in a plain browser they go to the
// mock backend (src/mock.ts), which implements the same LatchApi.
import type { ApiError, LatchApi, ServerEventMap } from "./contract.gen";

export class LatchError extends Error {
  readonly error: ApiError;
  constructor(error: ApiError) {
    super(error.message);
    this.error = error;
  }
}

interface WebView {
  postMessage(message: unknown): void;
  addEventListener(type: "message", listener: (e: MessageEvent) => void): void;
}

const webview = (window as unknown as { chrome?: { webview?: WebView } }).chrome?.webview;
export const isMock = !webview;

type EventName = keyof ServerEventMap;
type Listener<K extends EventName> = (data: ServerEventMap[K]) => void;

const listeners: { [K in EventName]: Set<Listener<K>> } = { state: new Set(), prompt: new Set() };
const pending = new Map<number, { resolve: (v: unknown) => void; reject: (e: unknown) => void }>();
let nextId = 1;

type Incoming =
  | { event: EventName; data: unknown }
  | { id: number; ok: true; result: unknown }
  | { id: number; ok: false; error: ApiError };

export function dispatch(msg: Incoming) {
  if ("event" in msg) {
    for (const l of listeners[msg.event] as Set<(d: unknown) => void>) l(msg.data);
    return;
  }
  const p = pending.get(msg.id);
  if (!p) return;
  pending.delete(msg.id);
  if (msg.ok) p.resolve(msg.result);
  else p.reject(new LatchError(msg.error));
}

webview?.addEventListener("message", (e) => dispatch(e.data as Incoming));

async function call(method: string, args: unknown): Promise<unknown> {
  if (!webview) {
    const { mockCall } = await import("./mock");
    return mockCall(method, args);
  }
  return new Promise((resolve, reject) => {
    const id = nextId++;
    pending.set(id, { resolve, reject });
    webview.postMessage({ id, method, args: args ?? {} });
  });
}

/** Typed client for every host method. */
export const api = new Proxy({} as LatchApi, {
  get: (_, method: string) => (args?: unknown) => call(method, args),
});

export function on<K extends EventName>(event: K, listener: Listener<K>): () => void {
  listeners[event].add(listener);
  return () => listeners[event].delete(listener);
}

/** Used by the mock backend to push events the way the host does. */
export function emit<K extends EventName>(event: K, data: ServerEventMap[K]) {
  dispatch({ event, data } as Incoming);
}

export function errorMessage(e: unknown): string {
  if (e instanceof LatchError) return e.error.message;
  return "Something went wrong. Try again, and restart Latch if it keeps happening.";
}

/** Applies the theme choice (`.dark` on <html>, following Windows for "system") and the Mica flag. */
let themeChoice: "system" | "light" | "dark" = "system";
const systemDark = window.matchMedia("(prefers-color-scheme: dark)");
function syncTheme() {
  const dark = themeChoice === "dark" || (themeChoice === "system" && systemDark.matches);
  document.documentElement.classList.toggle("dark", dark);
}
systemDark.addEventListener("change", syncTheme);

export function applyTheme(theme: "system" | "light" | "dark") {
  themeChoice = theme;
  syncTheme();
  document.documentElement.classList.toggle("mica", new URLSearchParams(location.search).get("mica") === "1");
}
