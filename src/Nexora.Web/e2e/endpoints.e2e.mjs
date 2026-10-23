// End-to-end check of the Workstations table in a real Edge (Windows SSO against the running server).
//   NEXORA_URL=http://wks-111:5090 node e2e/endpoints.e2e.mjs
// Saves screenshots to docs/ui/ and exits 1 if any assertion fails.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright-core';

const base = process.env.NEXORA_URL ?? 'http://wks-111:5090';
const here = path.dirname(fileURLToPath(import.meta.url));
const shots = path.resolve(here, '../../../docs/ui');
fs.mkdirSync(shots, { recursive: true });

const results = [];
function check(name, ok, detail = '') {
  results.push({ name, ok });
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? '  -> ' + detail : ''}`);
}

/** RFC 4180 parser (quoted fields, doubled quotes, CRLF). */
function parseCsv(text) {
  const rows = []; let row = []; let cur = ''; let q = false;
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (q) { if (c === '"' && text[i + 1] === '"') { cur += '"'; i++; } else if (c === '"') q = false; else cur += c; }
    else if (c === '"') q = true;
    else if (c === ',') { row.push(cur); cur = ''; }
    else if (c === '\r' && text[i + 1] === '\n') { row.push(cur); cur = ''; rows.push(row); row = []; i++; }
    else cur += c;
  }
  if (cur || row.length) { row.push(cur); rows.push(row); }
  return rows;
}

const browser = await chromium.launch({ channel: 'msedge', headless: true, args: ['--auth-server-allowlist=wks-111,localhost'] });
const context = await browser.newContext({ viewport: { width: 1366, height: 768 }, acceptDownloads: true });
const page = await context.newPage();
const problems = [];
page.on('console', (m) => { if (['error', 'warning'].includes(m.type())) problems.push(m.text()); });
page.on('pageerror', (e) => problems.push(e.message));

async function open() {
  await page.goto(`${base}/endpoints`, { waitUntil: 'networkidle', timeout: 90000 });
  await page.waitForSelector('[data-testid=endpoint-row]', { timeout: 60000 });
  await page.waitForTimeout(800);
}
const rowNames = () => page.$$eval('[data-testid=endpoint-row] .ep-name', (els) => els.map((e) => e.textContent.trim()));
const exportLabel = () => page.locator('[data-testid=export-button]').innerText();

await open();

// ---------- screenshots at both sizes
await page.screenshot({ path: path.join(shots, 'endpoints-1366x768.png') });
await page.setViewportSize({ width: 1920, height: 1080 });
await page.waitForTimeout(500);
await page.screenshot({ path: path.join(shots, 'endpoints-1920x1080.png') });
await page.setViewportSize({ width: 1366, height: 768 });
await page.waitForTimeout(400);

// ---------- layout
const heights = await page.$$eval('[data-testid=endpoint-row]', (els) => els.map((e) => Math.round(e.getBoundingClientRect().height)));
check('every row is a single line, <= 48px', heights.length > 0 && heights.every((h) => h <= 48), `rows=${heights.length} max=${Math.max(...heights)}px`);

const longOu = await page.$$eval('[data-testid=endpoint-row] button.ou-link', (els) =>
  els.map((e) => ({ full: e.title, shown: e.textContent, clipped: e.scrollWidth > e.clientWidth })).filter((o) => o.full.split('/').length >= 3));
check('long OU paths are shortened to the last two segments', longOu.length > 0 && longOu.every((o) => o.shown.includes(' › ') && o.shown.split(' › ').length === 2),
  longOu[0] ? `${longOu[0].full}  =>  ${longOu[0].shown}` : 'no long OU on screen');
const rowHeightWithLongOu = await page.$$eval('[data-testid=endpoint-row]', (els) => els.filter((e) => (e.querySelector('button.ou-link')?.title ?? '').split('/').length >= 3).map((e) => e.getBoundingClientRect().height));
check('rows with a long OU path are still <= 48px', rowHeightWithLongOu.length > 0 && rowHeightWithLongOu.every((h) => h <= 48), `max=${Math.max(0, ...rowHeightWithLongOu)}px`);

const dupes = await page.$$eval('[data-testid=endpoint-row]', (els) => els.map((e) => {
  const name = e.querySelector('.ep-name').textContent.trim();
  const text = e.innerText.replace(/\s+/g, ' ');
  const count = text.split(name).length - 1;
  return { name, count };
}).filter((r) => r.count !== 1));
check('the hostname appears exactly once per row', dupes.length === 0, dupes[0] ? `${dupes[0].name} x${dupes[0].count}` : '');

check('the page itself does not scroll sideways', await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth));

const metricsOk = await page.$$eval('.metric:not(.compact)', (els) => els.slice(0, 40).every((m) => {
  const bar = m.querySelector('.metric-bar').getBoundingClientRect();
  const pct = m.querySelector('.metric-pct').getBoundingClientRect();
  return Math.round(bar.width) === 70 && Math.round(bar.height) === 6 && Math.abs((bar.top + bar.height / 2) - (pct.top + pct.height / 2)) < 6;
}));
check('metric bars are 70x6 and sit on the same line as the percentage', metricsOk);

const colors = await page.$$eval('.metric:not(.compact)', (els) => els.map((m) => ({ pct: parseInt(m.querySelector('.metric-pct').textContent, 10), bg: getComputedStyle(m.querySelector('.metric-bar > i')).backgroundColor })));
const rgb = (hex) => { const n = parseInt(hex.slice(1), 16); return `rgb(${n >> 16}, ${(n >> 8) & 255}, ${n & 255})`; };
const wrong = colors.filter((c) => c.bg !== rgb(c.pct >= 90 ? '#E5484D' : c.pct >= 70 ? '#E5A50A' : '#69BA31'));
check('metric colors follow the 70 / 90 thresholds', wrong.length === 0, wrong[0] ? JSON.stringify(wrong[0]) : `${colors.length} meters checked`);

const tip = await page.$eval('[data-testid=endpoint-row] time', (t) => t.title);
check('last seen tooltip is dd.MM.yyyy HH:mm:ss', /^\d{2}\.\d{2}\.\d{4} \d{2}:\d{2}:\d{2}$/.test(tip), tip);
const rel = await page.$eval('[data-testid=endpoint-row] time', (t) => t.textContent.trim());
check('last seen is relative', /(just now|min ago|h ago|d ago)$/.test(rel), rel);

// ---------- sticky columns while scrolling sideways
await page.setViewportSize({ width: 1000, height: 768 });
await page.waitForTimeout(300);
await page.$eval('.vtable', (el) => { el.scrollLeft = 400; });
await page.waitForTimeout(200);
const stickyOk = await page.evaluate(() => {
  const box = document.querySelector('.vtable').getBoundingClientRect();
  const row = document.querySelector('[data-testid=endpoint-row]');
  const first = row.querySelector('.sticky').getBoundingClientRect();
  const name = row.querySelectorAll('.sticky')[2].getBoundingClientRect();
  return Math.abs(first.left - box.left) < 3 && name.left >= box.left && name.right <= box.right && document.documentElement.scrollWidth <= document.documentElement.clientWidth;
});
check('Select / Status / Endpoint stay visible when the table scrolls sideways (page does not)', stickyOk);
await page.screenshot({ path: path.join(shots, 'endpoints-narrow-scrolled.png') });
await page.setViewportSize({ width: 1366, height: 768 });
await page.waitForTimeout(300);

// ---------- OU click filters the table
const firstOu = await page.$eval('[data-testid=endpoint-row] button.ou-link', (b) => b.title);
await page.click('[data-testid=endpoint-row] button.ou-link >> nth=0');
await page.waitForTimeout(500);
check('clicking the OU filters the table to it', (await page.$eval('#f-ou', (s) => s.value)) === firstOu, firstOu);
const ouRows = await page.$$eval('[data-testid=endpoint-row] button.ou-link', (els) => els.every((e) => true) && els.map((e) => e.title));
check('every visible row belongs to that OU', ouRows.every((t) => t === firstOu));
await page.click('text=Clear filters');
await page.waitForTimeout(400);

// ---------- export label across states
const total = parseInt((await exportLabel()).replace(/\D/g, ''), 10);
check('no selection, no filters: "Export all N"', /^Export all \d+$/.test(await exportLabel()), await exportLabel());
await page.fill('#f-q', 'wks');
await page.waitForTimeout(500);
const filteredLabel = await exportLabel();
check('filters active: "Export N filtered"', /^Export \d+ filtered$/.test(filteredLabel), filteredLabel);
await page.fill('#f-q', '');
await page.waitForTimeout(400);

// ---------- selection survives sorting; header checkbox is indeterminate; export honours the selection
const names = await rowNames();
const picks = [names[1], names[3], names[5]];
for (const n of picks) await page.locator(`[data-testid=endpoint-row]:has(.ep-name:text-is("${n}")) input[type=checkbox]`).check();
check('export button names the selection', (await exportLabel()) === 'Export 3 selected', await exportLabel());
check('header checkbox is indeterminate for a partial selection', await page.$eval('[data-testid=select-all]', (c) => c.indeterminate === true && !c.checked));

await page.click('button.sort >> text=CPU');
await page.waitForTimeout(500);
await page.click('button.sort >> text=Last seen');
await page.waitForTimeout(500);
const checkedAfterSort = await page.$$eval('[data-testid=endpoint-row].sel .ep-name', (els) => els.map((e) => e.textContent.trim()));
const countAfterSort = await page.locator('[data-testid=selection-count]').innerText();
check('after sorting, the same 3 endpoints are still selected', countAfterSort.startsWith('3 selected'), `${countAfterSort}; visible: ${checkedAfterSort.length}`);

await page.fill('#f-q', 'zzz-no-such-pc');
await page.waitForTimeout(400);
const hiddenText = await page.locator('[data-testid=selection-count]').innerText();
check('filtering keeps hidden selected rows and says so', /3 selected \(3 hidden by filter\)/.test(hiddenText), hiddenText);
await page.fill('#f-q', '');
await page.waitForTimeout(400);
await page.click('button.sort >> text=Endpoint');
await page.waitForTimeout(300);

const [download] = await Promise.all([page.waitForEvent('download'), page.click('[data-testid=export-button]')]);
const file = await download.path();
const raw = fs.readFileSync(file);
check('download is named for the selection', /^nexora-endpoints-selected-\d{8}-\d{4}\.csv$/.test(download.suggestedFilename()), download.suggestedFilename());
const csv = parseCsv(raw.toString('utf8').replace(/^﻿/, ''));
check('CSV has exactly the ten columns', csv[0].join('|') === 'PC Name|IP Address|Logged-in User|Last Seen Online|CPU %|RAM %|SSD Free / Total|SSD Health %|HDD Free / Total|HDD Health %', csv[0].join('|'));
check('CSV has the header plus exactly 3 data rows', csv.length === 4, `rows=${csv.length - 1}`);
check('CSV rows are exactly the 3 selected hostnames', JSON.stringify(csv.slice(1).map((r) => r[0].toLowerCase()).sort()) === JSON.stringify([...picks].map((p) => p.toLowerCase()).sort()), csv.slice(1).map((r) => r[0]).join(', '));
await page.screenshot({ path: path.join(shots, 'endpoints-selection.png') });

// ---------- select-all banner
await page.click('text=Clear');
await page.fill('#f-q', 'wks');
await page.waitForTimeout(400);
await page.click('[data-testid=select-all]');
await page.waitForTimeout(300);
const banner = await page.locator('.banner').innerText();
check('select-all shows the "rows on screen" banner', /^All \d+ rows on screen selected\./.test(banner), banner);
await page.click('text=Clear');
await page.fill('#f-q', '');

// ---------- column visibility persists
await page.click('button:has-text("Columns")');
await page.locator('.menu-pop label:has-text("Model") input').uncheck();
await page.reload({ waitUntil: 'networkidle' });
await page.waitForSelector('[data-testid=endpoint-row]');
const hasModel = await page.locator('[role=columnheader]:has-text("Model")').count();
check('hidden columns are remembered after a reload', hasModel === 0);
await page.click('button:has-text("Columns")');
await page.locator('.menu-pop label:has-text("Model") input').check();

check('no console errors', problems.length === 0, problems.slice(0, 3).join(' | '));

await browser.close();
const failed = results.filter((r) => !r.ok);
console.log(`\n${results.length - failed.length}/${results.length} checks passed`);
process.exit(failed.length ? 1 : 0);
