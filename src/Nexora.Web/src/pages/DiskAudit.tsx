import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { get, post } from '../api';
import { ConfirmDialog, ErrorNotice, Loading } from '../ui';

interface AuditDrive {
  model: string; serial: string | null; busType: string | null; mediaTypeStored: string; mediaTypeCode: number | null; spindleSpeed: number | null;
  class: string; classSource: string | null; healthPercent: number | null; healthSource: string | null; physicalMatchedBy: string | null; smartMatched: boolean | null;
}
interface AuditPc {
  endpointId: string; hostname: string; ssdHealth: number | null; hddHealth: number | null; smartRecords: number | null; driveCount: number | null;
  drives: AuditDrive[]; findings: { code: string; message: string }[];
}
interface AuditReport {
  generatedUtc: string; endpoints: number; withDrives: number; scannedWithMapping: number; needRescan: number; nvmeWithoutSmartLog: number;
  byFinding: Record<string, number>; flagged: AuditPc[];
}

/** Hidden Admin page (no menu entry): which PCs have a suspect SSD/HDD mapping, and why. */
export function DiskAudit() {
  const qc = useQueryClient();
  const [confirm, setConfirm] = useState(false);
  const q = useQuery({ queryKey: ['disk-audit'], queryFn: () => get<AuditReport>('/api/disk-audit'), retry: 0 });
  const rescan = useMutation({
    mutationFn: () => post<{ due: number }>('/api/disk-audit/rescan'),
    onSuccess: () => { setConfirm(false); qc.invalidateQueries({ queryKey: ['disk-audit'] }); },
  });

  if (q.isLoading) return <Loading />;
  if (q.error || !q.data) return <ErrorNotice error={q.error} />;
  const r = q.data;

  return (
    <>
      <h1>Disk audit</h1>
      <p className="sub">Checks the SSD / HDD mapping of every PC from what the drive scans stored. Nothing is read from the PCs.</p>
      <div className="tiles">
        <div className="tile"><div className="n">{r.withDrives}</div><div className="l">PCs with drive data</div></div>
        <div className={`tile ${r.flagged.length > 0 ? 'alert' : 'accent'}`}><div className="n" data-testid="flagged-count">{r.flagged.length}</div><div className="l">Flagged</div></div>
        <div className="tile"><div className="n">{r.scannedWithMapping}</div><div className="l">Scanned with mapping</div></div>
        <div className={`tile ${r.needRescan > 0 ? 'bad' : ''}`}><div className="n">{r.needRescan}</div><div className="l">Waiting for a rescan</div></div>
        <div className="tile"><div className="n">{r.nvmeWithoutSmartLog}</div><div className="l">NVMe without SMART log</div></div>
      </div>
      <div className="toolbar">
        <button onClick={() => setConfirm(true)}>Rescan all drives</button>
        <a className="button" href="/api/disk-audit/export">Download CSV</a>
        <span className="muted">{Object.entries(r.byFinding).map(([k, v]) => `${k}: ${v}`).join(' · ') || 'No findings.'}</span>
      </div>
      <ErrorNotice error={rescan.error} />
      <div className="tablewrap">
        <table>
          <thead><tr><th>PC</th><th>Findings</th><th>SSD</th><th>HDD</th><th>Drives (serial · bus · media · spindle · class · health · matched by)</th></tr></thead>
          <tbody>
            {r.flagged.length === 0 && <tr><td colSpan={5} className="empty">Nothing flagged: every PC with drive data maps cleanly.</td></tr>}
            {r.flagged.map((p) => (
              <tr key={p.endpointId}>
                <td><b>{p.hostname}</b></td>
                <td>{p.findings.map((f) => <div key={f.code + f.message} title={f.message}><span className="tag warn">{f.code}</span></div>)}</td>
                <td className="num">{p.ssdHealth ?? '—'}</td><td className="num">{p.hddHealth ?? '—'}</td>
                <td>{p.drives.map((d) => (
                  <div key={d.model + (d.serial ?? '')} className="mono">
                    {d.model} · {d.serial ?? '-'} · {d.busType ?? '-'} · {d.mediaTypeCode ?? d.mediaTypeStored} · {d.spindleSpeed ?? '-'} · <b>{d.class}</b> · {d.healthPercent ?? 'N/A'}% · {d.physicalMatchedBy ?? '-'}
                  </div>
                ))}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {confirm && (
        <ConfirmDialog title="Rescan all drives" confirmLabel="Rescan" busy={rescan.isPending}
          message={<>Mark every PC's drives as due. The monitor reads them again over the next few minutes; nothing else changes. This is written to the audit log.</>}
          onConfirm={() => rescan.mutate()} onCancel={() => setConfirm(false)} />
      )}
    </>
  );
}
