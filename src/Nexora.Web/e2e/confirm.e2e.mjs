// Real-browser check of the type-to-confirm dialog on the Workstations screen (never confirms the delete).
//   NEXORA_URL=http://wks-111:5090 node e2e/confirm.e2e.mjs
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright-core';

const base = process.env.NEXORA_URL ?? 'http://wks-111:5090';
const shots = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../docs/ui');
fs.mkdirSync(shots, { recursive: true });
const results = [];
const check = (n, ok, d = '') => { results.push(ok); console.log(`${ok ? 'PASS' : 'FAIL'}  ${n}${d ? '  -> ' + d : ''}`); };

const browser = await chromium.launch({ channel: 'msedge', headless: true, args: ['--auth-server-allowlist=wks-111,localhost'] });
const page = await (await browser.newContext({ viewport: { width: 1366, height: 768 } })).newPage();
const problems = [];
page.on('pageerror', (e) => problems.push(e.message));
page.on('console', (m) => { if (m.type() === 'error') problems.push(m.text()); });

await page.goto(`${base}/endpoints`, { waitUntil: 'networkidle', timeout: 90000 });
await page.waitForSelector('[data-testid=endpoint-row]');
await page.click('[data-testid=select-all]');
const count = (await page.locator('.toolbar, .banner, [role=status]').allInnerTexts()).join(' ').match(/(\d[\d\s,]*) selected/i)?.[1]?.replace(/\D/g, '');
await page.click('button:has-text("Delete selected")');
const input = page.locator('#confirm-text');
await input.waitFor();
const phrase = (await page.locator('label[for=confirm-text] b').innerText()).trim();
check('the phrase uses the real selection size', !!count && phrase === `delete ${count}`, `${phrase} (selected ${count})`);
check('the input is focused when the dialog opens', await input.evaluate((e) => e === document.activeElement));

const button = page.locator('.dialog button.danger');
check('Delete starts disabled', await button.isDisabled());
await page.keyboard.type(phrase.slice(0, -1), { delay: 15 });
await page.waitForTimeout(1500); // live updates keep re-rendering the page meanwhile
check('typing works and focus survives live re-renders', (await input.inputValue()) === phrase.slice(0, -1) && (await input.evaluate((e) => e === document.activeElement)));
check('Delete still disabled for a partial phrase', await button.isDisabled());
await page.keyboard.press('Enter');
check('Enter does nothing for a partial phrase', await page.locator('.dialog').isVisible());
await page.keyboard.type(phrase.slice(-1));
check('Delete enables on the exact phrase', await button.isEnabled());
await page.screenshot({ path: path.join(shots, 'confirm-dialog.png') });
await page.keyboard.press('Escape');
check('Escape closes it without deleting', (await page.locator('.dialog').count()) === 0);
check('focus returns to the trigger', await page.evaluate(() => /Delete selected/.test(document.activeElement?.textContent ?? '')));
check('no console errors', problems.length === 0, problems.slice(0, 2).join(' | '));
await browser.close();
console.log(`\n${results.filter(Boolean).length}/${results.length} checks passed`);
process.exit(results.every(Boolean) ? 0 : 1);
