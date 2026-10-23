// Browser workflow with an in-process fixture API. Never connects to a deployed Nexora API.
// Run: node e2e/qa-isolated.mjs. Edge and the existing playwright-core dependency are required.
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createServer } from 'vite';
import react from '@vitejs/plugin-react';
import { chromium } from 'playwright-core';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const artifacts = path.resolve(root, '../../qa-reports/test-artifacts/browser-recovery');
await fs.mkdir(artifacts, { recursive: true });
const server = await createServer({ configFile: false, root, plugins: [react()], server: { host: '127.0.0.1', port: 0, proxy: {} } });
let browser;
const passed = [], errors = [], unexpected = [];
const record = (name) => { passed.push(name); console.log(`PASS ${name}`); };
try {
  await server.listen();
  const base = `http://127.0.0.1:${server.httpServer.address().port}`;
  browser = await chromium.launch({ channel: 'msedge', headless: true });
  const context = await browser.newContext({ viewport: { width: 1366, height: 768 }, serviceWorkers: 'block' });
  let role = 'Admin', detailReads = 0, submittedJob = null;
  const endpoints = [], groups = [];
  const now = new Date().toISOString();
  const entry = { code: 'demo.simulate', displayName: 'Simulate a job (demo)', category: 'power-session', risk: 'Low', minRole: 'Operator', method: 'Local', parameters: [], timeoutSeconds: 30, description: 'Fixture only', command: null, supportsCancel: true, supportsBulk: true, requiresUserNotice: false, needsConfirmation: false, needsReason: false, needsTypedConfirmation: false, heavy: false, isPower: false, checksProtected: false, implemented: true, allowed: true };
  const jobDetail = () => ({
    job: { id: 'job-1', actionCode: entry.code, actionName: entry.displayName, initiatorSid: 'qa', initiatorAccount: 'QA\\operator', status: 'Succeeded', createdUtc: now, finishedUtc: now, targetCount: 1, queued: 0, running: 0, succeeded: 1, warning: 0, failed: 0, cancelled: 0, timedOut: 0, skipped: 0, protectedOverride: false, risk: 'Low' },
    parametersJson: '{}', targets: [{ id: 'target-1', jobId: 'job-1', endpointId: endpoints[0].id, endpointName: endpoints[0].name, host: endpoints[0].hostname, status: 'Succeeded', startedUtc: now, finishedUtc: now, durationSeconds: 1, exitCode: 0, resultSummary: 'Simulated diagnostic completed', progress: 100, logLines: 1, hasOutputFile: false }],
  });
  await context.route('**/*', async (route) => {
    const req = route.request(), url = new URL(req.url()), method = req.method(), p = url.pathname;
    if (url.origin !== base) { unexpected.push(req.url()); return route.abort(); }
    const json = (data, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(data) });
    if (p.startsWith('/hubs/')) return json({ error: 'SignalR deliberately isolated' }, 503);
    if (!p.startsWith('/api/')) return route.continue();
    if (p === '/api/me') return json({ sid: 'qa', account: 'QA\\operator', displayName: 'QA Operator', role, status: 'Active', isSuperAdmin: false, detail: null });
    if (p === '/api/diagnose/profiles' || p.endsWith('/diagnose/runs')) return json({ error: 'Diagnostics is not enabled on this server.' }, 404);
    if (p.endsWith('/overview') && p.startsWith('/api/endpoints/')) return json({ online: true, lastSeenUtc: now, collectedUtc: null, stale: false, unavailableReason: null, inventory: null, live: null });
    if (p === '/api/antiforgery') return json({ token: 'fixture-only' });
    if (p === '/api/thresholds') return json({ cpuAlertPercent: 90, ramAlertPercent: 90, diskAlertPercent: 90, diskHealthOkFrom: 85, diskHealthCriticalBelow: 50 });
    if (p === '/api/helper/status') return json({ connected: true, everConnected: true, serverUrl: base, downloadAvailable: false });
    if (p === '/api/jobs/catalog') return json(role === 'Viewer' ? [] : [entry]);
    if (p === '/api/endpoints' && method === 'POST') {
      const body = req.postDataJSON();
      assert.equal(body.items.length, 1);
      const pc = { id: '3f1c0a52-7b8e-4d11-9a21-0c5d2e6f7a10', name: body.items[0].name, hostname: body.items[0].address, groupIds: [], reportedHostname: null, ipAddress: null, lastUser: null, osCaption: 'Fixture Windows', osVersion: null, uptimeSeconds: null, lastSeenUtc: now, online: true, cpuPercent: null, ramPercent: null, diskPercent: null, diskHealth: 'Unknown', diskHealthPercent: null, ssdHealthPercent: null, hddHealthPercent: null, ramTotalBytes: null, volumes: [], manufacturer: null, model: null, organizationalUnit: null, lastError: null, alerts: [], isPinned: false };
      endpoints.push(pc); return json([pc]);
    }
    if (p === '/api/endpoints') return json(endpoints);
    if (p === '/api/groups' && method === 'POST') { const g = { id: 'g-1', ...req.postDataJSON(), memberIds: [], online: 0 }; groups.push(g); return json(g); }
    if (p === '/api/groups') return json(groups);
    if (p === '/api/groups/g-1/members/changes' && method === 'POST') {
      const d = req.postDataJSON();
      groups[0].memberIds = [...groups[0].memberIds.filter((x) => !d.remove.includes(x)), ...d.add]; groups[0].online = groups[0].memberIds.length;
      endpoints[0].groupIds = groups[0].memberIds.includes('3f1c0a52-7b8e-4d11-9a21-0c5d2e6f7a10') ? ['g-1'] : [];
      return json(groups[0]);
    }
    if (p.startsWith('/api/endpoints/by-host/')) { detailReads++; return endpoints[0] && endpoints[0].hostname.toLowerCase() === decodeURIComponent(p.slice('/api/endpoints/by-host/'.length)).toLowerCase() ? json(endpoints[0]) : json({ error: 'Endpoint not found.' }, 404); }
    if (p === '/api/endpoints/3f1c0a52-7b8e-4d11-9a21-0c5d2e6f7a10') { detailReads++; return json(endpoints[0]); }
    if (p === '/api/endpoints/3f1c0a52-7b8e-4d11-9a21-0c5d2e6f7a10/hardware') return json({ status: 'Partial', collectedUtc: now, cached: false, serialsHidden: false, errors: ['Memory modules: fixture has no SMBIOS data'],
      os: { productName: 'Fixture Windows 11 Pro', edition: 'Professional', version: '24H2', build: '26100.4061', architecture: 'x64', installedUtc: null, bootTimeUtc: null },
      cpu: { manufacturer: 'Intel', model: 'Fixture CPU', physicalCores: 4, logicalProcessors: 8, architecture: 'x64', maxClockMhz: 3000 },
      memory: { installedBytes: null, usableBytes: null, technology: null, speedMhz: null, slotCount: null, occupiedSlots: 0, emptySlots: null, maxCapacityBytes: null, modules: [], modulesReported: false },
      board: null, bios: null, computer: null });
    if (p === '/api/endpoints/3f1c0a52-7b8e-4d11-9a21-0c5d2e6f7a10/profiles') return json([]);
    if (p === '/api/audit') return json({ items: [], total: 0, page: 1, pageSize: 50 });
    if (p === '/api/endpoints/3f1c0a52-7b8e-4d11-9a21-0c5d2e6f7a10/detail') { detailReads++; return json({ services: [], processes: [], disks: [], software: [], errors: [], warnings: [] }); }
    if (p === '/api/endpoints/3f1c0a52-7b8e-4d11-9a21-0c5d2e6f7a10/metrics' || p === '/api/endpoints/3f1c0a52-7b8e-4d11-9a21-0c5d2e6f7a10/disks') return json([]);
    if (p.endsWith('/services') && p.startsWith('/api/endpoints/')) return json([]);
    if (p === '/api/jobs' && method === 'GET') return json({ items: [], total: 0, page: 1, pageSize: 25 });
    if (p === '/api/jobs' && method === 'POST') { submittedJob = req.postDataJSON(); return json(jobDetail()); }
    if (p === '/api/jobs/job-1') return json(jobDetail());
    if (p === '/api/jobs/job-1/targets/target-1/log') return json([{ seq: 1, timestampUtc: now, stream: 'stdout', text: 'Fixture diagnostic completed' }]);
    unexpected.push(`${method} ${p}`); return json({ error: 'Unconfigured fixture route' }, 500);
  });
  const page = await context.newPage();
  page.on('pageerror', (e) => errors.push(e.message));
  await page.goto(`${base}/endpoints`);
  await page.getByRole('button', { name: 'Add workstation', exact: true }).waitFor();
  record('authenticated Admin shell (fixture identity, not Windows SSO)');
  await page.getByRole('button', { name: 'Add workstation', exact: true }).click();
  await page.getByLabel('Workstation name', { exact: true }).fill('QA workstation with a long descriptive name');
  await page.getByLabel('IP address or host name').fill('QA-PC-001');
  await page.getByRole('button', { name: 'Add', exact: true }).click();
  await page.locator('[data-testid=endpoint-row]').waitFor();
  record('add workstation');
  await page.getByRole('link', { name: 'Groups', exact: true }).click();
  await page.getByRole('button', { name: 'New group' }).click();
  await page.getByLabel('Name', { exact: true }).fill('QA Pilot');
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await page.getByRole('button', { name: 'Edit members' }).click();
  await page.getByRole('checkbox', { name: /QA workstation/ }).check();
  await page.getByRole('button', { name: 'Save members' }).click();
  await page.getByRole('link', { name: /QA workstation/ }).waitFor();
  assert.deepEqual(groups[0].memberIds, ['3f1c0a52-7b8e-4d11-9a21-0c5d2e6f7a10']);
  record('create group and assign individually added workstation');
  await page.getByRole('link', { name: 'Workstations', exact: true }).click();
  await page.locator('#f-group').selectOption('g-1');
  await page.locator('[data-testid=endpoint-row]').waitFor();
  assert.equal(await page.locator('[data-testid=endpoint-row]').count(), 1);
  const heights = await page.locator('[data-testid=endpoint-row]').evaluateAll(rows => rows.map(r => r.getBoundingClientRect().height));
  assert.ok(heights.every(h => h <= 48));
  assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
  await page.screenshot({ path: path.join(artifacts, 'workstations-1366.png'), fullPage: true });
  record('group filter, compact row and desktop overflow');
  await page.locator('[data-testid=endpoint-row]').dblclick();
  await page.getByRole('heading', { name: 'QA workstation with a long descriptive name', exact: true }).waitFor();
  const before = detailReads;
  await page.getByRole('button', { name: 'Refresh', exact: true }).click();
  await page.waitForFunction(() => !document.querySelector('button:disabled')?.textContent?.includes('Refreshing'));
  assert.ok(detailReads > before);   // the summary was read again
  record('open workstation and refresh live detail');
  await page.getByRole('link', { name: 'Remote Actions', exact: true }).click();
  await page.getByRole('button', { name: /Run Simulate a job/ }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Run', exact: true }).click();
  await page.getByRole('dialog').getByText(/Simulated diagnostic completed/).first().waitFor();
  assert.deepEqual(submittedJob.endpointIds, ['3f1c0a52-7b8e-4d11-9a21-0c5d2e6f7a10']);
  assert.equal(submittedJob.actionCode, 'demo.simulate');
  assert.match(page.url(), /[/]endpoints[/][^/]+[/]actions(?:[/]power-session)?$/);   // the output window opened in place
  await page.getByRole('link', { name: 'Open job' }).click();
  assert.match(page.url(), /[/]endpoints[/][^/]+[/]jobs[/]job-1[?]from=/);
  record('safe simulated action selects correct target and displays completion');
  await page.reload();
  await page.getByText('Simulated diagnostic completed', { exact: true }).first().waitFor();
  record('job survives browser refresh (fixture persistence)');
  await page.getByTestId('job-back').click();
  await page.getByRole('heading', { level: 1 }).waitFor();
  assert.match(page.url(), /[/]endpoints[/][^/]+[/]actions/);
  record('job Back returns to the workstation page');
  for (const [w, h] of [[390, 844], [768, 1024], [1366, 768], [1920, 1080]]) {
    await page.setViewportSize({ width: w, height: h });
    for (const tab of ['overview', 'hardware', 'storage', 'actions', 'services', 'processes', 'jobs', 'monitoring', 'logs', 'profiles', 'diagnose']) {
      await page.goto(`${base}/endpoints/3f1c0a52-7b8e-4d11-9a21-0c5d2e6f7a10/${tab}`);
      await page.getByRole('heading', { level: 1 }).waitFor();
      await page.waitForLoadState('networkidle');
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `horizontal overflow on ${tab} at ${w}px`);
      if (tab === 'overview' || tab === 'actions') await page.screenshot({ path: path.join(artifacts, `workstation-${tab}-${w}.png`), fullPage: true });
    }
  }
  await page.setViewportSize({ width: 1366, height: 768 });
  record('workstation tabs have no horizontal overflow at 390, 768, 1366 and 1920 px');
  role = 'Viewer';
  await page.goto(`${base}/endpoints`);
  await page.locator('[data-testid=endpoint-row]').waitFor();
  assert.equal(await page.getByRole('button', { name: 'Add workstation', exact: true }).count(), 0);
  await page.goto(`${base}/groups`);
  await page.getByRole('button', { name: /QA Pilot/ }).click();
  assert.equal(await page.getByRole('button', { name: 'Edit members' }).count(), 0);
  record('Viewer cannot access inventory or group mutation controls');
  await page.emulateMedia({ colorScheme: 'dark' });
  await page.screenshot({ path: path.join(artifacts, 'groups-dark.png'), fullPage: true });
  await page.setViewportSize({ width: 390, height: 844 });
  assert.ok(await page.getByRole('navigation', { name: 'Main' }).evaluate(el => el.getBoundingClientRect().height <= 80));
  assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
  await page.screenshot({ path: path.join(artifacts, 'groups-mobile.png'), fullPage: true });
  record('compact mobile navigation and no page overflow');
  assert.deepEqual(errors, []);
  assert.deepEqual(unexpected, []);
  record('no uncaught page errors or unconfigured/external requests');
} finally {
  await fs.writeFile(path.join(artifacts, 'results.json'), JSON.stringify({ passed, errors, unexpected }, null, 2));
  await browser?.close();
  await server.close();
}
