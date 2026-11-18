import { Check } from "lucide-react";
import { useState } from "react";
import { AppPicker, useAppPicker } from "@/components/AppPicker";
import { PinInput } from "@/components/pin-input";
import { Button } from "@/components/ui/button";
import { api, errorMessage } from "../bridge";
import { copy, t } from "../copy";
import { LatchMark } from "../mark";
import { useStore } from "../store";

function Step({ n, title, caption, children }: { n: number; title: string; caption?: string; children: React.ReactNode }) {
  return (
    <div className="max-w-[560px] animate-in fade-in-0 duration-150">
      <p className="text-xs text-muted-foreground tabular">{t(copy.setup.step, { n })}</p>
      <h1 className="mt-2 font-display text-[26px] leading-8 font-semibold">{title}</h1>
      {caption && <p className="mt-1 text-muted-foreground">{caption}</p>}
      <div className="mt-6">{children}</div>
    </div>
  );
}

function PinStep({ next }: { next: () => void }) {
  const [pin, setPin] = useState("");
  const [again, setAgain] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const submit = async () => {
    if (pin.length < 4) return setError(copy.setup.pinShort);
    if (pin !== again) return setError(copy.setup.pinMismatch);
    setBusy(true);
    try {
      await api.setPin({ pin });
      next();
    } catch (e) {
      setError(errorMessage(e));
      setBusy(false);
    }
  };
  return (
    <Step n={1} title={copy.setup.pinTitle} caption={copy.setup.pinCaption}>
      <div className="flex w-64 flex-col gap-3">
        <PinInput autoFocus value={pin} onChange={(v) => (setPin(v), setError(null))} label={copy.setup.pinNew} onEnter={() => void submit()} />
        <PinInput value={again} onChange={(v) => (setAgain(v), setError(null))} label={copy.setup.pinAgain} onEnter={() => void submit()} />
      </div>
      <p className="mt-1 h-5 text-xs leading-5 text-destructive">{error}</p>
      <Button className="mt-4" disabled={busy || pin.length < 4 || again.length < 4} onClick={() => void submit()}>
        {copy.common.continue}
      </Button>
    </Step>
  );
}

function MethodRow({ title, caption, on, action }: { title: string; caption: string; on: boolean; action: React.ReactNode }) {
  return (
    <div className="flex min-h-16 items-center gap-4 py-2">
      <div className="min-w-0 flex-1">
        <div className="font-medium">{title}</div>
        <div className="text-xs text-muted-foreground">{caption}</div>
      </div>
      {on ? (
        <span className="inline-flex h-9 items-center gap-1.5 text-[13px] text-muted-foreground">
          <Check className="size-4" aria-hidden />
          {copy.common.on}
        </span>
      ) : (
        action
      )}
    </div>
  );
}

function MethodsStep({ next }: { next: () => void }) {
  const methods = useStore((s) => s.snapshot!.methods);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const run = async (action: () => Promise<unknown>) => {
    setBusy(true);
    setError(null);
    try {
      await action();
    } catch (e) {
      setError(errorMessage(e));
    }
    setBusy(false);
  };
  return (
    <Step n={2} title={copy.setup.methodsTitle} caption={copy.setup.methodsCaption}>
      <MethodRow
        title={copy.setup.helloTitle}
        caption={methods.helloAvailable ? copy.setup.helloCaption : copy.setup.helloUnavailable}
        on={methods.helloOn}
        action={
          methods.helloAvailable && (
            <Button variant="outline" disabled={busy} onClick={() => void run(() => api.turnOnHello())}>
              {copy.setup.turnOn}
            </Button>
          )
        }
      />
      <MethodRow
        title={copy.setup.passkeyTitle}
        caption={methods.passkeyAvailable ? copy.setup.passkeyCaption : copy.setup.passkeyUnavailable}
        on={methods.passkeyOn}
        action={
          methods.passkeyAvailable && (
            <Button variant="outline" disabled={busy} onClick={() => void run(() => api.addPasskey())}>
              {copy.setup.add}
            </Button>
          )
        }
      />
      <p className="h-5 text-xs leading-5 text-destructive">{error}</p>
      <Button className="mt-4" disabled={busy} onClick={next}>
        {copy.common.continue}
      </Button>
    </Step>
  );
}

function AppsStep() {
  const picker = useAppPicker();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const n = picker.chosen.length;
  const finish = async () => {
    setBusy(true);
    try {
      await api.setLockedApps({ apps: picker.chosen });
      await api.finishSetup();
    } catch (e) {
      setError(errorMessage(e));
      setBusy(false);
    }
  };
  return (
    <Step n={3} title={copy.setup.appsTitle}>
      <AppPicker picker={picker} height={300} />
      <p className="h-5 text-xs leading-5 text-destructive">{error}</p>
      <div className="mt-3 flex items-center gap-2">
        <Button disabled={busy} onClick={() => void finish()}>
          {n === 0 ? copy.setup.finish : n === 1 ? copy.setup.lockOne : t(copy.setup.lockN, { n })}
        </Button>
        <Button variant="link" className="ml-auto px-0 text-muted-foreground hover:text-foreground" onClick={() => void picker.browse()}>
          {copy.addApps.browse}
        </Button>
      </div>
    </Step>
  );
}

export function FirstRun() {
  const pinSet = useStore((s) => s.snapshot!.methods.pinSet);
  const [step, setStep] = useState(pinSet ? 2 : 1);
  return (
    <div className="h-full overflow-y-auto px-10 pt-8 pb-10">
      <LatchMark size={32} />
      <div className="mt-8">
        {step === 1 && <PinStep next={() => setStep(2)} />}
        {step === 2 && <MethodsStep next={() => setStep(3)} />}
        {step === 3 && <AppsStep />}
      </div>
    </div>
  );
}
