import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import { chromium } from 'playwright-core';

const base = 'http://wks-111:5090';
const browser = await chromium.launch({ channel: 'msedge', headless: true,
  args: ['--auth-server-allowlist=wks-111', '--auth-negotiate-delegate-allowlist=wks-111'] });
try {
  const context = await browser.newContext({ viewport: { width: 1440, height: 1100 } });
  const page = await context.newPage();
  await page.goto(`${base}/endpoints/wks-122/diagnose/f831e94d-d8e1-4440-9fc3-77629fb62cc6`);
  await page.getByText('Collected evidence', { exact: true }).click();
  await page.getByRole('table', { name: 'Disk space', exact: true }).waitFor();
  await page.getByRole('table', { name: 'Processes using the most memory', exact: true }).waitFor();
  assert.equal(await page.getByLabel('Raw evidence JSON').isVisible(), false);
  await fs.mkdir('../../qa-reports/test-artifacts/diagnostic-evidence', { recursive: true });
  await page.screenshot({ path: '../../qa-reports/test-artifacts/diagnostic-evidence/desktop.png', fullPage: true });
  await page.setViewportSize({ width: 390, height: 844 });
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await page.screenshot({ path: '../../qa-reports/test-artifacts/diagnostic-evidence/mobile.png', fullPage: true });
  await page.getByText('Raw JSON for troubleshooting', { exact: true }).click();
  assert.equal(await page.getByLabel('Raw evidence JSON').isVisible(), true);
  console.log('Live production evidence: readable tables, collapsed raw JSON and mobile layout passed.');
} finally { await browser.close(); }
