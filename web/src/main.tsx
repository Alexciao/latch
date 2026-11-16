import { History as HistoryIcon, LayoutGrid, Settings as SettingsIcon } from "lucide-react";
import { StrictMode, useEffect, useRef } from "react";
import { createRoot } from "react-dom/client";
import { Button } from "@/components/ui/button";
import { TooltipProvider } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import { api, applyTheme } from "./bridge";
import { copy } from "./copy";
import { Apps } from "./pages/Apps";
import { FirstRun } from "./pages/FirstRun";
import { History } from "./pages/History";
import { Settings } from "./pages/Settings";
import { startStateSync, useStore, type Page } from "./store";
import "./styles.css";

const nav: { page: Page; label: string; icon: typeof LayoutGrid }[] = [
  { page: "apps", label: copy.nav.apps, icon: LayoutGrid },
  { page: "history", label: copy.nav.history, icon: HistoryIcon },
  { page: "settings", label: copy.nav.settings, icon: SettingsIcon },
];

function Shell() {
  const page = useStore((s) => s.page);
  const setPage = useStore((s) => s.setPage);
  return (
    <div className="flex h-full">
      <nav className="flex w-52 shrink-0 flex-col gap-1 px-3 pt-3" aria-label="Pages">
        {nav.map(({ page: p, label, icon: Icon }) => (
          <Button
            key={p}
            variant="ghost"
            aria-current={page === p ? "page" : undefined}
            onClick={() => setPage(p)}
            className={cn("justify-start gap-3 px-3 font-normal text-muted-foreground", page === p && "bg-accent font-medium text-foreground")}
          >
            <Icon className="size-4" strokeWidth={1.75} />
            {label}
          </Button>
        ))}
      </nav>
      <main className="min-w-0 flex-1 overflow-y-auto">
        <div className="max-w-[720px] px-8 pt-5 pb-12">
          {page === "apps" && <Apps />}
          {page === "history" && <History />}
          {page === "settings" && <Settings />}
        </div>
      </main>
    </div>
  );
}

function App() {
  const snapshot = useStore((s) => s.snapshot);
  const theme = snapshot?.settings.theme ?? "system";
  const ready = useRef(false);

  useEffect(() => applyTheme(theme), [theme]);

  // The host keeps the window cloaked until the first real frame is on screen.
  useEffect(() => {
    if (!snapshot || ready.current) return;
    ready.current = true;
    requestAnimationFrame(() => requestAnimationFrame(() => void api.windowReady()));
  }, [snapshot]);

  if (!snapshot) return null;
  return <TooltipProvider delayDuration={400}>{snapshot.setupDone ? <Shell /> : <FirstRun />}</TooltipProvider>;
}

applyTheme("system");
startStateSync();
createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
