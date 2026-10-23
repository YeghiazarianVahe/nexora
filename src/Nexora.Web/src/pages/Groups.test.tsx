// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { Groups } from './Groups';
import type { Me } from '../types';

const api = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn(), put: vi.fn(), del: vi.fn() }));
vi.mock('../api', () => ({ ...api, ApiError: class extends Error {} }));
vi.mock('../jobs/RemoteActions', () => ({ useCatalog: () => ({ data: [] }) }));
const group = { id: 'g1', name: 'Pilot group', description: '', memberIds: [], online: 0 };
const me = { role: 'Admin', status: 'Active' } as Me;
function show() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  render(<QueryClientProvider client={qc}><MemoryRouter><Groups me={me} /></MemoryRouter></QueryClientProvider>);
}
beforeEach(() => {
  vi.resetAllMocks();
  api.get.mockImplementation(async (path: string) => path === '/api/groups' ? [group] : []);
});
afterEach(cleanup);

it('shows inventory load failures and blocks member editing until retry succeeds', async () => {
  api.get.mockImplementation(async (path: string) => {
    if (path === '/api/endpoints') throw new Error('Inventory unavailable');
    return [group];
  });
  const user = userEvent.setup();
  show();
  await user.click(await screen.findByRole('button', { name: /Pilot group/ }));
  expect(await screen.findByRole('alert')).toHaveTextContent('Inventory unavailable');
  expect(screen.getByRole('button', { name: 'Edit members' })).toBeDisabled();
  api.get.mockImplementation(async (path: string) => path === '/api/groups' ? [group] : []);
  await user.click(screen.getByRole('button', { name: 'Retry workstations' }));
  await waitFor(() => expect(screen.getByRole('button', { name: 'Edit members' })).toBeEnabled());
});

it('surfaces a failed group deletion in the confirmation dialog', async () => {
  api.del.mockRejectedValue(new Error('Group could not be deleted'));
  const user = userEvent.setup();
  show();
  await user.click(await screen.findByRole('button', { name: /Pilot group/ }));
  await user.click(screen.getByRole('button', { name: 'Delete group' }));
  await user.click(screen.getByRole('button', { name: /^Delete$/ }));
  expect(await screen.findByRole('alert')).toHaveTextContent('Group could not be deleted');
  expect(screen.getByRole('dialog')).toBeInTheDocument();
});

it('assigns an existing workstation and preserves its ID in the membership request', async () => {
  api.get.mockImplementation(async (path: string) => path === '/api/groups' ? [group] : [{ id: 'pc1', name: 'Desk one', hostname: 'PC-ONE' }]);
  api.post.mockResolvedValue({ ...group, memberIds: ['pc1'] });
  const user = userEvent.setup();
  show();
  await user.click(await screen.findByRole('button', { name: /Pilot group/ }));
  await user.click(screen.getByRole('button', { name: 'Edit members' }));
  await user.click(screen.getByRole('checkbox', { name: /Desk one/ }));
  await user.click(screen.getByRole('button', { name: 'Save members' }));
  await waitFor(() => expect(api.post).toHaveBeenCalledWith('/api/groups/g1/members/changes', { add: ['pc1'], remove: [] }));
});
