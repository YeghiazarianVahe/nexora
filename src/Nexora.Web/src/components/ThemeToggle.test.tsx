// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { ThemeToggle } from './ThemeToggle';

beforeEach(() => { document.documentElement.dataset.theme = 'dark'; localStorage.clear(); });
afterEach(() => { cleanup(); vi.restoreAllMocks(); delete document.documentElement.dataset.theme; });

it('switches in both directions and remembers the chosen theme', async () => {
  render(<ThemeToggle />);
  await userEvent.click(screen.getByRole('button', { name: 'Switch to light mode' }));
  expect(document.documentElement.dataset.theme).toBe('light');
  expect(localStorage.getItem('nexora-theme')).toBe('light');
  await userEvent.click(screen.getByRole('button', { name: 'Switch to dark mode' }));
  expect(document.documentElement.dataset.theme).toBe('dark');
  expect(localStorage.getItem('nexora-theme')).toBe('dark');
});

it('starts from the theme applied before React loads', () => {
  document.documentElement.dataset.theme = 'light';
  render(<ThemeToggle />);
  expect(screen.getByRole('button', { name: 'Switch to dark mode' })).toBeInTheDocument();
});

it('still switches if browser storage is unavailable', async () => {
  vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('Storage blocked'); });
  render(<ThemeToggle />);
  await userEvent.click(screen.getByRole('button', { name: 'Switch to light mode' }));
  expect(document.documentElement.dataset.theme).toBe('light');
  expect(screen.getByRole('button', { name: 'Switch to dark mode' })).toBeInTheDocument();
});

it('keeps an open tab in sync when another tab changes the saved theme', () => {
  render(<ThemeToggle />);
  fireEvent(window, new StorageEvent('storage', { key: 'nexora-theme', newValue: 'light' }));
  expect(document.documentElement.dataset.theme).toBe('light');
  expect(screen.getByRole('button', { name: 'Switch to dark mode' })).toBeInTheDocument();
  fireEvent(window, new StorageEvent('storage', { key: 'nexora-theme', newValue: 'invalid' }));
  expect(document.documentElement.dataset.theme).toBe('light');
});
