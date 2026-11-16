import { CircleAlert, TriangleAlert, X } from "lucide-react";
import { useEffect, useState } from "react";
import { AddAppsDialog } from "@/components/AddAppsDialog";
import { AppIcon } from "@/components/app-icon";
import { EmptyState, PageHeader, SectionTitle } from "@/components/page-header";
import { Button } from "@/components/ui/button";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import { api, errorMessage } from "../bridge";
import { copy, dayInSentence, duration, t } from "../copy";
import type { AppRow, AttentionItem } from "../contract.gen";
import { attentionTone, toneText } from "../status";
import { useStore } from "../store";

function useMinuteClock() {
  const [now, setNow] = useState(Date.now());
  useEffect(() => {
    const id = setInterval(() => setNow(Date.now()), 15_000);
    return () => clearInterval(id);
  }, []);
  return now;
}

function caption(row: AppRow, now: number): string | null {
  if (row.status === "hidden") return copy.apps.hidden;
  if (row.status === "unlocked") return copy.apps.unlocked;
  if (row.status === "grace" && row.relocksAt) {
    const left = Date.parse(row.relocksAt) - now;
    return left < 60_000 ? copy.apps.unlockedRelocksSoon : t(copy.apps.unlockedRelocks, { time: duration(left) });
  }
  return null; // A normal locked row says nothing extra.
}

function attentionText(a: AttentionItem): string {
  switch (a.kind) {
    case "notRunning":
      return t(copy.apps.notRunning, { duration: duration(Date.parse(a.at) - Date.parse(a.from ?? a.at)), day: dayInSentence(a.at) });
    case "lockout":
      return t(copy.apps.lockout, { app: a.appName ?? "", day: dayInSentence(a.at) });
    case "appMissing":
      return t(copy.apps.missing, { app: a.appName ?? "" });
  }
}

function Attention({ items }: { items: AttentionItem[] }) {
  const setPage = useStore((s) => s.setPage);
  const [error, setError] = useState<string | null>(null);
  return (
    <section className="mb-8" aria-labelledby="attention">
      <SectionTitle id="attention">{copy.apps.attention}</SectionTitle>
      <ul>
        {items.map((a) => {
          const tone = attentionTone[a.kind];
          const Icon = tone === "danger" ? CircleAlert : TriangleAlert;
          return (
            <li key={a.id} className="flex min-h-12 items-center gap-3 py-1">
              <Icon className={cn("size-4 shrink-0", toneText[tone])} aria-hidden />
              <span className="min-w-0 flex-1">{attentionText(a)}</span>
              {a.kind === "appMissing" ? (
                <Button variant="outline" size="sm" onClick={() => api.removeApp({ id: a.appId! }).catch((e) => setError(errorMessage(e)))}>
                  {copy.common.remove}
                </Button>
              ) : (
                <>
                  <Button variant="outline" size="sm" onClick={() => setPage("history")}>
                    {copy.apps.viewHistory}
                  </Button>
                  <Tooltip>
                    <TooltipTrigger asChild>
                      <Button variant="ghost" size="icon-sm" aria-label={copy.common.dismiss} onClick={() => void api.dismissAttention({ id: a.id })}>
                        <X />
                      </Button>
                    </TooltipTrigger>
                    <TooltipContent>{copy.common.dismiss}</TooltipContent>
                  </Tooltip>
                </>
              )}
            </li>
          );
        })}
      </ul>
      {error && <p className="text-xs text-destructive">{error}</p>}
    </section>
  );
}

export function Apps() {
  const snapshot = useStore((s) => s.snapshot)!;
  const [adding, setAdding] = useState(false);
  const now = useMinuteClock();
  const apps = snapshot.apps;

  return (
    <>
      <PageHeader title={copy.apps.title} action={apps.length > 0 && <Button onClick={() => setAdding(true)}>{copy.apps.add}</Button>} />

      {snapshot.attention.length > 0 && <Attention items={snapshot.attention} />}

      {apps.length === 0 ? (
        <EmptyState title={copy.apps.emptyTitle} caption={copy.apps.emptyCaption} action={<Button onClick={() => setAdding(true)}>{copy.apps.add}</Button>} />
      ) : (
        <ul className="-mx-3">
          {apps.map((row) => {
            const cap = caption(row, now);
            return (
              <li key={row.id} className="group flex h-14 items-center gap-3 rounded-lg px-3 transition-colors hover:bg-accent focus-within:bg-accent">
                <AppIcon src={row.iconUrl} size={32} />
                <div className="min-w-0 flex-1">
                  <div className="truncate font-medium">{row.name}</div>
                  {cap && <div className="truncate text-xs text-muted-foreground">{cap}</div>}
                </div>
                <div className="flex gap-1 opacity-0 transition-opacity group-hover:opacity-100 group-focus-within:opacity-100">
                  {row.status === "hidden" && (
                    <Button variant="outline" size="sm" onClick={() => void api.showApp({ id: row.id })}>
                      {copy.apps.show}
                    </Button>
                  )}
                  <Button variant="ghost" size="sm" onClick={() => void api.removeApp({ id: row.id })}>
                    {copy.common.remove}
                  </Button>
                </div>
              </li>
            );
          })}
        </ul>
      )}

      {adding && <AddAppsDialog onClose={() => setAdding(false)} />}
    </>
  );
}
