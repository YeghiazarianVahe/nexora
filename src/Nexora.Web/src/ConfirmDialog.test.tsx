// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useEffect, useState } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ConfirmDialog } from './ui';

afterEach(cleanup);

/** Mimics the Workstations page: the parent re-renders constantly (live updates) and passes fresh inline callbacks. */
function Host({ onConfirm, phrase = 'delete 2699', ticking = false }: { onConfirm: () => void; phrase?: string; ticking?: boolean }) {
  const [open, setOpen] = useState(false);
  const [, setTick] = useState(0);
  useEffect(() => {
    if (!ticking) return;
    const t = setInterval(() => setTick((n) => n + 1), 5);
    return () => clearInterval(t);
  }, [ticking]);
  return (
    <>
      <button onClick={() => setOpen(true)}>Delete selected</button>
      {open && (
        <ConfirmDialog title="Delete selected workstations" danger requireText={phrase} confirmLabel="Delete 2699" message={<p>Removes records.</p>}
          onConfirm={() => { onConfirm(); setOpen(false); }} onCancel={() => setOpen(false)} />
      )}
    </>
  );
}

const open = async (user: ReturnType<typeof userEvent.setup>) => {
  await user.click(screen.getByRole('button', { name: 'Delete selected' }));
  return { input: screen.getByLabelText(/to confirm/i) as HTMLInputElement, button: screen.getByRole('button', { name: 'Delete 2699' }) as HTMLButtonElement };
};

describe('type-to-confirm dialog', () => {
  it('focuses the input on open and accepts typing immediately', async () => {
    const user = userEvent.setup();
    render(<Host onConfirm={() => {}} />);
    const { input } = await open(user);
    expect(input).toHaveFocus();
    await user.keyboard('delete');
    expect(input.value).toBe('delete');
  });

  it('keeps focus while the parent re-renders on every tick (the original bug)', async () => {
    const user = userEvent.setup();
    render(<Host onConfirm={() => {}} ticking />);
    const { input, button } = await open(user);
    await user.keyboard('delete 2699');
    expect(input.value).toBe('delete 2699');
    expect(input).toHaveFocus();
    expect(button).toBeEnabled();
  });

  it('enables Delete only for the exact phrase (word case-insensitive, trimmed, exact count)', async () => {
    const user = userEvent.setup();
    render(<Host onConfirm={() => {}} />);
    const { input, button } = await open(user);
    expect(button).toBeDisabled();
    for (const wrong of ['delete', 'delete 269', 'delete 26990', 'delete 2698']) {
      await user.clear(input); await user.type(input, wrong);
      expect(button, wrong).toBeDisabled();
    }
    await user.clear(input); await user.type(input, '  DeLeTe 2699 ');
    expect(button).toBeEnabled();
  });

  it('Enter submits only when the text matches', async () => {
    const user = userEvent.setup();
    const onConfirm = vi.fn();
    render(<Host onConfirm={onConfirm} />);
    const { input } = await open(user);
    await user.type(input, 'delete 26{Enter}');
    expect(onConfirm).not.toHaveBeenCalled();
    await user.type(input, '99{Enter}');
    expect(onConfirm).toHaveBeenCalledTimes(1);
  });

  it('Escape cancels and focus returns to the trigger', async () => {
    const user = userEvent.setup();
    const onConfirm = vi.fn();
    render(<Host onConfirm={onConfirm} />);
    const trigger = screen.getByRole('button', { name: 'Delete selected' });
    await open(user);
    await user.keyboard('{Escape}');
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(onConfirm).not.toHaveBeenCalled();
    expect(trigger).toHaveFocus();
  });

  it('allows paste and keeps Tab inside the dialog', async () => {
    const user = userEvent.setup();
    render(<Host onConfirm={() => {}} />);
    const { input, button } = await open(user);
    await user.click(input);
    await user.paste('delete 2699');
    expect(button).toBeEnabled();
    for (let i = 0; i < 6; i++) await user.tab();
    expect(screen.getByRole('dialog')).toContainElement(document.activeElement as HTMLElement);
  });
});
