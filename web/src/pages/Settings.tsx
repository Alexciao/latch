import { useState, type ReactNode } from "react";
import { PageHeader, SectionTitle } from "@/components/page-header";
import { PinInput } from "@/components/pin-input";
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Switch } from "@/components/ui/switch";
import { api, errorMessage } from "../bridge";
import { copy, t } from "../copy";
import type { PromptPurpose, SettingsPatch, ThemeChoice } from "../contract.gen";
import { useStore } from "../store";

const emptyPatch: SettingsPatch = { graceMinutes: null, relockOnClose: null, idleMinutes: null, startWithWindows: null, theme: null, lockAllHotkey: null };

function minutesLabel(m: number) {
  if (m === 0) return copy.settings.off;
  return m % 60 === 0 ? t(copy.units.hour, { n: m / 60 }) : t(copy.units.min, { n: m });
}

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="mb-8">
      <SectionTitle>{title}</SectionTitle>
      {children}
    </section>
  );
}

function Row({ label, caption, htmlFor, children }: { label: string; caption?: string | null; htmlFor?: string; children: ReactNode }) {
  return (
    <div className="flex min-h-12 items-center gap-6 py-1.5">
      <label htmlFor={htmlFor} className="min-w-0 flex-1">
        <div>{label}</div>
        {caption && <div className="text-xs text-muted-foreground">{caption}</div>}
      </label>
      {children}
    </div>
  );
}

function MinutesSelect({ id, value, choices, onChange }: { id: string; value: number; choices: number[]; onChange: (v: number) => void }) {
  return (
    <Select value={String(value)} onValueChange={(v) => onChange(Number(v))}>
      <SelectTrigger id={id} className="w-40">
        <SelectValue />
      </SelectTrigger>
      <SelectContent position="popper" align="end">
        {choices.map((m) => (
          <SelectItem key={m} value={String(m)}>
            {minutesLabel(m)}
          </SelectItem>
        ))}
      </SelectContent>
    </Select>
  );
}

function ChangePinDialog({ onClose }: { onClose: () => void }) {
  const [pin, setPin] = useState("");
  const [again, setAgain] = useState("");
  const [error, setError] = useState<string | null>(null);
  const save = async () => {
    if (pin.length < 4) return setError(copy.setup.pinShort);
    if (pin !== again) return setError(copy.setup.pinMismatch);
    try {
      await api.setPin({ pin });
      onClose();
    } catch (e) {
      setError(errorMessage(e));
    }
  };
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="sm:max-w-[400px]" aria-describedby={undefined}>
        <DialogHeader>
          <DialogTitle>{copy.settings.changePinTitle}</DialogTitle>
        </DialogHeader>
        <div className="flex flex-col gap-3">
          <PinInput autoFocus value={pin} onChange={(v) => (setPin(v), setError(null))} label={copy.setup.pinNew} onEnter={() => void save()} />
          <PinInput value={again} onChange={(v) => (setAgain(v), setError(null))} label={copy.setup.pinAgain} onEnter={() => void save()} />
          <p className="h-5 text-xs leading-5 text-destructive">{error}</p>
        </div>
        <DialogFooter>
          <Button variant="outline" onClick={onClose}>
            {copy.common.cancel}
          </Button>
          <Button disabled={pin.length < 4 || again.length < 4} onClick={() => void save()}>
            {copy.settings.changePinAction}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

export function Settings() {
  const snapshot = useStore((s) => s.snapshot)!;
  const { settings, methods } = snapshot;
  const [dialog, setDialog] = useState<"pin" | "removePasskey" | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const update = (patch: Partial<SettingsPatch>) => api.updateSettings({ ...emptyPatch, ...patch }).catch((e) => setError(errorMessage(e)));

  /** Sensitive changes ask for an unlock first; nothing happens if the prompt is dismissed. */
  const guarded = async (purpose: PromptPurpose, action: () => Promise<unknown> | void) => {
    setError(null);
    try {
      if (await api.requestUnlock({ purpose })) await action();
    } catch (e) {
      setError(errorMessage(e));
    }
  };

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
    <>
      <PageHeader title={copy.settings.title} />
      {error && <p className="-mt-3 mb-4 text-[13px] text-destructive">{error}</p>}

      <Section title={copy.settings.unlock}>
        <Row label={copy.settings.appPin}>
          <Button variant="outline" onClick={() => void guarded("changePin", () => setDialog("pin"))}>
            {copy.settings.change}
          </Button>
        </Row>
        <Row
          label={copy.settings.hello}
          htmlFor="hello"
          caption={!methods.helloAvailable ? copy.settings.helloUnavailable : methods.helloOn ? copy.settings.helloCaption : null}
        >
          <Switch
            id="hello"
            checked={methods.helloOn}
            disabled={busy || (!methods.helloAvailable && !methods.helloOn)}
            onCheckedChange={(on) => void (on ? run(() => api.turnOnHello()) : guarded("turnOffHello", () => api.turnOffHello()))}
          />
        </Row>
        <Row
          label={copy.settings.passkey}
          caption={!methods.passkeyAvailable ? copy.settings.passkeyUnavailable : methods.passkeyOn ? copy.settings.passkeyOn : null}
        >
          {methods.passkeyOn ? (
            <Button variant="outline" onClick={() => setDialog("removePasskey")}>
              {copy.settings.removePasskey}
            </Button>
          ) : (
            <Button variant="outline" disabled={busy || !methods.passkeyAvailable} onClick={() => void run(() => api.addPasskey())}>
              {copy.settings.addPasskey}
            </Button>
          )}
        </Row>
      </Section>

      <Section title={copy.settings.relocking}>
        <Row label={copy.settings.grace} htmlFor="grace" caption={copy.settings.graceCaption}>
          <MinutesSelect id="grace" value={settings.graceMinutes} choices={[0, 1, 5, 15, 60]} onChange={(v) => void update({ graceMinutes: v })} />
        </Row>
        <Row label={copy.settings.relockOnClose} htmlFor="relock-close">
          <Switch id="relock-close" checked={settings.relockOnClose} onCheckedChange={(v) => void update({ relockOnClose: v })} />
        </Row>
        <Row label={copy.settings.idle} htmlFor="idle">
          <MinutesSelect id="idle" value={settings.idleMinutes} choices={[0, 5, 10, 15, 30, 60]} onChange={(v) => void update({ idleMinutes: v })} />
        </Row>
      </Section>

      <Section title={copy.settings.general}>
        <Row label={copy.settings.startWithWindows} htmlFor="startup">
          <Switch
            id="startup"
            checked={settings.startWithWindows}
            onCheckedChange={(v) => void (v ? update({ startWithWindows: true }) : guarded("turnOffStartup", () => update({ startWithWindows: false })))}
          />
        </Row>
        <Row label={copy.settings.hotkey} htmlFor="hotkey" caption={settings.lockAllHotkeyTaken ? copy.settings.hotkeyTaken : null}>
          <Switch id="hotkey" checked={settings.lockAllHotkey} onCheckedChange={(v) => void update({ lockAllHotkey: v })} />
        </Row>
        <Row label={copy.settings.theme} htmlFor="theme">
          <Select value={settings.theme} onValueChange={(v) => void update({ theme: v as ThemeChoice })}>
            <SelectTrigger id="theme" className="w-40">
              <SelectValue />
            </SelectTrigger>
            <SelectContent position="popper" align="end">
              <SelectItem value="system">{copy.settings.themeSystem}</SelectItem>
              <SelectItem value="light">{copy.settings.themeLight}</SelectItem>
              <SelectItem value="dark">{copy.settings.themeDark}</SelectItem>
            </SelectContent>
          </Select>
        </Row>
      </Section>

      <Section title={copy.settings.about}>
        <p className="tabular">{t(copy.settings.version, { version: snapshot.version })}</p>
        <p className="mt-1 max-w-[560px] text-[13px] text-muted-foreground">{copy.settings.limitation}</p>
      </Section>

      {dialog === "pin" && <ChangePinDialog onClose={() => setDialog(null)} />}
      <AlertDialog open={dialog === "removePasskey"} onOpenChange={(o) => !o && setDialog(null)}>
        <AlertDialogContent className="sm:max-w-[420px]">
          <AlertDialogHeader>
            <AlertDialogTitle>{copy.settings.removePasskeyTitle}</AlertDialogTitle>
            <AlertDialogDescription>{copy.settings.removePasskeyCaption}</AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>{copy.common.cancel}</AlertDialogCancel>
            <AlertDialogAction onClick={() => void guarded("removePasskey", () => api.removePasskey())}>{copy.settings.removePasskeyAction}</AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </>
  );
}
