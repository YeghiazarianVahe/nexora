import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import { chromium } from 'playwright-core';

const base = 'http://wks-111:5090';
const out = '../../qa-reports/test-artifacts/themes';
await fs.mkdir(out, { recursive: true });
const browser = await chromium.launch({ channel: 'msedge', headless: true, args: ['--auth-server-allowlist=wks-111'] });
try {
  const context = await browser.newContext({ colorScheme: 'dark', viewport: { width: 1440, height: 1000 } });
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', e => errors.push(e.message));
  await page.goto(`${base}/dashboard`);
  await page.getByRole('button', { name: 'Switch to light mode' }).waitFor();
  assert.equal(await page.evaluate(() => document.documentElement.dataset.theme), 'dark');
  await page.getByRole('button', { name: 'Switch to light mode' }).click();
  assert.equal(await page.evaluate(() => getComputedStyle(document.body).backgroundColor), 'rgb(244, 246, 248)');
  await page.reload();
  await page.getByRole('button', { name: 'Switch to dark mode' }).waitFor();
  assert.equal(await page.evaluate(() => localStorage.getItem('nexora-theme')), 'light');
  for (const [name, route] of [
    ['dashboard', '/dashboard'], ['workstations', '/endpoints'], ['storage', '/endpoints/WKS-PH65/storage'],
    ['diagnostics', '/endpoints/wks-122/diagnose/f831e94d-d8e1-4440-9fc3-77629fb62cc6'],
    ['jobs', '/jobs'], ['settings', '/settings'],
  ]) {
    await page.goto(base + route);
    await page.getByRole('button', { name: 'Switch to dark mode' }).waitFor();
    if (name === 'storage') await page.getByRole('region', { name: 'Drive WD Blue SN570 250GB', exact: true }).waitFor();
    if (name === 'diagnostics') { await page.getByText('Collected evidence', { exact: true }).click(); await page.getByRole('table', { name: 'Disk space', exact: true }).waitFor(); }
    if (name === 'dashboard' || name === 'storage' || name === 'diagnostics') {
      await page.screenshot({ path: `${out}/${name}-light.png`, fullPage: true });
      await page.getByRole('button', { name: 'Switch to dark mode' }).click();
      assert.equal(await page.evaluate(() => getComputedStyle(document.body).backgroundColor), 'rgb(23, 23, 23)');
      await page.screenshot({ path: `${out}/${name}-dark.png`, fullPage: true });
      await page.getByRole('button', { name: 'Switch to light mode' }).click();
    }
  }
  await page.setViewportSize({ width: 390, height: 844 });
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await page.getByRole('button', { name: 'Switch to dark mode' }).click();
  await page.screenshot({ path: `${out}/mobile-dark.png`, fullPage: true });
  await page.getByRole('button', { name: 'Switch to light mode' }).click();
  await page.screenshot({ path: `${out}/mobile-light.png`, fullPage: true });
  const second = await context.newPage();
  await second.goto(`${base}/dashboard`);
  await second.getByRole('button', { name: 'Switch to dark mode' }).click();
  await page.getByRole('button', { name: 'Switch to light mode' }).waitFor();
  assert.equal(await page.evaluate(() => document.documentElement.dataset.theme), 'dark');
  assert.deepEqual(errors, []);
  await context.close();

  const blocked = await browser.newContext({ colorScheme: 'light' });
  await blocked.addInitScript(() => {
    Storage.prototype.getItem = () => { throw new Error('Storage blocked'); };
    Storage.prototype.setItem = () => { throw new Error('Storage blocked'); };
  });
  const fallback = await blocked.newPage();
  await fallback.goto(`${base}/dashboard`);
  await fallback.getByRole('button', { name: 'Switch to dark mode' }).click();
  assert.equal(await fallback.evaluate(() => document.documentElement.dataset.theme), 'dark');
  await blocked.close();
  console.log('Production theme checks passed: six screens, both themes, reload persistence, mobile, cross-tab sync and blocked storage.');
} finally { await browser.close(); }
