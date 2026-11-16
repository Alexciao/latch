/** Latch's own mark (the padlock tile), drawn inline so it shows at frame 1. */
export function LatchMark({ size = 48 }: { size?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 48 48" aria-hidden="true">
      <defs>
        <linearGradient id="latch-tile" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0" stopColor="#4a80e2" />
          <stop offset="1" stopColor="#2c5cbe" />
        </linearGradient>
      </defs>
      <rect x="2" y="2" width="44" height="44" rx="10.5" fill="url(#latch-tile)" />
      <path d="M16.8 22v-4.6a7.2 7.2 0 0 1 14.4 0V22" fill="none" stroke="#fff" strokeWidth="4" />
      <rect x="12.5" y="20.6" width="23" height="17.3" rx="3.4" fill="#fff" />
      <circle cx="24" cy="27.6" r="2.2" fill="#2f62c4" />
    </svg>
  );
}
