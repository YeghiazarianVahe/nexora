// Isolated browser integration. All API calls are intercepted; external origins are blocked.
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createServer } from 'vite';
import react from '@vitejs/plugin-react';
import { chromium } from 'playwright-core';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const artifacts = process.env.NEXORA_QA_OUTPUT ?? path.resolve(root, '../../qa-reports/test-artifacts/workstation-console');
await fs.mkdir(artifacts, { recursive: true });
const server = await createServer({ configFile: false, root, plugins: [react()], server: { host: '127.0.0.1', port: 0, proxy: {} } });
const id = '11111111-1111-4111-8111-111111111111', host = 'QA-PC-01', now = new Date().toISOString(), GB = 1024 ** 3;
const pc = { id, hostname: host, name: 'Finance workstation', online: true, ipAddress: '192.0.2.42', lastUser: 'CORP\\anna', groupIds: [], alerts: [],
  osCaption: 'Windows 11 Enterprise', osVersion: '10.0.26100', lastSeenUtc: now, cpuPercent: 37, ramPercent: 62, diskPercent: 60,
  diskHealth: 'OK', diskHealthPercent: 96, ssdHealthPercent: 96, hddHealthPercent: null, ramTotalBytes: 32 * GB, manufacturer: 'Dell', model: 'OptiPlex 7010',
  volumes: [{ letter: 'C:', fileSystem: 'NTFS', totalBytes: 512 * GB, freeBytes: 200 * GB }] };
const inventory = { status: 'Complete', collectedUtc: now, cached: true, errors: [], serialsHidden: false,
  computer: { manufacturer: 'Dell', model: 'OptiPlex 7010', hostname: host, domain: 'corp.example', domainJoined: true },
  cpu: { manufacturer: 'Intel', model: 'Intel Core i7-13700', physicalCores: 16, logicalProcessors: 24, architecture: 'x64', maxClockMhz: 5200 },
  os: { productName: 'Windows 11 Enterprise', edition: 'Enterprise', version: '24H2', kernelVersion: '10.0.26100', build: '26100.4061', architecture: 'x64', installedUtc: '2025-03-10T08:00:00Z', bootTimeUtc: new Date(Date.now()-8*3600_000).toISOString() },
  memory: { installedBytes: 32 * GB, usableBytes: 32 * GB, slotCount: 4, occupiedSlots: 2, emptySlots: 2, modulesReported: true, technology: 'DDR5', speedMhz: 4800,
    modules: [{ slot: 'DIMM A1', capacityBytes: 16*GB, manufacturer: 'Kingston', speedMhz: 4800, partNumber: 'KVR48U40BS8-16' }, { slot: 'DIMM B1', capacityBytes: 16*GB, manufacturer: 'Kingston', speedMhz: 4800, partNumber: 'KVR48U40BS8-16' }] },
  board: { manufacturer: 'Dell', model: '0M6C7G' }, bios: { manufacturer: 'Dell', version: '1.22.0', releaseDateUtc: '2026-02-01T00:00:00Z' } };
const disk = { diskKey: 'NVME1', model: 'Fixture NVMe 512 GB', serial: 'FIXTURE', mediaType: 'NVMe', type: 'SSD', busType: 'NVMe', sizeBytes: 512 * GB,
  healthPercent: 96, confidence: 'High', valueKind: 'estimated', stale: false, status: 'OK', temperatureC: 42, updatedUtc: now, attributes: [], temperatureHistory: [], summary: 'NVMe percentage used: 4%.',
  temperature: { currentC: 42, warnC: 70, critC: 80, source: 'default', lifetimeMaxC: null, status: 'OK' }, reasons: [{ code: 'nvme-used', text: 'NVMe percentage used: 4%.', delta: -4 }] };
const param = (name, type='String') => ({ name, type, label: name, required: true, maxLength: 256, min: 1, max: 99999, allowed: null, default: null });
const action = (code, displayName, category, parameters=[]) => ({ code, displayName, category, parameters, description: 'Isolated fixture action; no remote execution.', risk: 'Low', minRole: 'Operator', method: 'SystemTask', timeoutSeconds: 30,
  allowed: true, implemented: true, supportsCancel: true, supportsBulk: false, needsConfirmation: false, needsReason: false, needsTypedConfirmation: false, checksProtected: false, heavy: false, requiresUserNotice: false });
const catalog = [action('net.flushdns', 'Flush DNS cache', 'network-policy'), action('svc.start', 'Start service', 'services', [param('serviceName')]), action('proc.kill', 'Stop process', 'processes', [param('pid','Int'), param('processName')])];
const profile = (sid, account, localPath) => ({ sid, account, localPath, loaded: false, deletable: true, sizeBytes: GB, lastUsedUtc: now, sizeMeasuredUtc: now, state: 'Local', blockReason: null });
let createdAction = catalog[0], browser;
const passed = [], errors = [], unexpected = [];
const record = s => { passed.push(s); console.log(`PASS ${s}`); };
const job = () => ({ job: { id: 'fixture-job', actionCode: createdAction.code, actionName: createdAction.displayName, status: 'Succeeded', createdUtc: now, finishedUtc: now, risk: 'Low', targetCount: 1, succeeded: 1, failed: 0, running: 0, queued: 0, warning: 0, cancelled: 0, timedOut: 0, skipped: 0, initiatorAccount: 'QA\\operator' }, parametersJson: '{}', targets: [{ id: 'target', jobId: 'fixture-job', endpointId: id, host, endpointName: pc.name, status: 'Succeeded', resultSummary: 'Fixture completed', logLines: 0, progress: 100 }] });
try {
  await server.listen();
  const base = `http://127.0.0.1:${server.httpServer.address().port}`;
  browser = await chromium.launch({ channel: 'msedge', headless: true });
  const context = await browser.newContext({ viewport: { width: 1366, height: 900 }, serviceWorkers: 'block' });
  await context.route('**/*', async route => {
    const req = route.request(), url = new URL(req.url()), p = url.pathname;
    if (url.origin !== base) { unexpected.push(req.url()); return route.abort(); }
    const json = data => route.fulfill({ contentType: 'application/json', body: JSON.stringify(data) });
    if (p.startsWith('/hubs/')) return route.fulfill({ status: 503, body: 'Fixture: SignalR disabled' });
    if (!p.startsWith('/api/')) return route.continue();
    if (p === '/api/me') return json({ sid: 'qa', account: 'QA\\operator', role: 'Admin', status: 'Active', displayName: 'QA Operator' });
    if (p === '/api/antiforgery') return json({ token: 'fixture' });
    if (p === '/api/helper/status') return json({ connected: true, everConnected: true, serverUrl: base });
    if (p === '/api/thresholds') return json({ cpuAlertPercent: 90, ramAlertPercent: 90, diskAlertPercent: 90, healthOkFrom: 85, healthCriticalBelow: 50 });
    if (p === '/api/groups') return json([]);
    if (p === '/api/admin/access-requests') return json([]);
    if (p === '/api/jobs/catalog') return json(catalog);
    if (p === '/api/jobs' && req.method() === 'POST') { const body = req.postDataJSON(); assert.deepEqual(body.endpointIds, [id]); createdAction = catalog.find(a => a.code === body.actionCode); assert.ok(createdAction); return json(job()); }
    if (p === '/api/jobs') return json({ items: [job().job], total: 1, page: 1, pageSize: 25 });
    if (p === '/api/jobs/fixture-job') return json(job());
    if (p === '/api/jobs/fixture-job/targets/target/log') return json([]);
    if (p === `/api/endpoints/by-host/${host}` || p === `/api/endpoints/${id}`) return json(pc);
    if (p === `/api/endpoints/${id}/overview`) return json({ online: true, lastSeenUtc: now, collectedUtc: now, stale: false, inventory,
      live: { cpuLoadPercent: 37, ramPercentUsed: 62, ramTotalBytes: 32*GB, ramUsedBytes: 19.84*GB, ramFreeBytes: 12.16*GB, sampledUtc: now } });
    if (p.endsWith('/hardware')) return json(inventory);
    if (p === `/api/endpoints/${id}/performance`) return json({endpointId:id,hostname:host,sample:{cpuLoadPercent:37,ramPercentUsed:62,ramTotalBytes:32*GB,ramUsedBytes:19.84*GB,ramFreeBytes:12.16*GB,sampledUtc:new Date().toISOString()}});
    if (p.endsWith('/metrics')) return json(Array.from({ length: 16 }, (_, i) => ({ timeUtc: new Date(Date.now()-(15-i)*180_000).toISOString(), cpu: 20+(i*7)%40, ram: 50+i, disk: 60 })));
    if (p.endsWith('/disks')) return json([disk]);
    if (p.endsWith('/services')) return json({ items: [{ name: 'Spooler', displayName: 'Print Spooler', state: 'Stopped', startMode: 'Auto', allowed: ['start'] }] });
    if (p.endsWith('/detail')) return json({ services: [], processes: [{ pid: 4242, name: 'fixture.exe', workingSetBytes: GB, cpuPercent: 2, owner: 'CORP\\anna' }], errors: [], software: [], disks: [], warnings: [] });
    if (p.startsWith('/api/endpoints/') && p.endsWith('/profiles')) return json({ items: [profile('S-1-5-21-1-2-3-1001', 'CORP\\anna', 'C:\\Users\\anna'), profile('S-1-5-18', 'SYSTEM', 'C:\\Windows\\systemprofile'), profile('S-1-5-21-1-2-3-500', 'CORP\\renamed-admin', 'C:\\Users\\admin')] });
    if (p === '/api/diagnose/profiles') return json([{ id: 'high-cpu', name: 'High CPU usage', description: 'Checks CPU load.', collectors: 1 }]);
    if (p.endsWith('/diagnose/runs')) return json([]);
    if (p.endsWith('/diagnose') && req.method() === 'POST') return json({ runId: 'fixture-run', jobId: 'fixture-job' });
    if (p === '/api/diagnose/runs/fixture-run') return json({ id: 'fixture-run', endpointId: id, profileName: 'High CPU usage', status: 'Complete', complete: true, startedUtc: now, finishedUtc: now, jobId: 'fixture-job',
      findings: [{ id: 'cpu', cause: 'CPU pressure detected in fixture', severity: 'warning', confidence: 'medium', score: 4, evidence: [{ label: 'CPU load', value: '95%' }], actions: [{ kind: 'manual', label: 'Review processes', instructions: 'Confirm the workload with the user.' }] }], collectors: [{ id: 'system-resources', status: 'ok' }], notEvaluated: [] });
    if (p === '/api/audit') return json({ items: [], total: 0 });
    unexpected.push(`${req.method()} ${p}`); return route.fulfill({ status: 500, body: 'Unconfigured fixture' });
  });
  const page = await context.newPage();
  page.on('pageerror', e => errors.push(e.message));
  const go = tab => page.goto(`${base}/endpoints/${host}/${tab}`);
  for (const [tab, pick] of [['actions/network-policy', () => page.getByRole('button', { name: 'Run Flush DNS cache', exact: true })], ['services', () => page.getByRole('button', { name: 'Start Spooler', exact: true })], ['processes', () => page.getByRole('button', { name: 'Stop process fixture.exe (4242)', exact: true })]]) {
    await go(tab);
    if (tab === 'processes') await page.getByRole('button', { name: 'Read from workstation' }).click();
    await pick().click();
    await page.getByRole('dialog').getByRole('button', { name: 'Run', exact: true }).click();
    await page.getByRole('link', { name: 'Open job', exact: true }).click();
    await page.reload();
    await page.getByTestId('job-back').click();
    assert.equal(new URL(page.url()).pathname, `/endpoints/${host}/${tab}`);
    record(`${tab} → fixture job → refresh → Back preserves tab`);
  }
  await page.goto(`${base}/jobs/fixture-job`);
  assert.equal(await page.getByTestId('job-back').getAttribute('href'), '/jobs');
  record('direct global job uses Jobs fallback');
  await go('profiles');
  await page.getByRole('table', { name: 'Profiles' }).getByText('CORP\\anna', { exact: true }).waitFor();
  assert.equal(await page.getByRole('table', { name: 'Profiles' }).getByRole('row').count(), 2);
  record('protected profiles hidden even from legacy payload');
  await go('diagnose');
  await page.getByRole('radio', { name: /High CPU usage/ }).click();
  await page.getByRole('button', { name: 'Run diagnosis' }).click();
  await page.getByText('CPU pressure detected in fixture').click();
  await page.getByText('95%', { exact: true }).waitFor();
  await page.getByText('Confirm the workload with the user.').waitFor();
  record('symptom → diagnostic result → evidence and guided next step');
  for (const width of [390, 768, 1366, 1920]) {
    await page.setViewportSize({ width, height: 900 });
    for (const tab of ['overview', 'hardware', 'storage', 'actions/network-policy']) {
      await go(tab);
      await page.getByRole('heading', { name: pc.name, exact: true }).waitFor();
      await page.waitForLoadState('networkidle');
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `${tab} overflows at ${width}`);
      await page.screenshot({ path: path.join(artifacts, `${tab.replace('/', '-')}-${width}.png`), fullPage: true });
    }
  }
  record('populated workstation console fits 390 / 768 / 1366 / 1920 px');
  assert.deepEqual(errors, []);
  assert.deepEqual(unexpected, []);
} finally {
  await fs.writeFile(path.join(artifacts, 'results.json'), JSON.stringify({ passed, errors, unexpected }, null, 2));
  await browser?.close();
  await server.close();
}
