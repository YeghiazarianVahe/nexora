// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { HelperBanner, HelperIndicator, installCommand, type HelperStatus } from './helper';

const api = vi.hoisted(() => ({ get: vi.fn() }));
const hub = vi.hoisted(() => ({ handlers: {} as Record<string, () => void> }));
vi.mock('../api', () => ({ get: api.get, post: vi.fn(), ApiError: class extends Error {} }));
vi.mock('../realtime', () => ({
  useHubOn: (event: string, fn: () => void) => { hub.handlers[event] = fn; },
  useRealtime: () => ({ state: 'connected', connection: null }),
}));

const status = (over: Partial<HelperStatus>): HelperStatus => ({
  connected: false, everConnected: false, lastConnectedUtc: null, lastDisconnectedUtc: null, lastAttempt: null,
  serverUrl: 'http://wks-111:5090', downloadAvailable: true, ...over,
});

function mount(ui: React.ReactNode) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={qc}>{ui}</QueryClientProvider>);
}

beforeEach(() => { api.get.mockReset(); hub.handlers = {}; });
afterEach(cleanup);

describe('the helper banner', () => {
  it('first time: explains why, offers the download and a copy-paste command', async () => {
    api.get.mockResolvedValue(status({}));
    mount(<HelperBanner />);

    expect(await screen.findByText('Your Nexora Helper is not running.')).toBeInTheDocument();
    expect(screen.getByText(/runs in your own Windows session|run in your own Windows session/i)).toBeInTheDocument();
    expect(screen.getByText(/has not connected since the server started/i)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Download helper' })).toHaveAttribute('href', '/api/helper/download');
    expect(screen.getByRole('button', { name: 'Copy install command' })).toBeInTheDocument();
  });

  it('dropped: says it was connected and when, and that it reconnects by itself', async () => {
    api.get.mockResolvedValue(status({ everConnected: true, lastDisconnectedUtc: '2026-10-13T08:00:00Z' }));
    mount(<HelperBanner />);

    expect(await screen.findByText('Your Nexora Helper disconnected.')).toBeInTheDocument();
    expect(screen.getByText(/reconnects by itself/i)).toBeInTheDocument();
    expect(screen.queryByText(/has not connected since/i)).toBeNull();
  });

  it('shows the server\'s reason when the last attempt was refused', async () => {
    api.get.mockResolvedValue(status({ lastAttempt: { atUtc: '2026-10-13T08:00:00Z', outcome: 'rejected', reason: 'the Viewer role is below Operator', remoteIp: '10.0.0.7' } }));
    mount(<HelperBanner />);

    expect(await screen.findByText(/the Viewer role is below Operator/)).toBeInTheDocument();
  });

  it('copies the install command, which downloads with Windows sign-in and installs the per-user autostart', async () => {
    const user = userEvent.setup();
    const write = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText: write }, configurable: true });
    api.get.mockResolvedValue(status({}));
    mount(<HelperBanner />);

    await user.click(await screen.findByRole('button', { name: 'Copy install command' }));

    expect(write).toHaveBeenCalledWith(installCommand('http://wks-111:5090'));
    expect(installCommand('http://wks-111:5090')).toContain('iwr http://wks-111:5090/api/helper/download -UseDefaultCredentials');
    expect(installCommand('http://wks-111:5090')).toContain('--server http://wks-111:5090 --install');
    expect(installCommand('http://wks-111:5090')).not.toContain(':50' + '80');
    expect(await screen.findByRole('button', { name: 'Copied' })).toBeInTheDocument();
  });

  it('clears by itself when the server pushes that the helper connected, with no page refresh', async () => {
    api.get.mockResolvedValue(status({ everConnected: true }));
    mount(<><HelperBanner /><HelperIndicator /></>);
    expect(await screen.findByText('Your Nexora Helper disconnected.')).toBeInTheDocument();
    expect(screen.getByTestId('helper-indicator')).toHaveTextContent('Helper lost');

    api.get.mockResolvedValue(status({ connected: true, everConnected: true }));
    await act(async () => { hub.handlers.HelperStatusChanged?.(); });

    await waitFor(() => expect(screen.queryByText('Your Nexora Helper disconnected.')).toBeNull());
    expect(screen.getByTestId('helper-indicator')).toHaveTextContent('Helper connected');
  });

  it('appears again when the server pushes that the helper is gone', async () => {
    api.get.mockResolvedValue(status({ connected: true, everConnected: true }));
    mount(<HelperBanner />);
    await waitFor(() => expect(api.get).toHaveBeenCalledTimes(1));
    expect(screen.queryByText(/Nexora Helper/)).toBeNull();

    api.get.mockResolvedValue(status({ connected: false, everConnected: true }));
    await act(async () => { hub.handlers.HelperStatusChanged?.(); });

    expect(await screen.findByText('Your Nexora Helper disconnected.')).toBeInTheDocument();
  });
});

describe('the top-bar indicator', () => {
  it.each([
    [status({ connected: true, everConnected: true }), 'Helper connected'],
    [status({ everConnected: false }), 'Helper not running'],
    [status({ everConnected: true }), 'Helper lost'],
  ])('shows %#', async (s, text) => {
    api.get.mockResolvedValue(s);
    mount(<HelperIndicator />);
    await waitFor(() => expect(screen.getByTestId('helper-indicator')).toHaveTextContent(text));
  });

  it('says it is checking until the first answer arrives', () => {
    api.get.mockReturnValue(new Promise(() => undefined));
    mount(<HelperIndicator />);
    expect(screen.getByTestId('helper-indicator')).toHaveTextContent('Helper connecting');
  });
});
