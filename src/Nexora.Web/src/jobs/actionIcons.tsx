/** Small outline icons (24x24, stroke only) so every action and category has a glyph without a new dependency. Decorative: the name always follows. */
const PATHS: Record<string, string> = {
  power: 'M18.36 6.64a9 9 0 1 1-12.73 0M12 2v10',
  refresh: 'M23 4v6h-6M1 20v-6h6M3.51 9a9 9 0 0 1 14.85-3.36L23 10M1 14l4.64 4.36A9 9 0 0 0 20.49 15',
  tool: 'M14.7 6.3a1 1 0 0 0 0 1.4l1.6 1.6a1 1 0 0 0 1.4 0l3.77-3.77a6 6 0 0 1-7.94 7.94l-6.91 6.91a2.12 2.12 0 0 1-3-3l6.91-6.91a6 6 0 0 1 7.94-7.94z',
  shield: 'M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z',
  globe: 'M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20zM2 12h20M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10z',
  cpu: 'M4 4h16v16H4zM9 9h6v6H9zM9 1v3M15 1v3M9 20v3M15 20v3M20 9h3M20 14h3M1 9h3M1 14h3',
  activity: 'M22 12h-4l-3 9L9 3l-3 9H2',
  terminal: 'M4 17l6-6-6-6M12 19h8',
  drive: 'M22 12H2M5.45 5.11L2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11zM6 16h.01M10 16h.01',
  message: 'M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z',
  monitor: 'M2 3h20v14H2zM8 21h8M12 17v4',
  trash: 'M3 6h18M8 6V4h8v2M19 6l-1 14H6L5 6',
  download: 'M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4M7 10l5 5 5-5M12 15V3',
  user: 'M20 21v-2a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v2M12 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8z',
  grid: 'M3 3h7v7H3zM14 3h7v7h-7zM14 14h7v7h-7zM3 14h7v7H3z',
};

const BY_PREFIX: [string, string][] = [
  ['power.', 'power'], ['repair.', 'tool'], ['disk.', 'drive'], ['update.', 'download'], ['defender.', 'shield'], ['net.', 'globe'],
  ['svc.', 'cpu'], ['proc.', 'cpu'], ['session.', 'monitor'], ['profile.', 'user'], ['script.', 'terminal'], ['system.', 'refresh'], ['diag.', 'activity'],
];
const BY_CATEGORY: Record<string, string> = {
  'power-session': 'power', 'health-repair': 'tool', 'updates-security': 'shield', 'network-policy': 'globe',
  services: 'cpu', processes: 'cpu', profiles: 'user', diagnostics: 'activity', 'admin-scripts': 'terminal', all: 'grid',
};

export const iconName = (code: string, category: string) => BY_PREFIX.find(([p]) => code.startsWith(p))?.[1] ?? BY_CATEGORY[category] ?? 'grid';

export function Icon({ name, size = 18 }: { name: string; size?: number }) {
  return (
    <svg className="ico" width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true" focusable="false">
      <path d={PATHS[name] ?? PATHS.grid} />
    </svg>
  );
}

export const CategoryIcon = ({ category }: { category: string }) => <Icon name={BY_CATEGORY[category] ?? 'grid'} />;
export const ActionIcon = ({ code, category }: { code: string; category: string }) => <Icon name={iconName(code, category)} />;
