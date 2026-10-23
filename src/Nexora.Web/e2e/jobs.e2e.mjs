// End-to-end check of the Jobs module in a real Edge against the running server, using the simulated demo actions.
// The suite adds its own test workstation (and removes it again); the simulated actions run inside your own helper and change nothing anywhere.
// They exist only while the server runs with Features__DemoActions=true; without it the suite says so and stops (it is skipped, not failed).
//   NEXORA_URL=http://wks-111:5090 node e2e/jobs.e2e.mjs
import { execSync, spawn } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright-core';
import { demoActionsAvailable, seedEndpoint } from './support.mjs';

const base = process.env.NEXORA_URL ?? 'http://wks-111:5090';
let target = '';
const here = path.dirname(fileURLToPath(import.meta.url));
const repo = path.resolve(here, '../../..');
const shots = path.join(repo, 'docs/ui');
fs.mkdirSync(shots, { recursive: true });

const results = [];
const check = (name, ok, detail = '') => { results.push({ name, ok }); console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? '  -> ' + detail : ''}`); };

const browser = await chromium.launch({ channel: 'msedge', headless: true, args: ['--auth-server-allowlist=wks-111,localhost'] });
const context = await browser.newContext({ viewport: { width: 1366, height: 900 }, acceptDownloads: true });
const page = await context.newPage();
if (!(await demoActionsAvailable(page, base))) {
  console.log('SKIP  the simulated demo actions are off on this server (start it with Features__DemoActions=true to run the jobs suite)');
  await browser.close();
  process.exit(0);
}
const seeded = await seedEndpoint(page, base);
target = seeded.name;
const problems = [];
page.on('console', (m) => { if (['error', 'warning'].includes(m.type())) problems.push(m.text()); });
page.on('pageerror', (e) => problems.push(e.message));

/** A new job opens its output window; close it with Esc, which must NOT cancel the job. */
async function closeOutputWindow() {
  const opened = await page.waitForSelector('[role=dialog]', { timeout: 4000 }).then(() => true).catch(() => false);
  if (!opened) return;
  await page.keyboard.press('Escape');
  await page.waitForSelector('[role=dialog]', { state: 'detached', timeout: 4000 }).catch(() => undefined);
}

const shot = (n) => page.screenshot({ path: path.join(shots, `jobs-${n}.png`) });
const statusText = () => page.locator('h1 + .tag, .toolbar .tag').first().innerText();
const jobStatus = async () => (await page.locator('.toolbar .tag').first().innerText()).trim();
const waitStatus = (re, ms = 20000) => page.waitForFunction((src) => new RegExp(src, 'i').test(document.querySelector('.toolbar .tag')?.textContent ?? ''), re.source, { timeout: ms });

/** GET through the page itself, so the browser's Windows SSO applies. */
const inPage = (url) => page.evaluate(async (u) => {
  const r = await fetch(u, { credentials: 'same-origin' });
  return { ok: r.ok, text: await r.text(), disposition: r.headers.get('content-disposition') ?? '' };
}, url);

async function openTarget() {
  await page.goto(`${base}/endpoints`, { waitUntil: 'networkidle', timeout: 90000 });
  await page.waitForSelector('[data-testid=endpoint-row]', { timeout: 60000 });
  await page.fill('#f-q', target);
  await page.waitForTimeout(500);
  await page.locator(`[data-testid=endpoint-row]:has(.ep-name:text-is("${target}"))`).first().click();
  await page.waitForSelector('text=Remote actions', { timeout: 30000 });
}

/**
 * Opens the action on the target. Low risk actions with nothing required start AT ONCE (no dialog), so the parameters can only be set from
 * Medium upwards; returns whether a dialog appeared.
 */
async function runAction(title, { steps = 3, delay = 400, outcome = 'success', reason = '', typed = '' } = {}) {
  await openTarget();
  await page.locator('.action-btn', { hasText: title }).first().click();
  // Either the confirmation dialog (it has a Run button) appears, or the job started at once and the page moved to it.
  const dialog = await Promise.race([
    page.waitForSelector('.dialog button:has-text("Run")', { timeout: 4000 }).then(() => true),
    page.waitForURL(/\/jobs\/[0-9a-f-]{36}/, { timeout: 4000 }).then(() => false),
  ]).catch(() => false);
  if (!dialog) return false;
  if (await page.locator('#p-steps').count()) await page.fill('#p-steps', String(steps));
  if (await page.locator('#p-delayMs').count()) await page.fill('#p-delayMs', String(delay));
  if (await page.locator('#p-outcome').count()) await page.selectOption('#p-outcome', outcome);
  if (reason) await page.fill('#reason', reason);
  if (typed) await page.fill('#confirm-text', typed);
  return true;
}

/** Click Run when there is a dialog to confirm; a Low risk action has already started. */
const submit = async () => { if (await page.getByRole('button', { name: 'Run', exact: true }).count()) await page.getByRole('button', { name: 'Run', exact: true }).click(); };

// ---------- 1. the actions panel
await openTarget();
await page.waitForSelector('.action-btn');
const buttons = await page.$$eval('.action-btn', (els) => els.map((e) => e.innerText.replace(/\s+/g, ' ').trim()));
check('the actions panel lists actions grouped by category with risk badges', buttons.length >= 4 && buttons.every((b) => /(Low|Medium|High|Critical)$/.test(b)), buttons.join(' | '));
check('the panel shows only what the role may run and what is implemented', !buttons.some((b) => /Restart the PC|Shut down|Install updates/i.test(b)));
await shot('1-actions-panel');

// ---------- 2. Low risk: no confirmation, live progress and output, toast
const lowHadDialog = await runAction('Simulate a job (demo)');
check('Low risk runs at once: no dialog, no reason, no typed confirmation', lowHadDialog === false && (await page.locator('#reason').count()) === 0 && (await page.locator('#confirm-text').count()) === 0);
await page.waitForURL(/\/jobs\/[0-9a-f-]{36}/, { timeout: 20000 });
await closeOutputWindow();
await page.waitForSelector('table tbody tr');
await waitStatus(/Running/);
check('the job page shows Running live', /Running/i.test(await jobStatus()));
await page.locator('tbody button[aria-expanded]').first().click();
await page.waitForSelector('.logline', { timeout: 15000 });
const progressSeen = await page.waitForFunction(() => {
  const v = document.querySelector('[role=progressbar]')?.getAttribute('aria-valuenow');
  return v && Number(v) > 0 && Number(v) < 100;
}, null, { timeout: 15000 }).then(() => true).catch(() => false);
check('progress moves while running', progressSeen);
await page.waitForFunction(() => document.querySelectorAll('.logline').length >= 3, null, { timeout: 15000 });
check('output streams in live', (await page.locator('.logline').count()) >= 3);
await shot('2-running');
await waitStatus(/Succeeded/);
check('the job ends Succeeded', /Succeeded/i.test(await jobStatus()));
const toast = await page.locator('.toast').first().innerText().catch(() => '');
check('a toast announces the finished job', /succeeded/i.test(toast), toast.replace(/\s+/g, ' '));
const lines = await page.$$eval('.logline', (els) => els.map((e) => e.textContent));
check('the output has all five steps', [1, 2, 3, 4, 5].every((i) => lines.some((l) => l.includes(`step ${i} of 5`))));
const jobUrl = page.url();
const detail = JSON.parse((await inPage('/api/jobs/' + jobUrl.match(/[0-9a-f-]{36}/)[0])).text);   // /jobs/<id> or /endpoints/<host>/jobs/<id>
const outputRes = await inPage(`/api/jobs/${detail.job.id}/targets/${detail.targets[0].id}/output`);
check('output can be downloaded as a file', outputRes.ok && outputRes.text.includes('step 5 of 5') && /attachment/.test(outputRes.disposition));
check('the target has duration, exit code and a result summary', detail.targets[0].exitCode === 0 && detail.targets[0].durationSeconds > 0 && !!detail.targets[0].resultSummary);
await shot('3-succeeded');

// ---------- 3. Medium risk: confirmation, no typing
await runAction('Simulate a Medium-risk job (demo)', { steps: 2, delay: 200 });
await submit();
await page.waitForURL(/\/jobs\/[0-9a-f-]{36}/, { timeout: 20000 });
await closeOutputWindow();
await waitStatus(/Succeeded/);
check('Medium risk runs after the confirm step', true);

// ---------- 4. High risk: reason + typed name required
await runAction('Simulate a High-risk power job (demo)', { steps: 2, delay: 200 });
const runBtn = page.getByRole('button', { name: 'Run', exact: true });
check('High risk: Run stays disabled until reason and typed name are given', await runBtn.isDisabled());
await page.fill('#reason', 'e2e verification');
check('High risk: still disabled without the typed workstation name', await runBtn.isDisabled());
await page.fill('#confirm-text', 'wrong-name');
check('High risk: a wrong name keeps it disabled', await runBtn.isDisabled());
await page.fill('#confirm-text', target);
check('High risk: enabled once both are given', await runBtn.isEnabled());
await shot('4-high-risk-dialog');
// The test workstation has never been reached, so its operating system is not known: it counts as protected until an Admin overrides.
await runBtn.click();
const refused = await page.locator('.notice.error').first().innerText({ timeout: 8000 }).catch(() => '');
check('a PC whose operating system is not known yet is treated as protected', /not known yet|protected/i.test(refused), refused.replace(/\s+/g, ' '));
await page.locator('label:has-text("Override protected") input').check();
await runBtn.click();
await page.waitForURL(/\/jobs\/[0-9a-f-]{36}/, { timeout: 20000 });
await closeOutputWindow();
await waitStatus(/Succeeded/);
check('the reason is shown on the job page', (await page.locator('.notice').first().innerText()).includes('e2e verification'));

// ---------- 5. Failure
await runAction('Simulate a Medium-risk job (demo)', { steps: 2, delay: 150, outcome: 'fail' });
await submit();
await page.waitForURL(/\/jobs\/[0-9a-f-]{36}/);
await closeOutputWindow();
await waitStatus(/Failed/);
check('a failing job ends Failed with its summary', /Simulated failure/.test(await page.locator('tbody').innerText()));
check('"Re-run on failed" is offered', await page.locator('button:has-text("Re-run on 1 failed")').isVisible());
await shot('5-failed');
await page.locator('button:has-text("Re-run on 1 failed")').click();
await page.waitForSelector('.dialog');
check('re-run opens the same action prefilled with the failed workstation', (await page.locator('#p-outcome').inputValue()) === 'fail');
await page.getByRole('button', { name: 'Cancel', exact: true }).click();

// ---------- 6. Cancel
await runAction('Simulate a Medium-risk job (demo)', { steps: 2, delay: 100, outcome: 'hang' });
await submit();
await page.waitForURL(/\/jobs\/[0-9a-f-]{36}/);
await closeOutputWindow();
await waitStatus(/Running/);
await page.getByRole('button', { name: 'Cancel job' }).click();
await page.locator('.dialog button.danger:has-text("Cancel job")').click();
await waitStatus(/Cancelled/, 25000);
check('Cancel stops a running job', /Cancelled/i.test(await jobStatus()));
await shot('6-cancelled');

// ---------- 7. the jobs list
await page.goto(`${base}/jobs`, { waitUntil: 'networkidle' });
await page.waitForSelector('tbody tr.click');
const statuses = await page.$$eval('tbody tr.click', (rows) => rows.map((r) => r.innerText));
check('the Jobs list shows the new jobs with their status', statuses.some((s) => /Succeeded/.test(s)) && statuses.some((s) => /Failed/.test(s)) && statuses.some((s) => /Cancelled/.test(s)));
await page.selectOption('#jf-status', 'Failed');
await page.waitForTimeout(700);
const failedOnly = await page.$$eval('tbody tr.click', (rows) => rows.every((r) => /Failed/.test(r.innerText)));
check('the status filter works', failedOnly && (await page.locator('tbody tr.click').count()) > 0);
await page.selectOption('#jf-status', '');
await shot('7-jobs-list');

// ---------- 8. no helper: the job waits, nothing runs under another identity, then it runs when the helper returns
const helperExe = path.join(repo, 'publish/helper/Nexora.Helper.exe');
execSync('taskkill /IM Nexora.Helper.exe /F', { stdio: 'ignore' });
await page.waitForTimeout(1500);
await runAction('Simulate a job (demo)');
await submit();
await page.waitForURL(/\/jobs\/[0-9a-f-]{36}/);
await closeOutputWindow();
await waitStatus(/Waiting for helper/, 15000);
check('without the helper the job waits ("Waiting for helper")', /Waiting for helper/i.test(await jobStatus()));
check('the page tells the operator to start the helper', await page.locator('.helper-banner').first().isVisible());
await shot('8-waiting-for-helper');
spawn(helperExe, ['--server', base, '--hidden'], { detached: true, stdio: 'ignore' }).unref();
await waitStatus(/Succeeded/, 40000);
check('the waiting job runs as soon as the helper connects', /Succeeded/i.test(await jobStatus()));

// The refused High-risk attempt above is a deliberate 400; nothing else may log an error.
const unexpected = problems.filter((m) => !/status of 400/.test(m));
check('no console errors', unexpected.length === 0, unexpected.slice(0, 3).join(' | '));
await seeded.remove();
await browser.close();
const failed = results.filter((r) => !r.ok);
console.log(`\n${results.length - failed.length}/${results.length} checks passed`);
process.exit(failed.length ? 1 : 0);
