export class ApiError extends Error {
  constructor(
    public status: number,
    message: string,
    public body?: unknown,
  ) {
    super(message);
  }
}

let csrfToken: string | null = null;

async function getToken(force = false): Promise<string> {
  if (force || !csrfToken) {
    const res = await fetch('/api/antiforgery', { credentials: 'same-origin' });
    if (!res.ok) throw new ApiError(res.status, 'Could not get an anti-forgery token');
    csrfToken = ((await res.json()) as { token: string }).token;
  }
  return csrfToken;
}

export const currentToken = () => getToken();

/** Same-origin JSON call. State-changing calls carry the anti-forgery token; Windows SSO supplies identity. */
export async function api<T>(path: string, init: { method?: string; body?: unknown; signal?: AbortSignal } = {}): Promise<T> {
  const method = init.method ?? 'GET';
  const unsafe = method !== 'GET';

  const send = async (forceToken: boolean) => {
    const headers: Record<string, string> = {};
    if (unsafe) headers['X-XSRF-TOKEN'] = await getToken(forceToken);
    if (init.body !== undefined) headers['Content-Type'] = 'application/json';
    return fetch(path, {
      method,
      headers,
      credentials: 'same-origin',
      signal: init.signal,
      body: init.body === undefined ? undefined : JSON.stringify(init.body),
    });
  };

  let res = await send(false);
  if (unsafe && res.status === 400) {
    const text = await res.clone().text();
    if (text.includes('anti-forgery')) res = await send(true); // token expired or rotated: fetch a new one once
  }

  if (!res.ok) {
    let message = res.statusText;
    let parsed: unknown;
    try {
      const body = (await res.json()) as { error?: string; title?: string };
      parsed = body;
      message = body.error ?? body.title ?? message;
    } catch {
      /* not JSON */
    }
    throw new ApiError(res.status, message || `HTTP ${res.status}`, parsed);
  }
  if (res.status === 204) return undefined as T;
  return (await res.json()) as T;
}

export const get = <T,>(path: string, signal?: AbortSignal) => api<T>(path, signal ? { signal } : undefined);
export const post = <T,>(path: string, body?: unknown) => api<T>(path, { method: 'POST', body });
export const put = <T,>(path: string, body?: unknown) => api<T>(path, { method: 'PUT', body });
export const del = <T,>(path: string) => api<T>(path, { method: 'DELETE' });

/** POST that returns a file. The server names it (Content-Disposition); errors come back as JSON like everywhere else. */
export async function apiDownload(path: string, body: unknown): Promise<{ blob: Blob; filename: string }> {
  const send = async (force: boolean) => fetch(path, {
    method: 'POST',
    credentials: 'same-origin',
    headers: { 'X-XSRF-TOKEN': await getToken(force), 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
  let res = await send(false);
  if (res.status === 400 && (await res.clone().text()).includes('anti-forgery')) res = await send(true);
  if (!res.ok) {
    let message = res.statusText;
    try { message = ((await res.json()) as { error?: string }).error ?? message; } catch { /* not JSON */ }
    throw new ApiError(res.status, message || `HTTP ${res.status}`);
  }
  const disposition = res.headers.get('Content-Disposition') ?? '';
  const filename = /filename="([^"]+)"/.exec(disposition)?.[1] ?? 'nexora-endpoints.csv';
  return { blob: await res.blob(), filename };
}
