import { create } from "zustand";
import { api, on } from "./bridge";
import type { AppSnapshot } from "./contract.gen";

export type Page = "apps" | "history" | "settings";

interface Store {
  snapshot: AppSnapshot | null;
  page: Page;
  setPage(page: Page): void;
}

export const useStore = create<Store>((set) => ({
  snapshot: null,
  page: (new URLSearchParams(location.search).get("page") as Page | null) ?? "apps",
  setPage: (page) => set({ page }),
}));

/**
 * Structural sharing: keep the identity of every unchanged branch, so selectors only fire on real changes even
 * though the host pushes a whole new snapshot each time.
 */
export function replaceEqualDeep<T>(prev: T, next: T): T {
  if (prev === next) return prev;
  if (Array.isArray(prev) && Array.isArray(next)) {
    const out = next.map((item, i) => replaceEqualDeep(prev[i], item));
    return (out.length === prev.length && out.every((v, i) => v === prev[i]) ? prev : out) as T;
  }
  if (prev && next && typeof prev === "object" && typeof next === "object") {
    const p = prev as Record<string, unknown>;
    const n = next as Record<string, unknown>;
    const keys = Object.keys(n);
    let same = keys.length === Object.keys(p).length;
    const out: Record<string, unknown> = {};
    for (const k of keys) {
      out[k] = replaceEqualDeep(p[k], n[k]);
      if (out[k] !== p[k]) same = false;
    }
    return (same ? prev : out) as T;
  }
  return next;
}

export function startStateSync() {
  on("state", (s) => useStore.setState((st) => ({ snapshot: replaceEqualDeep(st.snapshot, s) })));
  void api.getState().then((s) => useStore.setState((st) => ({ snapshot: replaceEqualDeep(st.snapshot, s) })));
}
