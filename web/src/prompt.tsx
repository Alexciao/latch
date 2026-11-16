// The unlock prompt. Kept small so the pre-warmed view paints in a few frames. Layout never shifts after it
// appears: the error line, the field and the countdown all reserve the same space. While Windows Hello or the
// passkey dialog is up, the prompt becomes a compact card that the host parks just above that dialog.
import { StrictMode, useEffect, useLayoutEffect, useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import { AppIcon } from "@/components/app-icon";
import { PinInput } from "@/components/pin-input";
import { Button } from "@/components/ui/button";
import { api, applyTheme, isMock, on } from "./bridge";
import { clock, copy, t } from "./copy";
import type { PromptView, UnlockResult } from "./contract.gen";
import { LatchMark } from "./mark";
import "./styles.css";

/** "pin": the full prompt. "hello" / "passkey": a system dialog is up and the prompt is a compact card. */
type Mode = "pin" | "hello" | "passkey";

function title(p: PromptView): string {
  if (p.purpose === "app") return t(copy.prompt.appLocked, { app: p.appName });
  return copy.prompt[p.purpose];
}

function Icon({ view, size }: { view: PromptView; size: number }) {
  return view.icon ? <AppIcon src={view.icon} size={size} /> : <LatchMark size={size} />;
}

function Prompt({ view }: { view: PromptView }) {
  const [mode, setMode] = useState<Mode>(view.helloOn && !view.lockoutUntil ? "hello" : "pin");
  const [pin, setPin] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [lockoutUntil, setLockoutUntil] = useState<number | null>(view.lockoutUntil ? Date.parse(view.lockoutUntil) : null);
  const [now, setNow] = useState(Date.now());
  const input = useRef<HTMLInputElement>(null);
  const root = useRef<HTMLDivElement>(null);
  const started = useRef(false);
  const locked = lockoutUntil !== null && lockoutUntil > now;

  // After every layout change, once the frame is painted, report our height; the host sizes (and first reveals) the
  // window from it, so the window always matches what's painted.
  useLayoutEffect(() => {
    // requestAnimationFrame runs just before the frame is painted; a zero timeout from there runs right after it.
    // A cloaked window may get no animation frames at all, so a plain timer reports too, whichever comes first.
    let timer = 0;
    let reported = false;
    const report = () => {
      if (reported) return;
      reported = true;
      const height = Math.ceil(root.current?.getBoundingClientRect().height ?? 300);
      void api.promptPainted({ promptId: view.promptId, height });
      if (!started.current) {
        started.current = true;
        if (mode === "hello") void system("hello");
      }
    };
    const raf = requestAnimationFrame(() => {
      timer = window.setTimeout(report, 0);
    });
    const fallback = window.setTimeout(report, 50);
    return () => {
      cancelAnimationFrame(raf);
      clearTimeout(timer);
      clearTimeout(fallback);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [view.promptId, mode, locked]);

  useEffect(() => {
    if (!locked) return;
    const id = setInterval(() => setNow(Date.now()), 250);
    return () => clearInterval(id);
  }, [locked]);

  useEffect(() => {
    if (mode === "pin" && !locked) input.current?.focus();
  }, [locked, mode]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape" && mode === "pin") {
        e.preventDefault();
        void api.cancelPrompt({ promptId: view.promptId });
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  });

  function handle(r: UnlockResult) {
    setBusy(false);
    if (r.outcome === "ok" || r.outcome === "gone") return;
    setPin("");
    if (r.outcome === "lockedOut" && r.lockoutUntil) {
      setLockoutUntil(Date.parse(r.lockoutUntil));
      setNow(Date.now());
      setError(null);
      return;
    }
    if (r.outcome === "wrong") setError(copy.prompt.wrongPin);
    requestAnimationFrame(() => input.current?.focus());
  }

  async function submit() {
    if (busy || locked || pin.length < 6) return;
    setBusy(true);
    handle(await api.submitPin({ promptId: view.promptId, pin }));
  }

  /** Windows Hello or a passkey: compact card while the system dialog is up, back to the PIN if it's cancelled. */
  async function system(kind: "hello" | "passkey") {
    setMode(kind);
    setError(null);
    const r = kind === "hello" ? await api.useHello({ promptId: view.promptId }) : await api.usePasskey({ promptId: view.promptId });
    if (r.outcome === "ok" || r.outcome === "gone") return;
    setMode("pin");
    if (r.outcome === "lockedOut" && r.lockoutUntil) setLockoutUntil(Date.parse(r.lockoutUntil));
    else if (r.outcome === "wrong") setError(copy.prompt.notVerified);
  }

  if (mode !== "pin") {
    return (
      <div ref={root} className="flex items-center gap-3 px-5 py-4">
        <Icon view={view} size={32} />
        <div className="min-w-0">
          <p className="truncate font-display text-[15px] leading-5 font-semibold">{title(view)}</p>
          <p className="text-[13px] leading-5 text-muted-foreground">{mode === "hello" ? copy.prompt.helloWaiting : copy.prompt.passkeyWaiting}</p>
        </div>
      </div>
    );
  }

  return (
    <div ref={root} className="flex flex-col px-6 pt-6 pb-5">
      <Icon view={view} size={48} />
      <h1 className="mt-4 font-display text-lg leading-6 font-semibold break-words">{title(view)}</h1>

      <div className="mt-4 h-9">
        {locked ? (
          <p role="status" className="flex h-9 items-center text-[13px] text-destructive tabular">
            {t(copy.prompt.lockedOut, { time: clock(lockoutUntil! - now) })}
          </p>
        ) : (
          <PinInput
            ref={input}
            value={pin}
            disabled={busy}
            label={copy.prompt.pin}
            onChange={(v) => {
              setPin(v);
              setError(null);
            }}
            onEnter={() => void submit()}
          />
        )}
      </div>
      <p className="mt-1 h-5 text-xs leading-5 text-destructive" role="alert">
        {error}
      </p>

      {(view.helloOn || view.passkeyOn) && (
        <div className="mt-1 flex flex-col items-start">
          {view.helloOn && (
            <Button variant="link" size="sm" className="h-7 px-0 text-muted-foreground hover:text-foreground" onClick={() => void system("hello")}>
              {copy.prompt.useHello}
            </Button>
          )}
          {view.passkeyOn && (
            <Button variant="link" size="sm" className="h-7 px-0 text-muted-foreground hover:text-foreground" onClick={() => void system("passkey")}>
              {copy.prompt.usePasskey}
            </Button>
          )}
        </div>
      )}

      <div className="mt-5 grid grid-cols-2 gap-2">
        <Button variant="outline" onClick={() => void api.cancelPrompt({ promptId: view.promptId })}>
          {copy.prompt.notNow}
        </Button>
        <Button disabled={busy || locked || pin.length < 6} onClick={() => void submit()}>
          {copy.prompt.unlock}
        </Button>
      </div>
    </div>
  );
}

function App() {
  const [view, setView] = useState<PromptView | null>(null);

  useEffect(() => {
    const off = on("prompt", (p) => {
      setView(p);
      if (p) applyTheme(p.theme);
    });
    if (isMock) {
      void import("./mock").then(({ mockPrompt }) => {
        const p = mockPrompt();
        applyTheme(p.theme);
        setView(p);
      });
    }
    return off;
  }, []);

  // Keyed by prompt id so every prompt starts clean: empty field, no error.
  return view ? <Prompt key={view.promptId} view={view} /> : null;
}

applyTheme("system");
createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
