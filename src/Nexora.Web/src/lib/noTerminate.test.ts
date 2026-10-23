import { expect, it } from 'vitest';

const sources = import.meta.glob(['../**/*.ts', '../**/*.tsx', '../**/*.css', '!../**/*.test.ts', '!../**/*.test.tsx'], { query: '?raw', import: 'default', eager: true }) as Record<string, string>;

it('the UI never says Terminate or End process (a process is stopped)', () => {
  expect(Object.keys(sources).length).toBeGreaterThan(20);
  const hits = Object.entries(sources).filter(([, text]) => /\bterminat|end process/i.test(text)).map(([file]) => file);
  expect(hits).toEqual([]);
});
