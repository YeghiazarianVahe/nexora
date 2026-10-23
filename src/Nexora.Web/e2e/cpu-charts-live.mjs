import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import { chromium } from 'playwright-core';

const base = 'http://wks-111:5090';
const out = '../../qa-reports/test-artifacts/cpu-charts';
await fs.mkdir(out, { recursive: true });
const browser = await chromium.launch({ channel: 'msedge', headless: true, args: ['--auth-server-allowlist=wks-111'] });
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
  const errors = [];
  page.on('pageerror', e => errors.push(e.message));
  await page.goto(`${base}/endpoints/WKS-PH65/monitoring`);
  const chart = page.locator('figure.chart').filter({ has: page.locator('.chart-title', { hasText: /^CPU$/ }) });
  await chart.getByText('100%', { exact: true }).waitFor();
  await chart.getByText('alert 90%', { exact: true }).waitFor();
  assert.ok((await chart.innerHTML()).includes('latest sample'));
  assert.equal(await chart.locator('path').evaluateAll(paths => paths.some(p => /NaN|Infinity/.test(p.getAttribute('d') ?? ''))), false);
  const svg = chart.locator('svg');
  await svg.focus();
  await svg.press('ArrowLeft');
  await chart.getByRole('status').waitFor();
  await chart.getByRole('button', { name: 'Table', exact: true }).click();
  await chart.locator('tbody tr').first().waitFor();
  const values = await chart.locator('tbody td.num').allTextContents();
  assert.ok(values.length > 0);
  assert.ok(values.every(v => v === '-' || (Number.parseFloat(v) >= 0 && Number.parseFloat(v) <= 100)));
  const snapshot = await page.evaluate(async () => {
    const endpoint = await (await fetch('/api/endpoints/by-host/WKS-PH65')).json();
    const history = await (await fetch(`/api/endpoints/${endpoint.id}/metrics?range=6h`)).json();
    return { endpoint, latest: history.at(-1) };
  });
  assert.equal(snapshot.latest.timeUtc, snapshot.endpoint.lastSeenUtc);
  assert.equal(snapshot.latest.cpu, snapshot.endpoint.cpuPercent);
  await chart.getByRole('button', { name: 'Chart', exact: true }).click();
  for (const theme of ['light', 'dark']) {
    if (await page.evaluate(() => document.documentElement.dataset.theme) !== theme)
      await page.getByRole('button', { name: `Switch to ${theme} mode` }).click();
    await page.screenshot({ path: `${out}/monitoring-${theme}.png`, fullPage: true });
  }
  await page.setViewportSize({ width: 390, height: 844 });
  await page.waitForFunction(() => [...document.querySelectorAll('.chart-body')].every(el => el.scrollWidth <= el.clientWidth + 1));
  await page.screenshot({ path: `${out}/monitoring-mobile.png`, fullPage: true });
  assert.deepEqual(errors, []);
  const health = await page.evaluate(async () => (await fetch('/health')).json());
  assert.equal(health.status, 'ok');
  console.log(`Production CPU graph checks passed: 0–100% scale, threshold, keyboard tooltip, ${values.length} table samples, both themes and mobile.`);
} finally { await browser.close(); }
