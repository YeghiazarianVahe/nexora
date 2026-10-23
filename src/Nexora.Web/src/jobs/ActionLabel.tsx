/** The friendly action name with the technical command beneath it in muted monospace, so operators learn it ("Repair system files" / sfc /scannow). */
export function ActionLabel({ name, command }: { name: string; command?: string | null }) {
  return (
    <span className="alabel">
      <b className="aname">{name}</b>
      {command && <span className="acmd mono">{command}</span>}
    </span>
  );
}
