// End-to-end check of the Profiles screen in a real Edge against the running server.
//   NEXORA_URL=http://wks-111:5090 node e2e/profiles.e2e.mjs
// It uses the Nexora server PC (always in the inventory) and the account of whoever runs it; nothing is hard-coded to one person or PC.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright-core';
import { api, whoAmI } from './support.mjs';

const base = process.env.NEXORA_URL ?? 'http://wks-111:5090';
const me = whoAmI();
const serverName = process.env.COMPUTERNAME ?? 'WKS-111';
const here = path.dirname(fileURLToPath(import.meta.url));
const shots = path.resolve(here, '../../../docs/ui');
fs.mkdirSync(shots, { recursive: true });

const results = [];
const check = (name, ok, detail = '') => { results.push({ name, ok }); console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? '  -> ' + detail : ''}`); };

const browser = await chromium.launch({ channel: 'msedge', headless: true, args: ['--auth-server-allowlist=wks-111,localhost'] });
const context = await browser.newContext({ viewport: { width: 1366, height: 768 } });
const page = await context.newPage();
const problems = [];
page.on('console', (m) => { if (['error', 'warning'].includes(m.type())) problems.push(m.text()); });
page.on('pageerror', (e) => problems.push(e.message));

const rows = () => page.locator('table.ptable tbody tr:not(.skeleton-row)');
async function openProfiles(query = '') {
  await page.goto(`${base}/profiles${query}`, { waitUntil: 'networkidle', timeout: 90000 });
  await page.waitForSelector('table.ptable tbody tr:not(.skeleton-row)', { timeout: 60000 });
}

// ---------- the server is selectable and selected by default
await openProfiles();
const selectedLabel = await page.$eval('.picker select', (s) => s.options[s.selectedIndex].textContent);
check('the Nexora server is the default selection', new RegExp(serverName, 'i').test(selectedLabel), selectedLabel);
check('the page says it is the Nexora server', await page.locator('h2 .tag:has-text("Nexora server")').isVisible());
const headers = await page.$$eval('table.ptable thead th', (ths) => ths.map((t) => t.innerText.replace(/[↕▲▼]/g, '').trim()));
check('columns: Account, Profile folder, Size, Last used, State, Endpoint', JSON.stringify(headers.map((h) => h.toLowerCase())) === JSON.stringify(['account', 'profile folder', 'size', 'last used', 'state', 'endpoint']), headers.join(' | '));

const text = await page.locator('table.ptable tbody').innerText();
check('the operator\'s own profile is listed with its SID', text.toLowerCase().includes(me.account.toLowerCase()) && text.includes(me.sid));
check('system profiles are listed and marked', /NT AUTHORITY\\SYSTEM/.test(text) && /System/.test(text));
check('the profile in use is marked "In use"', /In use/.test(text));
check('the Endpoint column names the owning workstation', (await page.$$eval('table.ptable tbody tr td:last-child', (c) => c.map((x) => x.textContent))).every((t) => new RegExp(serverName, 'i').test(t ?? '')));

const heights = await page.$$eval('table.ptable tbody tr', (trs) => trs.map((r) => Math.round(r.getBoundingClientRect().height)));
check('every row is 44-48px high', heights.length > 0 && heights.every((h) => h >= 44 && h <= 48), `min=${Math.min(...heights)} max=${Math.max(...heights)}`);
const tip = await page.$eval('table.ptable tbody tr td:nth-child(4)', (td) => td.title);
check('last used has an absolute tooltip', /^\d{2}\.\d{2}\.\d{4} \d{2}:\d{2}:\d{2}$/.test(tip), tip);
const rel = await page.$eval('table.ptable tbody tr td:nth-child(4) time', (t) => t.textContent.trim());
check('last used is relative', /(just now|min ago|h ago|d ago)$/.test(rel), rel);
check('the page does not scroll sideways', await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth));
check('there is no delete control on this screen yet', (await page.locator('button:has-text("Delete")').count()) === 0);
await page.screenshot({ path: path.join(shots, 'profiles-1366x768.png') });
await page.setViewportSize({ width: 1920, height: 1080 });
await page.waitForTimeout(400);
await page.screenshot({ path: path.join(shots, 'profiles-1920x1080.png') });
await page.setViewportSize({ width: 1366, height: 768 });

// ---------- the selector never filters the server out
await page.fill('.picker input[type=search]', 'zzzz-no-such-workstation');
await page.waitForTimeout(300);
const optionTexts = await page.$$eval('.picker select option', (o) => o.map((x) => x.textContent));
check('the server stays selectable whatever the search says', optionTexts.some((t) => new RegExp(serverName, 'i').test(t ?? '')), optionTexts.join(' | '));
await page.fill('.picker input[type=search]', '');

// ---------- another workstation, and the sizes (any other PC that is online right now; the suite does not depend on one particular PC)
const others = ((await api(page).get('/api/endpoints')).json ?? []).filter((e) => e.online && !e.isPinned);
const target = others[0]?.name ?? '';
if (!target) console.log('SKIP  no other workstation is online, so selecting another PC and measuring its profiles is not checked');
if (target) {
  await page.fill('.picker input[type=search]', target);
  await page.waitForTimeout(300);
  const wanted = await page.$$eval('.picker select option', (o, t) => o.find((x) => (x.textContent ?? '').startsWith(t))?.getAttribute('value'), target);
  await page.selectOption('.picker select', wanted);
  await page.waitForSelector('table.ptable tbody tr:not(.skeleton-row), .notice.warn', { timeout: 60000 });
  const heading = await page.locator('h2').first().innerText();
  check(`${target} can be selected and its profiles load`, heading.includes(target) && (await rows().count()) > 0, heading.replace(/\s+/g, ' '));
  check('the Endpoint column shows it', (await page.$$eval('table.ptable tbody tr td:last-child', (c, t) => c.every((x) => (x.textContent ?? '').includes(t)), target)));

  const firstMeasure = page.locator('table.ptable tbody button:has-text("Measure")').first();
  if (await firstMeasure.count()) {
    await firstMeasure.click();
    const outcome = await Promise.race([
      page.waitForSelector('table.ptable td.num:has-text("B")', { timeout: 120000 }).then(() => 'size'),
      page.waitForSelector('.notice.error', { timeout: 120000 }).then(() => 'error'),
    ]).catch(() => 'timeout');
    const sizeText = outcome === 'size' ? (await page.locator('table.ptable td.num:has-text("B")').first().innerText()) : (await page.locator('.notice.error').first().innerText().catch(() => ''));
    check(`a profile size is measured through the helper on ${target}`, outcome === 'size', `${outcome}: ${sizeText.replace(/\s+/g, ' ')}`);
    await page.screenshot({ path: path.join(shots, 'profiles-sizes.png') });

    // cached for this browser session
    await page.reload({ waitUntil: 'networkidle' });
    await page.waitForSelector('table.ptable tbody tr:not(.skeleton-row)');
    check('measured sizes are remembered for the session', (await page.locator('table.ptable td.num:has-text("B")').count()) > 0);
  } else {
    check('a profile size is measured through the helper', false, 'no profile row offered a Measure button');
  }
}

// ---------- the profile tab of a workstation shows the same read-only panel
await page.goto(`${base}/endpoints`, { waitUntil: 'networkidle' });
await page.waitForSelector('[data-testid=endpoint-row]');
const firstRowName = await page.locator('[data-testid=endpoint-row] .ep-name').first().innerText();
check('the Nexora server is the first row of the workstation list', new RegExp(serverName, 'i').test(firstRowName) && (await page.locator('[data-testid=endpoint-row]').first().innerText()).includes('server'), firstRowName.replace(/\s+/g, ' '));
await page.locator('[data-testid=endpoint-row]').first().click();
await page.waitForSelector('role=tab[name="User profiles"]');
await page.click('role=tab[name="User profiles"]');
await page.waitForSelector('table.ptable tbody tr:not(.skeleton-row)', { timeout: 60000 });
check('the workstation page has no profile delete button', (await page.locator('table.ptable button:has-text("Delete")').count()) === 0);

check('no console errors', problems.length === 0, problems.slice(0, 3).join(' | '));
await browser.close();
const failed = results.filter((r) => !r.ok);
console.log(`\n${results.length - failed.length}/${results.length} checks passed`);
process.exit(failed.length ? 1 : 0);
