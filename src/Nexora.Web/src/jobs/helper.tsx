import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { get } from '../api';
import { useHubOn } from '../realtime';
import { fmtDate } from '../ui';

export interface HelperStatus {
  connected: boolean;
  /** Connected at some point since the server started: tells "dropped" from "never connected". */
  everConnected: boolean;
  lastConnectedUtc: string | null;
  lastDisconnectedUtc: string | null;
  lastAttempt: { atUtc: string; outcome: string; reason: string; remoteIp: string | null } | null;
  serverUrl: string;
  downloadAvailable: boolean;
}

/**
 * Whether the signed-in operator's helper is connected. The server pushes "HelperStatusChanged" the moment it connects or drops, so the
 * banner and the top bar follow within a second without a page refresh; the poll is only a safety net.
 */
export function useHelperStatus() {
  const qc = useQueryClient();
  useHubOn('HelperStatusChanged', () => { void qc.invalidateQueries({ queryKey: ['helper-status'] }); });
  return useQuery({ queryKey: ['helper-status'], queryFn: () => get<HelperStatus>('/api/helper/status'), refetchInterval: 15_000 });
}

/** One paste into PowerShell on the operator's own PC: download the helper with Windows sign-in, then install its per-user autostart and start it. */
export const installCommand = (serverUrl: string) =>
  '$d = "$env:LOCALAPPDATA\\Nexora"; New-Item -ItemType Directory -Force $d | Out-Null; '
  + `iwr ${serverUrl}/api/helper/download -UseDefaultCredentials -OutFile "$d\\Nexora.Helper.exe"; `
  + `& "$d\\Nexora.Helper.exe" --server ${serverUrl} --install`;

const hhmm = (iso: string | null) => (iso ? fmtDate(iso) : '');

export function HelperBanner() {
  const q = useHelperStatus();
  const [copied, setCopied] = useState(false);
  const s = q.data;
  if (!s || s.connected) return null;

  const dropped = s.everConnected;
  const command = installCommand(s.serverUrl);
  const copy = async () => {
    try { await navigator.clipboard.writeText(command); setCopied(true); window.setTimeout(() => setCopied(false), 2500); } catch { setCopied(false); }
  };

  return (
    <div className="notice warn helper-banner" role="status">
      <b>{dropped ? 'Your Nexora Helper disconnected.' : 'Your Nexora Helper is not running.'}</b>{' '}
      Actions run in your own Windows session, so they are audited as you; that is why the helper has to run on <i>your</i> PC, and why they wait until it does.
      {dropped ? (
        <p>It was connected until {hhmm(s.lastDisconnectedUtc)}. It reconnects by itself within a minute of the network or the server coming back; if it does not, start it again.</p>
      ) : (
        <p>It has not connected since the server started. First time on this PC? Download it and run it once; it then starts at every sign-in.</p>
      )}
      {s.lastAttempt && s.lastAttempt.outcome !== 'accepted' && (
        <p className="helper-reason"><b>The server refused your last attempt</b> ({hhmm(s.lastAttempt.atUtc)}): {s.lastAttempt.reason}.</p>
      )}
      <div className="toolbar" style={{ marginBottom: 0 }}>
        {s.downloadAvailable && <a className="button primary" href="/api/helper/download" download>Download helper</a>}
        <button onClick={copy} aria-live="polite">{copied ? 'Copied' : 'Copy install command'}</button>
        <span className="muted">or run <span className="mono">Nexora.Helper.exe --install</span> if you already have it. Check <span className="mono">Nexora.Helper.exe --status</span> if it still does not connect.</span>
      </div>
    </div>
  );
}

/** Small status in the top bar for operators, independent of any page. */
export function HelperIndicator() {
  const q = useHelperStatus();
  const s = q.data;
  const state = !s ? 'connecting' : s.connected ? 'connected' : s.everConnected ? 'lost' : 'not running';
  const dot = state === 'connected' ? 'ok' : state === 'connecting' || state === 'lost' ? 'warn' : 'bad';
  const title = state === 'connected' ? 'Your Nexora Helper is connected: actions run as you.'
    : state === 'connecting' ? 'Checking your helper...'
    : state === 'lost' ? 'Your helper was connected and has gone; it reconnects by itself.'
    : 'Your helper is not running. Open Jobs for how to start it.';
  return (
    <div className="conn helper-indicator" title={title} role="status" data-testid="helper-indicator">
      <span className={`dot ${dot}`} /> Helper {state}
    </div>
  );
}
