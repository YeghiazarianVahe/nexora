// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import type { DiskHealth } from '../types';
import { DriveHealth } from './DriveHealth';

const api = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn() }));
vi.mock('../api', () => ({ ...api, ApiError: class extends Error {} }));

const base: DiskHealth = {
  diskKey: 'd1', model: 'Test Drive', serial: 'S1', mediaType: 'HDD', type: 'HDD', busType: 'SATA', sizeBytes: 1e12, healthPercent: 70, status: 'Warning',
  temperatureC: 41, maxTemperatureC: null, powerOnHours: 100, wearPercent: null, readErrors: null, writeErrors: null, reallocatedSectors: null,
  pendingSectors: null, predictFailure: false, estimatedDaysLeft: null, summary: 'Some wear', attributes: [], updatedUtc: '2026-10-01T00:00:00Z', temperatureHistory: [],
};
const mount = (disks: unknown) => {
  api.get.mockResolvedValue(disks);
  render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><DriveHealth endpointId="e1" range="24h" canRefresh={false} /></QueryClientProvider>);
};
beforeEach(() => vi.resetAllMocks());
afterEach(cleanup);

it.each([5,6])('marks model %s as earlier using the backend model contract', async version => {
  mount([{ ...base, healthModelVersion:version,currentHealthModelVersion:7 }]);
  expect(await screen.findByText('Earlier health model · refresh scan')).toBeInTheDocument();
});
it.each([null,undefined,99])('does not present unknown model %s as verified', async version => {
  mount([{ ...base, healthModelVersion:version,currentHealthModelVersion:7 }]);
  expect(await screen.findByText('Health model not verified · refresh scan')).toBeInTheDocument();
});
it('does not warn for the current backend model version', async () => {
  mount([{ ...base, healthModelVersion:7,currentHealthModelVersion:7 }]);
  await screen.findByText('Test Drive');
  expect(screen.queryByText(/health model.*refresh scan/i)).not.toBeInTheDocument();
});

it('shows estimate confidence and distinguishes a stale last known temperature', async () => {
  mount([{ ...base, confidence: 'Low', valueKind: 'estimated', stale: true, temperatureC: null, lastKnownTemperatureC: 41 }]);
  expect(await screen.findByText('Low confidence')).toBeInTheDocument();
  expect(screen.getByText('Estimated health')).toBeInTheDocument();
  expect(screen.getByText('Stale · last known health')).toBeInTheDocument();
  expect(screen.getByText(/Last known temperature: 41 °C/)).toBeInTheDocument();
});

it('lists deductions, notes and given-back points and shows the equation when the invariant holds', async () => {
  mount([{ ...base, healthPercent: 80, reasons: [
    { code: 'realloc', text: 'Reallocated sectors <b>5</b>', delta: -25 },
    { code: 'note-x', text: 'Informational', delta: 0 },
    { code: 'floor-min', text: 'Floor applied', delta: 5 },
  ] }]);
  expect(await screen.findByText('Why this score?')).toBeInTheDocument();
  expect(screen.getByText('-25')).toBeInTheDocument();
  expect(screen.getByText('+5')).toBeInTheDocument();
  expect(screen.getByText('given back')).toBeInTheDocument();
  expect(screen.getByText('info')).toBeInTheDocument();
  expect(screen.getByText(/Reallocated sectors <b>5<\/b>/)).toBeInTheDocument(); // plain text, not markup
  expect(screen.getByTestId('reasons-total')).toHaveTextContent('Total: 100 - 20 = 80');
});

it('omits the equation footer when the invariant does not hold, but still lists the reasons', async () => {
  mount([{ ...base, reasons: [{ code: 'a', text: 'A', delta: -10 }] }]);
  expect(await screen.findByText('Why this score?')).toBeInTheDocument();
  expect(screen.queryByTestId('reasons-total')).toBeNull();
  expect(screen.getByText('-10')).toBeInTheDocument();
});

it('labels the independent heuristic and never claims an accuracy guarantee', async () => {
  mount([base]);
  expect(await screen.findByText(/Health \(heuristic estimate\)/)).toBeInTheDocument();
  expect(screen.getByLabelText(/Confidence reflects data coverage, not a guaranteed error bound/)).toBeInTheDocument();
  expect(document.body.textContent?.toLowerCase()).not.toContain('identical');
});

it('shows temperature separately with the source badge, lifetime max and status', async () => {
  mount([{ ...base, temperature: { currentC: 52, warnC: 50, critC: 60, source: 'default', lifetimeMaxC: 58, status: 'Warning' } }]);
  const block = await screen.findByTestId('temperature-block');
  expect(block).toHaveTextContent('52 °C');
  expect(block).toHaveTextContent('Warn 50 °C / Crit 60 °C');
  expect(block).toHaveTextContent('default (Admin setting)');
  expect(block).toHaveTextContent('lifetime max 58 °C');
  expect(block).toHaveTextContent('Warning');
  cleanup();
  mount([{ ...base, mediaType: 'NVMe', temperature: { currentC: 40, warnC: 70, critC: 80, source: 'drive', lifetimeMaxC: null, status: 'OK' } }]);
  const nvme = await screen.findByTestId('temperature-block');
  expect(nvme).toHaveTextContent('drive-reported');
  expect(nvme).not.toHaveTextContent(/lifetime max/);
});

it('shows the interface warning as a distinct chip and leaves the score untouched', async () => {
  mount([{ ...base, interfaceWarning: 'CRC errors: check the SATA cable' }]);
  const chip = await screen.findByTestId('interface-warning');
  expect(chip).toHaveTextContent('Cable/connection: CRC errors: check the SATA cable');
  expect(chip).not.toHaveClass('bad');
  expect(screen.getByText('70%')).toBeInTheDocument();
});

it('still renders a legacy payload without the new fields', async () => {
  mount([base]);
  expect(await screen.findByText('Test Drive')).toBeInTheDocument();
  expect(screen.getByText('41 °C')).toBeInTheDocument();
  expect(screen.queryByText('Why this score?')).toBeNull();
  expect(screen.queryByTestId('temperature-block')).toBeNull();
  expect(screen.queryByTestId('interface-warning')).toBeNull();
});
