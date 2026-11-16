import { Search } from "lucide-react";
import { useCallback, useEffect, useMemo, useState } from "react";
import { AppIcon } from "@/components/app-icon";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { Input } from "@/components/ui/input";
import { api, errorMessage } from "../bridge";
import { copy, t } from "../copy";
import type { InstalledApp } from "../contract.gen";

/** State for picking apps: the installed list (fetched once), the selection and Browse. */
export function useAppPicker() {
  const [apps, setApps] = useState<InstalledApp[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [initial, setInitial] = useState<Set<string>>(new Set());

  const load = useCallback(() => {
    setError(null);
    setApps(null);
    api
      .listInstalledApps()
      .then((list) => {
        setApps(list);
        const locked = new Set(list.filter((a) => a.locked).map((a) => a.key));
        setSelected(locked);
        setInitial(locked);
      })
      .catch((e) => setError(errorMessage(e)));
  }, []);

  useEffect(load, [load]);

  const toggle = (key: string) =>
    setSelected((s) => {
      const next = new Set(s);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });

  const browse = async () => {
    try {
      const app = await api.browseForProgram();
      if (!app) return;
      setApps((list) => [app, ...(list ?? []).filter((a) => a.key !== app.key)]);
      setSelected((s) => new Set(s).add(app.key));
    } catch (e) {
      setError(errorMessage(e));
    }
  };

  const chosen = (apps ?? []).filter((a) => selected.has(a.key));
  const added = chosen.filter((a) => !initial.has(a.key)).length;
  const removed = [...initial].filter((k) => !selected.has(k)).length;
  return { apps, error, selected, toggle, browse, load, chosen, added, removed };
}

export type Picker = ReturnType<typeof useAppPicker>;

export function AppPicker({ picker, height = 360 }: { picker: Picker; height?: number }) {
  const [query, setQuery] = useState("");
  const visible = useMemo(() => {
    const q = query.trim().toLowerCase();
    return (picker.apps ?? []).filter((a) => !q || a.name.toLowerCase().includes(q));
  }, [picker.apps, query]);

  return (
    <div>
      <div className="relative">
        <Search className="pointer-events-none absolute top-1/2 left-3 size-4 -translate-y-1/2 text-muted-foreground" aria-hidden />
        <Input autoFocus value={query} onChange={(e) => setQuery(e.target.value)} placeholder={copy.addApps.search} aria-label={copy.addApps.search} className="pl-9" />
      </div>
      <div className="-mx-2 mt-2 overflow-y-auto" style={{ height }}>
        {picker.error ? (
          <div className="px-2 pt-6">
            <p>{copy.addApps.loadFailed}</p>
            <Button variant="outline" className="mt-3" onClick={picker.load}>
              {copy.common.tryAgain}
            </Button>
          </div>
        ) : picker.apps === null ? (
          <p className="px-2 pt-6 text-muted-foreground">{copy.addApps.loading}</p>
        ) : visible.length === 0 ? (
          <p className="px-2 pt-6 text-muted-foreground">{t(copy.addApps.noMatch, { q: query.trim() })}</p>
        ) : (
          <ul>
            {visible.map((a) => (
              <li key={a.key}>
                <label className="flex h-10 items-center gap-3 rounded-md px-2 hover:bg-accent">
                  <Checkbox checked={picker.selected.has(a.key)} onCheckedChange={() => picker.toggle(a.key)} aria-label={a.name} />
                  <AppIcon src={a.iconUrl} size={24} />
                  <span className="min-w-0 flex-1 truncate">{a.name}</span>
                </label>
              </li>
            ))}
          </ul>
        )}
      </div>
    </div>
  );
}

/** "Lock 3 apps", "Lock 1 app", or "Save changes" when something is unlocked too. */
export function pickerAction(p: Picker): string {
  if (p.removed > 0) return copy.addApps.save;
  if (p.added === 0) return copy.addApps.noChange;
  return p.added === 1 ? copy.addApps.lockOne : t(copy.addApps.lockN, { n: p.added });
}
