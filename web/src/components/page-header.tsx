import type { ReactNode } from "react";

/** Every page starts with the same header at the same left edge: title, and at most one primary action. */
export function PageHeader({ title, action }: { title: string; action?: ReactNode }) {
  return (
    <header className="mb-6 flex h-10 items-center justify-between gap-4">
      <h1 className="font-display text-[26px] leading-8 font-semibold">{title}</h1>
      {action}
    </header>
  );
}

export function SectionTitle({ children, id }: { children: ReactNode; id?: string }) {
  return (
    <h2 id={id} className="mb-1 text-[13px] leading-5 font-semibold text-muted-foreground">
      {children}
    </h2>
  );
}

export function EmptyState({ title, caption, action }: { title: string; caption: string; action?: ReactNode }) {
  return (
    <div className="pt-16">
      <p className="font-display text-lg leading-6 font-semibold">{title}</p>
      <p className="mt-1 text-muted-foreground">{caption}</p>
      {action && <div className="mt-5">{action}</div>}
    </div>
  );
}
