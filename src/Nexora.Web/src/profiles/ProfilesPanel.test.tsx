// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { Me } from '../types';
import { StartedJobContext } from '../pages/endpoint/startedJob';
import { ProfilesPanel } from './ProfilesPanel';

const api = vi.hoisted(() => {
  class ApiError extends Error { constructor(public status: number, message: string, public body?: unknown) { super(message); } }
  return { get: vi.fn(), post: vi.fn(), ApiError };
});
vi.mock('../api', () => ({ ...api }));

const me = (role: Me['role']) => ({ account: 'AB\\op', displayName: 'Op', role, isSuperAdmin: false, status: 'Active' } as Me);
const ep = (online = true) => ({ id: 'e1', name: 'WKS-111', hostname: 'WKS-111', online });
const DAY = 86_400_000;
const ago = (days: number) => new Date(Date.now() - days * DAY).toISOString();
const row = (n: string, over: Record<string, unknown> = {}) => ({
  sid: `S-1-5-21-${n}`, account: `AB\\${n}`, localPath: `C:\\Users\\${n}`, loaded: false, sizeBytes: null, sizeMeasuredUtc: null,
  lastUsedUtc: ago(200), lastUsedSource: 'ProfileList', deletable: true, blockReason: null, ...over,
});
let list: unknown;
const onStarted = vi.fn();

beforeEach(() => {
  vi.resetAllMocks();
  window.sessionStorage.clear();
  list = { items: [] };
  api.get.mockImplementation(async (path: string) => (path === '/api/endpoints/e1/profiles' ? list : []));
  api.post.mockImplementation(async (path: string) => {
    if (path.endsWith('/delete-batch/confirm')) return { token: 'tok', expectedHostname: 'WKS-111', count: 1, items: [{ sid: 'S-1-5-21-old', account: 'AB\\old', path: 'C:\\Users\\old', sizeBytes: 2048 }] };
    if (path.endsWith('/delete-batch')) return { batchId: 'b1', jobIds: ['job-9'] };
    return { job: { id: 'j1' }, targets: [] };
  });
});
afterEach(cleanup);

function mount(role: Me['role'], online = true) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(<MemoryRouter><QueryClientProvider client={qc}><StartedJobContext.Provider value={onStarted}>
    <ProfilesPanel endpoint={ep(online)} me={me(role)} />
  </StartedJobContext.Provider></QueryClientProvider></MemoryRouter>);
}
const postsTo = (suffix: string) => api.post.mock.calls.filter((c) => String(c[0]).endsWith(suffix));

describe('Profiles tab', () => {
  it('hides system, service, default and renamed RID 500 profiles from older server payloads', async () => {
    list = { items: [row('human'),
      row('system', { sid: 'S-1-5-18', account: 'SYSTEM' }),
      row('service', { sid: 'S-1-5-80-1-2-3-4-5' }),
      row('Default'), row('Public'), row('LocalService'),
      row('renamed', { sid: 'S-1-5-21-1-2-3-500' }),
    ] };
    mount('Admin');
    await screen.findByText('AB\\human');
    const table = screen.getByRole('table', { name: 'Profiles' });
    expect(within(table).getAllByRole('row')).toHaveLength(2);
    expect(within(table).queryByText('SYSTEM')).toBeNull();
    expect(within(table).queryByText('AB\\renamed')).toBeNull();
    await userEvent.click(screen.getByRole('checkbox', { name: 'Select all removable profiles' }));
    expect(screen.getByRole('button', { name: 'Delete selected (1)' })).toBeEnabled();
  });
  it('shows a notice to a Viewer and fires no request', async () => {
    mount('Viewer');
    expect(screen.getByTestId('operator-required')).toBeInTheDocument();
    await new Promise((r) => setTimeout(r, 20));
    expect(api.get).not.toHaveBeenCalled();
    expect(api.post).not.toHaveBeenCalled();
  });

  it('lets an Operator list and calculate, but hides delete', async () => {
    list = { items: [row('old')] };
    mount('Operator');
    await screen.findByText('AB\\old');
    expect(screen.getByTestId('delete-admin-only')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Delete selected/ })).toBeNull();
    expect(screen.queryByRole('checkbox')).toBeNull();
    await userEvent.click(screen.getByRole('button', { name: 'Calculate AB\\old' }));
    await waitFor(() => expect(api.post).toHaveBeenCalledWith('/api/jobs', { actionCode: 'profile.sizes', endpointIds: ['e1'], parameters: { sid: 'S-1-5-21-old' } }));
    expect(onStarted).toHaveBeenCalledWith('j1');
  });

  it('Calculate all starts one size job for the whole workstation', async () => {
    list = { items: [row('old')] };
    mount('Operator');
    await screen.findByText('AB\\old');
    await userEvent.click(screen.getByRole('button', { name: 'Calculate all' }));
    await waitFor(() => expect(api.post).toHaveBeenCalledWith('/api/jobs', { actionCode: 'profile.sizes', endpointIds: ['e1'], parameters: {} }));
  });

  it('takes sizes from the server field, never from sessionStorage', async () => {
    window.sessionStorage.setItem('nexora.psize.e1.S-1-5-21-a', JSON.stringify({ bytes: 999, files: 1, errors: 0, partial: false, at: 1 }));
    list = { items: [row('a', { sizeBytes: 5 * 1024 * 1024, sizeMeasuredUtc: '2026-03-05T10:07:00Z' }), row('b')] };
    mount('Operator');
    await screen.findByText('AB\\a');
    expect(screen.getByText(/calculated 05\.03\.2026 14:07/)).toBeInTheDocument();
    expect(screen.getByText('Not calculated')).toBeInTheDocument();
    expect(screen.queryByText(/999/)).toBeNull();
  });

  it('labels the last-used source and shows unknown', async () => {
    list = { items: [row('a', { lastUsedSource: 'ProfileList' }), row('b', { lastUsedSource: 'NtUserDat' }), row('c', { lastUsedSource: 'Wmi' }), row('d', { lastUsedUtc: null, lastUsedSource: null })] };
    mount('Operator');
    await screen.findByText('AB\\a');
    expect(screen.getByText('registry load/unload time')).toBeInTheDocument();
    expect(screen.getByText('NTUSER.DAT modified')).toBeInTheDocument();
    expect(screen.getByText('WMI')).toBeInTheDocument();
    expect(screen.getByText('Unknown')).toBeInTheDocument();
  });

  it('shows the SID muted when the account is unresolved', async () => {
    list = { items: [row('x', { account: null })] };
    mount('Operator');
    expect((await screen.findAllByText('S-1-5-21-x')).length).toBeGreaterThan(0);
  });

  it('shows the offline banner with last known rows, actions disabled', async () => {
    list = { items: [row('old')] };
    mount('Admin', false);
    await screen.findByText('AB\\old');
    expect(screen.getByTestId('profiles-offline')).toHaveTextContent('last known');
    expect(screen.getByRole('button', { name: 'Calculate all' })).toBeDisabled();
  });

  it('shows the unavailable banner with the reason as text', async () => {
    list = { unavailable: true, reason: '<b>WMI timeout</b>', items: [] };
    mount('Operator');
    expect(await screen.findByTestId('profiles-unavailable')).toHaveTextContent('<b>WMI timeout</b>');
    expect(document.querySelector('.profiles b')).toBeNull();
  });

  it('does not allow selecting locked rows and shows the reason', async () => {
    list = { items: [row('sys', { deletable: false, blockReason: 'Signed-in user' }), row('old')] };
    mount('Admin');
    await screen.findByText('AB\\sys');
    expect(screen.getByRole('checkbox', { name: 'Select AB\\sys' })).toBeDisabled();
    expect(screen.getByText(/Locked: Signed-in user/)).toHaveAttribute('title', 'Signed-in user');
    expect(screen.getByRole('checkbox', { name: 'Select AB\\old' })).toBeEnabled();
  });

  it('the age filter only selects matching deletable rows with a known last use, and never deletes', async () => {
    list = { items: [
      row('old', { lastUsedUtc: ago(200) }), row('recent', { lastUsedUtc: ago(5) }),
      row('unknown', { lastUsedUtc: null, lastUsedSource: null }), row('locked', { lastUsedUtc: ago(300), deletable: false, blockReason: 'In use' }),
    ] };
    mount('Admin');
    await screen.findByText('AB\\old');
    await userEvent.click(screen.getByRole('button', { name: 'Select matching' }));
    expect(screen.getByRole('checkbox', { name: 'Select AB\\old' })).toBeChecked();
    expect(screen.getByRole('checkbox', { name: 'Select AB\\recent' })).not.toBeChecked();
    expect(screen.getByRole('checkbox', { name: 'Select AB\\unknown' })).not.toBeChecked();
    expect(screen.getByRole('checkbox', { name: 'Select AB\\locked' })).not.toBeChecked();
    expect(screen.getByText(/Nothing has been deleted/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Delete selected (1)' })).toBeInTheDocument();
    expect(api.post).not.toHaveBeenCalled();
  });

  it('Admin delete needs the confirm token, a reason and the exact hostname', async () => {
    list = { items: [row('old')] };
    mount('Admin');
    await screen.findByText('AB\\old');
    await userEvent.click(screen.getByRole('checkbox', { name: 'Select AB\\old' }));
    await userEvent.click(screen.getByRole('button', { name: 'Delete selected (1)' }));
    expect(api.post).toHaveBeenCalledWith('/api/endpoints/e1/profiles/delete-batch/confirm', { sids: ['S-1-5-21-old'] });
    const dlg = await screen.findByRole('dialog', { name: 'Delete user profiles' });
    expect(within(dlg).getByText('Critical')).toBeInTheDocument();
    expect(within(dlg).getByText('C:\\Users\\old')).toBeInTheDocument();
    expect(within(dlg).getByTestId('delete-total')).toHaveTextContent('2 KB');
    const run = within(dlg).getByRole('button', { name: 'Run' });
    expect(run).toBeDisabled();
    await userEvent.type(within(dlg).getByLabelText(/Type the hostname/), 'wks-111');
    await userEvent.type(within(dlg).getByLabelText(/Reason/), 'left the company');
    expect(run).toBeDisabled(); // wrong case: must match exactly
    await userEvent.clear(within(dlg).getByLabelText(/Type the hostname/));
    await userEvent.type(within(dlg).getByLabelText(/Type the hostname/), 'WKS-111');
    expect(run).toBeEnabled();
    await userEvent.click(run);
    await waitFor(() => expect(api.post).toHaveBeenCalledWith('/api/endpoints/e1/profiles/delete-batch', { sids: ['S-1-5-21-old'], token: 'tok', typed: 'WKS-111', reason: 'left the company' }));
    const results = await screen.findByTestId('delete-results');
    expect(within(results).getByRole('link', { name: 'Open job for AB\\old' })).toHaveAttribute('href', '/endpoints/WKS-111/jobs/job-9?from=%2Fendpoints%2FWKS-111%2Fprofiles');
  });

  it('keeps Run disabled without a reason even when the hostname matches', async () => {
    list = { items: [row('old')] };
    mount('Admin');
    await userEvent.click(await screen.findByRole('checkbox', { name: 'Select AB\\old' }));
    await userEvent.click(screen.getByRole('button', { name: 'Delete selected (1)' }));
    const dlg = await screen.findByRole('dialog');
    await userEvent.type(within(dlg).getByLabelText(/Type the hostname/), 'WKS-111');
    expect(within(dlg).getByRole('button', { name: 'Run' })).toBeDisabled();
    expect(postsTo('/delete-batch')).toHaveLength(0);
  });

  it('shows a server error from delete-batch as plain text', async () => {
    list = { items: [row('old')] };
    api.post.mockImplementation(async (path: string) => {
      if (path.endsWith('/delete-batch/confirm')) return { token: 'tok', expectedHostname: 'WKS-111', count: 1, items: [{ sid: 'S-1-5-21-old', account: 'AB\\old', path: 'C:\\Users\\old', sizeBytes: null }] };
      throw new api.ApiError(403, '<img src=x onerror=alert(1)> forbidden');
    });
    mount('Admin');
    await userEvent.click(await screen.findByRole('checkbox', { name: 'Select AB\\old' }));
    await userEvent.click(screen.getByRole('button', { name: 'Delete selected (1)' }));
    const dlg = await screen.findByRole('dialog');
    await userEvent.type(within(dlg).getByLabelText(/Type the hostname/), 'WKS-111');
    await userEvent.type(within(dlg).getByLabelText(/Reason/), 'r');
    await userEvent.click(within(dlg).getByRole('button', { name: 'Run' }));
    expect(await within(dlg).findByRole('alert')).toHaveTextContent('<img src=x onerror=alert(1)> forbidden');
    expect(dlg.querySelector('img')).toBeNull();
  });

  it('shows per-profile reasons from a 400, resolved from the rows, as text', async () => {
    list = { items: [row('old')] };
    api.post.mockRejectedValue(new api.ApiError(400, 'Some profiles cannot be deleted', { reasons: [{ sid: 'S-1-5-21-old', reason: '<i>is loaded</i>' }] }));
    mount('Admin');
    await userEvent.click(await screen.findByRole('checkbox', { name: 'Select AB\\old' }));
    await userEvent.click(screen.getByRole('button', { name: 'Delete selected (1)' }));
    const li = await screen.findByTestId('delete-reasons');
    expect(li).toHaveTextContent('AB\\old');
    expect(li).toHaveTextContent('C:\\Users\\old');
    expect(li).toHaveTextContent('<i>is loaded</i>');
    expect(li.querySelector('i')).toBeNull();
  });

  it('shows an unknown last-used source as raw text', async () => {
    list = { items: [row('a', { lastUsedSource: 'HelperX' })] };
    mount('Operator');
    expect(await screen.findByText('HelperX')).toBeInTheDocument();
  });

  it('shows a confirm error as plain text and opens no dialog', async () => {
    list = { items: [row('old')] };
    api.post.mockRejectedValue(new api.ApiError(400, 'Profile is loaded'));
    mount('Admin');
    await userEvent.click(await screen.findByRole('checkbox', { name: 'Select AB\\old' }));
    await userEvent.click(screen.getByRole('button', { name: 'Delete selected (1)' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Profile is loaded');
    expect(screen.queryByRole('dialog')).toBeNull();
  });
});
