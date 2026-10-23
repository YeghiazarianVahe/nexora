// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, render, screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { EndpointSummary } from '../types';
import { DriveHealthCell, FreeSpaceCell, MetricCell } from './EndpointParts';
import { systemVolume } from '../lib/disks';

const api = vi.hoisted(() => ({ get: vi.fn() }));
vi.mock('../api', () => ({ get: api.get, post: vi.fn(), ApiError: class extends Error {} }));

afterEach(cleanup);
api.get.mockResolvedValue({ cpuAlertPercent: 90, ramAlertPercent: 90, diskAlertPercent: 90, healthOkFrom: 85, healthCriticalBelow: 50 });

const GB = 1024 ** 3;
type Row = Pick<EndpointSummary, 'name' | 'ssdHealthPercent' | 'hddHealthPercent' | 'volumes' | 'ramPercent' | 'ramTotalBytes'>;

/** The three health/space cells exactly as the Endpoints table renders them. */
function Table({ rows }: { rows: Row[] }) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return (
    <QueryClientProvider client={qc}>
      <table>
        <tbody>
          {rows.map((e) => (
            <tr key={e.name} data-testid={e.name}>
              <td data-col="ssd"><DriveHealthCell value={e.ssdHealthPercent} kind="SSD" /></td>
              <td data-col="hdd"><DriveHealthCell value={e.hddHealthPercent} kind="HDD" /></td>
              <td data-col="free"><FreeSpaceCell volume={systemVolume(e)} volumes={e.volumes} /></td>
              <td data-col="ram"><MetricCell value={e.ramPercent} online kind="ram" title={`${(e.ramTotalBytes ?? 0) / GB} GB`} /></td>
            </tr>
          ))}
        </tbody>
      </table>
    </QueryClientProvider>
  );
}

const vol = (letter: string, total: number, free: number) => ({ letter, fileSystem: 'NTFS', totalBytes: total * GB, freeBytes: free * GB });
const rows: Row[] = [
  { name: 'ssd-only', ssdHealthPercent: 96, hddHealthPercent: null, volumes: [vol('C:', 475, 120)], ramPercent: 50, ramTotalBytes: 16 * GB },
  { name: 'hdd-only', ssdHealthPercent: null, hddHealthPercent: 94, volumes: [vol('C:', 931, 400)], ramPercent: 80, ramTotalBytes: 8 * GB },
  { name: 'dual', ssdHealthPercent: 92, hddHealthPercent: 88, volumes: [vol('D:', 1800, 900), vol('C:', 465, 216)], ramPercent: 78, ramTotalBytes: 32 * GB },
  { name: 'nothing', ssdHealthPercent: null, hddHealthPercent: null, volumes: null, ramPercent: null, ramTotalBytes: null },
];

describe('SSD / HDD / Disk free cells in the table', () => {
  const cell = (row: string, col: string) => within(screen.getByTestId(row)).getByText((_, el) => el?.getAttribute('data-col') === col, { selector: 'td' });

  it('SSD-only PC: SSD shows the health, HDD shows a dash', () => {
    render(<Table rows={rows} />);
    expect(cell('ssd-only', 'ssd')).toHaveTextContent('96%');
    expect(cell('ssd-only', 'hdd')).toHaveTextContent('—');
  });

  it('HDD-only PC: SSD shows a dash, HDD shows the health', () => {
    render(<Table rows={rows} />);
    expect(cell('hdd-only', 'ssd')).toHaveTextContent('—');
    expect(cell('hdd-only', 'hdd')).toHaveTextContent('94%');
  });

  it('dual-drive PC: both columns show their own worst health', () => {
    render(<Table rows={rows} />);
    expect(cell('dual', 'ssd')).toHaveTextContent('92%');
    expect(cell('dual', 'hdd')).toHaveTextContent('88%');
  });

  it('disk free shows the system drive free / total and lists every volume in the tooltip', () => {
    render(<Table rows={rows} />);
    expect(cell('ssd-only', 'free')).toHaveTextContent('120.0 / 475.0 GB');
    const dual = cell('dual', 'free');
    expect(dual).toHaveTextContent('216.0 / 465.0 GB'); // C:, not the first volume
    const tip = dual.querySelector('.freecell')!.getAttribute('title')!;
    expect(tip).toContain('C: NTFS 216.0 GB free of 465.0 GB');
    expect(tip).toContain('D: NTFS 900.0 GB free of 1.8 TB');
  });

  it('a PC with no data shows dashes everywhere', () => {
    render(<Table rows={rows} />);
    for (const col of ['ssd', 'hdd', 'free']) expect(cell('nothing', col)).toHaveTextContent('—');
  });

  it('the RAM meter carries the used / total tooltip', () => {
    render(<Table rows={rows} />);
    expect(cell('ssd-only', 'ram').querySelector('.metric')).toHaveAttribute('title', '16 GB');
  });
});
