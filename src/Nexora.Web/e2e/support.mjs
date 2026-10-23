// Shared by the end-to-end suites: they seed the data they need and clean it up, instead of depending on a particular PC being in the fleet.
import { execSync } from 'node:child_process';

/** The Windows SID and account of the person running the suite (the one whose Edge session signs in to Nexora). */
export function whoAmI() {
  const out = execSync('C:/Windows/System32/whoami.exe /user /fo csv /nh', { encoding: 'utf8' }).trim();
  const [account, sid] = out.split('","').map((s) => s.replace(/"/g, ''));
  return { account, sid };
}

/** fetch() inside the page, so the browser's Windows sign-in and the anti-forgery cookie/token apply. */
export function api(page) {
  const call = (method, url, body) => page.evaluate(async ({ method, url, body }) => {
    const headers = { 'Content-Type': 'application/json' };
    if (method !== 'GET') {
      const t = await (await fetch('/api/antiforgery', { credentials: 'same-origin' })).json();
      headers['X-XSRF-TOKEN'] = t.token;
    }
    const r = await fetch(url, { method, headers, credentials: 'same-origin', body: body === undefined ? undefined : JSON.stringify(body) });
    const text = await r.text();
    let json = null;
    try { json = JSON.parse(text); } catch { /* not JSON */ }
    return { status: r.status, ok: r.ok, json, text };
  }, { method, url, body });
  return { get: (u) => call('GET', u), post: (u, b) => call('POST', u, b), del: (u) => call('DELETE', u) };
}

/**
 * Adds a workstation just for this run (a name that cannot clash with a real PC) and returns it; remove() deletes it again.
 * The simulated actions run in YOUR helper and touch nothing on the target, so an address that answers nothing is enough.
 */
export async function seedEndpoint(page, base) {
  await page.goto(`${base}/dashboard`, { waitUntil: 'networkidle' });
  const a = api(page);
  const name = `E2E-${Date.now().toString(36).toUpperCase()}`;
  const added = await a.post('/api/endpoints', { items: [{ name, address: name.toLowerCase() }], groupIds: [] });
  if (!added.ok) throw new Error(`Could not seed a test workstation (${added.status}): ${added.text}. The suite needs an Admin account.`);
  const created = Array.isArray(added.json) ? added.json[0] : added.json?.[0] ?? added.json;
  return { name, id: created.id, remove: () => a.del(`/api/endpoints/${created.id}`) };
}

/** Whether the server lists the simulated demo actions (Features:DemoActions); the jobs suite cannot run without them. */
export async function demoActionsAvailable(page, base) {
  await page.goto(`${base}/dashboard`, { waitUntil: 'networkidle' });
  const r = await api(page).get('/api/jobs/catalog');
  return r.ok && Array.isArray(r.json) && r.json.some((c) => c.code === 'demo.simulate');
}
