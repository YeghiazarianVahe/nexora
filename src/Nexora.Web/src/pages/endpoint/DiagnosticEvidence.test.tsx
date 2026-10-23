// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest';
import { cleanup, render, screen, within } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { DiagnosticEvidence } from './DiagnosticEvidence';

afterEach(cleanup);

it('shows resource percentages and byte values as understandable measurements', () => {
  render(<DiagnosticEvidence evidence={{ 'system-resources': {
    cpu: { loadPercent: 8.3, logicalProcessors: 8 },
    memory: { totalBytes: 16 * 1024 ** 3, freeBytes: 2 * 1024 ** 3, usedPercent: 87.5, pageFileUsedPercent: 31.5 },
    disk: { volumes: [{ letter: 'C', freeBytes: 30 * 1024 ** 3, sizeBytes: 250 * 1024 ** 3, freePercent: 12 }] },
    search: { serviceState: 'Running', startMode: 'Auto', indexing: { state: null } },
  } }} />);
  expect(screen.getByText('8.3% · 8 logical processors')).toBeInTheDocument();
  expect(screen.getByText(/14.0 GB used of 16.0 GB/)).toBeInTheDocument();
  const disk = screen.getByRole('table', { name: 'Disk space' });
  expect(within(disk).getByText('30.0 GB')).toBeInTheDocument();
  expect(within(disk).getByText('12%')).toBeInTheDocument();
  expect(screen.getByText(/Indexing details not available/)).toBeInTheDocument();
});

it('keeps separate app processes and labels CPU and memory tables', () => {
  render(<DiagnosticEvidence evidence={{ 'pc-slow-processes': {
    sampleSeconds: 2.3,
    topMemory: [{ name: 'chrome.exe', pid: 1, workingSetBytes: 512 * 1024 ** 2, cpuPercent: 0 }, { name: 'chrome.exe', pid: 2, workingSetBytes: 256 * 1024 ** 2, cpuPercent: 0.8 }],
    topCpu: [{ name: 'chrome.exe', pid: 2, workingSetBytes: 256 * 1024 ** 2, cpuPercent: 0.8 }],
  } }} />);
  const memory = screen.getByRole('table', { name: 'Processes using the most memory' });
  expect(within(memory).getAllByText('chrome.exe')).toHaveLength(2);
  expect(within(memory).getByText('512.0 MB')).toBeInTheDocument();
  expect(within(memory).getByText('0%')).toBeInTheDocument();
  expect(screen.getByRole('table', { name: 'Processes using the most CPU' })).toBeInTheDocument();
});

it('shows missing data and incomplete collection honestly without rendering markup', () => {
  render(<DiagnosticEvidence evidence={{ 'system-resources': { incomplete: true, incompleteReason: 'Access denied', errors: ['<script>bad()</script>'], memory: { usedPercent: null } } }} />);
  expect(screen.getByText(/Some data could not be collected: Access denied/)).toBeInTheDocument();
  expect(screen.getAllByText('Not available').length).toBeGreaterThan(0);
  expect(screen.getByText('<script>bad()</script>')).toBeInTheDocument();
  expect(document.querySelector('script')).toBeNull();
});

it('formats unfamiliar collector fields and preserves zero values', () => {
  render(<DiagnosticEvidence evidence={{ 'network-probe': { freeBytes: 0, lossPercent: 0, reachable: false, status: null } }} />);
  expect(screen.getByText('0.0 MB')).toBeInTheDocument();
  expect(screen.getByText('0%')).toBeInTheDocument();
  expect(screen.getByText('No')).toBeInTheDocument();
});
