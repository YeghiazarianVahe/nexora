import { useCallback, useEffect, useRef, useState } from 'react';
import { get } from '../api';
import { useHubOn, useRealtime } from '../realtime';
import type { LogLineDto } from './jobTypes';

/**
 * Merges log lines by their sequence number: a line is identified by its seq, so duplicates (a push that also arrived in a page, or a page
 * re-read after a reconnect) collapse into one, lines that arrive out of order end up in order, and nothing is lost to a "highest seq so far"
 * shortcut. The common case, new lines after the last one, is a plain append.
 */
export function mergeLines(prev: LogLineDto[], incoming: LogLineDto[]): LogLineDto[] {
  if (incoming.length === 0) return prev;
  const lastSeq = prev.length > 0 ? prev[prev.length - 1].seq : -1;
  let ordered = true;
  for (let i = 0; i < incoming.length; i++) {
    if (incoming[i].seq <= (i === 0 ? lastSeq : incoming[i - 1].seq)) { ordered = false; break; }
  }
  if (ordered) return prev.concat(incoming);

  const bySeq = new Map<number, LogLineDto>();
  for (const l of prev) bySeq.set(l.seq, l);
  for (const l of incoming) if (!bySeq.has(l.seq)) bySeq.set(l.seq, l);
  return [...bySeq.values()].sort((a, b) => a.seq - b.seq);
}

/** The first sequence number we are NOT sure to have: lines are numbered from 1 without gaps, so a gap means something is missing. */
export function firstGap(lines: LogLineDto[]): number {
  let expected = 1;
  for (const l of lines) {
    if (l.seq > expected) return expected - 1;
    if (l.seq === expected) expected++;
  }
  return expected - 1;
}

/**
 * The output of one target: loads what exists, then keeps up with the live pushes. When the connection is lost and re-established it subscribes
 * to the job again (server-side groups die with the connection) and re-reads from the last line it is sure to have, so a drop never loses lines.
 */
export function useJobLog(jobId: string, targetId: string) {
  const { connection, epoch } = useRealtime();
  const [lines, setLines] = useState<LogLineDto[]>([]);
  const have = useRef<LogLineDto[]>([]);

  const apply = useCallback((incoming: LogLineDto[]) => {
    const next = mergeLines(have.current, incoming);
    if (next === have.current) return;
    have.current = next;
    setLines(next);
  }, []);

  // A different target starts from nothing.
  useEffect(() => {
    have.current = [];
    setLines([]);
  }, [jobId, targetId]);

  // (Re)subscribe to the job's group; the server only allows the initiator or an Admin.
  useEffect(() => {
    if (!connection) return;
    void connection.invoke('SubscribeJob', jobId).catch(() => undefined);
  }, [connection, epoch, jobId]);

  // Load, and re-load after every reconnect from where we are sure we are complete.
  useEffect(() => {
    let cancelled = false;
    (async () => {
      let after = firstGap(have.current);
      for (;;) {
        const page = await get<LogLineDto[]>(`/api/jobs/${jobId}/targets/${targetId}/log?after=${after}&take=1000`).catch(() => [] as LogLineDto[]);
        if (cancelled || page.length === 0) return;
        apply(page);
        after = page[page.length - 1].seq;
        if (page.length < 1000) return;
      }
    })();
    return () => { cancelled = true; };
  }, [jobId, targetId, epoch, apply]);

  useHubOn('JobLog', (e: { jobId: string; targetId: string; lines: LogLineDto[] }) => {
    if (e.targetId === targetId) apply(e.lines);
  });

  return lines;
}
