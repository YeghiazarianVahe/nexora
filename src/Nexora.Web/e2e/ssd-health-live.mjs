import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import { chromium } from 'playwright-core';

const browser = await chromium.launch({ channel: 'msedge', headless: true, args: ['--auth-server-allowlist=wks-111'] });
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
  await page.goto('http://wks-111:5090/endpoints/WKS-PH65/storage');
  const drive = page.getByRole('region', { name: 'Drive WD Blue SN570 250GB', exact: true });
  await drive.waitFor();
  await drive.getByText('92%', { exact: true }).first().waitFor();
  const endpoint = await page.evaluate(async () => (await fetch('/api/endpoints/by-host/WKS-PH65')).json());
  assert.equal(endpoint.ssdHealthPercent, 92);
  assert.equal(endpoint.hddHealthPercent, 100);
  await fs.mkdir('../../qa-reports/test-artifacts/diagnostic-evidence', { recursive: true });
  await page.screenshot({ path: '../../qa-reports/test-artifacts/diagnostic-evidence/wks-ph65-ssd-92.png', fullPage: true });
  console.log('Production browser verified WKS-PH65: SSD 92%, HDD 100%.');
} finally { await browser.close(); }
