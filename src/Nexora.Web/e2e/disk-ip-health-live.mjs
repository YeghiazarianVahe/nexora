import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import { chromium } from 'playwright-core';

const browser = await chromium.launch({ channel: 'msedge', headless: true, args: ['--auth-server-allowlist=wks-111'] });
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
  await page.goto('http://wks-111:5090/');
  const evidence = await page.evaluate(async () => {
    const list = await (await fetch('/api/endpoints')).json();
    const endpoint = list.find(e => e.hostname === '192.168.18.11');
    if (!endpoint) throw new Error('Affected endpoint missing');
    const { token } = await (await fetch('/api/antiforgery')).json();
    const response = await fetch(`/api/endpoints/${endpoint.id}/disks/refresh`, { method: 'POST', headers: { 'X-XSRF-TOKEN': token } });
    if (!response.ok) throw new Error(`Refresh failed ${response.status}`);
    return { endpoint, disks: await response.json() };
  });
  const ssd = evidence.disks.find(d => d.model === 'KBG50ZNS256G NVMe KIOXIA 256GB');
  assert.equal(ssd.healthPercent, 56);
  assert.equal(ssd.wearPercent, 44); // The API exposes native wear instead of Windows' placeholder zero.
  assert.ok(ssd.reasons.some(r => r.code === 'nvme-used' && r.delta === -44));
  await page.goto('http://wks-111:5090/endpoints/192.168.18.11/storage');
  const drive = page.getByRole('region', { name: 'Drive KBG50ZNS256G NVMe KIOXIA 256GB', exact: true });
  await drive.getByText('56%', { exact: true }).first().waitFor();
  const output = '../../qa-reports/test-artifacts/disk-ip-health';
  await fs.mkdir(output, { recursive: true });
  await fs.writeFile(`${output}/production.json`, JSON.stringify(evidence, null, 2));
  await page.screenshot({ path: `${output}/kioxia-56.png`, fullPage: true });
  console.log('Production verified: IP-registered WKS-TB011 KIOXIA SSD shows 56%, native endurance used 44%.');
} finally { await browser.close(); }
