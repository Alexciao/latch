import { useEffect, useState } from "react";
import { api, errorMessage } from "../bridge";
import { copy, day, dayKey, moment, t, time } from "../copy";
import type { HistoryEntry } from "../contract.gen";
import { historyTone, toneText } from "../status";
import { useStore } from "../store";
import { EmptyState, PageHeader, SectionTitle } from "@/components/page-header";
import { Button } from "@/components/ui/button";

function text(e: HistoryEntry): string {
  const app = e.app ?? "";
  switch (e.kind) {
    case "unlocked":
      return t(e.method === "hello" ? copy.history.unlockedHello : e.method === "passkey" ? copy.history.unlockedPasskey : copy.history.unlockedPin, { app });
    case "failed":
      return t(e.method === "passkey" ? copy.history.failedPasskey : copy.history.failedPin, { app });
    case "lockoutStarted":
      return t(copy.history.lockout, { app });
    case "promptCancelled":
      return t(copy.history.cancelled, { app });
    case "latchStarted":
      return copy.history.started;
    case "latchQuit":
      return copy.history.quit;
    case "latchNotRunning":
      return t(copy.history.notRunning, { from: moment(e.from ?? e.at), to: time(e.at) });
  }
}

const isProblem = (e: HistoryEntry) => historyTone[e.kind] !== "normal";

/** Groups by calendar day, newest day first; inside a day, problems come first, then the rest, newest first. */
function group(entries: HistoryEntry[]) {
  const days = new Map<string, HistoryEntry[]>();
  for (const e of entries) {
    const k = dayKey(e.at);
    if (!days.has(k)) days.set(k, []);
    days.get(k)!.push(e);
  }
  return [...days.values()].map((list) => {
    const sorted = [...list].sort((a, b) => Date.parse(b.at) - Date.parse(a.at));
    return { label: day(list[0].at), entries: [...sorted.filter(isProblem), ...sorted.filter((e) => !isProblem(e))] };
  });
}

export function History() {
  const revision = useStore((s) => s.snapshot!.historyRevision);
  const [entries, setEntries] = useState<HistoryEntry[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = () => {
    setError(null);
    api.getHistory().then(setEntries, (e) => setError(errorMessage(e)));
  };
  useEffect(load, [revision]);

  return (
    <>
      <PageHeader title={copy.history.title} />
      {error ? (
        <div>
          <p>{error}</p>
          <Button variant="outline" className="mt-3" onClick={load}>
            {copy.common.tryAgain}
          </Button>
        </div>
      ) : entries === null ? null : entries.length === 0 ? (
        <EmptyState title={copy.history.emptyTitle} caption={copy.history.emptyCaption} />
      ) : (
        <div className="flex animate-in flex-col gap-6 fade-in-0 duration-150">
          {group(entries).map((g) => (
            <section key={g.label}>
              <SectionTitle>{g.label}</SectionTitle>
              <ul>
                {g.entries.map((e, i) => (
                  <li key={e.at + i} className="flex gap-4 py-1.5">
                    <span className="w-12 shrink-0 text-[13px] leading-5 text-muted-foreground tabular">{time(e.at)}</span>
                    <span className={`min-w-0 flex-1 break-words ${toneText[historyTone[e.kind]]}`}>{text(e)}</span>
                  </li>
                ))}
              </ul>
            </section>
          ))}
        </div>
      )}
    </>
  );
}
