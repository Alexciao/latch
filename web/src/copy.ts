// All UI copy comes from copy.json (the one copy file, glossary included). This module only formats it.
import copy from "./copy.json";

export { copy };

/** Replaces {name} placeholders. */
export function t(text: string, args: Record<string, string | number> = {}): string {
  return text.replace(/\{(\w+)\}/g, (_, k: string) => String(args[k] ?? `{${k}}`));
}

const MINUTE = 60_000;

/** Durations: "45 min", "2 h", "2 h 15 min". One formatter for every duration in the app. */
export function duration(ms: number): string {
  const totalMin = Math.max(1, Math.round(ms / MINUTE));
  if (totalMin < 60) return t(copy.units.min, { n: totalMin });
  const h = Math.floor(totalMin / 60);
  const m = totalMin % 60;
  return m === 0 ? t(copy.units.hour, { n: h }) : t(copy.units.hourMin, { h, m });
}

/** "0:28", "14:05": the lockout countdown. */
export function clock(ms: number): string {
  const s = Math.max(0, Math.ceil(ms / 1000));
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
}

const startOfDay = (d: Date) => new Date(d.getFullYear(), d.getMonth(), d.getDate());

/** Days between two dates by calendar day (not 24 h spans). */
function calendarDays(from: Date, to: Date): number {
  return Math.round((startOfDay(to).getTime() - startOfDay(from).getTime()) / 86_400_000);
}

/** The one date rule: relative by calendar day under 7 days, absolute after. */
export function day(iso: string | Date, now = new Date()): string {
  const d = typeof iso === "string" ? new Date(iso) : iso;
  const diff = calendarDays(d, now);
  if (diff <= 0) return copy.history.today;
  if (diff === 1) return copy.history.yesterday;
  if (diff < 7) return d.toLocaleDateString("en-GB", { weekday: "long" });
  return d.toLocaleDateString("en-GB", { day: "numeric", month: "short", year: "numeric" });
}

/** "today", "yesterday", "on Monday", "on 12 Nov 2026" — the day rule inside a sentence. */
export function dayInSentence(iso: string, now = new Date()): string {
  const d = day(iso, now);
  if (d === copy.history.today || d === copy.history.yesterday) return d.toLowerCase();
  return `on ${d}`;
}

export function time(iso: string): string {
  return new Date(iso).toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" });
}

/** A moment in a sentence: time only when it's today, otherwise day and time. */
export function moment(iso: string, now = new Date()): string {
  const d = day(iso, now);
  return d === copy.history.today ? time(iso) : `${d} ${time(iso)}`;
}

export function dayKey(iso: string): string {
  const d = new Date(iso);
  return `${d.getFullYear()}-${d.getMonth()}-${d.getDate()}`;
}
