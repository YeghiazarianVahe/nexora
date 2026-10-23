// ESLint for the React code. TypeScript itself is checked by `tsc` (npm run typecheck); lint adds what tsc cannot: the Rules of Hooks and
// exhaustive dependencies (react-hooks), and accessibility (jsx-a11y). Parsed with Babel because typescript-eslint does not support TypeScript 7 yet.
import babelParser from '@babel/eslint-parser';
import reactHooks from 'eslint-plugin-react-hooks';
import jsxA11y from 'eslint-plugin-jsx-a11y';
import globals from 'globals';

export default [
  { ignores: ['dist/**', 'node_modules/**', 'e2e/**', '**/*.test.ts', '**/*.test.tsx'] },
  {
    files: ['src/**/*.{ts,tsx}'],
    languageOptions: {
      parser: babelParser,
      parserOptions: {
        requireConfigFile: false,
        babelOptions: { presets: ['@babel/preset-typescript', '@babel/preset-react'] },
      },
      globals: { ...globals.browser },
    },
    plugins: { 'react-hooks': reactHooks, 'jsx-a11y': jsxA11y },
    linterOptions: { reportUnusedDisableDirectives: 'error' },
    rules: {
      'react-hooks/rules-of-hooks': 'error',
      ...jsxA11y.flatConfigs.recommended.rules,
      'react-hooks/exhaustive-deps': 'error',
      // A scrollable log must be reachable by keyboard: tabIndex is allowed on these roles.
      'jsx-a11y/no-noninteractive-tabindex': ['error', { roles: ['tabpanel', 'log', 'region'] }],
    },
  },
];
