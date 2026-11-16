import { Input } from "@/components/ui/input";
import { cn } from "@/lib/utils";

/** A masked, digits-only PIN field. Enter submits. */
export function PinInput({
  value,
  onChange,
  label,
  onEnter,
  className,
  ...rest
}: {
  value: string;
  onChange: (v: string) => void;
  label: string;
  onEnter?: () => void;
  className?: string;
  autoFocus?: boolean;
  disabled?: boolean;
  ref?: React.Ref<HTMLInputElement>;
}) {
  return (
    <Input
      type="password"
      inputMode="numeric"
      autoComplete="off"
      aria-label={label}
      placeholder={label}
      maxLength={12}
      value={value}
      onChange={(e) => onChange(e.target.value.replace(/\D/g, "").slice(0, 12))}
      onKeyDown={(e) => e.key === "Enter" && onEnter?.()}
      className={cn("tabular tracking-[0.2em] placeholder:tracking-normal", className)}
      {...rest}
    />
  );
}
