import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import { chromium } from 'playwright-core';

const out = '../../qa-reports/test-artifacts/one-second-performance';
await fs.mkdir(out, { recursive: true });
const browser = await chromium.launch({ channel: 'msedge', headless: true, args: ['--auth-server-allowlist=wks-111'] });
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
  const requests = [], samples = [], errors = [];
  page.on('pageerror', e => errors.push(e.message));
  page.on('request', request => { if (/\/performance$/.test(request.url())) requests.push({ url: request.url(), time: Date.now() }); });
  page.on('response', async response => {
    if (/\/performance$/.test(response.url()) && response.status() === 200) {
      try { samples.push(await response.json()); } catch { /* a canceled response is not a sample */ }
    }
  });
  await page.goto('http://wks-111:5090/endpoints/WKS-111');
  await page.getByText('CPU and RAM update every second', { exact: true }).waitFor();
  await page.waitForTimeout(6500);
  assert.ok(samples.length >= 6, `Only ${samples.length} samples arrived`);
  const times = [...new Set(samples.map(r => Date.parse(r.sample.sampledUtc)))].sort((a, b) => a - b);
  assert.ok(times.length >= 5);
  const intervals = times.slice(1).map((t, i) => t - times[i]).sort((a, b) => a - b);
  const medianMs = intervals[Math.floor(intervals.length / 2)];
  assert.ok(medianMs >= 800 && medianMs < 1250, `Sample interval ${medianMs}ms`);
  assert.ok(samples.every(r => r.hostname.toUpperCase() === 'WKS-111' && r.sample.cpuLoadPercent >= 0 && r.sample.cpuLoadPercent <= 100 && r.sample.ramPercentUsed >= 0 && r.sample.ramPercentUsed <= 100));
  assert.ok(await page.locator('.wsd-performance time').getAttribute('datetime'));
  await page.screenshot({ path: `${out}/wks-111-live.png`, fullPage: true });

  await page.evaluate(() => {
    Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => 'hidden' });
    document.dispatchEvent(new Event('visibilitychange'));
  });
  await page.waitForTimeout(300);
  const hidden = requests.length;
  await page.waitForTimeout(2200);
  assert.equal(requests.length, hidden, 'Polling continued while hidden');
  await page.evaluate(() => {
    Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => 'visible' });
    document.dispatchEvent(new Event('visibilitychange'));
  });
  await page.waitForTimeout(1500);
  assert.ok(requests.length > hidden, 'Polling did not resume');
  await page.goto('http://wks-111:5090/dashboard');
  await page.waitForTimeout(300);
  const left = requests.length;
  await page.waitForTimeout(2200);
  assert.equal(requests.length, left, 'Polling continued after leaving the workstation');
  assert.deepEqual(errors, []);
  await fs.writeFile(`${out}/cadence.json`, JSON.stringify({ count: samples.length, medianMs, times, requests }, null, 2));
  console.log(`WKS-111: ${samples.length} CPU/RAM samples; median interval ${medianMs}ms. Hidden-tab pause, resume, navigation stop and live display passed.`);
} finally { await browser.close(); }
