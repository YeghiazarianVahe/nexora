import { useEffect, useState } from 'react';

export function ThemeToggle() {
  const [theme, setTheme] = useState(() => document.documentElement.dataset.theme === 'light' ? 'light' : 'dark');
  useEffect(() => {
    const sync = (event: StorageEvent) => {
      if (event.key === 'nexora-theme' && (event.newValue === 'light' || event.newValue === 'dark')) {
        document.documentElement.dataset.theme = event.newValue;
        setTheme(event.newValue);
      }
    };
    window.addEventListener('storage', sync);
    return () => window.removeEventListener('storage', sync);
  }, []);

  const next = theme === 'dark' ? 'light' : 'dark';
  return <button type="button" className="theme-toggle" aria-label={`Switch to ${next} mode`} title={`Switch to ${next} mode`}
    onClick={() => {
      document.documentElement.dataset.theme = next;
      setTheme(next);
      try { localStorage.setItem('nexora-theme', next); } catch { /* the switch still works for this page */ }
    }}>
    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" aria-hidden="true">
      {next === 'light' ? <><circle cx="12" cy="12" r="4" /><path d="M12 2v2m0 16v2M2 12h2m16 0h2M5 5l1.5 1.5m11 11L19 19M5 19l1.5-1.5m11-11L19 5" /></> : <path d="M20.5 14A8.5 8.5 0 0 1 10 3.5 8.5 8.5 0 1 0 20.5 14Z" />}
    </svg>
    <span>{next === 'light' ? 'Light mode' : 'Dark mode'}</span>
  </button>;
}
