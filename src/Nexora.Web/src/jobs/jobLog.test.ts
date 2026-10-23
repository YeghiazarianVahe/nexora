import { describe, expect, it } from 'vitest';
import { firstGap, mergeLines } from './jobLog';
import type { LogLineDto } from './jobTypes';
import { parseOutput } from './outputParsers';

const line = (seq: number, text = `line ${seq}`): LogLineDto => ({ seq, timestampUtc: '2026-10-13T10:00:00Z', stream: 'stdout', text });
const seqs = (ls: LogLineDto[]) => ls.map((l) => l.seq);

describe('mergeLines (M7): lines are identified by seq', () => {
  it('appends new lines in order', () => {
    expect(seqs(mergeLines([line(1), line(2)], [line(3), line(4)]))).toEqual([1, 2, 3, 4]);
  });

  it('drops duplicates, such as a push that also arrived in a page', () => {
    expect(seqs(mergeLines([line(1), line(2), line(3)], [line(2), line(3), line(4)]))).toEqual([1, 2, 3, 4]);
  });

  it('puts lines that arrive late into their place instead of losing them behind a high-water mark', () => {
    const afterReconnect = mergeLines([line(1), line(2), line(5)], [line(3), line(4)]);
    expect(seqs(afterReconnect)).toEqual([1, 2, 3, 4, 5]);
  });

  it('returns the same array when nothing is new (no re-render)', () => {
    const prev = [line(1), line(2)];
    expect(mergeLines(prev, [])).toBe(prev);
    expect(seqs(mergeLines(prev, [line(2)]))).toEqual([1, 2]);
  });

  it('keeps the first copy of a seq', () => {
    expect(mergeLines([line(1, 'first')], [line(1, 'second')])[0].text).toBe('first');
  });
});

describe('firstGap: where to re-read from after a reconnect', () => {
  it('is the last seq of the unbroken run from 1', () => {
    expect(firstGap([])).toBe(0);
    expect(firstGap([line(1), line(2), line(3)])).toBe(3);
    expect(firstGap([line(1), line(2), line(5), line(6)])).toBe(2); // 3 and 4 are missing: ask again from 2
  });
});

describe('parseOutput: plain-language summaries', () => {
  const sum = (code: string, text: string) => parseOutput(code, text.split('\n'));

  it('sfc', () => {
    expect(sum('repair.sfc', 'Windows Resource Protection did not find any integrity violations.')).toMatchObject({ tone: 'ok' });
    expect(sum('repair.sfc', 'Windows Resource Protection found corrupt files and successfully repaired them.')).toMatchObject({ tone: 'warn' });
    const bad = sum('repair.sfc', 'Windows Resource Protection found corrupt files but was unable to fix some of them.');
    expect(bad?.tone).toBe('bad');
    expect(bad?.details.join(' ')).toContain('RestoreHealth');
  });

  it('DISM, with the error code and the common causes', () => {
    expect(sum('repair.dism.scan', 'No component store corruption detected.\nThe operation completed successfully.')).toMatchObject({ tone: 'ok', headline: expect.stringContaining('No component store corruption') });
    expect(sum('repair.dism.check', 'The component store is repairable.')).toMatchObject({ tone: 'warn' });
    expect(sum('repair.dism.restore', 'Error: 0x800f081f\nThe source files could not be found.')).toMatchObject({ tone: 'bad', headline: expect.stringContaining('0x800f081f') });
  });

  it('chkdsk, updates and pending reboot', () => {
    expect(sum('disk.chkdsk.scan', 'Windows has scanned the file system and found no problems.')?.tone).toBe('ok');
    expect(sum('update.scan', 'KB5031356 Cumulative update\nKB5030999 .NET update\nKB5031356 again')).toMatchObject({ headline: '2 updates listed.' });
    expect(sum('update.scan', 'No updates found')?.tone).toBe('ok');
    expect(sum('update.install', 'Installed 3 updates\nReboot required: True')).toMatchObject({ tone: 'warn', headline: '3 updates installed.' });
    expect(sum('system.pendingreboot', 'RebootRequired: True')).toMatchObject({ tone: 'warn' });
    expect(sum('system.pendingreboot', 'RebootRequired: False')).toMatchObject({ tone: 'ok' });
  });

  it('says nothing when it does not recognise the output, or for other actions', () => {
    expect(sum('repair.sfc', 'something completely different')).toBeNull();
    expect(sum('svc.restart', 'The operation completed successfully.')).toBeNull();
    expect(parseOutput('repair.sfc', [])).toBeNull();
  });
});
