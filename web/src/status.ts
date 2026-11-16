// The one status table. Every surface reads colour from here: danger when something failed or data is at risk,
// warning when the owner should act but nothing is broken, accent for information, and no colour when normal.
import type { AttentionKind, HistoryKind } from "./contract.gen";

export type Tone = "danger" | "warning" | "accent" | "normal";

export const historyTone: Record<HistoryKind, Tone> = {
  failed: "danger",
  lockoutStarted: "danger",
  latchNotRunning: "warning",
  unlocked: "normal",
  promptCancelled: "normal",
  latchStarted: "normal",
  latchQuit: "normal",
};

export const attentionTone: Record<AttentionKind, Tone> = {
  lockout: "danger",
  notRunning: "warning",
  appMissing: "warning",
};

export const toneText: Record<Tone, string> = {
  danger: "text-destructive",
  warning: "text-warning",
  accent: "text-info",
  normal: "",
};
