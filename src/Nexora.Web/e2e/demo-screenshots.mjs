// Renders the UI against an in-process API of purely synthetic data and saves the README screenshots to docs/images/.
// Run: node e2e/demo-screenshots.mjs   (needs Chrome or Edge; never talks to a real server)
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createServer } from 'vite';
import react from '@vitejs/plugin-react';
import { chromium } from 'playwright-core';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const out = path.resolve(root, '../../docs/images');
await fs.mkdir(out, { recursive: true });

// ---------- deterministic synthetic fleet ----------
let seed = 7;
const rnd = () => (seed = (seed * 48271) % 2147483647) / 2147483647;
const pick = (a) => a[Math.floor(rnd() * a.length)];
const GB = 1024 ** 3;
const now = Date.now();
const iso = (msAgo) => new Date(now - msAgo).toISOString();
const users = ['alice.demo', 'bob.demo', 'carol.demo', 'dave.demo', 'erin.demo', 'frank.demo', 'grace.demo', 'heidi.demo'];
const models = [['Dell', 'OptiPlex 7090'], ['HP', 'EliteDesk 800 G6'], ['Lenovo', 'ThinkCentre M90q'], ['Dell', 'Latitude 5520']];
const ous = ['Head Office/Workstations', 'Branch A/Workstations', 'Branch B/Workstations'];
const GROUPS = [
  { id: 'g-1', name: 'Pilot', description: 'First wave of the rollout', memberIds: [], online: 0 },
  { id: 'g-2', name: 'Head office', description: 'Head office floors 1-4', memberIds: [], online: 0 },
];
const endpoints = Array.from({ length: 24 }, (_, i) => {
  const n = i === 0 ? 111 : 100 + i * 7;
  const [manufacturer, model] = pick(models);
  const online = i === 0 || rnd() > 0.2;
  const ssd = Math.round(60 + rnd() * 40), hdd = rnd() > 0.5 ? Math.round(70 + rnd() * 30) : null;
  const cpu = online ? Math.round(rnd() * (i % 6 === 3 ? 100 : 55)) : null;
  const ram = online ? Math.round(30 + rnd() * (i % 7 === 4 ? 68 : 40)) : null;
  const total = 476 * GB, free = Math.round(total * (0.15 + rnd() * 0.6));
  const worst = Math.min(ssd, hdd ?? 100);
  const alerts = [];
  if (cpu !== null && cpu >= 90) alerts.push('cpu');
  if (ram !== null && ram >= 90) alerts.push('ram');
  if (worst < 70) alerts.push('diskhealth');
  return {
    id: `00000000-0000-4000-8000-${String(i + 1).padStart(12, '0')}`,
    hostname: `wks-${n}`, name: `WKS-${n}`, reportedHostname: `WKS-${n}`, ipAddress: `10.20.30.${n % 250}`,
    lastUser: online ? `CONTOSO\\${pick(users)}` : null, osCaption: 'Microsoft Windows 11 Pro', osVersion: '10.0.26100',
    uptimeSeconds: online ? Math.round(rnd() * 20 * 86400) : null, lastSeenUtc: iso(online ? rnd() * 60_000 : 3 * 86400_000), online,
    cpuPercent: cpu, ramPercent: ram, diskPercent: Math.round(100 - (free / total) * 100),
    groupIds: i < 6 ? ['g-1'] : i < 16 ? ['g-2'] : [],
    diskHealth: worst >= 85 ? 'OK' : worst >= 50 ? 'Warning' : 'Critical', diskHealthPercent: worst, ssdHealthPercent: ssd, hddHealthPercent: hdd,
    ramTotalBytes: 16 * GB, volumes: [{ letter: 'C', fileSystem: 'NTFS', totalBytes: total, freeBytes: free }],
    manufacturer, model, organizationalUnit: pick(ous), ouStatus: 'found', ouError: null, lastError: null, diskScanError: null,
    alerts, isPinned: i === 0,
  };
});
for (const g of GROUPS) { g.memberIds = endpoints.filter((e) => e.groupIds.includes(g.id)).map((e) => e.id); g.online = endpoints.filter((e) => e.groupIds.includes(g.id) && e.online).length; }
const first = endpoints[0];

const auditActions = ['endpoint.add', 'job.create', 'group.update', 'endpoint.refresh', 'job.create'];
const audit = Array.from({ length: 14 }, (_, i) => ({
  id: 100 - i, timestampUtc: iso((i + 1) * 17 * 60_000), actorSid: 'S-1-5-21-1111111111-2222222222-3333333333-46118',
  actorAccount: 'CONTOSO\\demo.admin', action: auditActions[i % auditActions.length], targetType: 'Endpoint', targetId: endpoints[i % 24].id,
  oldValue: null, newValue: null, clientIp: '10.20.30.5', details: `Synthetic demo entry on ${endpoints[i % 24].name}`, success: i % 9 !== 8,
}));
const dashboard = () => ({
  total: endpoints.length, online: endpoints.filter((e) => e.online).length, offline: endpoints.filter((e) => !e.online).length,
  cpuAlerts: endpoints.filter((e) => e.alerts.includes('cpu')).length, ramAlerts: endpoints.filter((e) => e.alerts.includes('ram')).length,
  diskAlerts: 1, diskHealthAlerts: endpoints.filter((e) => e.alerts.includes('diskhealth')).length,
  alertEndpoints: endpoints.filter((e) => e.alerts.length), alertEndpointTotal: endpoints.filter((e) => e.alerts.length).length,
  recentActions: audit.slice(0, 6),
});
const series = (n, step, base, amp) => Array.from({ length: n }, (_, i) => ({ t: iso((n - i) * step), v: Math.max(2, Math.min(99, Math.round(base + Math.sin(i / 5) * amp + rnd() * 8))) }));
const fleetHistory = series(72, 5 * 60_000, 38, 14).map((p, i) => ({ timeUtc: p.t, avgCpu: p.v, avgRam: Math.round(52 + Math.cos(i / 7) * 8), online: 19 + (i % 4) }));
const metrics = () => series(72, 5 * 60_000, 30, 20).map((p, i) => ({ timeUtc: p.t, cpu: p.v, ram: Math.round(55 + Math.cos(i / 6) * 10), disk: 48 }));

const disk = (key, model, type, mediaType, bus, size, health, extra = {}) => ({
  diskKey: key, model, serial: key, mediaType, type, busType: bus, sizeBytes: size, healthPercent: health, status: health >= 85 ? 'OK' : 'Warning',
  temperatureC: 36, maxTemperatureC: 52, powerOnHours: 14210, wearPercent: 100 - health, readErrors: 0, writeErrors: 0, reallocatedSectors: 0,
  pendingSectors: 0, predictFailure: false, estimatedDaysLeft: null, summary: 'Healthy. No SMART warnings.', attributes: [], updatedUtc: iso(120_000),
  temperatureHistory: [], confidence: 'High', healthModelVersion: 4, currentHealthModelVersion: 4, firmware: '1.0.2', reasons: [{ code: 'baseline', text: 'Starting score', delta: 0 }],
  temperature: { currentC: 36, warnC: 70, critC: 80, source: 'drive', lifetimeMaxC: null, status: 'OK' }, temperatureStatus: 'OK', ...extra,
});
const disks = [
  disk('DEMO-SSD-0001', 'Demo NVMe 512GB', 'SSD', 'NVMe', 'NVMe', 512 * 1e9, 96),
  disk('DEMO-HDD-0002', 'Demo HDD 1TB', 'HDD', 'HDD', 'SATA', 1e12, 88, { powerOnHours: 31200, wearPercent: null, temperatureC: 41 }),
];
const hardware = {
  status: 'Complete', collectedUtc: iso(300_000), cached: false, serialsHidden: false, errors: [],
  os: { productName: 'Microsoft Windows 11 Pro', edition: 'Professional', version: '24H2', build: '26100.4061', architecture: 'x64', installedUtc: iso(400 * 86400_000), bootTimeUtc: iso(3 * 86400_000), fastStartupEnabled: false },
  cpu: { manufacturer: 'Intel', model: 'Core i5-10500', physicalCores: 6, logicalProcessors: 12, architecture: 'x64', maxClockMhz: 3100 },
  memory: { installedBytes: 16 * GB, usableBytes: 15.8 * GB, technology: 'DDR4', speedMhz: 2666, slotCount: 2, occupiedSlots: 2, emptySlots: 0, maxCapacityBytes: 64 * GB, modulesReported: true,
    modules: [0, 1].map((i) => ({ slot: `DIMM${i + 1}`, bank: `BANK ${i}`, capacityBytes: 8 * GB, manufacturer: 'Demo Memory', speedMhz: 2666, configuredSpeedMhz: 2666, partNumber: `DM-8G-${i}`, technology: 'DDR4' })) },
  board: { manufacturer: 'Demo Systems', model: 'DS-0A1B', serial: 'DEMO-BOARD-001' }, bios: { manufacturer: 'Demo BIOS', version: '1.9.2', releaseDateUtc: iso(500 * 86400_000) },
  computer: { manufacturer: 'Dell', model: 'OptiPlex 7090', hostname: 'WKS-111', domain: 'contoso.local', domainJoined: true },
};
const overview = { online: true, lastSeenUtc: iso(20_000), collectedUtc: iso(300_000), stale: false, unavailableReason: null, inventory: hardware,
  live: { cpuLoadPercent: 23, ramTotalBytes: 16 * GB, ramUsedBytes: 7.4 * GB, ramFreeBytes: 8.6 * GB, ramPercentUsed: 46, sampledUtc: iso(5_000) } };
const catalog = [
  { code: 'net.flushdns', displayName: 'Flush DNS cache', category: 'network', risk: 'Low', minRole: 'Operator', method: 'Local', parameters: [] },
  { code: 'repair.sfc', displayName: 'Repair system files', category: 'repair', risk: 'Medium', minRole: 'Operator', method: 'Local', parameters: [] },
];

// ---------- app under a synthetic API ----------
const server = await createServer({ configFile: false, root, plugins: [react()], server: { host: '127.0.0.1', port: 0, proxy: {} }, logLevel: 'error' });
let browser;
const missing = [];
try {
  await server.listen();
  const base = `http://127.0.0.1:${server.httpServer.address().port}`;
  const channel = process.env.NEXORA_BROWSER ?? 'chrome';
  browser = await chromium.launch({ channel, headless: true });
  const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, serviceWorkers: 'block', colorScheme: 'dark' });
  await context.route('**/*', async (route) => {
    const req = route.request(), url = new URL(req.url()), p = url.pathname;
    if (url.origin !== base) return route.abort();
    const json = (data, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(data) });
    if (p.startsWith('/hubs/')) return json({ error: 'offline' }, 503);
    if (!p.startsWith('/api/')) return route.continue();
    if (p === '/api/me') return json({ sid: 'S-1-5-21-1111111111-2222222222-3333333333-46118', account: 'CONTOSO\\demo.admin', displayName: 'Demo Admin', role: 'SuperAdmin', status: 'Active', isSuperAdmin: true, detail: null });
    if (p === '/api/antiforgery') return json({ token: 'demo' });
    if (p === '/api/thresholds') return json({ cpuAlertPercent: 90, ramAlertPercent: 90, diskAlertPercent: 90, diskHealthOkFrom: 85, diskHealthCriticalBelow: 50 });
    if (p === '/api/helper/status') return json({ connected: true, everConnected: true, serverUrl: base, downloadAvailable: false });
    if (p === '/api/jobs/catalog') return json(catalog);
    if (p === '/api/dashboard') return json(dashboard());
    if (p === '/api/dashboard/history') return json(fleetHistory);
    if (p === '/api/endpoints') return json(endpoints);
    if (p === '/api/groups') return json(GROUPS);
    if (p === '/api/audit') return json({ items: audit, total: audit.length, page: 1, pageSize: 50 });
    if (p === '/api/jobs') return json({ items: [], total: 0, page: 1, pageSize: 25 });
    const m = p.match(/^\/api\/endpoints\/([^/]+)(?:\/(.+))?$/);
    if (m) {
      const ep = m[1] === 'by-host' ? first : endpoints.find((e) => e.id === m[1]) ?? first;
      switch (m[2] ?? '') {
        case '': return json(ep);
        case 'overview': return json(overview);
        case 'hardware': return json(hardware);
        case 'disks': return json(disks);
        case 'metrics': return json(metrics());
        case 'profiles': return json([]);
        case 'detail': return json({ services: [], processes: [], disks: [], software: [], errors: [], warnings: [] });
      }
      if (m[1] === 'by-host') return json(first);
    }
    missing.push(`${req.method()} ${p}`);
    return json(req.method() === 'GET' ? [] : {});
  });

  const page = await context.newPage();
  const shot = async (route, file, { wait, viewport } = {}) => {
    await page.setViewportSize(viewport ?? { width: 1440, height: 900 });
    await page.goto(`${base}${route}`, { waitUntil: 'networkidle' });
    if (wait) await page.waitForSelector(wait, { timeout: 15000 });
    await page.waitForTimeout(700);
    await page.screenshot({ path: path.join(out, file) });
    console.log('saved', file);
  };
  await shot('/dashboard', 'dashboard.png', { wait: '.tile' });
  await shot('/endpoints', 'workstations.png', { wait: '[data-testid=endpoint-row]' });
  await shot(`/endpoints/${first.id}/overview`, 'workstation-overview.png', { wait: 'h1' });
  await shot(`/endpoints/${first.id}/storage`, 'workstation-storage.png', { wait: 'h1' });
  await shot('/groups', 'groups.png', { wait: 'h1' });
  await shot('/audit', 'audit.png', { wait: 'h1' });
  if (missing.length) console.log('unmocked (served empty):', [...new Set(missing)].join(', '));
} finally {
  await browser?.close();
  await server.close();
}
