import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import { chromium } from 'playwright-core';

const browser = await chromium.launch({ channel: 'msedge', headless: true, args: ['--auth-server-allowlist=wks-111'] });
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
  await page.goto('http://wks-111:5090/');
  const result = await page.evaluate(async () => {
    const endpoints = await (await fetch('/api/endpoints')).json();
    const { token } = await (await fetch('/api/antiforgery')).json();
    const records = [];
    for (const host of ['WKS-111','192.168.18.11','WKS-PH65']) {
      const e = endpoints.find(e => e.hostname.toUpperCase() === host.toUpperCase());
      if (!e) throw new Error(`Missing endpoint ${host}`);
      const response = await fetch(`/api/endpoints/${e.id}/disks/refresh`, { method: 'POST', headers: { 'X-XSRF-TOKEN': token } });
      if (!response.ok) throw new Error(`${host} refresh failed ${response.status}`);
      records.push({ host, disks: await response.json() });
    }
    const audit = await (await fetch('/api/disk-audit')).json();
    return { records, audit };
  });
  const localHdd = result.records[0].disks.find(d => d.model === 'ST1000VX012-3CU10C');
  assert.equal(localHdd.healthPercent, 100);
  assert.ok(localHdd.attributes.some(a => a.id === 5 && a.raw === 0));
  assert.ok(localHdd.attributes.some(a => a.id === 197 && a.raw === 0));
  assert.ok(localHdd.attributes.some(a => a.id === 198 && a.raw === 0));
  assert.ok(localHdd.reasons.some(r => r.code === 'hdd-smart-raw'));
  assert.equal(result.records[1].disks.find(d => d.model.includes('KIOXIA')).healthPercent, 56);
  assert.equal(result.records[2].disks.find(d => d.model === 'WD Blue SN570 250GB').healthPercent, 92);
  assert.equal(result.audit.byFinding.NoHealth ?? 0, 0);
  await page.goto('http://wks-111:5090/endpoints/WKS-111/storage');
  const drive = page.getByRole('region', { name: 'Drive ST1000VX012-3CU10C', exact: true });
  await drive.getByText('100%', { exact: true }).first().waitFor();
  const output = '../../qa-reports/test-artifacts/hdd-health';
  await fs.mkdir(output, { recursive: true });
  await fs.writeFile(`${output}/production.json`, JSON.stringify(result, null, 2));
  await page.screenshot({ path: `${output}/wks-111-hdd.png`, fullPage: true });
  console.log('Verified production HDD 100%, SSD references 56%/92%, fleet has no missing disk-health values.');
} finally { await browser.close(); }
