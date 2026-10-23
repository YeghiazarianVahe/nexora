import { createContext, useCallback, useContext, useMemo, useRef, useState, type ReactNode } from 'react';
import { Link } from 'react-router-dom';

type Tone = 'ok' | 'warn' | 'bad' | 'info';
interface Toast { id: number; tone: Tone; title: string; body?: string; href?: string }

interface ToastApi { push: (t: Omit<Toast, 'id'>) => void }
const Ctx = createContext<ToastApi>({ push: () => undefined });
export const useToasts = () => useContext(Ctx);

/** Short-lived notices in the corner, announced to screen readers. Used when the operator's jobs finish. */
export function ToastProvider({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<Toast[]>([]);
  const next = useRef(1);

  const dismiss = useCallback((id: number) => setToasts((t) => t.filter((x) => x.id !== id)), []);
  const push = useCallback((t: Omit<Toast, 'id'>) => {
    const id = next.current++;
    setToasts((all) => [...all.slice(-4), { ...t, id }]);
    window.setTimeout(() => dismiss(id), 9000);
  }, [dismiss]);
  const api = useMemo(() => ({ push }), [push]);

  return (
    <Ctx.Provider value={api}>
      {children}
      <div className="toasts" role="region" aria-label="Notifications" aria-live="polite">
        {toasts.map((t) => (
          <div key={t.id} className={`toast ${t.tone}`}>
            <div className="toast-body">
              <b>{t.title}</b>
              {t.body && <div className="muted">{t.body}</div>}
              {t.href && <Link to={t.href} onClick={() => dismiss(t.id)}>View job</Link>}
            </div>
            <button className="ghost small" aria-label="Dismiss" onClick={() => dismiss(t.id)}>✕</button>
          </div>
        ))}
      </div>
    </Ctx.Provider>
  );
}
