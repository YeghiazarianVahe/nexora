import { useQuery } from '@tanstack/react-query';
import { useMemo } from 'react';
import { get } from '../api';
import type { CatalogEntry } from './jobTypes';

/** Actions this user may run. The server enforces the same rules on every request. */
export function useCatalog() {
  return useQuery({ queryKey: ['job-catalog'], queryFn: () => get<CatalogEntry[]>('/api/jobs/catalog'), staleTime: 300_000 });
}

/** Code to catalog entry, so lists and the audit log can show the friendly name and the command behind it. */
export function useCatalogIndex() {
  const q = useCatalog();
  return useMemo(() => new Map((q.data ?? []).map((e) => [e.code, e] as const)), [q.data]);
}
