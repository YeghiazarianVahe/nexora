// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import {
  ENDPOINT_TABS, backTarget, diagnoseRunPath, endpointActionsPath, endpointJobPath, endpointPath, jobPath, loadListQuery, normalizeTab, parentOf, parseOrigin, safeOrigin, saveListQuery,
} from './origin';

const ID = '11111111-1111-4111-8111-111111111111';

describe('origin validation', () => {
  it('accepts workstation tabs (GUID and host forms) and the jobs list', () => {
    expect(safeOrigin(`/endpoints/${ID}/actions`)).toBe(`/endpoints/${ID}/actions`);
    expect(safeOrigin(`/endpoints/${ID}`)).toBe(`/endpoints/${ID}/overview`);
    expect(safeOrigin('/endpoints/WKS-44/logs')).toBe('/endpoints/WKS-44/logs');
    expect(safeOrigin('/endpoints/wks-44.corp.local')).toBe('/endpoints/wks-44.corp.local/overview');
    expect(safeOrigin('/jobs')).toBe('/jobs');
    expect(safeOrigin('/endpoints/WKS-44/actions/network-policy')).toBe('/endpoints/WKS-44/actions/network-policy');
    expect(parseOrigin(`/endpoints/${ID}/logs`)).toMatchObject({ kind: 'workstation', ref: ID, tab: 'logs' });
    expect(parseOrigin('/endpoints/WKS-44/jobs')).toMatchObject({ kind: 'workstation', ref: 'WKS-44', tab: 'jobs' });
  });

  it('maps legacy tab names', () => {
    expect(safeOrigin(`/endpoints/${ID}/storage`)).toBe(`/endpoints/${ID}/storage`);
    expect(safeOrigin('/endpoints/WKS-44/hardware')).toBe('/endpoints/WKS-44/hardware');
    expect(safeOrigin('/endpoints/WKS-44/monitoring')).toBe('/endpoints/WKS-44/monitoring');
  });

  it.each([
    null, undefined, '', 'jobs', 'https://evil.example', 'http://localhost/jobs', '//evil.example', '/\evil.example', '\evil', 'javascript:alert(1)',
    '/settings', '/jobs/abc', '/endpoints', `/endpoints/${ID}/unknown`, `/endpoints/${ID}/actions/extra`, '/endpoints/WKS-44/jobs/123',
    `/endpoints/${ID}/../../x`, '/endpoints/../x', '/endpoints/a%2f..%2fb/logs', '/endpoints/-bad/logs', '/endpoints/AB PI/logs', '/endpoints/WKS-44//logs',
    '/jobs\n', `/endpoints/${ID}/actions\u0000`, 'x'.repeat(500),
  ])('rejects %j', (raw) => {
    expect(safeOrigin(raw as string | null | undefined)).toBeNull();
  });

  it('drops query strings and fragments instead of passing them on', () => {
    expect(safeOrigin(`/endpoints/${ID}/actions?next=http://evil`)).toBe(`/endpoints/${ID}/actions`);
    expect(safeOrigin('/endpoints/WKS-44/actions#frag')).toBe('/endpoints/WKS-44/actions');
  });
});

describe('path helpers', () => {
  it('builds every route', () => {
    expect(endpointPath('WKS-44')).toBe('/endpoints/WKS-44');
    expect(endpointPath('WKS-44', 'overview')).toBe('/endpoints/WKS-44/overview');
    for (const t of ENDPOINT_TABS) expect(endpointPath('WKS-44', t)).toBe(`/endpoints/WKS-44/${t}`);
    expect(endpointJobPath('WKS-44', 'j1')).toBe('/endpoints/WKS-44/jobs/j1');
    expect(endpointJobPath('WKS-44', 'j1', { output: true })).toBe('/endpoints/WKS-44/jobs/j1?output=1');
    expect(diagnoseRunPath('WKS-44', 'r1')).toBe('/endpoints/WKS-44/diagnose/r1');
  });
  it('preserves the originating tab for scoped jobs and rejects other hosts', () => {
    for (const tab of ['services', 'processes', 'actions', 'jobs']) {
      const url = endpointJobPath('WKS-44', 'j1', { from: `/endpoints/WKS-44/${tab}` });
      expect(new URL(url, 'http://localhost').searchParams.get('from')).toBe(`/endpoints/WKS-44/${tab}`);
    }
    expect(endpointJobPath('WKS-44', 'j1', { from: '/endpoints/OTHER/services' })).toBe('/endpoints/WKS-44/jobs/j1');
    expect(endpointJobPath('WKS-44', 'j1', { from: '//evil.example' })).toBe('/endpoints/WKS-44/jobs/j1');
  });
  it('encodes untrusted segments', () => {
    expect(endpointPath('a/b?c#d')).toBe('/endpoints/a%2Fb%3Fc%23d');
    expect(endpointJobPath('x', '../y')).toBe('/endpoints/x/jobs/..%2Fy');
  });
  it('normalizes tab names', () => {
    expect(normalizeTab('disks')).toBe('storage');
    expect(normalizeTab('storage')).toBe('storage');
    expect(normalizeTab('hardware')).toBe('hardware');
    expect(normalizeTab('monitoring')).toBe('monitoring');
    expect(normalizeTab('nonsense')).toBeNull();
    expect(normalizeTab('constructor')).toBeNull();
    expect(normalizeTab(undefined)).toBeNull();
  });
});

describe('jobPath (bulk jobs)', () => {
  it('adds a validated origin and the output flag', () => {
    expect(jobPath('j1')).toBe('/jobs/j1');
    expect(jobPath('j1', `/endpoints/${ID}/actions`, { output: true })).toBe(`/jobs/j1?from=%2Fendpoints%2F${ID}%2Factions&output=1`);
    expect(jobPath('j1', '/endpoints/WKS-44/storage')).toBe('/jobs/j1?from=%2Fendpoints%2FWKS-44%2Fstorage');
  });
  it('never emits an unsafe origin, and the jobs list needs none', () => {
    expect(jobPath('j1', 'https://evil.example')).toBe('/jobs/j1');
    expect(jobPath('j1', '/jobs')).toBe('/jobs/j1');
  });
});

describe('endpointActionsPath', () => {
  it('retains only bounded action search and recognized risk filters across a refreshed job URL', () => {
    const from = '/endpoints/WKS-44/actions/network-policy?q=DNS&risk=Low,Fake&redirect=https://evil.example';
    const path = endpointJobPath('WKS-44', 'j1', { from });
    expect(new URL(path, 'http://localhost').searchParams.get('from')).toBe('/endpoints/WKS-44/actions/network-policy?q=DNS&risk=Low');
  });
  it('builds the tab path, with and without a category, encoding the host', () => {
    expect(endpointActionsPath('WKS-44')).toBe('/endpoints/WKS-44/actions');
    expect(endpointActionsPath('WKS-44', 'health-repair')).toBe('/endpoints/WKS-44/actions/health-repair');
    expect(endpointActionsPath('a/b', 'x y')).toBe('/endpoints/a%2Fb/actions/x%20y');
  });
});

describe('parentOf', () => {
  it.each([
    ['/endpoints/WKS-44/jobs/123', '/endpoints/WKS-44'],
    ['/endpoints/WKS-44/diagnose/r1', '/endpoints/WKS-44/diagnose'],
    ['/endpoints/WKS-44/actions/health-repair', '/endpoints/WKS-44'],
    ['/endpoints/WKS-44/overview', '/endpoints'],
    ['/endpoints/WKS-44/processes', '/endpoints'],
    ['/endpoints/WKS-44/jobs', '/endpoints'],
    ['/endpoints/WKS-44/diagnose', '/endpoints'],
    ['/endpoints/WKS-44', '/endpoints'],
    ['/endpoints/WKS-44/', '/endpoints'],
    ['/endpoints', '/dashboard'],
    ['/jobs/abc', '/jobs'],
    ['/jobs', '/dashboard'],
    ['/dashboard', '/dashboard'],
    ['/', '/dashboard'],
  ])('%s -> %s', (from, to) => {
    expect(parentOf(from)).toBe(to);
  });
});

describe('remembered list query', () => {
  afterEach(() => window.sessionStorage.clear());

  it('restores filters and sort, and only the list parameters', () => {
    saveListQuery('q=pi&status=offline&sort=cpu&dir=desc&evil=http%3A%2F%2Fx&redirect=//x');
    expect(loadListQuery()).toBe('?q=pi&status=offline&sort=cpu&dir=desc');
    expect(backTarget('/endpoints/WKS-44/storage')).toBe('/endpoints?q=pi&status=offline&sort=cpu&dir=desc');
  });
  it('only the list uses it', () => {
    saveListQuery('q=pi');
    expect(backTarget('/endpoints/WKS-44/jobs/1')).toBe('/endpoints/WKS-44');
    expect(backTarget('/jobs/1')).toBe('/jobs');
  });
  it('is empty when nothing was saved or storage throws', () => {
    expect(backTarget('/endpoints/WKS-44')).toBe('/endpoints');
    const spy = Object.getOwnPropertyDescriptor(window, 'sessionStorage')!;
    Object.defineProperty(window, 'sessionStorage', { configurable: true, get() { throw new Error('blocked'); } });
    try {
      expect(() => saveListQuery('q=pi')).not.toThrow();
      expect(backTarget('/endpoints/WKS-44')).toBe('/endpoints');
    } finally { Object.defineProperty(window, 'sessionStorage', spy); }
  });
});
