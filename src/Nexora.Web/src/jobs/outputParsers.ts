/**
 * Plain-language summaries of what the well-known Windows tools printed, so an operator does not have to read a thousand lines of sfc or DISM
 * output. A summary is only ever offered when the output says something recognisable; otherwise there is none (never a guess).
 */
export type SummaryTone = 'ok' | 'warn' | 'bad' | 'info';

export interface OutputSummary {
  tone: SummaryTone;
  headline: string;
  details: string[];
}

const has = (text: string, re: RegExp) => re.test(text);

function sfc(text: string): OutputSummary | null {
  if (has(text, /did not find any integrity violations/i)) return { tone: 'ok', headline: 'No integrity violations found.', details: ['Windows system files are intact.'] };
  if (has(text, /found corrupt files and successfully repaired/i)) return { tone: 'warn', headline: 'Corrupt files were found and repaired.', details: ['Restart the PC to finish the repair.', 'Details are in C:\\Windows\\Logs\\CBS\\CBS.log.'] };
  if (has(text, /found corrupt files but was unable to fix/i)) return { tone: 'bad', headline: 'Corrupt files were found that could not be repaired.', details: ['Run "Repair Windows image" (DISM RestoreHealth), then run this again.', 'Details are in C:\\Windows\\Logs\\CBS\\CBS.log.'] };
  if (has(text, /pending system repair operation/i)) return { tone: 'warn', headline: 'A restart is pending.', details: ['Restart the PC, then run this again.'] };
  if (has(text, /could not perform the requested operation/i)) return { tone: 'bad', headline: 'The scan could not run.', details: ['Check that the PC is not in the middle of an update, then try again.'] };
  return null;
}

function dism(text: string): OutputSummary | null {
  const code = /Error:\s*(0x[0-9a-f]+)/i.exec(text)?.[1];
  if (code) {
    const hint = /0x800f081f|0x800f0906|0x800f0907/i.test(code) ? ['The source files could not be found. The PC may need access to Windows Update or a repair source.'] : [];
    return { tone: 'bad', headline: `DISM failed (${code}).`, details: hint };
  }
  if (has(text, /No component store corruption detected/i)) return { tone: 'ok', headline: 'No component store corruption detected.', details: [] };
  if (has(text, /The component store is repairable/i)) return { tone: 'warn', headline: 'The component store is damaged but repairable.', details: ['Run "Repair Windows image" (DISM RestoreHealth).'] };
  if (has(text, /The restore operation completed successfully/i)) return { tone: 'ok', headline: 'The Windows image was repaired.', details: ['Run "Repair system files" (sfc) afterwards.'] };
  if (has(text, /The operation completed successfully/i)) return { tone: 'ok', headline: 'DISM finished successfully.', details: [] };
  return null;
}

function chkdsk(text: string): OutputSummary | null {
  if (has(text, /found no problems/i)) return { tone: 'ok', headline: 'The file system has no problems.', details: [] };
  if (has(text, /found problems|errors found|corrupt/i)) return { tone: 'warn', headline: 'The scan found problems with the file system.', details: ['Schedule "Fix disk at next boot" to repair them.'] };
  return null;
}

function updatesFound(text: string): OutputSummary | null {
  const kbs = new Set([...text.matchAll(/\bKB\d{6,8}\b/gi)].map((m) => m[0].toUpperCase()));
  if (has(text, /no (pending )?updates (were )?(found|available)/i)) return { tone: 'ok', headline: 'No updates are waiting.', details: [] };
  if (kbs.size > 0) return { tone: 'info', headline: `${kbs.size} update${kbs.size === 1 ? '' : 's'} listed.`, details: [...kbs].slice(0, 20) };
  return null;
}

function updatesInstall(text: string): OutputSummary | null {
  const installed = /installed\D{0,12}(\d+)/i.exec(text)?.[1];
  const reboot = has(text, /reboot[^\n]{0,20}(required|true|yes|pending)|restart[^\n]{0,20}(required|pending)/i);
  if (has(text, /failed|error/i) && !installed) return { tone: 'bad', headline: 'The update installation reported errors.', details: reboot ? ['A restart is also required.'] : [] };
  if (installed !== undefined || reboot) {
    return { tone: reboot ? 'warn' : 'ok', headline: `${installed ?? 'The'} update${installed === '1' ? '' : 's'} installed.`, details: reboot ? ['A restart is required to finish.'] : [] };
  }
  return null;
}

function pendingReboot(text: string): OutputSummary | null {
  if (has(text, /reboot\s*required\W+\s*(true|yes|1)|restart is (pending|required)|pending reboot:\s*(yes|true)/i)) return { tone: 'warn', headline: 'A restart is pending.', details: ['The PC is waiting to restart before updates or repairs finish.'] };
  if (has(text, /reboot\s*required\W+\s*(false|no|0)|no (restart|reboot) (is )?(pending|required)|pending reboot:\s*(no|false)/i)) return { tone: 'ok', headline: 'No restart is pending.', details: [] };
  return null;
}

/** The summary for the output of one target of this action, or null when there is nothing recognisable to say. */
export function parseOutput(actionCode: string, lines: readonly string[]): OutputSummary | null {
  const text = lines.join('\n');
  if (text.trim() === '') return null;
  if (actionCode === 'repair.sfc') return sfc(text);
  if (actionCode.startsWith('repair.dism')) return dism(text);
  if (actionCode.startsWith('disk.chkdsk')) return chkdsk(text);
  if (actionCode === 'update.scan' || actionCode === 'update.history') return updatesFound(text);
  if (actionCode === 'update.install') return updatesInstall(text);
  if (actionCode === 'system.pendingreboot') return pendingReboot(text);
  return null;
}
