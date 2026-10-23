import { actionLabel, type CatalogEntry } from '../jobs/jobTypes';
import type { AuditEntry } from '../types';

/**
 * Job entries in the audit log carry the action code ("repair.sfc"); show the friendly name next to it. Everything else keeps its own label.
 * job.create stores the code as the new value; job.target stores it inside the details.
 */
export function auditActionName(a: Pick<AuditEntry, 'action' | 'newValue' | 'details'>, index: ReadonlyMap<string, CatalogEntry>): string | null {
  let code: string | null = null;
  if (a.action === 'job.create' || a.action === 'job.cancel') code = a.newValue;
  else if (a.action === 'job.target' && a.details) {
    try { code = (JSON.parse(a.details) as { action?: string }).action ?? null; } catch { code = null; }
  }
  if (!code) return null;
  const name = index.get(code)?.displayName;
  if (name !== undefined) return actionLabel(code, name);
  return code === 'proc.kill' ? actionLabel(code, code) : null;
}
