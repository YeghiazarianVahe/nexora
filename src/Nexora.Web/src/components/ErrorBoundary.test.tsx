// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { ErrorBoundary } from './ErrorBoundary';

function Bomb({ explode }: { explode: boolean }) {
  if (explode) throw new Error('widget exploded');
  return <p>all good</p>;
}

describe('ErrorBoundary (M9)', () => {
  it('shows the problem and the rest of the page keeps working, then Try again recovers', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => undefined);
    const user = userEvent.setup();
    function Harness() {
      const [explode, setExplode] = useState(true);
      return (
        <>
          <button onClick={() => setExplode(false)}>fix it</button>
          <p>menu stays</p>
          <ErrorBoundary what="this page"><Bomb explode={explode} /></ErrorBoundary>
        </>
      );
    }
    render(<Harness />);

    expect(screen.getByTestId('error-boundary')).toHaveTextContent('Something went wrong while showing this page');
    expect(screen.getByTestId('error-boundary')).toHaveTextContent('widget exploded');
    expect(screen.getByText('menu stays')).toBeInTheDocument();

    await user.click(screen.getByText('fix it'));
    await user.click(screen.getByText('Try again'));
    expect(screen.getByText('all good')).toBeInTheDocument();
  });
});
