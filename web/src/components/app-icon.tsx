import { cn } from "@/lib/utils";

/** App icons at a fixed size with a reserved box, so nothing moves when the image arrives. */
export function AppIcon({ src, size = 32, className }: { src: string | null; size?: number; className?: string }) {
  return (
    <span className={cn("inline-block shrink-0 overflow-hidden rounded-md", className)} style={{ width: size, height: size }}>
      {src ? <img src={src} alt="" width={size} height={size} decoding="sync" draggable={false} className="size-full object-contain" /> : <span className="block size-full rounded-md bg-muted" />}
    </span>
  );
}
