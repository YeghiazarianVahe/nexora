/**
 * Selection is a Set of endpoint ids, never row indexes, so it survives virtualization, sorting, filtering and live
 * updates (rows re-rendering, reordering or being replaced by new objects).
 */
export type Selection = ReadonlySet<string>;

export const emptySelection: Selection = new Set();

export function toggle(sel: Selection, id: string, on: boolean): Selection {
  const next = new Set(sel);
  if (on) next.add(id); else next.delete(id);
  return next;
}

export function addMany(sel: Selection, ids: readonly string[]): Selection {
  return new Set([...sel, ...ids]);
}

export function removeMany(sel: Selection, ids: readonly string[]): Selection {
  const drop = new Set(ids);
  return new Set([...sel].filter((id) => !drop.has(id)));
}

/** Drops ids that no longer exist (a deleted or renamed endpoint must not stay selected and be acted on later). Returns the same set when nothing changed. */
export function prune(sel: Selection, existingIds: ReadonlySet<string>): Selection {
  const kept = [...sel].filter((id) => existingIds.has(id));
  return kept.length === sel.size ? sel : new Set(kept);
}

export type HeaderState = 'none' | 'some' | 'all';

/** State of the header checkbox for the rows in the current filtered view. */
export function headerState(sel: Selection, visibleIds: readonly string[]): HeaderState {
  if (visibleIds.length === 0) return 'none';
  const n = visibleIds.filter((id) => sel.has(id)).length;
  return n === 0 ? 'none' : n === visibleIds.length ? 'all' : 'some';
}

/** Selected endpoints that the current filter is hiding. */
export function hiddenCount(sel: Selection, visibleIds: readonly string[]): number {
  const visible = new Set(visibleIds);
  return [...sel].filter((id) => !visible.has(id)).length;
}

/** Header checkbox: select every visible row, or (when all are already selected) deselect them. */
export function toggleHeader(sel: Selection, visibleIds: readonly string[]): Selection {
  return headerState(sel, visibleIds) === 'all' ? removeMany(sel, visibleIds) : addMany(sel, visibleIds);
}
