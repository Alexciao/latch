import { useState } from "react";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { api, errorMessage } from "../bridge";
import { copy } from "../copy";
import { AppPicker, pickerAction, useAppPicker } from "./AppPicker";

export function AddAppsDialog({ onClose }: { onClose: () => void }) {
  const picker = useAppPicker();
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const changed = picker.added + picker.removed > 0;

  const save = async () => {
    setSaving(true);
    try {
      await api.setLockedApps({ apps: picker.chosen });
      onClose();
    } catch (e) {
      setError(errorMessage(e));
      setSaving(false);
    }
  };

  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="sm:max-w-[520px]" aria-describedby={undefined}>
        <DialogHeader>
          <DialogTitle>{copy.addApps.title}</DialogTitle>
        </DialogHeader>
        <div>
          <AppPicker picker={picker} height={340} />
          <p className="h-5 text-xs leading-5 text-destructive">{error}</p>
        </div>
        <DialogFooter className="sm:justify-between">
          <Button variant="link" className="px-0 text-muted-foreground hover:text-foreground" onClick={() => void picker.browse()}>
            {copy.addApps.browse}
          </Button>
          <div className="flex gap-2">
            <Button variant="outline" onClick={onClose}>
              {copy.common.cancel}
            </Button>
            <Button disabled={!changed || saving} onClick={() => void save()}>
              {pickerAction(picker)}
            </Button>
          </div>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
