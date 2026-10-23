import { fmtDate } from '../../ui';

type Data = Record<string, unknown>;
const object = (v: unknown): Data => v !== null && typeof v === 'object' && !Array.isArray(v) ? v as Data : {};
const rows = (v: unknown): Data[] => Array.isArray(v) ? v.map(object) : [];
const number = (v: unknown) => typeof v === 'number' && Number.isFinite(v) ? v : null;
const percent = (v: unknown) => number(v) === null ? 'Not available' : `${v}%`;
const bytes = (v: unknown) => {
  const n = number(v);
  if (n === null) return 'Not available';
  const unit = n >= 1024 ** 3 ? 'GB' : 'MB';
  return `${(n / 1024 ** (unit === 'GB' ? 3 : 2)).toFixed(1)} ${unit}`;
};
const label = (s: string) => s.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/[-_]/g, ' ').replace(/^./, c => c.toUpperCase());
const text = (v: unknown) => v === null || v === undefined ? 'Not available' : typeof v === 'boolean' ? v ? 'Yes' : 'No' : String(v).length > 1_000 ? `${String(v).slice(0, 1_000)}… (see raw JSON)` : String(v);

function Resources({ data }: { data: Data }) {
  const cpu = object(data.cpu), memory = object(data.memory), disk = object(data.disk), search = object(data.search);
  const total = number(memory.totalBytes), free = number(memory.freeBytes);
  return <>
    <dl className="dg-meta dg-resource-summary">
      <div><dt>CPU usage</dt><dd>{percent(cpu.loadPercent)} · {text(cpu.logicalProcessors)} logical processors</dd></div>
      <div><dt>Memory usage</dt><dd>{percent(memory.usedPercent)}{total !== null && free !== null && <> · {bytes(total - free)} used of {bytes(total)}</>}</dd></div>
      <div><dt>Available memory</dt><dd>{bytes(memory.freeBytes)}</dd></div>
      <div><dt>Page file usage</dt><dd>{percent(memory.pageFileUsedPercent)}</dd></div>
    </dl>
    {rows(disk.volumes).length > 0 && <div className="dg-table-wrap"><table aria-label="Disk space"><thead><tr><th>Drive</th><th>Free space</th><th>Total size</th><th>Free</th></tr></thead><tbody>
      {rows(disk.volumes).map((v, i) => <tr key={i}><th scope="row">{text(v.letter)}:{v.label ? ` (${text(v.label)})` : ''}</th><td>{bytes(v.freeBytes)}</td><td>{bytes(v.sizeBytes)}</td><td>{percent(v.freePercent)}</td></tr>)}
    </tbody></table></div>}
    {Object.keys(search).length > 0 && <p className="small">Windows Search: {text(search.serviceState)} · Startup: {text(search.startMode)}{Object.values(object(search.indexing)).every(v => v === null) && ' · Indexing details not available'}</p>}
    {Object.values(object(search.indexing)).some(v => v !== null) && <Values value={search.indexing} />}
  </>;
}

function Processes({ data }: { data: Data }) {
  return <>
    <p className="muted small">A snapshot of individual processes{number(data.sampleSeconds) !== null ? `, sampled over ${data.sampleSeconds} seconds` : ''}. Multiple rows for the same app are separate processes.</p>
    {(['topMemory', 'topCpu'] as const).map(key => rows(data[key]).length > 0 && <div key={key} className="dg-table-wrap"><table aria-label={key === 'topMemory' ? 'Processes using the most memory' : 'Processes using the most CPU'}>
      <caption>{key === 'topMemory' ? 'Processes using the most memory' : 'Processes using the most CPU'}</caption>
      <thead><tr><th>Process</th><th>Process ID</th><th>Memory</th><th>CPU</th></tr></thead>
      <tbody>{rows(data[key]).slice(0, 50).map((p, i) => <tr key={i}><th scope="row">{text(p.name)}</th><td>{text(p.pid)}</td><td>{bytes(p.workingSetBytes)}</td><td>{percent(p.cpuPercent)}</td></tr>)}</tbody>
    </table></div>)}
  </>;
}

// Keep other diagnostic profiles readable too, without interpreting absent data as a failure.
function Values({ value, name = '', depth = 0 }: { value: unknown; name?: string; depth?: number }) {
  if (depth > 6) return <span>See raw JSON for additional details.</span>;
  if (Array.isArray(value)) return value.length ? <ul>{value.slice(0, 50).map((v, i) => <li key={i}><Values value={v} depth={depth + 1} /></li>)}{value.length > 50 && <li>More entries available in raw JSON.</li>}</ul> : <span>None</span>;
  if (value !== null && typeof value === 'object') return <dl className="dg-evidence">{Object.entries(object(value)).map(([k, v]) => <div key={k}><dt title={label(k)}>{label(k)}</dt><dd><Values value={v} name={k} depth={depth + 1} /></dd></div>)}</dl>;
  return <span>{name.endsWith('Bytes') ? bytes(value) : name.endsWith('Percent') ? percent(value) : text(value)}</span>;
}

export function DiagnosticEvidence({ evidence }: { evidence: Data }) {
  return <div className="dg-readable-evidence">{Object.entries(evidence).map(([id, value]) => {
    const data = object(value);
    const errors = Array.isArray(data.errors) ? data.errors : [];
    const known = id === 'system-resources' || id === 'pc-slow-processes';
    return <section key={id} aria-label={id === 'system-resources' ? 'System resources' : id === 'pc-slow-processes' ? 'Processes' : label(id)}>
      <h3>{id === 'system-resources' ? 'System resources' : id === 'pc-slow-processes' ? 'Processes' : label(id)}</h3>
      {typeof data.collectedUtc === 'string' && <p className="muted small">Collected {fmtDate(data.collectedUtc)}{number(data.elapsedMs) !== null ? ` · ${(Number(data.elapsedMs) / 1000).toFixed(1)} seconds` : ''}</p>}
      {(data.incomplete === true || data.truncated === true) && <p className="notice warn">{data.incomplete === true ? `Some data could not be collected${data.incompleteReason ? `: ${text(data.incompleteReason)}` : '.'}` : 'The collected output was shortened.'}</p>}
      {errors.length > 0 && <div className="notice warn"><b>Collection notes</b><ul>{errors.map((error, i) => <li key={i}>{text(error)}</li>)}</ul></div>}
      {known ? id === 'system-resources' ? <Resources data={data} /> : <Processes data={data} /> : <Values value={value} />}
    </section>;
  })}<p className="muted small">Sizes use 1,024-based units. “Not available” means the collector did not report a value.</p></div>;
}
