// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { StatLine, TimeChart } from './charts';

afterEach(cleanup);
const line = (values: (number | null)[]) => [{ key: 'cpu', label: 'CPU', color: 'blue', values }];

it('keeps low CPU load on a 0–100 scale with a visible alert threshold', () => {
  render(<TimeChart title="CPU" times={[0, 60_000]} series={line([1, 5])} threshold={{ value: 90, label: 'alert 90%' }} />);
  expect(screen.getByText('100%')).toBeInTheDocument();
  expect(screen.getByText('alert 90%')).toBeInTheDocument();
});

it('breaks the line across missing samples and long polling outages', () => {
  const { container } = render(<TimeChart title="CPU" times={[0, 60_000, 120_000, 1_200_000, 1_260_000]} series={line([10, 15, 20, 30, 35])} />);
  expect(container.querySelectorAll('path[stroke="blue"]')).toHaveLength(2);
});

it('sanitizes invalid points, sorts timestamps, and avoids degenerate paths on duplicates', () => {
  const { container } = render(<TimeChart title="CPU" times={[60_000, 0, 60_000, NaN]} series={line([5, 10, 20, Infinity])} />);
  expect(container.innerHTML).not.toMatch(/NaN|Infinity/);
  fireEvent.click(screen.getByRole('button', { name: 'Table' }));
  expect(screen.getByText('20.0%')).toBeInTheDocument();
  expect(screen.queryByText('5.0%')).not.toBeInTheDocument();
});

it('shows unavailable data instead of a fabricated zero or an old value labelled now', () => {
  const { rerender } = render(<TimeChart title="CPU" times={[0, 60_000]} series={line([-1, 101])} />);
  expect(screen.getByText(/No data in this range/)).toBeInTheDocument();
  rerender(<StatLine values={[10, null]} />);
  expect(screen.getByText('Unavailable')).toBeInTheDocument();
  expect(screen.getByText(/latest sample/)).toBeInTheDocument();
});

it('keeps a focused tooltip valid when a refreshed series becomes shorter', () => {
  const { rerender, container } = render(<TimeChart title="CPU" times={[0, 60_000, 120_000]} series={line([10, 20, 30])} />);
  fireEvent.focus(screen.getByRole('group'));
  rerender(<TimeChart title="CPU" times={[60_000]} series={line([20])} />);
  expect(screen.getByRole('status')).toHaveTextContent('20.0%');
  expect(container.innerHTML).not.toMatch(/NaN|Infinity/);
});
